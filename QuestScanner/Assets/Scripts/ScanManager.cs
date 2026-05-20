/*
 * MetaScan — Scan Manager
 * Main orchestrator — coordinates all scanning components via state machine.
 * States: Idle → Connecting → Connected → Selecting → Scanning → Processing → Complete
 */

using System;
using UnityEngine;

namespace MetaScan
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
        [SerializeField] private ObjectSelector objectSelector;
        [SerializeField] private PointCloudVisualizer pointCloudVisualizer;

        [Header("Scan Settings")]
        [SerializeField] private int minFramesRequired = 30;
        [SerializeField] private int targetFrames = 200;
        [SerializeField] private float scanTimeout = 300f;

        [Header("Colors")]
        [SerializeField] private Color goodColor = new Color(0.2f, 0.9f, 0.4f);
        [SerializeField] private Color badColor = new Color(1.0f, 0.3f, 0.3f);
        [SerializeField] private Color warnColor = new Color(1.0f, 0.8f, 0.2f);
        [SerializeField] private Color selectColor = new Color(1.0f, 0.6f, 0.0f);

        public enum ScanState
        {
            Idle, Connecting, Connected, Selecting, Scanning, Processing, Complete, Error
        }

        public ScanState CurrentState { get; private set; } = ScanState.Idle;
        private float scanStartTime;
        private Transform headTransform;

        private void Start()
        {
            FindComponents();
            SubscribeToEvents();
            SetState(ScanState.Idle);
            Debug.Log("[MetaScan-Manager] Initialized");
        }

        private void FindComponents()
        {
            if (cameraCapture == null) cameraCapture = FindFirstObjectByType<CameraCapture>();
            if (depthCapture == null) depthCapture = FindFirstObjectByType<DepthCapture>();
            if (dataStreamer == null) dataStreamer = FindFirstObjectByType<DataStreamer>();
            if (controllerManager == null) controllerManager = FindFirstObjectByType<ControllerManager>();
            if (handUIManager == null) handUIManager = FindFirstObjectByType<HandUIManager>();
            if (scanPointer == null) scanPointer = FindFirstObjectByType<ScanPointer>();
            if (objectSelector == null) objectSelector = FindFirstObjectByType<ObjectSelector>();
            if (pointCloudVisualizer == null) pointCloudVisualizer = FindFirstObjectByType<PointCloudVisualizer>();

            // Find head transform
#if META_XR_SDK
            OVRCameraRig rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null && rig.centerEyeAnchor != null)
                headTransform = rig.centerEyeAnchor;
            else
