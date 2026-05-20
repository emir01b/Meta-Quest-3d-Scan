/*
 * MetaScan — Hand UI Manager
 * Creates a World Space Canvas attached to the left controller.
 * Panel is visible while left grip is held down.
 * Uses only built-in Unity UI (no TMPro dependency).
 */

using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace MetaScan
{
    public class HandUIManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ControllerManager controllerManager;

        [Header("Default Connection Settings")]
        [SerializeField] private string defaultIP = "192.168.1.100";
        [SerializeField] private int defaultPort = 8765;

        [Header("UI Settings")]
        [SerializeField] private float canvasWidth = 0.32f;
        [SerializeField] private float canvasHeight = 0.26f;
        [SerializeField] private float canvasScale = 0.001f;
        [SerializeField] private Vector3 canvasOffset = new Vector3(0.05f, 0.1f, -0.08f);
        [SerializeField] private Vector3 canvasRotation = new Vector3(30f, 0f, 0f);

        [Header("Colors")]
        [SerializeField] private Color panelColor = new Color(0.05f, 0.05f, 0.12f, 0.94f);
        [SerializeField] private Color accentColor = new Color(0.39f, 0.40f, 0.95f, 1.0f);
        [SerializeField] private Color successColor = new Color(0.2f, 0.9f, 0.4f, 1.0f);
        [SerializeField] private Color warningColor = new Color(1.0f, 0.8f, 0.2f, 1.0f);
        [SerializeField] private Color errorColor = new Color(1.0f, 0.3f, 0.3f, 1.0f);

        // UI elements
        private Canvas uiCanvas;
        private GameObject canvasObject;

        private InputField ipInputField;
        private InputField portInputField;
        private Button connectButton;
        private Text connectButtonText;
        private Image connectionIndicator;

        private Button selectObjectButton;
        private Button scanButton;
        private Button stopButton;

        private Text statusText;
        private Text frameCountText;
        private Text qualityText;
        private Text instructionText;
        private Slider progressBar;

        // Events
        public event Action<string, int> OnConnectRequested;
        public event Action OnSelectObjectRequested;
        public event Action OnScanRequested;
        public event Action OnStopRequested;

        private bool isUIVisible = false; // Starts hidden

        // Public accessors
        public Canvas UICanvas => uiCanvas;

        private void Start()
        {
            if (controllerManager == null)
                controllerManager = FindFirstObjectByType<ControllerManager>();

            CreateUI();

            // Delay event system setup by one frame so OVRCameraRig anchors are ready
            StartCoroutine(DelayedSetup());

            // Subscribe to grip events for hold-to-show
            if (controllerManager != null)
            {
                controllerManager.OnLeftGripDown += OnLeftGripDown;
                controllerManager.OnLeftGripUp += OnLeftGripUp;
            }

            // Start hidden
            SetVisible(false);
        }

        private System.Collections.IEnumerator DelayedSetup()
        {
            yield return null; // Wait one frame for all components to initialize
            SetupEventSystem();
        }

        private void OnLeftGripDown()
        {
            SetVisible(true);
        }

        private void OnLeftGripUp()
        {
            SetVisible(false);
        }

        // =========================================================
        // UI Creation
        // =========================================================

        private void CreateUI()
        {
            Transform leftAnchor = null;

            if (controllerManager != null && controllerManager.LeftHandAnchor != null)
            {
                leftAnchor = controllerManager.LeftHandAnchor;
            }
#if META_XR_SDK
            else
            {
                OVRCameraRig cameraRig = FindFirstObjectByType<OVRCameraRig>();
                if (cameraRig != null)
                    leftAnchor = cameraRig.leftHandAnchor;
            }
#endif

            if (leftAnchor == null)
            {
                Debug.LogWarning("[MetaScan-UI] Left hand anchor not found. Using camera fallback.");
                Camera cam = Camera.main;
                if (cam != null)
                {
                    GameObject anchorObj = new GameObject("FallbackLeftAnchor");
                    anchorObj.transform.SetParent(cam.transform, false);
                    anchorObj.transform.localPosition = new Vector3(-0.3f, -0.2f, 0.5f);
                    leftAnchor = anchorObj.transform;
                }
                else
                {
                    Debug.LogError("[MetaScan-UI] No camera found. Cannot create UI.");
                    return;
                }
            }

            // Create Canvas
            canvasObject = new GameObject("MetaScan_UICanvas");
            canvasObject.transform.SetParent(leftAnchor, false);
            canvasObject.transform.localPosition = canvasOffset;
            canvasObject.transform.localRotation = Quaternion.Euler(canvasRotation);
            canvasObject.transform.localScale = Vector3.one * canvasScale;

            uiCanvas = canvasObject.AddComponent<Canvas>();
            uiCanvas.renderMode = RenderMode.WorldSpace;
            uiCanvas.sortingOrder = 100;

            RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(canvasWidth / canvasScale, canvasHeight / canvasScale);

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10;
            scaler.referencePixelsPerUnit = 100;

            // BoxCollider for VRUIPointer's Physics.Raycast to detect canvas hits
            BoxCollider canvasCollider = canvasObject.AddComponent<BoxCollider>();
            Vector2 canvasSize = canvasRect.sizeDelta;
            canvasCollider.size = new Vector3(canvasSize.x, canvasSize.y, 1f);
            canvasCollider.center = Vector3.zero;
            canvasObject.AddComponent<GraphicRaycaster>();

            // Build UI sections
            CreateBackground(canvasRect);
            CreateConnectionSection(canvasRect);
            CreateActionSection(canvasRect);
            CreateStatusSection(canvasRect);

            Debug.Log("[MetaScan-UI] Hand UI created and attached to left controller");
        }

        private void CreateBackground(RectTransform parent)
        {
            GameObject bgObj = CreateUIObj("Background", parent);
            Image bg = bgObj.AddComponent<Image>();
            bg.color = panelColor;
            StretchFill(bgObj);

            // Accent border
            GameObject borderObj = CreateUIObj("Border", bgObj.transform);
            Image border = borderObj.AddComponent<Image>();
            border.color = accentColor * 0.4f;
            RectTransform borderRect = borderObj.GetComponent<RectTransform>();
            borderRect.anchorMin = Vector2.zero;
            borderRect.anchorMax = Vector2.one;
            borderRect.offsetMin = new Vector2(-2, -2);
            borderRect.offsetMax = new Vector2(2, 2);
            borderObj.transform.SetAsFirstSibling();
        }

        private void CreateConnectionSection(RectTransform parent)
        {
            float w = parent.sizeDelta.x;
            float h = parent.sizeDelta.y;
            float pad = 8f;

            // Title
            CreateLabel(parent, "MetaScan", 18, accentColor,
                new Vector2(pad, 6f), new Vector2(w - pad * 2, 24f), TextAnchor.MiddleCenter);

            // Subtitle
            CreateLabel(parent, "3D Object Scanner", 9, new Color(0.6f, 0.6f, 0.7f),
                new Vector2(pad, 28f), new Vector2(w - pad * 2, 14f), TextAnchor.MiddleCenter);

            // Connection indicator dot
            GameObject indObj = CreateUIObj("ConnectionDot", parent);
            connectionIndicator = indObj.AddComponent<Image>();
            connectionIndicator.color = errorColor;
            SetRect(indObj, new Vector2(pad, 48f), new Vector2(8f, 8f));

            // IP label
            CreateLabel(parent, "IP:", 10, Color.gray,
                new Vector2(pad + 12f, 46f), new Vector2(20f, 14f), TextAnchor.MiddleLeft);

            // IP Input
            string initialIP = PlayerPrefs.GetString("MetaScan_ServerIP", defaultIP);
            string initialPort = PlayerPrefs.GetInt("MetaScan_ServerPort", defaultPort).ToString();

            DataStreamer streamer = FindFirstObjectByType<DataStreamer>();
            if (streamer != null)
            {
                if (PlayerPrefs.HasKey("MetaScan_ServerIP"))
                {
                    streamer.ServerIP = initialIP;
                    if (int.TryParse(initialPort, out int parsedPort))
                    {
                        streamer.ServerPort = parsedPort;
                    }
                }
                else
                {
                    initialIP = streamer.ServerIP;
                    initialPort = streamer.ServerPort.ToString();
                }
            }

            ipInputField = CreateInput(parent, initialIP,
                new Vector2(pad + 32f, 44f), new Vector2(w - pad * 2 - 32f - 56f, 20f));
            ipInputField.gameObject.name = "Input_IP";

            // Port Input
            portInputField = CreateInput(parent, initialPort,
                new Vector2(w - pad - 52f, 44f), new Vector2(52f, 20f));
            portInputField.gameObject.name = "Input_Port";

            // Connect button
            connectButton = CreateBtn(parent, "Baglan", accentColor,
                new Vector2(pad, 70f), new Vector2(w - pad * 2, 24f));
            connectButtonText = connectButton.GetComponentInChildren<Text>();
            connectButton.onClick.AddListener(OnConnectClicked);
        }

        private void CreateActionSection(RectTransform parent)
        {
            float w = parent.sizeDelta.x;
            float pad = 8f;
            float y = 102f;
            float bw = (w - pad * 4) / 3f;

            // Select Object button
            selectObjectButton = CreateBtn(parent, "Cisim Sec", warningColor,
                new Vector2(pad, y), new Vector2(bw, 28f));
            selectObjectButton.onClick.AddListener(OnSelectObjectClicked);
            selectObjectButton.interactable = false;

            // Scan button
            scanButton = CreateBtn(parent, ">> Tara", successColor,
                new Vector2(pad * 2 + bw, y), new Vector2(bw, 28f));
            scanButton.onClick.AddListener(OnScanClicked);
            scanButton.interactable = false;

            // Stop button
            stopButton = CreateBtn(parent, "[] Dur", errorColor,
                new Vector2(pad * 3 + bw * 2, y), new Vector2(bw, 28f));
            stopButton.onClick.AddListener(OnStopClicked);
            stopButton.interactable = false;
        }

        private void CreateStatusSection(RectTransform parent)
        {
            float w = parent.sizeDelta.x;
            float pad = 8f;
            float y = 140f;

            statusText = CreateLabel(parent, "Hazir", 12, Color.white,
                new Vector2(pad, y), new Vector2(w - pad * 2, 16f), TextAnchor.MiddleLeft);

            frameCountText = CreateLabel(parent, "Frame: 0 / 200", 10, Color.gray,
                new Vector2(pad, y + 18f), new Vector2(w / 2f, 14f), TextAnchor.MiddleLeft);

            qualityText = CreateLabel(parent, "Kalite: --", 10, Color.gray,
                new Vector2(w / 2f, y + 18f), new Vector2(w / 2f - pad, 14f), TextAnchor.MiddleRight);

            instructionText = CreateLabel(parent, "Sol gribi basili tut = panel", 9, warningColor,
                new Vector2(pad, y + 36f), new Vector2(w - pad * 2, 24f), TextAnchor.MiddleCenter);

            progressBar = CreateProgressSlider(parent,
                new Vector2(pad, y + 64f), new Vector2(w - pad * 2, 8f));
        }

        // =========================================================
        // UI Helper Methods
        // =========================================================

        private GameObject CreateUIObj(string name, Transform parent)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            return obj;
        }

        private void StretchFill(GameObject obj)
        {
            RectTransform r = obj.GetComponent<RectTransform>();
            if (r == null) r = obj.AddComponent<RectTransform>();
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;
        }

        private void SetRect(GameObject obj, Vector2 pos, Vector2 size)
        {
            RectTransform r = obj.GetComponent<RectTransform>();
            if (r == null) r = obj.AddComponent<RectTransform>();
            r.anchorMin = new Vector2(0, 1);
            r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(pos.x, -pos.y);
            r.sizeDelta = size;
        }

        private Text CreateLabel(RectTransform parent, string text, int fontSize,
            Color color, Vector2 position, Vector2 size, TextAnchor alignment)
        {
            string safeName = text.Length > 12 ? text.Substring(0, 12) : text;
            GameObject obj = CreateUIObj("Lbl_" + safeName, parent);
            Text t = obj.AddComponent<Text>();
            t.text = text;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = alignment;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            SetRect(obj, position, size);
            return t;
        }

        private InputField CreateInput(RectTransform parent, string defaultText,
            Vector2 position, Vector2 size)
        {
            GameObject obj = CreateUIObj("Input", parent);
            Image bg = obj.AddComponent<Image>();
            bg.color = new Color(0.15f, 0.15f, 0.2f, 1f);
            SetRect(obj, position, size);

            // Text child
            GameObject textObj = CreateUIObj("Text", obj.transform);
            Text inputText = textObj.AddComponent<Text>();
            inputText.fontSize = 10;
            inputText.color = Color.white;
            inputText.alignment = TextAnchor.MiddleLeft;
            inputText.supportRichText = false;
            inputText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(4, 2);
            textRect.offsetMax = new Vector2(-4, -2);

            // Placeholder
            GameObject phObj = CreateUIObj("Placeholder", obj.transform);
            Text phText = phObj.AddComponent<Text>();
            phText.text = defaultText;
            phText.fontSize = 10;
            phText.color = new Color(1f, 1f, 1f, 0.3f);
            phText.fontStyle = FontStyle.Italic;
            phText.alignment = TextAnchor.MiddleLeft;
            phText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            RectTransform phRect = phObj.GetComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.offsetMin = new Vector2(4, 2);
            phRect.offsetMax = new Vector2(-4, -2);

            InputField field = obj.AddComponent<InputField>();
            field.textComponent = inputText;
            field.placeholder = phText;
            field.text = defaultText;

            return field;
        }

        private Button CreateBtn(RectTransform parent, string label, Color color,
            Vector2 position, Vector2 size)
        {
            GameObject obj = CreateUIObj("Btn_" + label, parent);
            Image bg = obj.AddComponent<Image>();
            bg.color = color;
            SetRect(obj, position, size);

            Button btn = obj.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.normalColor = color;
            cb.highlightedColor = color * 1.2f;
            cb.pressedColor = color * 0.8f;
            cb.disabledColor = color * 0.3f;
            cb.fadeDuration = 0.1f;
            btn.colors = cb;

            // Label
            GameObject textObj = CreateUIObj("Label", obj.transform);
            Text t = textObj.AddComponent<Text>();
            t.text = label;
            t.fontSize = 11;
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter;
            t.fontStyle = FontStyle.Bold;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            StretchFill(textObj);

            return btn;
        }

        private Slider CreateProgressSlider(RectTransform parent, Vector2 position, Vector2 size)
        {
            GameObject obj = CreateUIObj("Progress", parent);
            RectTransform r = obj.AddComponent<RectTransform>();
            SetRect(obj, position, size);

            // BG
            GameObject bgObj = CreateUIObj("BG", obj.transform);
            bgObj.AddComponent<Image>().color = new Color(0.2f, 0.2f, 0.3f, 1f);
            StretchFill(bgObj);

            // Fill area
            GameObject fillArea = CreateUIObj("FillArea", obj.transform);
            RectTransform far = fillArea.AddComponent<RectTransform>();
            far.anchorMin = Vector2.zero;
            far.anchorMax = Vector2.one;
            far.offsetMin = Vector2.zero;
            far.offsetMax = Vector2.zero;

            // Fill
            GameObject fillObj = CreateUIObj("Fill", fillArea.transform);
            fillObj.AddComponent<Image>().color = accentColor;
            RectTransform fillRect = fillObj.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;

            Slider slider = obj.AddComponent<Slider>();
            slider.fillRect = fillRect;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0f;
            slider.interactable = false;

            return slider;
        }

        // =========================================================
        // Event System
        // =========================================================

        private void SetupEventSystem()
        {
            EventSystem es = FindFirstObjectByType<EventSystem>();
            if (es == null)
            {
                GameObject esObj = new GameObject("EventSystem");
                es = esObj.AddComponent<EventSystem>();
                esObj.AddComponent<StandaloneInputModule>();
            }
        }

        // =========================================================
        // Button Handlers
        // =========================================================

        private void OnConnectClicked()
        {
            string ip = ipInputField != null ? ipInputField.text : "192.168.1.100";
            int port = 8765;
            if (portInputField != null)
            {
                int.TryParse(portInputField.text, out port);
                if (port <= 0 || port > 65535) port = 8765;
            }

            // Save to PlayerPrefs so it is remembered on next launch
            PlayerPrefs.SetString("MetaScan_ServerIP", ip);
            PlayerPrefs.SetInt("MetaScan_ServerPort", port);
            PlayerPrefs.Save();

            OnConnectRequested?.Invoke(ip, port);
        }

        private void OnSelectObjectClicked() { OnSelectObjectRequested?.Invoke(); }
        private void OnScanClicked() { OnScanRequested?.Invoke(); }
        private void OnStopClicked() { OnStopRequested?.Invoke(); }

        // =========================================================
        // Public API
        // =========================================================

        public void SetVisible(bool visible)
        {
            isUIVisible = visible;
            if (canvasObject != null) canvasObject.SetActive(visible);
        }

        public void ToggleVisible() { SetVisible(!isUIVisible); }

        public void SetStatus(string text, Color color)
        {
            if (statusText != null) { statusText.text = text; statusText.color = color; }
        }

        public void SetInstruction(string text)
        {
            if (instructionText != null) instructionText.text = text;
        }

        public void SetFrameCount(int current, int target)
        {
            if (frameCountText != null)
                frameCountText.text = "Frame: " + current + " / " + target;
        }

        public void SetQuality(string text, Color color)
        {
            if (qualityText != null) { qualityText.text = text; qualityText.color = color; }
        }

        public void SetProgress(float value)
        {
            if (progressBar != null) progressBar.value = Mathf.Clamp01(value);
        }

        public void SetConnectionStatus(bool connected)
        {
            if (connectionIndicator != null)
                connectionIndicator.color = connected ? successColor : errorColor;
            if (connectButtonText != null)
                connectButtonText.text = connected ? "Bagli" : "Baglan";
        }

        public void SetSelectButtonEnabled(bool enabled)
        {
            if (selectObjectButton != null) selectObjectButton.interactable = enabled;
        }

        public void SetScanButtonsState(bool scanEnabled, bool stopEnabled)
        {
            if (scanButton != null) scanButton.interactable = scanEnabled;
            if (stopButton != null) stopButton.interactable = stopEnabled;
        }

        public void SetStopButtonText(string text)
        {
            if (stopButton != null)
            {
                Text t = stopButton.GetComponentInChildren<Text>();
                if (t != null) t.text = text;
            }
        }

        public void SetConnectButtonEnabled(bool enabled)
        {
            if (connectButton != null) connectButton.interactable = enabled;
        }

        private void OnDestroy()
        {
            if (connectButton != null) connectButton.onClick.RemoveListener(OnConnectClicked);
            if (selectObjectButton != null) selectObjectButton.onClick.RemoveListener(OnSelectObjectClicked);
            if (scanButton != null) scanButton.onClick.RemoveListener(OnScanClicked);
            if (stopButton != null) stopButton.onClick.RemoveListener(OnStopClicked);

            if (controllerManager != null)
            {
                controllerManager.OnLeftGripDown -= OnLeftGripDown;
                controllerManager.OnLeftGripUp -= OnLeftGripUp;
            }
        }
    }
}
