/*
 * MetaScan — Object Selector
 * Allows user to select a target object by pointing and defining a bounding sphere.
 * Right trigger hold: set center, drag to set radius.
 * Visualizes selection area with a wireframe sphere.
 */

using System;
using UnityEngine;

namespace MetaScan
{
    public class ObjectSelector : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ControllerManager controllerManager;
        [SerializeField] private ScanPointer scanPointer;

        [Header("Selection Settings")]
        [SerializeField] private float minRadius = 0.05f;
        [SerializeField] private float maxRadius = 2.0f;
        [SerializeField] private Color selectionColor = new Color(1.0f, 0.6f, 0.0f, 0.4f);
        [SerializeField] private Color confirmedColor = new Color(0.2f, 0.9f, 0.4f, 0.3f);
        [SerializeField] private int sphereSegments = 32;

        // Selection state
        public bool IsSelecting { get; private set; }
        public bool HasSelection { get; private set; }
        public Vector3 SelectionCenter { get; private set; }
        public float SelectionRadius { get; private set; }

        // Visual
        private GameObject sphereVisual;
        private LineRenderer[] ringRenderers;
        private bool isDragging;
        private Vector3 dragStartPoint;
        private float currentSelectionDistance = 0.8f;

        // Events
        public event Action<Vector3, float> OnObjectSelected;
        public event Action OnSelectionCleared;

        private void Start()
        {
            if (controllerManager == null)
                controllerManager = FindFirstObjectByType<ControllerManager>();
            if (scanPointer == null)
                scanPointer = FindFirstObjectByType<ScanPointer>();

            CreateSelectionVisual();
            SetVisualsActive(false);
        }

        /// <summary>
        /// Enter selection mode.
        /// </summary>
        public void StartSelecting()
        {
            IsSelecting = true;
            HasSelection = false;
            isDragging = false;

            currentSelectionDistance = 0.8f;
            SelectionRadius = 0.15f; // Varsayılan 15cm önizleme boyutu

            if (controllerManager != null)
            {
                controllerManager.OnRightTriggerDown += OnTriggerDown;
                controllerManager.OnRightTriggerUp += OnTriggerUp;

                Ray ray = controllerManager.GetControllerRay(false);
                SelectionCenter = ray.origin + ray.direction * currentSelectionDistance;
            }
            else
            {
                Camera cam = Camera.main;
                if (cam != null)
                    SelectionCenter = cam.transform.position + cam.transform.forward * currentSelectionDistance;
                else
                    SelectionCenter = Vector3.forward * currentSelectionDistance;
            }

            SetVisualsActive(true); // Seçime tıklar tıklamaz küre belirmeli
            UpdateVisualTransform();
            UpdateVisualColor(selectionColor);

            Debug.Log("[MetaScan-Selector] Selection mode activated");
        }

        /// <summary>
        /// Exit selection mode.
        /// </summary>
        public void StopSelecting()
        {
            IsSelecting = false;
            isDragging = false;

            if (controllerManager != null)
            {
                controllerManager.OnRightTriggerDown -= OnTriggerDown;
                controllerManager.OnRightTriggerUp -= OnTriggerUp;
            }

            Debug.Log("[MetaScan-Selector] Selection mode deactivated");
        }

        /// <summary>
        /// Clear current selection.
        /// </summary>
        public void ClearSelection()
        {
            HasSelection = false;
            SetVisualsActive(false);
            OnSelectionCleared?.Invoke();
        }

        private void OnTriggerDown()
        {
            if (!IsSelecting) return;

            // Tetik basıldığında küre merkezini mevcut hizalanmış konumda kilitler
            dragStartPoint = SelectionCenter;
            isDragging = true;
            SelectionRadius = minRadius;

            SetVisualsActive(true);
            UpdateVisualTransform();

            // Haptic feedback
            if (controllerManager != null)
                controllerManager.SendHaptic(false, 0.3f, 0.3f, 0.05f);
        }

        private void OnTriggerUp()
        {
            if (!IsSelecting || !isDragging) return;

            isDragging = false;

            if (SelectionRadius >= minRadius)
            {
                HasSelection = true;
                UpdateVisualColor(confirmedColor);
                OnObjectSelected?.Invoke(SelectionCenter, SelectionRadius);

                // Haptic confirmation
                if (controllerManager != null)
                    controllerManager.SendHaptic(false, 0.5f, 0.6f, 0.15f);

                Debug.Log($"[MetaScan-Selector] Object selected: center={SelectionCenter}, radius={SelectionRadius:F2}m");
            }
            else
            {
                SetVisualsActive(false);
            }
        }

