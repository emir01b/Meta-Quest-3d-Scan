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
            SetVisualsActive(false);

            if (controllerManager != null)
            {
                controllerManager.OnRightTriggerDown += OnTriggerDown;
                controllerManager.OnRightTriggerUp += OnTriggerUp;
            }

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

            // Use scan pointer's hit point or controller forward position
            if (scanPointer != null && scanPointer.HasHit)
            {
                dragStartPoint = scanPointer.HitPoint;
            }
            else
            {
                // Default: 1m in front of right controller
                Ray ray = controllerManager.GetControllerRay(false);
                dragStartPoint = ray.origin + ray.direction * 1.0f;
            }

            isDragging = true;
            SelectionCenter = dragStartPoint;
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
            if (!IsSelecting || !isDragging) return;

            // Update radius based on current pointer position
            Vector3 currentPoint;
            if (scanPointer != null && scanPointer.HasHit)
            {
                currentPoint = scanPointer.HitPoint;
            }
            else
            {
                Ray ray = controllerManager.GetControllerRay(false);
                currentPoint = ray.origin + ray.direction * 1.0f;
            }

            float dist = Vector3.Distance(dragStartPoint, currentPoint);
            SelectionRadius = Mathf.Clamp(dist, minRadius, maxRadius);

            // Keep center at the initial point
            SelectionCenter = dragStartPoint;

            UpdateVisualTransform();
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
