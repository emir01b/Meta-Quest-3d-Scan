/*
 * Meta3D Scanner - Data Streamer
 * Streams captured frames, depth data, and metadata to PC server
 * over WebSocket connection.
 */

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Meta3DScanner
{
    public class DataStreamer : MonoBehaviour
    {
        [Header("Server Settings")]
        [SerializeField] private string serverIP = "192.168.1.100";
        [SerializeField] private int serverPort = 8765;
        [SerializeField] private string scanName = "scan";

        [Header("Streaming Settings")]
        [SerializeField] private int maxQueueSize = 10;
        [SerializeField] private float reconnectDelay = 3f;
        [SerializeField] private bool autoReconnect = true;

        [Header("References")]
        [SerializeField] private CameraCapture cameraCapture;
        [SerializeField] private DepthCapture depthCapture;

        // WebSocket
        private WebSocketClient wsClient;
        private bool isConnected = false;
        private bool isSessionActive = false;
        private string currentSessionId;

        // Queue for frame data
        private ConcurrentQueue<byte[]> sendQueue = new ConcurrentQueue<byte[]>();
        private Thread sendThread;
        private bool sendThreadRunning = false;

        // Stats
        private int framesSent = 0;
        private int framesDropped = 0;
        private float lastFeedbackTime = 0;

        // Events
        public event Action<string> OnConnected;
        public event Action<string> OnDisconnected;
        public event Action<string> OnSessionStarted;
        public event Action<int> OnFrameSent;
        public event Action<FrameFeedback> OnFrameFeedback;
        public event Action<string> OnError;

        public bool IsConnected => isConnected;
        public bool IsSessionActive => isSessionActive;
        public string SessionId => currentSessionId;
        public int FramesSent => framesSent;

        [Serializable]
        public class FrameFeedback
        {
            public string status;
            public int frame_index;
            public bool quality_ok;
            public float blur_score;
            public float brightness;
            public string[] quality_issues;
            public int total_frames;
        }

        [Serializable]
        private class ServerMessage
        {
            public string action;
            public string session_id;
            public string message;
            public int total_frames;
            public bool ready_for_reconstruction;
            // Frame feedback fields
            public string status;
            public int frame_index;
            public bool quality_ok;
            public float blur_score;
            public float brightness;
            public string[] quality_issues;
        }

        private void Start()
        {
            if (cameraCapture == null)
                cameraCapture = GetComponent<CameraCapture>();
            if (depthCapture == null)
                depthCapture = GetComponent<DepthCapture>();

            // Subscribe to camera events
            if (cameraCapture != null)
            {
                cameraCapture.OnFrameCaptured += HandleFrameCaptured;
            }
        }

        /// <summary>
        /// Connect to the PC server
        /// </summary>
        public void Connect()
        {
            string wsUrl = $"ws://{serverIP}:{serverPort}/ws/scan";
            Debug.Log($"[Meta3D] Connecting to {wsUrl}...");

            StartCoroutine(ConnectCoroutine(wsUrl));
        }

        private IEnumerator ConnectCoroutine(string url)
        {
            wsClient = new WebSocketClient(url);
            wsClient.OnOpen += () =>
            {
                isConnected = true;
                Debug.Log("[Meta3D] Connected to server!");
                OnConnected?.Invoke(url);
            };

            wsClient.OnClose += (reason) =>
            {
                isConnected = false;
                isSessionActive = false;
                Debug.Log($"[Meta3D] Disconnected: {reason}");
                OnDisconnected?.Invoke(reason);
            };

            wsClient.OnMessage += HandleServerMessage;

            wsClient.OnError += (error) =>
            {
                Debug.LogError($"[Meta3D] WebSocket error: {error}");
                OnError?.Invoke(error);
            };

            wsClient.Connect();

            // Start send thread
            sendThreadRunning = true;
            sendThread = new Thread(SendThreadLoop);
            sendThread.IsBackground = true;
            sendThread.Start();

            yield return null;
        }

        /// <summary>
        /// Start a scanning session
        /// </summary>
        public void StartSession(string name = null)
        {
            if (!isConnected)
            {
                OnError?.Invoke("Not connected to server");
                return;
            }

            scanName = name ?? scanName;
            string msg = JsonUtility.ToJson(new
            {
                action = "start_session",
                name = scanName
            });
            wsClient.SendText($"{{\"action\":\"start_session\",\"name\":\"{scanName}\"}}");
            Debug.Log("[Meta3D] Starting session...");
        }

        /// <summary>
        /// Stop the scanning session
        /// </summary>
        public void StopSession()
        {
            isSessionActive = false;
            cameraCapture?.StopCapture();
            wsClient?.SendText("{\"action\":\"stop_session\"}");
            Debug.Log($"[Meta3D] Session stopped. Frames sent: {framesSent}");
        }

        /// <summary>
        /// Handle incoming messages from server
        /// </summary>
        private void HandleServerMessage(string message)
        {
            try
            {
                ServerMessage msg = JsonUtility.FromJson<ServerMessage>(message);

                switch (msg.action)
                {
                    case "session_started":
                        currentSessionId = msg.session_id;
                        isSessionActive = true;
                        framesSent = 0;
                        framesDropped = 0;
                        Debug.Log($"[Meta3D] Session started: {currentSessionId}");
                        OnSessionStarted?.Invoke(currentSessionId);
                        // Start capturing
                        cameraCapture?.StartCapture();
                        break;

                    case "session_stopped":
                        isSessionActive = false;
                        Debug.Log($"[Meta3D] Session stopped. Total frames: {msg.total_frames}. " +
                                  $"Ready for reconstruction: {msg.ready_for_reconstruction}");
                        break;

                    case "frame_result":
                        if (msg.status == "ok")
                        {
                            FrameFeedback feedback = new FrameFeedback
                            {
                                status = msg.status,
                                frame_index = msg.frame_index,
                                quality_ok = msg.quality_ok,
                                blur_score = msg.blur_score,
                                brightness = msg.brightness,
                                quality_issues = msg.quality_issues,
                                total_frames = msg.total_frames
                            };
                            OnFrameFeedback?.Invoke(feedback);
                        }
                        break;

                    case "error":
                        Debug.LogWarning($"[Meta3D] Server error: {msg.message}");
                        OnError?.Invoke(msg.message);
                        break;

                    case "pong":
                        // Heartbeat response
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Meta3D] Failed to parse server message: {e.Message}");
            }
        }

        /// <summary>
        /// Handle captured frame from camera and queue for sending
        /// </summary>
        private void HandleFrameCaptured(byte[] imageBytes, CameraCapture.CameraFrameData frameData)
        {
            if (!isSessionActive || !isConnected) return;

            // Check queue size to avoid memory issues
            if (sendQueue.Count >= maxQueueSize)
            {
                framesDropped++;
                if (framesDropped % 10 == 0)
                {
                    Debug.LogWarning($"[Meta3D] Queue full, dropped {framesDropped} frames");
                }
                return;
            }

            // Build binary message
            byte[] message = BuildFrameMessage(imageBytes, frameData);
            sendQueue.Enqueue(message);
        }

        /// <summary>
        /// Build a binary frame message for the server.
        /// Format: [4 bytes header_size][JSON header][image bytes]
        /// </summary>
        private byte[] BuildFrameMessage(byte[] imageBytes, CameraCapture.CameraFrameData frameData)
        {
            // Serialize metadata to JSON
            string metadataJson = JsonUtility.ToJson(frameData);
            byte[] headerBytes = Encoding.UTF8.GetBytes(metadataJson);
            int headerSize = headerBytes.Length;

            // Build message: [header_size (4 bytes)][header JSON][image data]
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                writer.Write(headerSize); // 4 bytes, little-endian uint32
                writer.Write(headerBytes); // JSON header
                writer.Write(imageBytes); // Image data

                // TODO: Add depth data from DepthCapture if available

                return ms.ToArray();
            }
        }

        /// <summary>
        /// Background thread for sending queued frames
        /// </summary>
        private void SendThreadLoop()
        {
            while (sendThreadRunning)
            {
                if (sendQueue.TryDequeue(out byte[] data))
                {
                    try
                    {
                        wsClient?.SendBinary(data);
                        Interlocked.Increment(ref framesSent);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[Meta3D] Send error: {e.Message}");
                    }
                }
                else
                {
                    Thread.Sleep(5); // Small sleep when queue is empty
                }
            }
        }

        /// <summary>
        /// Set server connection info
        /// </summary>
        public void SetServerInfo(string ip, int port)
        {
            serverIP = ip;
            serverPort = port;
        }

        public void Disconnect()
        {
            sendThreadRunning = false;
            isSessionActive = false;
            isConnected = false;
            sendThread?.Join(1000);
            wsClient?.Close();
            Debug.Log("[Meta3D] Disconnected from server");
        }

        private void OnDestroy()
        {
            if (cameraCapture != null)
            {
                cameraCapture.OnFrameCaptured -= HandleFrameCaptured;
            }
            Disconnect();
        }
    }

    /// <summary>
    /// Simple WebSocket client wrapper for Unity.
    /// On Quest (Android), this uses System.Net.WebSockets.
    /// </summary>
    public class WebSocketClient
    {
        private System.Net.WebSockets.ClientWebSocket ws;
        private string url;
        private Thread receiveThread;
        private bool isRunning;

        public event Action OnOpen;
        public event Action<string> OnClose;
        public event Action<string> OnMessage;
        public event Action<string> OnError;

        public WebSocketClient(string url)
        {
            this.url = url;
            ws = new System.Net.WebSockets.ClientWebSocket();
        }

        public async void Connect()
        {
            try
            {
                await ws.ConnectAsync(new Uri(url), System.Threading.CancellationToken.None);
                isRunning = true;
                OnOpen?.Invoke();

                // Start receive loop
                receiveThread = new Thread(ReceiveLoop);
                receiveThread.IsBackground = true;
                receiveThread.Start();
            }
            catch (Exception e)
            {
                OnError?.Invoke(e.Message);
            }
        }

        private async void ReceiveLoop()
        {
            byte[] buffer = new byte[65536];

            while (isRunning && ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                try
                {
                    var segment = new ArraySegment<byte>(buffer);
                    var result = await ws.ReceiveAsync(segment, System.Threading.CancellationToken.None);

                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        OnMessage?.Invoke(message);
                    }
                    else if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    {
                        isRunning = false;
                        OnClose?.Invoke("Server closed connection");
                    }
                }
                catch (Exception e)
                {
                    if (isRunning)
                    {
                        OnError?.Invoke(e.Message);
                        isRunning = false;
                    }
                }
            }
        }

        public async void SendText(string text)
        {
            if (ws.State != System.Net.WebSockets.WebSocketState.Open) return;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                await ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    System.Net.WebSockets.WebSocketMessageType.Text,
                    true,
                    System.Threading.CancellationToken.None
                );
            }
            catch (Exception e)
            {
                OnError?.Invoke(e.Message);
            }
        }

        public async void SendBinary(byte[] data)
        {
            if (ws.State != System.Net.WebSockets.WebSocketState.Open) return;
            try
            {
                await ws.SendAsync(
                    new ArraySegment<byte>(data),
                    System.Net.WebSockets.WebSocketMessageType.Binary,
                    true,
                    System.Threading.CancellationToken.None
                );
            }
            catch (Exception e)
            {
                OnError?.Invoke(e.Message);
            }
        }

        public async void Close()
        {
            isRunning = false;
            try
            {
                if (ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    await ws.CloseAsync(
                        System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                        "Client closing",
                        System.Threading.CancellationToken.None
                    );
                }
                ws.Dispose();
            }
            catch { }
        }
    }
}
