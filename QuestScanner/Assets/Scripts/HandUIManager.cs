/*
 * Meta3D Scanner - Hand UI Manager
 * Creates and manages a World Space Canvas attached to the left controller.
 * Contains server connection UI, scan controls, and status display.
 * 
 * Hierarchy: OVRCameraRig > TrackingSpace > LeftHandAnchor > UI Canvas
 */

using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

namespace Meta3DScanner
{
    public class HandUIManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ControllerManager controllerManager;

        [Header("UI Settings")]
        [SerializeField] private float canvasWidth = 0.3f;   // 30cm wide
        [SerializeField] private float canvasHeight = 0.22f;  // 22cm tall
        [SerializeField] private float canvasScale = 0.001f;  // World scale multiplier
        [SerializeField] private Vector3 canvasOffset = new Vector3(0.05f, 0.1f, -0.08f);
        [SerializeField] private Vector3 canvasRotation = new Vector3(30f, 0f, 0f);

        [Header("Colors")]
        [SerializeField] private Color panelColor = new Color(0.05f, 0.05f, 0.12f, 0.92f);
        [SerializeField] private Color accentColor = new Color(0.0f, 0.75f, 1.0f, 1.0f);
        [SerializeField] private Color successColor = new Color(0.2f, 0.9f, 0.4f, 1.0f);
        [SerializeField] private Color warningColor = new Color(1.0f, 0.8f, 0.2f, 1.0f);
        [SerializeField] private Color errorColor = new Color(1.0f, 0.3f, 0.3f, 1.0f);

        // UI elements (created at runtime)
        private Canvas uiCanvas;
        private GameObject canvasObject;

        // Connection section
        private TMP_InputField ipInputField;
        private TMP_InputField portInputField;
        private Button connectButton;
        private TextMeshProUGUI connectButtonText;
        private Image connectionIndicator;

        // Scan section
        private Button scanButton;
        private Button stopButton;
        private TextMeshProUGUI scanButtonText;
        private TextMeshProUGUI stopButtonText;

        // Status section
        private TextMeshProUGUI statusText;
        private TextMeshProUGUI frameCountText;
        private TextMeshProUGUI qualityText;
        private TextMeshProUGUI instructionText;
        private Slider progressBar;

        // Event System
        private EventSystem eventSystem;

        // Events
        public event Action<string, int> OnConnectRequested;
        public event Action OnScanRequested;
        public event Action OnStopRequested;

        // State
        private bool isUIVisible = true;

        // Public accessors for UI elements
        public TextMeshProUGUI StatusText => statusText;
        public TextMeshProUGUI FrameCountText => frameCountText;
        public TextMeshProUGUI QualityText => qualityText;
        public TextMeshProUGUI InstructionText => instructionText;
        public Slider ProgressBar => progressBar;
        public Image ConnectionIndicator => connectionIndicator;
        public Button ScanButton => scanButton;
        public Button StopButton => stopButton;
        public Button ConnectButton => connectButton;
        public Canvas UICanvas => uiCanvas;

        private void Start()
        {
            if (controllerManager == null)
            {
                controllerManager = FindObjectOfType<ControllerManager>();
            }

            CreateUI();
            SetupEventSystem();
        }

