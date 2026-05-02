/*
 * MetaScan — Scan Pointer
 * Right controller laser pointer for targeting objects and scanning areas.
 * Uses LineRenderer for visual ray and shows hit point effects.
 */

using UnityEngine;

namespace MetaScan
{
    public class ScanPointer : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ControllerManager controllerManager;

        [Header("Ray Settings")]
        [SerializeField] private float maxRayDistance = 10f;
        [SerializeField] private float rayWidth = 0.003f;
        [SerializeField] private Color idleColor = new Color(0.3f, 0.5f, 1.0f, 0.6f);
        [SerializeField] private Color scanColor = new Color(0.2f, 1.0f, 0.4f, 0.8f);
        [SerializeField] private Color selectColor = new Color(1.0f, 0.6f, 0.0f, 0.8f);

        [Header("Hit Point")]
        [SerializeField] private float hitDotSize = 0.015f;
        [SerializeField] private Color hitDotColor = new Color(1f, 1f, 1f, 0.9f);

        // State
        public bool IsRayActive { get; private set; }
        public bool IsScanning { get; private set; }
        public bool IsInSelectMode { get; set; }
        public bool HasHit { get; private set; }
        public Vector3 HitPoint { get; private set; }
        public Vector3 HitNormal { get; private set; }

        private LineRenderer lineRenderer;
        private GameObject hitDot;
        private MeshRenderer hitDotRenderer;
        private Transform rayOrigin;

        private void Start()
        {
            if (controllerManager == null)
                controllerManager = FindFirstObjectByType<ControllerManager>();

            CreateRayVisual();
            CreateHitDot();
            SetRayActive(false);
        }

        private void CreateRayVisual()
        {
            GameObject rayObj = new GameObject("ScanRay");
            rayObj.transform.SetParent(transform, false);

            lineRenderer = rayObj.AddComponent<LineRenderer>();
            lineRenderer.positionCount = 2;
            lineRenderer.startWidth = rayWidth;
            lineRenderer.endWidth = rayWidth * 0.5f;
            lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            lineRenderer.startColor = idleColor;
            lineRenderer.endColor = idleColor * 0.3f;
            lineRenderer.useWorldSpace = true;
            lineRenderer.enabled = false;
        }

        private void CreateHitDot()
        {
            hitDot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            hitDot.name = "HitDot";
            hitDot.transform.SetParent(transform, false);
            hitDot.transform.localScale = Vector3.one * hitDotSize;

            // Remove collider to avoid interference
            Collider col = hitDot.GetComponent<Collider>();
            if (col != null) Destroy(col);

            hitDotRenderer = hitDot.GetComponent<MeshRenderer>();
            hitDotRenderer.material = new Material(Shader.Find("Sprites/Default"));
            hitDotRenderer.material.color = hitDotColor;
            hitDot.SetActive(false);
        }

        private void Update()
        {
            if (!IsRayActive || controllerManager == null) return;

            UpdateRay();
        }

        private void UpdateRay()
        {
            // Get ray from right controller
            rayOrigin = controllerManager.RightHandAnchor;
            if (rayOrigin == null) return;

            Ray ray = new Ray(rayOrigin.position, rayOrigin.forward);
            Vector3 endPoint;

            // Raycast against scene
            if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance))
            {
                HasHit = true;
                HitPoint = hit.point;
                HitNormal = hit.normal;
                endPoint = hit.point;

                // Show hit dot
                hitDot.SetActive(true);
                hitDot.transform.position = hit.point;
                hitDot.transform.up = hit.normal;
            }
            else
            {
                HasHit = false;
                endPoint = ray.origin + ray.direction * maxRayDistance;
                hitDot.SetActive(false);
            }

            // Update line
            lineRenderer.SetPosition(0, ray.origin);
            lineRenderer.SetPosition(1, endPoint);

            // Update color based on mode
            Color currentColor = IsInSelectMode ? selectColor : (IsScanning ? scanColor : idleColor);
            lineRenderer.startColor = currentColor;
            lineRenderer.endColor = currentColor * 0.3f;
        }

        // =========================================================
        // Public API
        // =========================================================

        public void SetRayActive(bool active)
        {
            IsRayActive = active;
            if (lineRenderer != null) lineRenderer.enabled = active;
            if (hitDot != null) hitDot.SetActive(false);
            if (!active) HasHit = false;
        }

        public void StartScanning()
        {
            IsScanning = true;
            IsInSelectMode = false;
        }

        public void StopScanning()
        {
            IsScanning = false;
        }

        public void SetSelectMode(bool selectMode)
        {
            IsInSelectMode = selectMode;
            IsScanning = false;
        }

        public float GetHitDistance()
        {
            if (!HasHit || rayOrigin == null) return 0f;
            return Vector3.Distance(rayOrigin.position, HitPoint);
        }
    }
}
