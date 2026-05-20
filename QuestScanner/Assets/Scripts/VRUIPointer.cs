/*
 * MetaScan — VR UI Pointer
 * Visible laser pointer from right controller for interacting with World Space UI.
 * Uses Physics.Raycast + manual button click invocation — no OVR UI dependencies.
 * Shows laser only when UI panel is visible.
 * Opens Meta Quest System Keyboard Overlay for InputField interaction.
 */

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace MetaScan
{
    public class VRUIPointer : MonoBehaviour
    {
        [Header("Ray Settings")]
        [SerializeField] private float maxDistance = 5f;
        [SerializeField] private float laserWidth = 0.005f;
        [SerializeField] private Color laserColor = new Color(0.4f, 0.6f, 1.0f, 0.7f);
        [SerializeField] private Color hoverColor = new Color(0.3f, 1.0f, 0.5f, 0.9f);
        [SerializeField] private float hitDotSize = 0.01f;

        // References
        private ControllerManager controllerManager;
        private HandUIManager handUIManager;

        // Visuals
        private LineRenderer laserLine;
        private GameObject hitDot;
        private MeshRenderer hitDotRenderer;

        // Interaction state
        private Button hoveredButton;
        private InputField hoveredInputField;
        private Image hoveredImage;
        private Color originalButtonColor;

        // System keyboard state
        private TouchScreenKeyboard activeKeyboard;
        private InputField activeKeyboardField;

        private void Start()
        {
            controllerManager = FindFirstObjectByType<ControllerManager>();
            handUIManager = FindFirstObjectByType<HandUIManager>();
            CreateLaserVisual();
            CreateHitDot();
        }

        private void CreateLaserVisual()
        {
            GameObject obj = new GameObject("UILaser");
            obj.transform.SetParent(transform);

            laserLine = obj.AddComponent<LineRenderer>();
            laserLine.positionCount = 2;
            laserLine.startWidth = laserWidth;
            laserLine.endWidth = laserWidth * 0.3f;
            laserLine.material = new Material(Shader.Find("Sprites/Default"));
            laserLine.startColor = laserColor;
            laserLine.endColor = laserColor * 0.3f;
            laserLine.useWorldSpace = true;
            laserLine.enabled = false;
        }

        private void CreateHitDot()
        {
            hitDot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            hitDot.name = "UIHitDot";
            hitDot.transform.SetParent(transform);
            hitDot.transform.localScale = Vector3.one * hitDotSize;

            Collider col = hitDot.GetComponent<Collider>();
            if (col != null) Destroy(col);

            hitDotRenderer = hitDot.GetComponent<MeshRenderer>();
            hitDotRenderer.material = new Material(Shader.Find("Sprites/Default"));
            hitDotRenderer.material.color = Color.white;
            hitDot.SetActive(false);
        }

        private void Update()
        {
            // Always sync system keyboard text back to the InputField
            SyncKeyboardText();

            // Only show laser when UI panel is visible
            bool panelVisible = handUIManager != null
                && handUIManager.UICanvas != null
                && handUIManager.UICanvas.gameObject.activeSelf;

            if (laserLine != null) laserLine.enabled = panelVisible;

            if (!panelVisible)
            {
                if (hoveredButton != null || hoveredInputField != null) ClearHover();
                if (hitDot != null) hitDot.SetActive(false);
                return;
            }

            if (controllerManager == null) return;
            Transform rightHand = controllerManager.RightHandAnchor;
            if (rightHand == null) return;

            Ray ray = new Ray(rightHand.position, rightHand.forward);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit, maxDistance))
            {
                // Draw laser to hit point
                laserLine.SetPosition(0, ray.origin);
                laserLine.SetPosition(1, hit.point);
                hitDot.SetActive(true);
                hitDot.transform.position = hit.point + hit.normal * 0.001f;

                // Check if we hit a canvas
                Canvas canvas = hit.collider.GetComponentInParent<Canvas>();
                if (canvas != null)
                {
                    Button btn = FindButtonAtWorldPoint(canvas, hit.point);
                    InputField inputField = btn == null ? FindInputFieldAtWorldPoint(canvas, hit.point) : null;

                    UpdateHover(btn, inputField);
                    HandleTrigger(btn, inputField);

                    // Change laser color on hover
                    bool hasHover = (btn != null || inputField != null);
                    Color c = hasHover ? hoverColor : laserColor;
                    laserLine.startColor = c;
                    laserLine.endColor = c * 0.3f;
                    if (hitDotRenderer != null)
                        hitDotRenderer.material.color = hasHover ? hoverColor : Color.white;
                }
                else
                {
                    UpdateHover(null, null);
                    ResetLaserColor();
                }
            }
            else
            {
                // Draw laser to max distance
                laserLine.SetPosition(0, ray.origin);
                laserLine.SetPosition(1, ray.origin + ray.direction * maxDistance);
                hitDot.SetActive(false);
                UpdateHover(null, null);
                ResetLaserColor();
            }
        }

        // =========================================================
        // System Keyboard Sync (runs every frame in Update)
        // =========================================================

        private void SyncKeyboardText()
        {
            if (activeKeyboard == null || activeKeyboardField == null) return;

            // Her karede anlık harf harf eşitleme yap
            activeKeyboardField.text = activeKeyboard.text;

            if (activeKeyboard.status == TouchScreenKeyboard.Status.Done)
            {
                activeKeyboardField.text = activeKeyboard.text;
                activeKeyboardField.DeactivateInputField();
                Debug.Log("[MetaScan-Pointer] Keyboard done. Text: " + activeKeyboard.text);
                activeKeyboard = null;
                activeKeyboardField = null;
            }
            else if (activeKeyboard.status == TouchScreenKeyboard.Status.Canceled ||
                     activeKeyboard.status == TouchScreenKeyboard.Status.LostFocus)
            {
                activeKeyboardField.DeactivateInputField();
                activeKeyboard = null;
                activeKeyboardField = null;
            }
        }

        // =========================================================
        // Hit Detection
        // =========================================================

        private Button FindButtonAtWorldPoint(Canvas canvas, Vector3 worldPoint)
        {
            Button[] buttons = canvas.GetComponentsInChildren<Button>(false);

            foreach (Button btn in buttons)
            {
                if (!btn.gameObject.activeInHierarchy || !btn.interactable) continue;

                RectTransform btnRect = btn.GetComponent<RectTransform>();
                Vector3 btnLocal = btnRect.InverseTransformPoint(worldPoint);
                Rect rect = btnRect.rect;
                if (rect.Contains(new Vector2(btnLocal.x, btnLocal.y)))
                {
                    return btn;
                }
            }
            return null;
        }

        private InputField FindInputFieldAtWorldPoint(Canvas canvas, Vector3 worldPoint)
        {
            InputField[] fields = canvas.GetComponentsInChildren<InputField>(false);

            foreach (InputField field in fields)
            {
                if (!field.gameObject.activeInHierarchy || !field.interactable) continue;

                RectTransform fieldRect = field.GetComponent<RectTransform>();
                Vector3 fieldLocal = fieldRect.InverseTransformPoint(worldPoint);
                Rect rect = fieldRect.rect;
                if (rect.Contains(new Vector2(fieldLocal.x, fieldLocal.y)))
                {
                    return field;
                }
            }
            return null;
        }

        // =========================================================
        // Hover Management
        // =========================================================

        private void UpdateHover(Button newBtn, InputField newField)
        {
            if (newBtn == hoveredButton && newField == hoveredInputField) return;

            ClearHover();

            if (newBtn != null)
            {
                hoveredButton = newBtn;
                hoveredImage = newBtn.GetComponent<Image>();
                if (hoveredImage != null)
                {
                    originalButtonColor = hoveredImage.color;
                    hoveredImage.color = originalButtonColor * 1.3f;
                }
                if (controllerManager != null)
                    controllerManager.SendHaptic(false, 0.1f, 0.15f, 0.03f);
            }
            else if (newField != null)
            {
                hoveredInputField = newField;
                hoveredImage = newField.GetComponent<Image>();
                if (hoveredImage != null)
                {
                    originalButtonColor = hoveredImage.color;
                    hoveredImage.color = originalButtonColor * 1.3f;
                }
                if (controllerManager != null)
                    controllerManager.SendHaptic(false, 0.1f, 0.15f, 0.03f);
            }
        }

        private void ClearHover()
        {
            if (hoveredImage != null)
            {
                hoveredImage.color = originalButtonColor;
            }
            hoveredButton = null;
            hoveredInputField = null;
            hoveredImage = null;
        }

        // =========================================================
        // Trigger / Click Handling
        // =========================================================

        private void HandleTrigger(Button btn, InputField field)
        {
            bool triggerDown = false;
#if META_XR_SDK
            triggerDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
#else
            triggerDown = Input.GetMouseButtonDown(0);
#endif

            if (triggerDown)
            {
                if (btn != null)
                {
                    btn.onClick.Invoke();

                    if (hoveredImage != null)
                    {
                        StartCoroutine(FlashButton(hoveredImage, originalButtonColor));
                    }

                    if (controllerManager != null)
                        controllerManager.SendHaptic(false, 0.5f, 0.5f, 0.1f);

                    Debug.Log("[MetaScan-Pointer] Button clicked: " + btn.gameObject.name);
                }
                else if (field != null)
                {
                    // Open Meta Quest System Keyboard Overlay
                    // Requires OVRManager.requiresSystemKeyboard = true (set in MRSetup.cs)
                    // and "oculus.software.overlay_keyboard" in AndroidManifest.xml
                    string currentText = field.text ?? "";

                    activeKeyboard = TouchScreenKeyboard.Open(
                        currentText,
                        TouchScreenKeyboardType.Default,
                        false,  // autocorrection
                        false,  // multiline
                        false,  // secure
                        false,  // alert
                        ""      // placeholder
                    );
                    activeKeyboardField = field;

                    Debug.Log("[MetaScan-Pointer] System keyboard opened for: " + field.gameObject.name);

                    if (hoveredImage != null)
                    {
                        StartCoroutine(FlashButton(hoveredImage, originalButtonColor));
                    }

                    if (controllerManager != null)
                        controllerManager.SendHaptic(false, 0.5f, 0.5f, 0.1f);
                }
            }
        }

        // =========================================================
        // Visual Helpers
        // =========================================================

        private System.Collections.IEnumerator FlashButton(Image img, Color originalColor)
        {
            if (img != null) img.color = Color.white;
            yield return new WaitForSeconds(0.1f);
            if (img != null) img.color = originalColor * 1.3f;
        }

        private void ResetLaserColor()
        {
            if (laserLine != null)
            {
                laserLine.startColor = laserColor;
                laserLine.endColor = laserColor * 0.3f;
            }
            if (hitDotRenderer != null)
                hitDotRenderer.material.color = Color.white;
        }
    }
}