        /// <summary>
        /// Create the entire UI hierarchy at runtime.
        /// </summary>
        private void CreateUI()
        {
            Transform leftAnchor = null;

            // Find left hand anchor
            if (controllerManager != null && controllerManager.LeftHandAnchor != null)
            {
                leftAnchor = controllerManager.LeftHandAnchor;
            }
            else
            {
                OVRCameraRig cameraRig = FindObjectOfType<OVRCameraRig>();
                if (cameraRig != null)
                {
                    leftAnchor = cameraRig.leftHandAnchor;
                }
            }

            if (leftAnchor == null)
            {
                Debug.LogError("[Meta3D-UI] Left hand anchor not found! UI cannot be attached.");
                return;
            }

            // Create Canvas GameObject as child of left hand anchor
            canvasObject = new GameObject("ScannerUI_Canvas");
            canvasObject.transform.SetParent(leftAnchor, false);
            canvasObject.transform.localPosition = canvasOffset;
            canvasObject.transform.localRotation = Quaternion.Euler(canvasRotation);
            canvasObject.transform.localScale = Vector3.one * canvasScale;

            // Add Canvas component
            uiCanvas = canvasObject.AddComponent<Canvas>();
            uiCanvas.renderMode = RenderMode.WorldSpace;
            uiCanvas.sortingOrder = 100;

            // Set canvas size
            RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(canvasWidth / canvasScale, canvasHeight / canvasScale);

            // Add CanvasScaler
            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10;
            scaler.referencePixelsPerUnit = 100;

            // Add GraphicRaycaster for VR interaction
            GraphicRaycaster raycaster = canvasObject.AddComponent<GraphicRaycaster>();

            // Try to add OVRRaycaster if available (Meta SDK interaction)
            // This will work with VR controllers to interact with UI
            try
            {
                var ovrRaycaster = canvasObject.AddComponent<OVRRaycaster>();
                if (ovrRaycaster != null)
                {
                    // Prefer OVRRaycaster, disable standard raycaster
                    raycaster.enabled = false;
                }
            }
            catch
            {
                // OVRRaycaster not available, use standard GraphicRaycaster
                Debug.Log("[Meta3D-UI] OVRRaycaster not available, using standard raycaster");
            }

            // Build UI elements
            CreateBackground(canvasRect);
            CreateConnectionSection(canvasRect);
            CreateScanSection(canvasRect);
            CreateStatusSection(canvasRect);

            Debug.Log("[Meta3D-UI] Hand UI created and attached to left controller");
        }

        private void CreateBackground(RectTransform parent)
        {
            GameObject bgObj = new GameObject("Background");
            bgObj.transform.SetParent(parent, false);

            Image bg = bgObj.AddComponent<Image>();
            bg.color = panelColor;

            RectTransform bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            // Add rounded corners effect via another layer
            GameObject borderObj = new GameObject("Border");
            borderObj.transform.SetParent(bgObj.transform, false);

            Image border = borderObj.AddComponent<Image>();
            border.color = accentColor * 0.5f;

            RectTransform borderRect = borderObj.GetComponent<RectTransform>();
            borderRect.anchorMin = Vector2.zero;
            borderRect.anchorMax = Vector2.one;
            borderRect.offsetMin = new Vector2(-2, -2);
            borderRect.offsetMax = new Vector2(2, 2);

            // Move border behind background
            borderObj.transform.SetAsFirstSibling();
        }

        private void CreateConnectionSection(RectTransform parent)
        {
            float panelWidth = parent.sizeDelta.x;
            float panelHeight = parent.sizeDelta.y;
            float padding = 8f;

            // Title
            CreateLabel(parent, "Meta3D Scanner", 18, accentColor,
                new Vector2(padding, panelHeight - 30f),
                new Vector2(panelWidth - padding * 2, 24f),
                TextAlignmentOptions.Center);

            // IP Input Field
            CreateLabel(parent, "Sunucu IP:", 10, Color.gray,
                new Vector2(padding, panelHeight - 52f),
                new Vector2(60f, 16f),
                TextAlignmentOptions.Left);

            ipInputField = CreateInputField(parent, "192.168.1.100",
                new Vector2(70f, panelHeight - 54f),
                new Vector2(panelWidth - 70f - padding - 55f, 20f));

            // Port Input Field
            portInputField = CreateInputField(parent, "8765",
                new Vector2(panelWidth - padding - 50f, panelHeight - 54f),
                new Vector2(50f, 20f));

            // Connection Indicator
            GameObject indicatorObj = new GameObject("ConnectionIndicator");
            indicatorObj.transform.SetParent(parent, false);
            connectionIndicator = indicatorObj.AddComponent<Image>();
            connectionIndicator.color = errorColor;
            RectTransform indRect = indicatorObj.GetComponent<RectTransform>();
            indRect.anchorMin = new Vector2(0, 1);
            indRect.anchorMax = new Vector2(0, 1);
            indRect.pivot = new Vector2(0, 1);
            indRect.anchoredPosition = new Vector2(padding, -7f);
            indRect.sizeDelta = new Vector2(8f, 8f);

            // Connect Button
            connectButton = CreateButton(parent, "Bağlan", accentColor,
                new Vector2(padding, panelHeight - 82f),
                new Vector2(panelWidth - padding * 2, 24f));
            connectButtonText = connectButton.GetComponentInChildren<TextMeshProUGUI>();
            connectButton.onClick.AddListener(OnConnectButtonClicked);
        }