#endif
            {
                Camera cam = Camera.main;
                if (cam != null) headTransform = cam.transform;
            }
        }

        private void SubscribeToEvents()
        {
            if (dataStreamer != null)
            {
                dataStreamer.OnConnected += OnServerConnected;
                dataStreamer.OnDisconnected += OnServerDisconnected;
                dataStreamer.OnSessionStarted += OnSessionStarted;
                dataStreamer.OnFrameFeedback += OnFrameFeedback;
                dataStreamer.OnError += OnServerError;
            }

            if (handUIManager != null)
            {
                handUIManager.OnConnectRequested += OnConnectRequested;
                handUIManager.OnSelectObjectRequested += OnSelectObjectRequested;
                handUIManager.OnScanRequested += OnScanRequested;
                handUIManager.OnStopRequested += OnStopRequested;
            }

            if (objectSelector != null)
            {
                objectSelector.OnObjectSelected += OnObjectSelected;
            }
        }

        // ================== State Machine ==================

        private void SetState(ScanState newState)
        {
            CurrentState = newState;
            UpdateUI();
            Debug.Log($"[MetaScan-Manager] State → {newState}");
        }

        private void UpdateUI()
        {
            if (handUIManager == null) return;

            switch (CurrentState)
            {
                case ScanState.Idle:
                    handUIManager.SetStatus("Hazir", Color.white);
                    handUIManager.SetInstruction("Sunucuya baglanmak icin Baglan'a basin");
                    handUIManager.SetConnectButtonEnabled(true);
                    handUIManager.SetSelectButtonEnabled(false);
                    handUIManager.SetScanButtonsState(false, false);
                    handUIManager.SetConnectionStatus(false);
                    if (scanPointer != null) scanPointer.SetRayActive(false);
                    break;

                case ScanState.Connecting:
                    handUIManager.SetStatus("Baglaniyor...", warnColor);
                    handUIManager.SetInstruction("Sunucuya baglaniyor...");
                    handUIManager.SetConnectButtonEnabled(false);
                    break;

                case ScanState.Connected:
                    handUIManager.SetStatus("Bagli", goodColor);
                    handUIManager.SetInstruction("Cisim Sec ile taranacak nesneyi belirleyin");
                    handUIManager.SetConnectionStatus(true);
                    handUIManager.SetSelectButtonEnabled(true);
                    handUIManager.SetScanButtonsState(false, false);
                    handUIManager.SetStopButtonText("[] Dur");
                    if (scanPointer != null)
                    {
                        scanPointer.SetRayActive(true);
                        scanPointer.SetSelectMode(false);
                    }
                    break;

                case ScanState.Selecting:
                    handUIManager.SetStatus("Cisim Secimi", selectColor);
                    handUIManager.SetInstruction("Sag tetik ile nesneye isaret edip basili tutun, surukleyerek alani belirleyin");
                    handUIManager.SetSelectButtonEnabled(true); // Yeniden secime izin ver
                    handUIManager.SetScanButtonsState(false, true); // Dur (Iptal) butonunu aktif et
                    handUIManager.SetStopButtonText("Iptal");
                    if (scanPointer != null)
                    {
                        scanPointer.SetRayActive(true);
                        scanPointer.SetSelectMode(true);
                    }
                    break;

                case ScanState.Scanning:
                    handUIManager.SetStatus("Taraniyor...", goodColor);
                    handUIManager.SetInstruction("Nesneyi sag kontrol ile tarayin. Etrafinda dolasin.");
                    handUIManager.SetSelectButtonEnabled(false);
                    handUIManager.SetScanButtonsState(false, true);
                    handUIManager.SetStopButtonText("[] Dur");
                    if (scanPointer != null)
                    {
                        scanPointer.SetRayActive(true);
                        scanPointer.StartScanning();
                    }
                    break;

                case ScanState.Processing:
                    handUIManager.SetStatus("Isleniyor...", warnColor);
                    handUIManager.SetInstruction("3D model olusturuluyor...");
                    handUIManager.SetSelectButtonEnabled(false);
                    handUIManager.SetScanButtonsState(false, false);
                    if (scanPointer != null)
                    {
                        scanPointer.StopScanning();
                        scanPointer.SetRayActive(false);
                    }
                    break;

                case ScanState.Complete:
                    handUIManager.SetStatus("Tamamlandi!", goodColor);
                    handUIManager.SetInstruction("3D model olusturuldu! Web viewer'da goruntuleyebilirsiniz.");
                    handUIManager.SetSelectButtonEnabled(true);
                    handUIManager.SetScanButtonsState(false, false);
                    break;

                case ScanState.Error:
                    handUIManager.SetStatus("Hata!", badColor);
                    handUIManager.SetConnectButtonEnabled(true);
                    handUIManager.SetSelectButtonEnabled(false);
                    handUIManager.SetScanButtonsState(false, false);
                    break;
            }
        }

        // ================== UI Callbacks ==================

        private void OnConnectRequested(string ip, int port)
        {
            SetState(ScanState.Connecting);
            dataStreamer.SetServerInfo(ip, port);
            dataStreamer.Connect();
        }

        private void OnSelectObjectRequested()
        {
            SetState(ScanState.Selecting);
            if (objectSelector != null)
                objectSelector.StartSelecting();
            if (pointCloudVisualizer != null)
                pointCloudVisualizer.ClearPoints();
        }

        private void OnObjectSelected(Vector3 center, float radius)
        {
            // Object selected — enable scan button
            if (handUIManager != null)
            {
                handUIManager.SetStatus("Cisim Secildi", goodColor);
                handUIManager.SetInstruction(
                    $"Alan secildi (r={radius:F2}m). Taramayi Baslat'a basin.");
                handUIManager.SetSelectButtonEnabled(true); // Yeniden secime izin ver
                handUIManager.SetScanButtonsState(true, true); // Taramayi baslat aktif, iptal aktif
                handUIManager.SetStopButtonText("Iptal");
            }

            // Stop selection mode on pointer
            if (scanPointer != null) scanPointer.SetSelectMode(false);
            if (objectSelector != null) objectSelector.StopSelecting();

            // Haptic feedback
            if (controllerManager != null)
                controllerManager.SendHaptic(false, 0.5f, 0.7f, 0.2f);
        }

        private void OnScanRequested()
        {
            SetState(ScanState.Scanning);
            scanStartTime = Time.time;

            // Start camera capture
            if (cameraCapture != null) cameraCapture.StartCapturing();

            // Start streaming session
            dataStreamer.StartSession("scan");
        }

        private void OnStopRequested()
        {
            if (CurrentState == ScanState.Selecting)
            {
                if (scanPointer != null) scanPointer.SetSelectMode(false);
                if (objectSelector != null)
                {
                    objectSelector.StopSelecting();
                    objectSelector.ClearSelection();
                }
                SetState(ScanState.Connected);
                return;
            }

            // Stop scanning
            if (scanPointer != null) scanPointer.StopScanning();
            if (cameraCapture != null) cameraCapture.StopCapturing();
            dataStreamer.StopSession();

            if (dataStreamer.FramesSent >= minFramesRequired)
            {
                SetState(ScanState.Processing);
                if (handUIManager != null)
                    handUIManager.SetInstruction(
                        dataStreamer.FramesSent + " frame kaydedildi. 3D model olusturuluyor...");
            }
            else
            {
                if (handUIManager != null)
                    handUIManager.SetInstruction(
                        "Yetersiz frame (" + dataStreamer.FramesSent + "/" + minFramesRequired + ").");
                SetState(ScanState.Connected);
            }
        }

        // ================== Server Events ==================

        private void OnServerConnected(string url)
        {
            SetState(ScanState.Connected);
        }

        private void OnServerDisconnected(string reason)
        {
            SetState(ScanState.Idle);
            if (scanPointer != null) scanPointer.StopScanning();
            if (cameraCapture != null) cameraCapture.StopCapturing();
        }

        private void OnSessionStarted(string id)
        {
            Debug.Log("[MetaScan-Manager] Session: " + id);
        }

        private void OnFrameFeedback(DataStreamer.FrameFeedback fb)
        {
            if (handUIManager == null) return;

            handUIManager.SetFrameCount(fb.total_frames, targetFrames);
            handUIManager.SetProgress(Mathf.Clamp01((float)fb.total_frames / targetFrames));

            if (fb.quality_ok)
            {
                handUIManager.SetQuality("Kalite: Iyi", goodColor);
            }
            else
            {
                string issues = fb.quality_issues != null && fb.quality_issues.Length > 0
                    ? string.Join(", ", fb.quality_issues)
                    : "unknown";
                handUIManager.SetQuality("Kalite: " + issues, badColor);
            }

            // Add visualization points
            AddPointsFromFrame(fb.quality_ok);

            if (fb.total_frames >= targetFrames)
            {
                handUIManager.SetInstruction("Yeterli frame toplandi! Dur'a basin.");
                // Haptic notification
                if (controllerManager != null)
                    controllerManager.SendHaptic(true, 0.3f, 0.5f, 0.3f);
            }
        }

        private void OnServerError(string err)
        {
            Debug.LogError("[MetaScan-Manager] Error: " + err);
            if (handUIManager != null)
                handUIManager.SetInstruction("Hata: " + err);
            SetState(ScanState.Error);
        }

        // ================== Point Cloud ==================

        private void AddPointsFromFrame(bool qualityOk)
        {
            if (pointCloudVisualizer == null || headTransform == null) return;

            float hitDist = 0f;
            if (scanPointer != null && scanPointer.HasHit)
                hitDist = scanPointer.GetHitDistance();

            pointCloudVisualizer.AddFramePoints(
                headTransform.position, headTransform.forward,
                headTransform.right, headTransform.up,
                qualityOk, hitDist);
        }

        // ================== Update ==================

        private void Update()
        {
            if (CurrentState == ScanState.Scanning
                && Time.time - scanStartTime > scanTimeout)
            {
                if (handUIManager != null)
                    handUIManager.SetInstruction("Tarama zaman asimina ugradi.");
                OnStopRequested();
            }
        }

        private void OnDestroy()
        {
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
                handUIManager.OnSelectObjectRequested -= OnSelectObjectRequested;
                handUIManager.OnScanRequested -= OnScanRequested;
                handUIManager.OnStopRequested -= OnStopRequested;
            }
            if (objectSelector != null)
            {
                objectSelector.OnObjectSelected -= OnObjectSelected;
            }
        }
    }
}
