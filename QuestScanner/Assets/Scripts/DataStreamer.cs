using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MetaScan
{
    public class DataStreamer : MonoBehaviour
    {
        [SerializeField] private string serverIP = "192.168.1.100";
        [SerializeField] private int serverPort = 8765;
        [SerializeField] private int maxQueueSize = 30;

        public bool IsConnected { get; private set; }
        public string SessionId { get; private set; }
        public int FramesSent { get; private set; }
        public int FramesAccepted { get; private set; }
        public int FramesDropped { get; private set; }
        public event Action<string> OnConnected;
        public event Action<string> OnDisconnected;
        public event Action<string> OnSessionStarted;
        public event Action<int, bool> OnSessionStopped;
        public event Action<FrameFeedback> OnFrameFeedback;
        public event Action<string, string> OnReconstructionStatus;
        public event Action<string> OnError;

        private WebSocketClient wsClient;
        private CameraCapture cameraCapture;
        private readonly Queue<OutgoingMessage> sendQueue = new Queue<OutgoingMessage>();
        private bool isSending, sessionReady, stopRequested, stopQueued, stopAcknowledged, reconstructionRunning;
        private float nextStatusPoll;

        private struct OutgoingMessage
        {
            public byte[] payload;
            public WebSocketMessageType type;
            public bool frame;
        }

        [Serializable] private class IntrinsicsData { public float fx, fy, cx, cy; }
        [Serializable] private class PoseData { public Vector3 position; public Quaternion rotation; }
        [Serializable] private class FrameMetadata
        {
            public int protocol_version = 2;
            public int frame_index;
            public string camera_timestamp, camera_id;
            public string image_orientation = "top_left";
            public string pose_space = "unity_world_camera_to_world";
            public int image_width, image_height;
            public IntrinsicsData intrinsics;
            public PoseData pose;
        }
        [Serializable] private class SelectionData { public Vector3 center; public float radius; }
        [Serializable] private class ClientCommand
        {
            public string action, name, method;
            public SelectionData roi;
        }
        [Serializable] private class ServerMessage
        {
            public string action, session_id, status, message, step, error;
            public int total_frames, progress;
            public bool ready_for_reconstruction, quality_ok;
            public string[] quality_issues;
        }
        [Serializable] public class FrameFeedback
        {
            public string status;
            public int total_frames;
            public bool quality_ok;
            public string[] quality_issues;
        }

        private void Start()
        {
            cameraCapture = FindFirstObjectByType<CameraCapture>();
            if (cameraCapture != null)
            {
                cameraCapture.OnFrameCaptured += OnFrameCaptured;
                cameraCapture.OnCaptureError += ReportError;
            }
        }

        public string ServerIP { get => serverIP; set => serverIP = value; }
        public int ServerPort { get => serverPort; set => serverPort = value; }
        public void SetServerInfo(string ip, int port) { serverIP = ip; serverPort = port; }

        public void Connect()
        {
            if (wsClient != null) Disconnect();
            string url = $"ws://{serverIP}:{serverPort}/ws/scan";
            Debug.Log("[MetaScan-Stream] Connecting to " + url);
            var client = new WebSocketClient();
            wsClient = client;
            StartCoroutine(client.Connect(url,
                () => { if (wsClient == client) { IsConnected = true; OnConnected?.Invoke(url); } },
                message => { if (wsClient == client) HandleMessage(message); },
                error => { if (wsClient == client) ReportError(error); },
                reason =>
                {
                    if (wsClient != client) return;
                    IsConnected = false;
                    sessionReady = false;
                    reconstructionRunning = false;
                    wsClient = null;
                    cameraCapture?.StopCapturing();
                    OnDisconnected?.Invoke(reason);
                }));
        }

        public void Disconnect()
        {
            cameraCapture?.StopCapturing();
            wsClient?.Close();
            wsClient = null;
            IsConnected = false;
            sessionReady = false;
            reconstructionRunning = false;
            stopRequested = stopQueued = stopAcknowledged = false;
            SessionId = null;
            sendQueue.Clear();
        }

        public void StartSession(string name = "scan", bool hasSelection = false,
            Vector3 selectionCenter = default, float selectionRadius = 0f)
        {
            if (!IsConnected || cameraCapture == null || !cameraCapture.IsReady)
            {
                ReportError("Physical camera or server is not ready.");
                return;
            }
            if (sessionReady || (SessionId != null && !stopAcknowledged) || reconstructionRunning)
            {
                ReportError("A session is already active.");
                return;
            }
            SessionId = null;
            stopRequested = stopQueued = stopAcknowledged = false;
            FramesSent = FramesAccepted = FramesDropped = 0;
            var command = new ClientCommand { action = "start_session", name = name };
            if (hasSelection)
                command.roi = new SelectionData { center = selectionCenter, radius = selectionRadius };
            EnqueueText(JsonUtility.ToJson(command));
        }

        public void StopSession()
        {
            cameraCapture?.StopCapturing();
            if (!IsConnected || stopRequested) return;
            stopRequested = true;
            if (sessionReady && !isSending)
                StartCoroutine(ProcessSendQueue());
        }

        public void RequestReconstruction(string method = "colmap")
        {
            if (IsConnected && SessionId != null)
                EnqueueText(JsonUtility.ToJson(new ClientCommand { action = "start_reconstruction", method = method }));
        }

        private void Update()
        {
            if (reconstructionRunning && IsConnected && Time.unscaledTime >= nextStatusPoll)
            {
                EnqueueText(JsonUtility.ToJson(new ClientCommand { action = "get_reconstruction_status" }));
                nextStatusPoll = Time.unscaledTime + 2f;
            }
        }

        private void OnFrameCaptured(CameraCapture.CapturedFrame frame)
        {
            if (!IsConnected || !sessionReady || stopRequested) return;
            if (sendQueue.Count >= maxQueueSize)
            {
                FramesDropped++;
                cameraCapture.StopCapturing();
                ReportError("Network queue is full; capture stopped before losing more frames.");
                StopSession();
                return;
            }
            var meta = new FrameMetadata
            {
                frame_index = frame.frameIndex,
                camera_timestamp = frame.cameraTimestamp,
                camera_id = frame.cameraId,
                image_width = frame.imageWidth,
                image_height = frame.imageHeight,
                intrinsics = new IntrinsicsData { fx = frame.fx, fy = frame.fy, cx = frame.cx, cy = frame.cy },
                pose = new PoseData { position = frame.position, rotation = frame.rotation }
            };
            byte[] metadata = Encoding.UTF8.GetBytes(JsonUtility.ToJson(meta));
            using (var stream = new MemoryStream(16 + metadata.Length + frame.imageData.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { (byte)'M', (byte)'S', (byte)'F', (byte)'2' });
                writer.Write((uint)metadata.Length);
                writer.Write((uint)frame.imageData.Length);
                writer.Write((uint)0); // No uncalibrated Environment Depth.
                writer.Write(metadata);
                writer.Write(frame.imageData);
                sendQueue.Enqueue(new OutgoingMessage { payload = stream.ToArray(), type = WebSocketMessageType.Binary, frame = true });
            }
            if (!isSending) StartCoroutine(ProcessSendQueue());
        }

        private void EnqueueText(string message)
        {
            sendQueue.Enqueue(new OutgoingMessage
            {
                payload = Encoding.UTF8.GetBytes(message), type = WebSocketMessageType.Text
            });
            if (!isSending) StartCoroutine(ProcessSendQueue());
        }

        private IEnumerator ProcessSendQueue()
        {
            isSending = true;
            while (IsConnected)
            {
                if (sendQueue.Count == 0 && stopRequested && sessionReady && !stopQueued)
                {
                    stopQueued = true;
                    sendQueue.Enqueue(new OutgoingMessage
                    {
                        payload = Encoding.UTF8.GetBytes("{\"action\":\"stop_session\"}"),
                        type = WebSocketMessageType.Text
                    });
                }
                if (sendQueue.Count == 0) break;
                var outgoing = sendQueue.Dequeue();
                Task send = wsClient.Send(outgoing.payload, outgoing.type);
                float deadline = Time.realtimeSinceStartup + 30f;
                while (!send.IsCompleted && Time.realtimeSinceStartup < deadline)
                    yield return null;
                if (!send.IsCompleted || send.IsFaulted || send.IsCanceled)
                {
                    ReportError("WebSocket send failed or timed out: " + send.Exception?.GetBaseException().Message);
                    Disconnect();
                    break;
                }
                if (outgoing.frame) FramesSent++;
            }
            isSending = false;
        }

        private void HandleMessage(string json)
        {
            ServerMessage message;
            try { message = JsonUtility.FromJson<ServerMessage>(json); }
            catch (Exception exc) { ReportError("Invalid server JSON: " + exc.Message); return; }
            if (message == null) return;
            switch (message.action)
            {
                case "session_started":
                    SessionId = message.session_id;
                    sessionReady = true;
                    OnSessionStarted?.Invoke(SessionId);
                    if (stopRequested && !isSending) StartCoroutine(ProcessSendQueue());
                    else cameraCapture?.StartCapturing();
                    break;
                case "frame_result":
                    FramesAccepted = message.total_frames;
                    OnFrameFeedback?.Invoke(new FrameFeedback
                    {
                        status = message.status, total_frames = message.total_frames,
                        quality_ok = message.quality_ok,
                        quality_issues = message.quality_issues
                    });
                    break;
                case "session_stopped":
                    sessionReady = false;
                    stopAcknowledged = true;
                    FramesAccepted = message.total_frames;
                    OnSessionStopped?.Invoke(message.total_frames, message.ready_for_reconstruction);
                    break;
                case "reconstruction_started":
                    reconstructionRunning = true;
                    nextStatusPoll = 0f;
                    OnReconstructionStatus?.Invoke("running", "Starting reconstruction");
                    break;
                case "reconstruction_status":
                    OnReconstructionStatus?.Invoke(message.status, message.step ?? message.error);
                    if (message.status == "completed" || message.status == "failed" ||
                        message.status == "interrupted")
                        reconstructionRunning = false;
                    break;
                case "error":
                    ReportError(message.message);
                    break;
            }
        }

        private void ReportError(string message)
        {
            Debug.LogError("[MetaScan-Stream] " + message);
            OnError?.Invoke(message);
        }

        private void OnDestroy()
        {
            Disconnect();
            if (cameraCapture != null)
            {
                cameraCapture.OnFrameCaptured -= OnFrameCaptured;
                cameraCapture.OnCaptureError -= ReportError;
            }
        }
    }

    public sealed class WebSocketClient
    {
        private ClientWebSocket socket;
        private CancellationTokenSource cancellation;
        private bool closed;
        private const int MaxMessageBytes = 1024 * 1024;

        public IEnumerator Connect(string url, Action onOpen, Action<string> onMessage,
            Action<string> onError, Action<string> onClose)
        {
            socket = new ClientWebSocket();
            cancellation = new CancellationTokenSource();
            Task connect = socket.ConnectAsync(new Uri(url), cancellation.Token);
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!connect.IsCompleted && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (!connect.IsCompleted || connect.IsFaulted || connect.IsCanceled)
            {
                onError?.Invoke("Connection failed: " + (connect.Exception?.ToString() ?? "timeout or cancellation"));
                Close();
                yield break;
            }
            onOpen?.Invoke();
            byte[] buffer = new byte[8192];
            using (var message = new MemoryStream())
            {
                while (!closed && socket.State == WebSocketState.Open)
                {
                    Task<WebSocketReceiveResult> receive = socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cancellation.Token);
                    while (!receive.IsCompleted) yield return null;
                    if (receive.IsFaulted || receive.IsCanceled)
                    {
                        if (!closed) onError?.Invoke("Receive failed: " + receive.Exception?.GetBaseException().Message);
                        break;
                    }
                    var result = receive.Result;
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > MaxMessageBytes)
                    {
                        onError?.Invoke("Unsupported or oversized server message.");
                        break;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                    {
                        onMessage?.Invoke(Encoding.UTF8.GetString(message.ToArray()));
                        message.SetLength(0);
                    }
                }
            }
            Close();
            onClose?.Invoke("Connection closed");
        }

        public Task Send(byte[] data, WebSocketMessageType type)
        {
            if (closed || socket == null || socket.State != WebSocketState.Open)
                return Task.FromException(new InvalidOperationException("WebSocket is closed"));
            return socket.SendAsync(new ArraySegment<byte>(data), type, true, cancellation.Token);
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            cancellation?.Cancel();
            socket?.Abort();
            socket?.Dispose();
            cancellation?.Dispose();
        }
    }
}