        private void CreateScanSection(RectTransform parent)
        {
            float panelWidth = parent.sizeDelta.x;
            float panelHeight = parent.sizeDelta.y;
            float padding = 8f;
            float yPos = panelHeight - 114f;
            float buttonWidth = (panelWidth - padding * 3) / 2f;

            // Scan Button
            scanButton = CreateButton(parent, "▶ Tara", successColor,
                new Vector2(padding, yPos),
                new Vector2(buttonWidth, 28f));
            scanButtonText = scanButton.GetComponentInChildren<TextMeshProUGUI>();
            scanButton.onClick.AddListener(OnScanButtonClicked);
            scanButton.interactable = false;

            // Stop Button
            stopButton = CreateButton(parent, "■ Dur", errorColor,
                new Vector2(padding * 2 + buttonWidth, yPos),
                new Vector2(buttonWidth, 28f));
            stopButtonText = stopButton.GetComponentInChildren<TextMeshProUGUI>();
            stopButton.onClick.AddListener(OnStopButtonClicked);
            stopButton.interactable = false;
        }

        private void CreateStatusSection(RectTransform parent)
        {
            float panelWidth = parent.sizeDelta.x;
            float panelHeight = parent.sizeDelta.y;
            float padding = 8f;
            float yStart = panelHeight - 152f;

            // Status Text
            statusText = CreateLabel(parent, "Hazır", 12, Color.white,
                new Vector2(padding, yStart),
                new Vector2(panelWidth - padding * 2, 16f),
                TextAlignmentOptions.Left);

            // Frame Count
            frameCountText = CreateLabel(parent, "Frame: 0 / 200", 10, Color.gray,
                new Vector2(padding, yStart - 18f),
                new Vector2(panelWidth / 2f, 14f),
                TextAlignmentOptions.Left);

            // Quality Text
            qualityText = CreateLabel(parent, "Kalite: —", 10, Color.gray,
                new Vector2(panelWidth / 2f, yStart - 18f),
                new Vector2(panelWidth / 2f - padding, 14f),
                TextAlignmentOptions.Right);

            // Instruction Text
            instructionText = CreateLabel(parent, "Sunucuya bağlanmak için Bağlan'a basın", 9, warningColor,
                new Vector2(padding, yStart - 36f),
                new Vector2(panelWidth - padding * 2, 24f),
                TextAlignmentOptions.Center);

            // Progress Bar
            progressBar = CreateProgressBar(parent,
                new Vector2(padding, yStart - 64f),
                new Vector2(panelWidth - padding * 2, 8f));
        }

        // =========================================================
        // UI Element Creators
        // =========================================================

        private TextMeshProUGUI CreateLabel(RectTransform parent, string text, float fontSize,
            Color color, Vector2 position, Vector2 size, TextAlignmentOptions alignment)
        {
            GameObject obj = new GameObject("Label_" + text);
            obj.transform.SetParent(parent, false);

            TextMeshProUGUI tmp = obj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = alignment;
            tmp.enableAutoSizing = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;

            RectTransform rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(position.x, -position.y + parent.sizeDelta.y);
            rect.sizeDelta = size;

            return tmp;
        }

