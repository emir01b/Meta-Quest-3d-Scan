/*
 * Meta3D Scanner - Scan Manager
 * Main orchestrator for the scanning process in MR mode.
 * Connects HandUIManager, ControllerManager, ScanPointer,
 * PointCloudVisualizer, and data streaming components.
 */

using System;
using System.Collections;
using UnityEngine;

namespace Meta3DScanner
{
    public class ScanManager : MonoBehaviour
    {
        [Header("Components (Auto-found if empty)")]
        [SerializeField] private CameraCapture cameraCapture;
        [SerializeField] private DepthCapture depthCapture;
        [SerializeField] private DataStreamer dataStreamer;
        [SerializeField] private ControllerManager controllerManager;
        [SerializeField] private HandUIManager handUIManager;
        [SerializeField] private ScanPointer scanPointer;
        [SerializeField] private PointCloudVisualizer pointCloudVisualizer;

        [Header("Scan Settings")]
        [SerializeField] private int minFramesRequired = 30;
        [SerializeField] private int targetFrames = 200;
        [SerializeField] private float scanTimeout = 300f; // 5 minutes max

        [Header("Audio Feedback")]
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private AudioClip captureSound;
        [SerializeField] private AudioClip warningSound;
        [SerializeField] private AudioClip completeSound;

        [Header("Colors")]
        [SerializeField] private Color goodQualityColor = new Color(0.2f, 0.9f, 0.4f);
        [SerializeField] private Color badQualityColor = new Color(1.0f, 0.3f, 0.3f);
        [SerializeField] private Color warningColor = new Color(1.0f, 0.8f, 0.2f);

        // State
        private enum ScanState
        {
            Idle,
            Connecting,
            Connected,
            Scanning,
            Processing,
            Complete,
            Error
        }
        private ScanState currentState = ScanState.Idle;
        private float scanStartTime;
        private int qualityIssueCount = 0;

        // Coverage tracking
        private float[] coverageAngles;
        private int coverageSections = 36;

        // Head tracking for point cloud (OVRCameraRig)
        private OVRCameraRig ovrCameraRig;

        private void Start()
        {
            FindComponents();
            InitializeCoverage();
            SubscribeToEvents();
            SetState(ScanState.Idle);

            // Try to add AudioSource if not assigned
            if (audioSource == null)
            {
                audioSource = GetComponent<AudioSource>();
                if (audioSource == null)
                {
                    audioSource = gameObject.AddComponent<AudioSource>();
                    audioSource.playOnAwake = false;
                    audioSource.spatialBlend = 0f; // 2D sound
                }
            }

            Debug.Log("[Meta3D-ScanManager] Initialized in MR mode");
        }

        private void FindComponents()
        {
            if (cameraCapture == null) cameraCapture = GetComponent<CameraCapture>();
            if (cameraCapture == null) cameraCapture = FindObjectOfType<CameraCapture>();

            if (depthCapture == null) depthCapture = GetComponent<DepthCapture>();
            if (depthCapture == null) depthCapture = FindObjectOfType<DepthCapture>();

            if (dataStreamer == null) dataStreamer = GetComponent<DataStreamer>();
            if (dataStreamer == null) dataStreamer = FindObjectOfType<DataStreamer>();

            if (controllerManager == null) controllerManager = FindObjectOfType<ControllerManager>();
            if (handUIManager == null) handUIManager = FindObjectOfType<HandUIManager>();
            if (scanPointer == null) scanPointer = FindObjectOfType<ScanPointer>();
            if (pointCloudVisualizer == null) pointCloudVisualizer = FindObjectOfType<PointCloudVisualizer>();

            ovrCameraRig = FindObjectOfType<OVRCameraRig>();
        }

        private void InitializeCoverage()
        {
            coverageAngles = new float[coverageSections];
        }

        private void SubscribeToEvents()
        {
            // DataStreamer events
            if (dataStreamer != null)
            {
                dataStreamer.OnConnected += OnServerConnected;
                dataStreamer.OnDisconnected += OnServerDisconnected;
                dataStreamer.OnSessionStarted += OnSessionStarted;
                dataStreamer.OnFrameFeedback += OnFrameFeedback;
                dataStreamer.OnError += OnServerError;
            }

            // Hand UI events
            if (handUIManager != null)
            {
                handUIManager.OnConnectRequested += OnConnectRequested;
                handUIManager.OnScanRequested += OnScanRequested;
                handUIManager.OnStopRequested += OnStopRequested;
            }

            // Controller events (Y button = toggle UI)
            if (controllerManager != null)
            {
                controllerManager.OnLeftSecondaryButtonDown += OnToggleUI;
            }
        }

        // =================================================================
        // State Management
        // =================================================================

        private void SetState(ScanState newState)
        {
            currentState = newState;
            UpdateUI();
        }