        private void Update()
        {
            if (!IsSelecting) return;

            // Sağ analog çubuğu (Joystick) dikey hareketini (yukarı/aşağı) oku
            float joyY = 0f;
#if META_XR_SDK
            Vector2 joy = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            joyY = joy.y;
#else
            if (Input.GetKey(KeyCode.UpArrow)) joyY = 1f;
            else if (Input.GetKey(KeyCode.DownArrow)) joyY = -1f;
#endif

            if (Mathf.Abs(joyY) > 0.1f)
            {
                // Joystick ile derinliği ayarla (1.0 m/s hızında)
                currentSelectionDistance += joyY * Time.deltaTime * 1.0f;
                currentSelectionDistance = Mathf.Clamp(currentSelectionDistance, 0.1f, maxRadius * 2f);
            }

            if (!isDragging)
            {
                // Sürükleme yapmıyorken (sadece işaret ediyorken) küreyi derinliğe göre yerleştir
                if (controllerManager != null)
                {
                    Ray ray = controllerManager.GetControllerRay(false);
                    SelectionCenter = ray.origin + ray.direction * currentSelectionDistance;
                }
                else
                {
                    Camera cam = Camera.main;
                    if (cam != null)
                        SelectionCenter = cam.transform.position + cam.transform.forward * currentSelectionDistance;
                }

                SelectionRadius = 0.15f; // Sürükleme öncesi sabit önizleme boyutu
                UpdateVisualTransform();
            }
            else
            {
                // Sürüklerken (boyut ayarlıyorken) kilitli merkezden güncel işaret ucuna olan mesafeyi yarıçap yap
                Vector3 currentPoint;
                if (controllerManager != null)
                {
                    Ray ray = controllerManager.GetControllerRay(false);
                    currentPoint = ray.origin + ray.direction * currentSelectionDistance;
                }
                else
                {
                    Camera cam = Camera.main;
                    if (cam != null)
                        currentPoint = cam.transform.position + cam.transform.forward * currentSelectionDistance;
                    else
                        currentPoint = Vector3.forward * currentSelectionDistance;
                }

                float dist = Vector3.Distance(dragStartPoint, currentPoint);
                SelectionRadius = Mathf.Clamp(dist, minRadius, maxRadius);
                SelectionCenter = dragStartPoint; // Merkez kilitli kalır
                UpdateVisualTransform();
            }
        }

        // =========================================================
        // Visualization — Wireframe Sphere using LineRenderers
        // =========================================================

        private void CreateSelectionVisual()
        {
            sphereVisual = new GameObject("SelectionSphere");
            sphereVisual.transform.SetParent(transform, false);

            // Create 3 ring LineRenderers (XY, XZ, YZ planes)
            ringRenderers = new LineRenderer[3];

            for (int i = 0; i < 3; i++)
            {
                GameObject ringObj = new GameObject("Ring_" + i);
                ringObj.transform.SetParent(sphereVisual.transform, false);

                LineRenderer lr = ringObj.AddComponent<LineRenderer>();
                lr.positionCount = sphereSegments + 1;
                lr.startWidth = 0.005f;
                lr.endWidth = 0.005f;
                lr.loop = false;
                lr.useWorldSpace = false;
                lr.material = new Material(Shader.Find("Sprites/Default"));
                lr.startColor = selectionColor;
                lr.endColor = selectionColor;

                ringRenderers[i] = lr;
            }
        }

        private void UpdateVisualTransform()
        {
            if (sphereVisual == null) return;

            sphereVisual.transform.position = SelectionCenter;

            // Update ring positions for the current radius
            for (int ring = 0; ring < 3; ring++)
            {
                LineRenderer lr = ringRenderers[ring];
                for (int j = 0; j <= sphereSegments; j++)
                {
                    float angle = (float)j / sphereSegments * Mathf.PI * 2f;
                    float x = Mathf.Cos(angle) * SelectionRadius;
                    float y = Mathf.Sin(angle) * SelectionRadius;

                    Vector3 pos;
                    switch (ring)
                    {
                        case 0: pos = new Vector3(x, y, 0); break;  // XY
                        case 1: pos = new Vector3(x, 0, y); break;  // XZ
                        default: pos = new Vector3(0, x, y); break;  // YZ
                    }
                    lr.SetPosition(j, pos);
                }
            }
        }

        private void UpdateVisualColor(Color color)
        {
            if (ringRenderers == null) return;
            foreach (var lr in ringRenderers)
            {
                if (lr != null)
                {
                    lr.startColor = color;
                    lr.endColor = color;
                }
            }
        }

        private void SetVisualsActive(bool active)
        {
            if (sphereVisual != null) sphereVisual.SetActive(active);
        }

        /// <summary>
        /// Check if a point is within the selection volume.
        /// </summary>
        public bool IsPointInSelection(Vector3 point)
        {
            if (!HasSelection) return false;
            return Vector3.Distance(point, SelectionCenter) <= SelectionRadius;
        }

        private void OnDestroy()
        {
            if (controllerManager != null)
            {
                controllerManager.OnRightTriggerDown -= OnTriggerDown;
                controllerManager.OnRightTriggerUp -= OnTriggerUp;
            }
        }
    }
}
