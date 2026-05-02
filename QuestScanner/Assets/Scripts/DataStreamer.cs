/*
 * MetaScan — Data Streamer
 * Streams captured frames to PC server via WebSocket.
 * Implements binary frame protocol with send queue.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace MetaScan
{
    public class DataStreamer : MonoBehaviour
    {
        [Header("Server Settings")]
        [SerializeField] private string serverIP = "192.168.1.100";
        [SerializeField] private int serverPort = 8765;

        [Header("Streaming Settings")]
        [SerializeField] private int maxQueueSize = 30;

        // State
        public bool IsConnected { get; private set; }
        public string SessionId { get; private set; }
        public int FramesSent { get; private set; }

        // Events
        public event Action<string> OnConnected;
        public event Action<string> OnDisconnected;
        public event Action<string> OnSessionStarted;
        public event Action<FrameFeedback> OnFrameFeedback;
        public event Action<string> OnError;

        // WebSocket
        private WebSocketClient wsClient;
        private Queue<byte[]> sendQueue = new Queue<byte[]>();
        private bool isSending;

        // Frame protocol
        private static readonly byte[] FRAME_MAGIC = new byte[] { 0x4D, 0x53, 0x46, 0x01 }; // "MSF\x01"
        private const int HEADER_SIZE = 32;

        // Components
        private CameraCapture cameraCapture;
        private DepthCapture depthCapture;

        [Serializable]
        public class FrameFeedback
        {
            public string status;
            public int total_frames;
            public bool quality_ok;
            public string[] quality_issues;
        }

        private void Start()
        {
            cameraCapture = FindFirstObjectByType<CameraCapture>();
            depthCapture = FindFirstObjectByType<DepthCapture>();

            if (cameraCapture != null)
                cameraCapture.OnFrameCaptured += OnFrameCaptured;
        }

        // =========================================================
        // Connection
        // =========================================================

        public void SetServerInfo(string ip, int port)
        {
            serverIP = ip;
            serverPort = port;
        }

        public void Connect()
        {
            string url = $"ws://{serverIP}:{serverPort}/ws/scan";
            Debug.Log($"[MetaScan-Stream] Connecting to {url}...");

            wsClient = new WebSocketClient();
            StartCoroutine(wsClient.Connect(url,
                onOpen: () =>
                {
                    IsConnected = true;
                    OnConnected?.Invoke(url);
                    Debug.Log("[MetaScan-Stream] Connected to server");
                },
                onMessage: (msg) => HandleMessage(msg),
                onError: (err) =>
                {
                    OnError?.Invoke(err);
                    Debug.LogError($"[MetaScan-Stream] Error: {err}");
                },
                onClose: (reason) =>
                {
                    IsConnected = false;
                    OnDisconnected?.Invoke(reason);
                    Debug.Log($"[MetaScan-Stream] Disconnected: {reason}");
                }
            ));
        }

        public void Disconnect()
        {
            if (wsClient != null)
            {
                wsClient.Close();
                wsClient = null;
            }
            IsConnected = false;
        }

        // =========================================================
        // Session Management
        // =========================================================

        public void StartSession(string name = "scan")
        {
            if (!IsConnected) return;
            FramesSent = 0;
            sendQueue.Clear();

            string msg = "{\"action\":\"start_session\",\"name\":\"" + name + "\"}";
            wsClient.SendText(msg);
        }

        public void StopSession()
        {
            if (!IsConnected) return;

            // Stop capturing first
            if (cameraCapture != null) cameraCapture.StopCapturing();

            string msg = "{\"action\":\"stop_session\"}";
            wsClient.SendText(msg);
        }

        // =========================================================
        // Frame Handling
        // =========================================================

        private void OnFrameCaptured(CameraCapture.CapturedFrame frame)
        {
            if (!IsConnected || wsClient == null) return;

            // Get depth data
            byte[] depthData = new byte[0];
            if (depthCapture != null)
                depthData = depthCapture.GetDepthData();

            // Build binary frame
            byte[] binaryFrame = BuildBinaryFrame(frame, depthData);

            if (sendQueue.Count < maxQueueSize)
            {
                sendQueue.Enqueue(binaryFrame);
                if (!isSending)
                    StartCoroutine(ProcessSendQueue());
            }
        }

        private byte[] BuildBinaryFrame(CameraCapture.CapturedFrame frame, byte[] depthData)
        {
            // Pose data: 7 floats (px, py, pz, qx, qy, qz, qw)
            byte[] poseBytes = new byte[28];
            Buffer.BlockCopy(BitConverter.GetBytes(frame.position.x), 0, poseBytes, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(frame.position.y), 0, poseBytes, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(frame.position.z), 0, poseBytes, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(frame.rotation.x), 0, poseBytes, 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(frame.rotation.y), 0, poseBytes, 16, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(frame.rotation.z), 0, poseBytes, 20, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(frame.rotation.w), 0, poseBytes, 24, 4);

            // Intrinsics data: 4 floats (fx, fy, cx, cy)
            byte[] intrinsicsBytes = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(frame.fx), 0, intrinsicsBytes, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(frame.fy), 0, intrinsicsBytes, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(frame.cx), 0, intrinsicsBytes, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(frame.cy), 0, intrinsicsBytes, 12, 4);

            int imageLen = frame.imageData.Length;
            int depthLen = depthData.Length;
            int poseLen = poseBytes.Length;
            int intrinsicsLen = intrinsicsBytes.Length;

            int totalLen = HEADER_SIZE + imageLen + depthLen + poseLen + intrinsicsLen;
            byte[] buffer = new byte[totalLen];
            int offset = 0;

            // Header
            Buffer.BlockCopy(FRAME_MAGIC, 0, buffer, offset, 4); offset += 4;
            Buffer.BlockCopy(BitConverter.GetBytes((uint)frame.frameIndex), 0, buffer, offset, 4); offset += 4;
            Buffer.BlockCopy(BitConverter.GetBytes(frame.timestamp), 0, buffer, offset, 8); offset += 8;
            Buffer.BlockCopy(BitConverter.GetBytes((uint)imageLen), 0, buffer, offset, 4); offset += 4;
            Buffer.BlockCopy(BitConverter.GetBytes((uint)depthLen), 0, buffer, offset, 4); offset += 4;
            Buffer.BlockCopy(BitConverter.GetBytes((uint)poseLen), 0, buffer, offset, 4); offset += 4;
            Buffer.BlockCopy(BitConverter.GetBytes((uint)intrinsicsLen), 0, buffer, offset, 4); offset += 4;

            // Data segments
            Buffer.BlockCopy(frame.imageData, 0, buffer, offset, imageLen); offset += imageLen;
            if (depthLen > 0)
            {
                Buffer.BlockCopy(depthData, 0, buffer, offset, depthLen); offset += depthLen;
            }
            Buffer.BlockCopy(poseBytes, 0, buffer, offset, poseLen); offset += poseLen;
            Buffer.BlockCopy(intrinsicsBytes, 0, buffer, offset, intrinsicsLen);

            return buffer;
        }

        private IEnumerator ProcessSendQueue()
        {
            isSending = true;

            while (sendQueue.Count > 0 && IsConnected)
            {
                byte[] data = sendQueue.Dequeue();
                wsClient.SendBinary(data);
                FramesSent++;
                yield return null; // Yield one frame between sends
            }

            isSending = false;
        }

        // =========================================================
        // Message Handling
        // =========================================================

        private void HandleMessage(string message)
        {
            try
            {
                // Simple JSON parsing without external dependencies
                if (message.Contains("\"action\""))
                {
                    if (message.Contains("\"session_started\""))
                    {
                        // Extract session_id
                        int idStart = message.IndexOf("\"session_id\"") + 14;
                        int idEnd = message.IndexOf("\"", idStart);
                        if (idStart > 13 && idEnd > idStart)
                        {
                            SessionId = message.Substring(idStart, idEnd - idStart);
                            OnSessionStarted?.Invoke(SessionId);
                        }
                    }
                    else if (message.Contains("\"frame_result\""))
                    {
                        FrameFeedback feedback = ParseFrameFeedback(message);
                        if (feedback != null)
                            OnFrameFeedback?.Invoke(feedback);
                    }
                    else if (message.Contains("\"session_stopped\""))
                    {
                        Debug.Log("[MetaScan-Stream] Session stopped by server");
                    }
                    else if (message.Contains("\"error\""))
                    {
                        int msgStart = message.IndexOf("\"message\"") + 11;
                        int msgEnd = message.IndexOf("\"", msgStart);
                        if (msgStart > 10 && msgEnd > msgStart)
                        {
                            string errorMsg = message.Substring(msgStart, msgEnd - msgStart);
                            OnError?.Invoke(errorMsg);
                        }
                    }
                    else if (message.Contains("\"pong\""))
                    {
                        // Heartbeat response, no action needed
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[MetaScan-Stream] Message parse error: {e.Message}");
            }
        }

        private FrameFeedback ParseFrameFeedback(string json)
        {
            FrameFeedback fb = new FrameFeedback();

            // status
            fb.status = ExtractJsonString(json, "status");

            // total_frames
            string framesStr = ExtractJsonValue(json, "total_frames");
            if (int.TryParse(framesStr, out int frames))
                fb.total_frames = frames;

            // quality_ok
            string qualityStr = ExtractJsonValue(json, "quality_ok");
            fb.quality_ok = qualityStr == "true";

            // quality_issues (simplified — extract first issue)
            fb.quality_issues = new string[0];
            int issuesStart = json.IndexOf("\"quality_issues\"");
            if (issuesStart >= 0)
            {
                int arrStart = json.IndexOf("[", issuesStart);
                int arrEnd = json.IndexOf("]", arrStart);
                if (arrStart >= 0 && arrEnd > arrStart)
                {
                    string arrContent = json.Substring(arrStart + 1, arrEnd - arrStart - 1).Trim();
                    if (arrContent.Length > 0)
                    {
                        string[] parts = arrContent.Split(',');
                        fb.quality_issues = new string[parts.Length];
                        for (int i = 0; i < parts.Length; i++)
                        {
                            fb.quality_issues[i] = parts[i].Trim().Trim('"');
                        }
                    }
                }
            }

            return fb;
        }

        private string ExtractJsonString(string json, string key)
        {
            string searchKey = "\"" + key + "\"";
            int keyIdx = json.IndexOf(searchKey);
            if (keyIdx < 0) return "";

            int colonIdx = json.IndexOf(":", keyIdx + searchKey.Length);
            if (colonIdx < 0) return "";

            int valueStart = json.IndexOf("\"", colonIdx + 1);
            if (valueStart < 0) return "";

            int valueEnd = json.IndexOf("\"", valueStart + 1);
            if (valueEnd < 0) return "";

            return json.Substring(valueStart + 1, valueEnd - valueStart - 1);
        }

        private string ExtractJsonValue(string json, string key)
        {
            string searchKey = "\"" + key + "\"";
            int keyIdx = json.IndexOf(searchKey);
            if (keyIdx < 0) return "";

            int colonIdx = json.IndexOf(":", keyIdx + searchKey.Length);
            if (colonIdx < 0) return "";

            int start = colonIdx + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t'))
                start++;

            int end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']' && json[end] != ' ')
                end++;

            return json.Substring(start, end - start).Trim('"');
        }

        private void OnDestroy()
        {
            Disconnect();
            if (cameraCapture != null)
                cameraCapture.OnFrameCaptured -= OnFrameCaptured;
        }
    }

    // =========================================================
    // Minimal WebSocket Client (Unity-compatible)
    // =========================================================

    public class WebSocketClient
    {
        private System.Net.WebSockets.ClientWebSocket ws;
        private bool isConnected;
        private byte[] receiveBuffer = new byte[65536];

        private Action onOpen;
        private Action<string> onMessage;
        private Action<string> onError;
        private Action<string> onClose;

        public IEnumerator Connect(string url,
            Action onOpen, Action<string> onMessage,
            Action<string> onError, Action<string> onClose)
        {
            this.onOpen = onOpen;
            this.onMessage = onMessage;
            this.onError = onError;
            this.onClose = onClose;

            ws = new System.Net.WebSockets.ClientWebSocket();

            var connectTask = ws.ConnectAsync(new Uri(url), System.Threading.CancellationToken.None);

            while (!connectTask.IsCompleted)
                yield return null;

            if (connectTask.IsFaulted)
            {
                onError?.Invoke(connectTask.Exception?.InnerException?.Message ?? "Connection failed");
                yield break;
            }

            isConnected = true;
            onOpen?.Invoke();

            // Start receiving
            var receiveCoroutine = ReceiveLoop();
            while (receiveCoroutine.MoveNext())
                yield return receiveCoroutine.Current;
        }

        private IEnumerator ReceiveLoop()
        {
            while (isConnected && ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var buffer = new ArraySegment<byte>(receiveBuffer);
                var receiveTask = ws.ReceiveAsync(buffer, System.Threading.CancellationToken.None);

                while (!receiveTask.IsCompleted)
                    yield return null;

                if (receiveTask.IsFaulted)
                {
                    onError?.Invoke(receiveTask.Exception?.InnerException?.Message ?? "Receive error");
                    break;
                }

                var result = receiveTask.Result;

                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    isConnected = false;
                    onClose?.Invoke("Server closed connection");
                    break;
                }

                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
                    onMessage?.Invoke(message);
                }
            }
        }

        public void SendText(string text)
        {
            if (!isConnected || ws == null) return;
            byte[] data = Encoding.UTF8.GetBytes(text);
            var segment = new ArraySegment<byte>(data);
            ws.SendAsync(segment, System.Net.WebSockets.WebSocketMessageType.Text,
                true, System.Threading.CancellationToken.None);
        }

        public void SendBinary(byte[] data)
        {
            if (!isConnected || ws == null) return;
            var segment = new ArraySegment<byte>(data);
            ws.SendAsync(segment, System.Net.WebSockets.WebSocketMessageType.Binary,
                true, System.Threading.CancellationToken.None);
        }

        public void Close()
        {
            isConnected = false;
            if (ws != null && ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                    "Client closing", System.Threading.CancellationToken.None);
            }
        }
    }
}