        private void UpdateUI()
        {
            if (handUIManager == null) return;

            switch (currentState)
            {
                case ScanState.Idle:
                    handUIManager.SetStatus("Hazır", Color.white);
                    handUIManager.SetInstruction("Sunucuya bağlanmak için Bağlan'a basın");
                    handUIManager.SetConnectButtonEnabled(true);
                    handUIManager.SetScanButtonsState(false, false);
                    handUIManager.SetConnectionStatus(false);
                    if (scanPointer != null) scanPointer.SetRayActive(false);
                    break;

                case ScanState.Connecting:
                    handUIManager.SetStatus("Bağlanıyor...", warningColor);
                    handUIManager.SetInstruction("Sunucuya bağlanılıyor...");
                    handUIManager.SetConnectButtonEnabled(false);
                    break;

                case ScanState.Connected:
                    handUIManager.SetStatus("Bağlı", goodQualityColor);
                    handUIManager.SetInstruction("Taramaya başlamak için ▶ Tara'ya basın");
                    handUIManager.SetConnectionStatus(true);
                    handUIManager.SetScanButtonsState(true, false);
                    if (scanPointer != null) scanPointer.SetRayActive(true);
                    break;

                case ScanState.Scanning:
                    handUIManager.SetStatus("Taranıyor...", goodQualityColor);
                    handUIManager.SetInstruction("Nesneyi sağ kontrol ile tarayın.\nYavaş ve düzgün hareket edin.");
                    handUIManager.SetScanButtonsState(false, true);
                    if (scanPointer != null)
                    {
                        scanPointer.SetRayActive(true);
                        scanPointer.StartScanning();
                    }
                    break;

                case ScanState.Processing:
                    handUIManager.SetStatus("İşleniyor...", warningColor);
                    handUIManager.SetInstruction("3D model oluşturuluyor, lütfen bekleyin...");
                    handUIManager.SetScanButtonsState(false, false);
                    if (scanPointer != null)
                    {
                        scanPointer.StopScanning();
                        scanPointer.SetRayActive(false);
                    }
                    break;

                case ScanState.Complete:
                    handUIManager.SetStatus("Tamamlandı!", goodQualityColor);
                    handUIManager.SetInstruction("3D model başarıyla oluşturuldu!");
                    handUIManager.SetScanButtonsState(true, false);
                    PlaySound(completeSound);
                    break;

                case ScanState.Error:
                    handUIManager.SetStatus("Hata!", badQualityColor);
                    handUIManager.SetConnectButtonEnabled(true);
                    handUIManager.SetScanButtonsState(false, false);
                    break;
            }
        }

        // =================================================================
        // UI Event Handlers (from HandUIManager)
        // =================================================================

        private void OnConnectRequested(string ip, int port)
        {
            SetState(ScanState.Connecting);
            dataStreamer.SetServerInfo(ip, port);
            dataStreamer.Connect();
        }

        private void OnScanRequested()
        {
            SetState(ScanState.Scanning);
            scanStartTime = Time.time;
            qualityIssueCount = 0;
            coverageAngles = new float[coverageSections];

            // Clear previous point cloud
            if (pointCloudVisualizer != null)
            {
                pointCloudVisualizer.ClearPoints();
            }

            dataStreamer.StartSession("scan");
        }

        private void OnStopRequested()
        {
            if (scanPointer != null) scanPointer.StopScanning();
            dataStreamer.StopSession();

            if (dataStreamer.FramesSent >= minFramesRequired)
            {
                SetState(ScanState.Processing);
                if (handUIManager != null)
                {
                    handUIManager.SetInstruction(
                        $"{dataStreamer.FramesSent} frame kaydedildi.\nPC'de rekonstrüksiyon başlatılıyor...");
                }
            }
            else
            {
                if (handUIManager != null)
                {
                    handUIManager.SetInstruction(
                        $"Yetersiz frame ({dataStreamer.FramesSent}/{minFramesRequired}).\nDaha fazla tarama yapın.");
                }
                SetState(ScanState.Connected);
            }
        }

        private void OnToggleUI()
        {
            if (handUIManager != null)
            {
                handUIManager.ToggleVisible();
            }
        }

        // =================================================================
        // Server Event Handlers
        // =================================================================

        private void OnServerConnected(string url)
        {
            SetState(ScanState.Connected);
            Debug.Log($"[Meta3D-ScanManager] Connected to: {url}");
        }

        private void OnServerDisconnected(string reason)
        {
            SetState(ScanState.Idle);
            if (scanPointer != null) scanPointer.StopScanning();
        }

        private void OnSessionStarted(string sessionId)
        {
            Debug.Log($"[Meta3D-ScanManager] Session: {sessionId}");
        }

