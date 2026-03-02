/*
 * Meta3D Scanner - Scan Manager
 * Main orchestrator for the scanning process.
 * Manages the scanning workflow and provides UI callbacks.
 */

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Meta3DScanner
{
    public class ScanManager : MonoBehaviour
    {
        [Header("Components")]
        [SerializeField] private CameraCapture cameraCapture;
        [SerializeField] private DepthCapture depthCapture;
        [SerializeField] private DataStreamer dataStreamer;

        [Header("Server Configuration")]
        [SerializeField] private string serverIP = "192.168.1.100";
        [SerializeField] private int serverPort = 8765;

        [Header("UI References")]
        [SerializeField] private Canvas scanCanvas;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private TextMeshProUGUI frameCountText;
        [SerializeField] private TextMeshProUGUI qualityText;
        [SerializeField] private TextMeshProUGUI instructionText;
        [SerializeField] private Image connectionIndicator;
        [SerializeField] private Button connectButton;
        [SerializeField] private Button scanButton;
        [SerializeField] private Button stopButton;
        [SerializeField] private Slider progressBar;

        [Header("Scan Settings")]
        [SerializeField] private int minFramesRequired = 30;
        [SerializeField] private int targetFrames = 200;
        [SerializeField] private float scanTimeout = 300f; // 5 minutes max

        [Header("Audio Feedback")]
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private AudioClip captureSound;
        [SerializeField] private AudioClip warningSound;
        [SerializeField] private AudioClip completeSound;

        [Header("Visual Guidance")]
        [SerializeField] private GameObject guidanceArrow;
        [SerializeField] private LineRenderer pathRenderer;
        [SerializeField] private Color goodQualityColor = Color.green;
        [SerializeField] private Color badQualityColor = Color.red;
        [SerializeField] private Color warningColor = Color.yellow;

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
        private float[] coverageAngles; // Track which angles have been captured
        private int coverageSections = 36; // 10 degree sections around the object

        private void Start()
        {
            // Auto-find components if not assigned
            if (cameraCapture == null) cameraCapture = GetComponent<CameraCapture>();
            if (depthCapture == null) depthCapture = GetComponent<DepthCapture>();
            if (dataStreamer == null) dataStreamer = GetComponent<DataStreamer>();

            // Initialize coverage tracking
            coverageAngles = new float[coverageSections];

            // Subscribe to events
            if (dataStreamer != null)
            {
                dataStreamer.OnConnected += OnServerConnected;
                dataStreamer.OnDisconnected += OnServerDisconnected;
                dataStreamer.OnSessionStarted += OnSessionStarted;
                dataStreamer.OnFrameFeedback += OnFrameFeedback;
                dataStreamer.OnError += OnServerError;
            }

            // Setup UI
            SetupUI();
            SetState(ScanState.Idle);
        }

        private void SetupUI()
        {
            if (connectButton != null)
                connectButton.onClick.AddListener(OnConnectPressed);
            if (scanButton != null)
                scanButton.onClick.AddListener(OnScanPressed);
            if (stopButton != null)
                stopButton.onClick.AddListener(OnStopPressed);

            // Default UI state
            if (scanButton != null) scanButton.interactable = false;
            if (stopButton != null) stopButton.interactable = false;
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
            if (statusText == null) return;

            switch (currentState)
            {
                case ScanState.Idle:
                    statusText.text = "Hazir";
                    statusText.color = Color.white;
                    instructionText.SetText("Sunucuya bağlanmak için Connect'e basın");
                    if (connectButton != null) connectButton.interactable = true;
                    if (scanButton != null) scanButton.interactable = false;
                    if (stopButton != null) stopButton.interactable = false;
                    break;

                case ScanState.Connecting:
                    statusText.text = "Bağlanıyor...";
                    statusText.color = warningColor;
                    instructionText.SetText($"Sunucuya bağlanılıyor: {serverIP}:{serverPort}");
                    if (connectButton != null) connectButton.interactable = false;
                    break;

                case ScanState.Connected:
                    statusText.text = "Bağlı";
                    statusText.color = goodQualityColor;
                    instructionText.SetText("Taramaya başlamak için Scan'e basın");
                    if (connectionIndicator != null) connectionIndicator.color = goodQualityColor;
                    if (scanButton != null) scanButton.interactable = true;
                    break;

                case ScanState.Scanning:
                    statusText.text = "Taranıyor...";
                    statusText.color = goodQualityColor;
                    instructionText.SetText("Nesne etrafında yavaşça dönün.\nDüzgün ve sabit hareket edin.");
                    if (scanButton != null) scanButton.interactable = false;
                    if (stopButton != null) stopButton.interactable = true;
                    break;

                case ScanState.Processing:
                    statusText.text = "İşleniyor...";
                    statusText.color = warningColor;
                    instructionText.SetText("3D model oluşturuluyor, lütfen bekleyin...");
                    if (stopButton != null) stopButton.interactable = false;
                    break;

                case ScanState.Complete:
                    statusText.text = "Tamamlandı!";
                    statusText.color = goodQualityColor;
                    instructionText.SetText("3D model başarıyla oluşturuldu!");
                    if (scanButton != null) scanButton.interactable = true;
                    PlaySound(completeSound);
                    break;

                case ScanState.Error:
                    statusText.text = "Hata!";
                    statusText.color = badQualityColor;
                    if (connectButton != null) connectButton.interactable = true;
                    break;
            }
        }

        // =================================================================
        // Button Handlers
        // =================================================================

        private void OnConnectPressed()
        {
            SetState(ScanState.Connecting);
            dataStreamer.SetServerInfo(serverIP, serverPort);
            dataStreamer.Connect();
        }

        private void OnScanPressed()
        {
            SetState(ScanState.Scanning);
            scanStartTime = Time.time;
            qualityIssueCount = 0;
            coverageAngles = new float[coverageSections];
            dataStreamer.StartSession("scan");
        }

        private void OnStopPressed()
        {
            dataStreamer.StopSession();
            if (dataStreamer.FramesSent >= minFramesRequired)
            {
                SetState(ScanState.Processing);
                instructionText.SetText($"{dataStreamer.FramesSent} frame kaydedildi.\nPC'de rekonstrüksiyon başlatılıyor...");
            }
            else
            {
                instructionText.SetText($"Yetersiz frame ({dataStreamer.FramesSent}/{minFramesRequired}).\nDaha fazla tarama yapın.");
                SetState(ScanState.Connected);
            }
        }

        // =================================================================
        // Event Handlers
        // =================================================================

        private void OnServerConnected(string url)
        {
            SetState(ScanState.Connected);
            Debug.Log($"[Meta3D] Connected to: {url}");
        }

        private void OnServerDisconnected(string reason)
        {
            SetState(ScanState.Idle);
            if (connectionIndicator != null) connectionIndicator.color = badQualityColor;
        }

        private void OnSessionStarted(string sessionId)
        {
            Debug.Log($"[Meta3D] Session: {sessionId}");
        }

        private void OnFrameFeedback(DataStreamer.FrameFeedback feedback)
        {
            // Update frame counter
            if (frameCountText != null)
            {
                frameCountText.text = $"{feedback.total_frames} / {targetFrames}";
            }

            // Update progress
            if (progressBar != null)
            {
                progressBar.value = Mathf.Clamp01((float)feedback.total_frames / targetFrames);
            }

            // Update quality indicator
            if (qualityText != null)
            {
                if (feedback.quality_ok)
                {
                    qualityText.text = "Kalite: İyi";
                    qualityText.color = goodQualityColor;
                }
                else
                {
                    string issues = string.Join(", ", feedback.quality_issues ?? new string[0]);
                    qualityText.text = $"Kalite: {issues}";
                    qualityText.color = badQualityColor;
                    qualityIssueCount++;
                    PlaySound(warningSound);
                }
            }

            // Update coverage
            UpdateCoverage();

            // Check if we have enough frames
            if (feedback.total_frames >= targetFrames)
            {
                instructionText.SetText("Yeterli frame toplandı! Durdurmak için Stop'a basın.");
                PlaySound(captureSound);
            }
        }

        private void OnServerError(string error)
        {
            Debug.LogError($"[Meta3D] Error: {error}");
            instructionText.SetText($"Hata: {error}");
            SetState(ScanState.Error);
        }

        // =================================================================
        // Coverage Tracking
        // =================================================================

        private void UpdateCoverage()
        {
            // Calculate the angle of the camera relative to the initial position
            // This helps guide the user to capture all angles
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 forward = cam.transform.forward;
            float angle = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360f;

            int section = Mathf.FloorToInt(angle / (360f / coverageSections));
            section = Mathf.Clamp(section, 0, coverageSections - 1);
            coverageAngles[section] = 1f;

            // Update guidance arrow to point to uncovered sections
            UpdateGuidanceArrow();
        }

        private void UpdateGuidanceArrow()
        {
            if (guidanceArrow == null) return;

            // Find the nearest uncovered section
            float currentAngle = Camera.main.transform.eulerAngles.y;
            int currentSection = Mathf.FloorToInt(currentAngle / (360f / coverageSections));

            float nearestAngle = -1;
            float minDistance = float.MaxValue;

            for (int i = 0; i < coverageSections; i++)
            {
                if (coverageAngles[i] < 0.5f) // Not well covered
                {
                    float sectionAngle = i * (360f / coverageSections);
                    float distance = Mathf.Abs(Mathf.DeltaAngle(currentAngle, sectionAngle));
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        nearestAngle = sectionAngle;
                    }
                }
            }

            if (nearestAngle >= 0)
            {
                guidanceArrow.SetActive(true);
                guidanceArrow.transform.localRotation = Quaternion.Euler(0, 0, -(nearestAngle - currentAngle));
            }
            else
            {
                guidanceArrow.SetActive(false); // All sections covered!
            }
        }

        /// <summary>
        /// Get coverage percentage (0-100)
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
                    instructionText.SetText("Tarama zaman aşımına uğradı.");
                    OnStopPressed();
                }

                // Update path visualization
                UpdatePathVisualization();
            }
        }

        private void UpdatePathVisualization()
        {
            if (pathRenderer == null) return;

            // Add current position to path
            Camera cam = Camera.main;
            if (cam != null)
            {
                int posCount = pathRenderer.positionCount;
                pathRenderer.positionCount = posCount + 1;
                pathRenderer.SetPosition(posCount, cam.transform.position);
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
        }
    }
}