        private TMP_InputField CreateInputField(RectTransform parent, string placeholder,
            Vector2 position, Vector2 size)
        {
            GameObject obj = new GameObject("InputField");
            obj.transform.SetParent(parent, false);

            Image bg = obj.AddComponent<Image>();
            bg.color = new Color(0.15f, 0.15f, 0.2f, 1f);

            RectTransform rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(position.x, -position.y + parent.sizeDelta.y);
            rect.sizeDelta = size;

            // Text area
            GameObject textArea = new GameObject("TextArea");
            textArea.transform.SetParent(obj.transform, false);
            RectTransform textAreaRect = textArea.AddComponent<RectTransform>();
            textAreaRect.anchorMin = Vector2.zero;
            textAreaRect.anchorMax = Vector2.one;
            textAreaRect.offsetMin = new Vector2(4, 2);
            textAreaRect.offsetMax = new Vector2(-4, -2);
            RectMask2D mask = textArea.AddComponent<RectMask2D>();

            // Input text
            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(textArea.transform, false);
            TextMeshProUGUI inputText = textObj.AddComponent<TextMeshProUGUI>();
            inputText.fontSize = 10;
            inputText.color = Color.white;
            inputText.alignment = TextAlignmentOptions.Left;
            inputText.richText = false;

            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            // Placeholder
            GameObject phObj = new GameObject("Placeholder");
            phObj.transform.SetParent(textArea.transform, false);
            TextMeshProUGUI phText = phObj.AddComponent<TextMeshProUGUI>();
            phText.text = placeholder;
            phText.fontSize = 10;
            phText.color = new Color(1f, 1f, 1f, 0.3f);
            phText.fontStyle = FontStyles.Italic;
            phText.alignment = TextAlignmentOptions.Left;

            RectTransform phRect = phObj.GetComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.offsetMin = Vector2.zero;
            phRect.offsetMax = Vector2.zero;

            // Add TMP_InputField
            TMP_InputField inputField = obj.AddComponent<TMP_InputField>();
            inputField.textViewport = textAreaRect;
            inputField.textComponent = inputText;
            inputField.placeholder = phText;
            inputField.text = placeholder;
            inputField.pointSize = 10;

            return inputField;
        }

        private Button CreateButton(RectTransform parent, string label, Color color,
            Vector2 position, Vector2 size)
        {
            GameObject obj = new GameObject("Button_" + label);
            obj.transform.SetParent(parent, false);

            Image bg = obj.AddComponent<Image>();
            bg.color = color;

            RectTransform rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(position.x, -position.y + parent.sizeDelta.y);
            rect.sizeDelta = size;

            Button btn = obj.AddComponent<Button>();
            ColorBlock colors = btn.colors;
            colors.normalColor = color;
            colors.highlightedColor = color * 1.2f;
            colors.pressedColor = color * 0.8f;
            colors.disabledColor = color * 0.4f;
            colors.fadeDuration = 0.1f;
            btn.colors = colors;

            // Button label text
            GameObject textObj = new GameObject("Label");
            textObj.transform.SetParent(obj.transform, false);
            TextMeshProUGUI tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 11;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontStyle = FontStyles.Bold;

            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            return btn;
        }

        private Slider CreateProgressBar(RectTransform parent, Vector2 position, Vector2 size)
        {
            GameObject obj = new GameObject("ProgressBar");
            obj.transform.SetParent(parent, false);

            RectTransform rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(position.x, -position.y + parent.sizeDelta.y);
            rect.sizeDelta = size;

            // Background
            GameObject bgObj = new GameObject("Background");
            bgObj.transform.SetParent(obj.transform, false);
            Image bgImage = bgObj.AddComponent<Image>();
            bgImage.color = new Color(0.2f, 0.2f, 0.3f, 1f);
            RectTransform bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            // Fill area
            GameObject fillArea = new GameObject("FillArea");
            fillArea.transform.SetParent(obj.transform, false);
            RectTransform fillAreaRect = fillArea.AddComponent<RectTransform>();
            fillAreaRect.anchorMin = Vector2.zero;
            fillAreaRect.anchorMax = Vector2.one;
            fillAreaRect.offsetMin = Vector2.zero;
            fillAreaRect.offsetMax = Vector2.zero;

            // Fill
            GameObject fillObj = new GameObject("Fill");
            fillObj.transform.SetParent(fillArea.transform, false);
            Image fillImage = fillObj.AddComponent<Image>();
            fillImage.color = accentColor;
            RectTransform fillRect = fillObj.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;

            // Slider component
            Slider slider = obj.AddComponent<Slider>();
            slider.fillRect = fillRect;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0f;
            slider.interactable = false; // Read-only progress bar

            return slider;
        }

        // =========================================================
        // Event System
        // =========================================================