        private void OnFrameFeedback(DataStreamer.FrameFeedback feedback)
        {
            if (handUIManager == null) return;

            // Update frame counter
            handUIManager.SetFrameCount(feedback.total_frames, targetFrames);

            // Update progress
            handUIManager.SetProgress(Mathf.Clamp01((float)feedback.total_frames / targetFrames));

            // Update quality indicator
            if (feedback.quality_ok)
            {
                handUIManager.SetQuality("Kalite: İyi", goodQualityColor);
            }
            else
            {
                string issues = string.Join(", ", feedback.quality_issues ?? new string[0]);
                handUIManager.SetQuality($"Kalite: {issues}", badQualityColor);
                qualityIssueCount++;
                PlaySound(warningSound);
            }

            // Add points to point cloud based on camera position
            AddPointsFromFrame(feedback.quality_ok);

            // Update coverage
            UpdateCoverage();

            // Check if enough frames
            if (feedback.total_frames >= targetFrames)
            {
                handUIManager.SetInstruction("Yeterli frame toplandı! Durdurmak için ■ Dur'a basın.");
                PlaySound(captureSound);
            }
        }

        private void OnServerError(string error)
        {
            Debug.LogError($"[Meta3D-ScanManager] Error: {error}");
            if (handUIManager != null)
            {
                handUIManager.SetInstruction($"Hata: {error}");
            }
            SetState(ScanState.Error);
        }

        // =================================================================
        // Point Cloud from Frame
        // =================================================================

        private void AddPointsFromFrame(bool qualityOk)
        {
            if (pointCloudVisualizer == null) return;

            // Get camera transform from OVRCameraRig
            Transform camTransform = null;
            if (ovrCameraRig != null && ovrCameraRig.centerEyeAnchor != null)
            {
                camTransform = ovrCameraRig.centerEyeAnchor;
            }
            else
            {
                Camera mainCam = Camera.main;
                if (mainCam != null) camTransform = mainCam.transform;
            }

            if (camTransform == null) return;

            // Estimate surface distance (use scan pointer hit if available or default)
            float hitDistance = 0f;
            if (scanPointer != null && scanPointer.HasHit)
            {
                hitDistance = scanPointer.GetHitDistance();
            }

            pointCloudVisualizer.AddFramePoints(
                camTransform.position,
                camTransform.forward,
                camTransform.right,
                camTransform.up,
                qualityOk,
                hitDistance
            );
        }

        // =================================================================
        // Coverage Tracking
        // =================================================================

        private void UpdateCoverage()
        {
            Transform camTransform = null;
            if (ovrCameraRig != null && ovrCameraRig.centerEyeAnchor != null)
            {
                camTransform = ovrCameraRig.centerEyeAnchor;
            }
            else
            {
                Camera cam = Camera.main;
                if (cam != null) camTransform = cam.transform;
            }

            if (camTransform == null) return;

            Vector3 forward = camTransform.forward;
            float angle = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360f;

            int section = Mathf.FloorToInt(angle / (360f / coverageSections));
            section = Mathf.Clamp(section, 0, coverageSections - 1);
            coverageAngles[section] = 1f;
        }

        /// <summary>
        /// Get coverage percentage (0-100).
        /// </summary>
        public float GetCoveragePercent()
        {
            if (coverageAngles == null) return 0;
            float covered = 0;
            foreach (float v in coverageAngles)
            {
                if (v > 0.5f) covered++;
            }
            return (covered / coverageSections) * 100f;
        }

        // =================================================================
        // Audio
        // =================================================================

        private void PlaySound(AudioClip clip)
        {
            if (audioSource != null && clip != null)
            {
                audioSource.PlayOneShot(clip);
            }
        }

        // =================================================================
        // Update Loop
        // =================================================================

        private void Update()
        {
            if (currentState == ScanState.Scanning)
            {
                // Check timeout
                if (Time.time - scanStartTime > scanTimeout)
                {
                    if (handUIManager != null)
                    {
                        handUIManager.SetInstruction("Tarama zaman aşımına uğradı.");
                    }
                    OnStopRequested();
                }
            }
        }

        private void OnDestroy()
        {
            // Unsubscribe from events
            if (dataStreamer != null)
            {
                dataStreamer.OnConnected -= OnServerConnected;
                dataStreamer.OnDisconnected -= OnServerDisconnected;
                dataStreamer.OnSessionStarted -= OnSessionStarted;
                dataStreamer.OnFrameFeedback -= OnFrameFeedback;
                dataStreamer.OnError -= OnServerError;
            }

            if (handUIManager != null)
            {
                handUIManager.OnConnectRequested -= OnConnectRequested;
                handUIManager.OnScanRequested -= OnScanRequested;
                handUIManager.OnStopRequested -= OnStopRequested;
            }

            if (controllerManager != null)
            {
                controllerManager.OnLeftSecondaryButtonDown -= OnToggleUI;
            }
        }
    }
}
