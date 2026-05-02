/*
 * MetaScan — VR UI Pointer
 * Visible laser pointer from right controller for interacting with World Space UI.
 * Uses Physics.Raycast + manual button click invocation — no OVR UI dependencies.
 * Shows laser only when UI panel is visible.
 */

using UnityEngine;
using UnityEngine.UI;

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
        private Image hoveredImage;
        private Color originalButtonColor;

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
            // Only show laser when UI panel is visible
            bool panelVisible = handUIManager != null
                && handUIManager.UICanvas != null
                && handUIManager.UICanvas.gameObject.activeSelf;

            if (laserLine != null) laserLine.enabled = panelVisible;

            if (!panelVisible)
            {
                if (hoveredButton != null) ClearHover();
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
                    UpdateHover(btn);
                    HandleTrigger(btn);

                    // Change laser color on hover
                    Color c = btn != null ? hoverColor : laserColor;
                    laserLine.startColor = c;
                    laserLine.endColor = c * 0.3f;
                    if (hitDotRenderer != null)
                        hitDotRenderer.material.color = btn != null ? hoverColor : Color.white;
                }
                else
                {
                    UpdateHover(null);
                    ResetLaserColor();
                }
            }
            else
            {
                // Draw laser to max distance
                laserLine.SetPosition(0, ray.origin);
                laserLine.SetPosition(1, ray.origin + ray.direction * maxDistance);
                hitDot.SetActive(false);
                UpdateHover(null);
                ResetLaserColor();
            }
        }

        private Button FindButtonAtWorldPoint(Canvas canvas, Vector3 worldPoint)
        {
            // Get all buttons in the canvas
            Button[] buttons = canvas.GetComponentsInChildren<Button>(false);

            foreach (Button btn in buttons)
            {
                if (!btn.gameObject.activeInHierarchy || !btn.interactable) continue;

                RectTransform btnRect = btn.GetComponent<RectTransform>();

                // Convert world point to button's local space
                Vector3 btnLocal = btnRect.InverseTransformPoint(worldPoint);

                // Check if point is within button bounds
                Rect rect = btnRect.rect;
                if (rect.Contains(new Vector2(btnLocal.x, btnLocal.y)))
                {
                    return btn;
                }
            }
            return null;
        }

        private void UpdateHover(Button newBtn)
        {
            if (newBtn == hoveredButton) return;

            // Clear old hover
            if (hoveredButton != null) ClearHover();

            // Set new hover
            if (newBtn != null)
            {
                hoveredButton = newBtn;
                hoveredImage = newBtn.GetComponent<Image>();
                if (hoveredImage != null)
                {
                    originalButtonColor = hoveredImage.color;
                    hoveredImage.color = originalButtonColor * 1.3f;
                }

                // Haptic feedback on hover
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
            hoveredImage = null;
        }

        private void HandleTrigger(Button btn)
        {
            if (btn == null) return;

            bool triggerDown = false;
#if META_XR_SDK
            triggerDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
#else
            triggerDown = Input.GetMouseButtonDown(0);
#endif

            if (triggerDown)
            {
                // Invoke button click
                btn.onClick.Invoke();

                // Visual flash feedback
                if (hoveredImage != null)
                {
                    StartCoroutine(FlashButton(hoveredImage, originalButtonColor));
                }

                // Haptic click feedback
                if (controllerManager != null)
                    controllerManager.SendHaptic(false, 0.5f, 0.5f, 0.1f);

                Debug.Log("[MetaScan-Pointer] Button clicked: " + btn.gameObject.name);
            }
        }

        private System.Collections.IEnumerator FlashButton(Image img, Color originalColor)
        {
            if (img != null) img.color = Color.white;
            yield return new WaitForSeconds(0.1f);
            if (img != null) img.color = originalColor * 1.3f; // Stay in hover state
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