        private void SetupEventSystem()
        {
            // Find or create EventSystem
            eventSystem = FindObjectOfType<EventSystem>();
            if (eventSystem == null)
            {
                GameObject esObj = new GameObject("EventSystem");
                eventSystem = esObj.AddComponent<EventSystem>();
            }

            // Remove StandaloneInputModule if present (not suitable for VR)
            StandaloneInputModule standaloneInput = eventSystem.GetComponent<StandaloneInputModule>();
            if (standaloneInput != null)
            {
                DestroyImmediate(standaloneInput);
            }

            // Try to add OVRInputModule for VR controller interaction
            try
            {
                var ovrInputModule = eventSystem.gameObject.GetComponent<OVRInputModule>();
                if (ovrInputModule == null)
                {
                    ovrInputModule = eventSystem.gameObject.AddComponent<OVRInputModule>();
                }
            }
            catch
            {
                // If OVRInputModule is not available, add back StandaloneInputModule as fallback
                if (eventSystem.GetComponent<BaseInputModule>() == null)
                {
                    eventSystem.gameObject.AddComponent<StandaloneInputModule>();
                }
                Debug.LogWarning("[Meta3D-UI] OVRInputModule not available, using fallback input module");
            }

            Debug.Log("[Meta3D-UI] Event system configured");
        }

        // =========================================================
        // Button Event Handlers
        // =========================================================

        private void OnConnectButtonClicked()
        {
            string ip = ipInputField != null ? ipInputField.text : "192.168.1.100";
            int port = 8765;

            if (portInputField != null)
            {
                int.TryParse(portInputField.text, out port);
                if (port <= 0 || port > 65535) port = 8765;
            }

            OnConnectRequested?.Invoke(ip, port);
        }

        private void OnScanButtonClicked()
        {
            OnScanRequested?.Invoke();
        }

        private void OnStopButtonClicked()
        {
            OnStopRequested?.Invoke();
        }

        // =========================================================
        // Public API
        // =========================================================

        /// <summary>
        /// Show or hide the hand UI.
        /// </summary>
        public void SetVisible(bool visible)
        {
            isUIVisible = visible;
            if (canvasObject != null)
            {
                canvasObject.SetActive(visible);
            }
        }

        /// <summary>
        /// Toggle UI visibility.
        /// </summary>
        public void ToggleVisible()
        {
            SetVisible(!isUIVisible);
        }

        /// <summary>
        /// Update the status display text.
        /// </summary>
        public void SetStatus(string text, Color color)
        {
            if (statusText != null)
            {
                statusText.text = text;
                statusText.color = color;
            }
        }

        /// <summary>
        /// Update the instruction text.
        /// </summary>
        public void SetInstruction(string text)
        {
            if (instructionText != null)
            {
                instructionText.text = text;
            }
        }

        /// <summary>
        /// Update the frame count display.
        /// </summary>
        public void SetFrameCount(int current, int target)
        {
            if (frameCountText != null)
            {
                frameCountText.text = $"Frame: {current} / {target}";
            }
        }

        /// <summary>
        /// Update the quality display.
        /// </summary>
        public void SetQuality(string text, Color color)
        {
            if (qualityText != null)
            {
                qualityText.text = text;
                qualityText.color = color;
            }
        }

        /// <summary>
        /// Update the progress bar.
        /// </summary>
        public void SetProgress(float value)
        {
            if (progressBar != null)
            {
                progressBar.value = Mathf.Clamp01(value);
            }
        }

        /// <summary>
        /// Update connection indicator color.
        /// </summary>
        public void SetConnectionStatus(bool connected)
        {
            if (connectionIndicator != null)
            {
                connectionIndicator.color = connected ? successColor : errorColor;
            }

            if (connectButtonText != null)
            {
                connectButtonText.text = connected ? "Bağlı ✓" : "Bağlan";
            }
        }

        /// <summary>
        /// Enable/disable scan and stop buttons
        /// </summary>
        public void SetScanButtonsState(bool scanEnabled, bool stopEnabled)
        {
            if (scanButton != null) scanButton.interactable = scanEnabled;
            if (stopButton != null) stopButton.interactable = stopEnabled;
        }

        /// <summary>
        /// Enable/disable connect button
        /// </summary>
        public void SetConnectButtonEnabled(bool enabled)
        {
            if (connectButton != null) connectButton.interactable = enabled;
        }

        private void OnDestroy()
        {
            if (connectButton != null)
                connectButton.onClick.RemoveListener(OnConnectButtonClicked);
            if (scanButton != null)
                scanButton.onClick.RemoveListener(OnScanButtonClicked);
            if (stopButton != null)
                stopButton.onClick.RemoveListener(OnStopButtonClicked);
        }
    }
}
