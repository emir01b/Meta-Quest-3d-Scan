/*
 * Meta3D Scanner - Scan Pointer
 * Ray from right controller for selecting and scanning objects.
 * Shows a visual laser pointer with LineRenderer.
 * 
 * Trigger = Start/Pause scanning on the aimed area.
 */

using UnityEngine;

namespace Meta3DScanner
{
    public class ScanPointer : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ControllerManager controllerManager;
        [SerializeField] private PointCloudVisualizer pointCloudVisualizer;

        [Header("Ray Settings")]
        [SerializeField] private float maxRayDistance = 5.0f;
        [SerializeField] private float rayWidth = 0.003f;
        [SerializeField] private LayerMask raycastMask = ~0; // Everything

        [Header("Visual Settings")]
        [SerializeField] private Color idleRayColor = new Color(0.5f, 0.8f, 1.0f, 0.6f);
        [SerializeField] private Color scanningRayColor = new Color(0.2f, 1.0f, 0.5f, 0.9f);
        [SerializeField] private Color hitPointColor = new Color(0.0f, 1.0f, 0.7f, 1.0f);
        [SerializeField] private float hitDotSize = 0.01f;

        // Components
        private LineRenderer lineRenderer;
        private GameObject hitDotObject;
        private MeshRenderer hitDotRenderer;

        // State
        private bool isScanning = false;
        private bool isRayActive = true;
        private Vector3 lastHitPoint;
        private Vector3 lastHitNormal;
        private bool hasHit = false;

        // Events
        public event System.Action<Vector3, Vector3> OnScanPointCaptured; // position, normal

        public bool IsScanning => isScanning;
        public bool HasHit => hasHit;
        public Vector3 LastHitPoint => lastHitPoint;
        public Vector3 LastHitNormal => lastHitNormal;

        private void Start()
        {
            if (controllerManager == null)
            {
                controllerManager = FindObjectOfType<ControllerManager>();
            }
            if (pointCloudVisualizer == null)
            {
                pointCloudVisualizer = FindObjectOfType<PointCloudVisualizer>();
            }

            CreateLineRenderer();
            CreateHitDot();

            Debug.Log("[Meta3D-Pointer] Scan pointer initialized");
        }

        private void CreateLineRenderer()
        {
            // Create LineRenderer on this object
            lineRenderer = gameObject.AddComponent<LineRenderer>();
            lineRenderer.positionCount = 2;
            lineRenderer.startWidth = rayWidth;
            lineRenderer.endWidth = rayWidth * 0.5f;
            lineRenderer.useWorldSpace = true;
            lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lineRenderer.receiveShadows = false;

            // Create a simple material for the ray
            Material rayMat = new Material(Shader.Find("Sprites/Default"));
            rayMat.color = idleRayColor;
            lineRenderer.material = rayMat;
            lineRenderer.startColor = idleRayColor;
            lineRenderer.endColor = idleRayColor * 0.5f;

            lineRenderer.enabled = false;
        }

        private void CreateHitDot()
        {
            hitDotObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            hitDotObject.name = "ScanPointer_HitDot";
            hitDotObject.transform.localScale = Vector3.one * hitDotSize;

            // Remove collider (we don't want to hit the dot itself)
            Collider col = hitDotObject.GetComponent<Collider>();
            if (col != null) Destroy(col);

            // Set material
            hitDotRenderer = hitDotObject.GetComponent<MeshRenderer>();
            Material dotMat = new Material(Shader.Find("Sprites/Default"));
            dotMat.color = hitPointColor;
            hitDotRenderer.material = dotMat;
            hitDotRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            hitDotRenderer.receiveShadows = false;

            hitDotObject.SetActive(false);
        }

        private void Update()
        {
            if (!isRayActive || controllerManager == null) return;

            UpdateRaycast();
        }

        private void UpdateRaycast()
        {
            Ray ray = controllerManager.GetRightControllerRay();
            RaycastHit hit;

            lineRenderer.enabled = true;
            lineRenderer.SetPosition(0, ray.origin);

            if (Physics.Raycast(ray, out hit, maxRayDistance, raycastMask))
            {
                hasHit = true;
                lastHitPoint = hit.point;
                lastHitNormal = hit.normal;

                // Update line endpoint to hit point
                lineRenderer.SetPosition(1, hit.point);

                // Show hit dot
                hitDotObject.SetActive(true);
                hitDotObject.transform.position = hit.point;
                hitDotObject.transform.up = hit.normal;

                // If scanning, add points at the hit location
                if (isScanning)
                {
                    AddScanPoint(hit.point, hit.normal);
                }
            }
            else
            {
                hasHit = false;
                lineRenderer.SetPosition(1, ray.origin + ray.direction * maxRayDistance);
                hitDotObject.SetActive(false);
            }

            // Update ray color based on scan state
            Color targetColor = isScanning ? scanningRayColor : idleRayColor;
            lineRenderer.startColor = targetColor;
            lineRenderer.endColor = targetColor * 0.5f;
            lineRenderer.material.color = targetColor;
        }

        private void AddScanPoint(Vector3 position, Vector3 normal)
        {
            // Throttle: add point every few frames to avoid overwhelming the visualizer
            if (Time.frameCount % 3 != 0) return;

            if (pointCloudVisualizer != null)
            {
                // Default to good quality green color
                Color pointColor = new Color(0.2f, 0.9f, 0.4f);
                pointCloudVisualizer.AddPoint(position, pointColor);
            }

            OnScanPointCaptured?.Invoke(position, normal);

            // Haptic feedback per scan point
            if (controllerManager != null)
            {
                controllerManager.PulseScanHaptic();
            }
        }

        /// <summary>
        /// Start scanning (called when scan session begins).
        /// </summary>
        public void StartScanning()
        {
            isScanning = true;
            Debug.Log("[Meta3D-Pointer] Scanning started");
        }

        /// <summary>
        /// Stop scanning.
        /// </summary>
        public void StopScanning()
        {
            isScanning = false;
            Debug.Log("[Meta3D-Pointer] Scanning stopped");
        }

        /// <summary>
        /// Show or hide the ray.
        /// </summary>
        public void SetRayActive(bool active)
        {
            isRayActive = active;
            if (!active)
            {
                lineRenderer.enabled = false;
                hitDotObject.SetActive(false);
            }
        }

        /// <summary>
        /// Get the current hit distance.
        /// </summary>
        public float GetHitDistance()
        {
            if (!hasHit || controllerManager == null) return maxRayDistance;
            Ray ray = controllerManager.GetRightControllerRay();
            return Vector3.Distance(ray.origin, lastHitPoint);
        }

        private void OnDestroy()
        {
            if (hitDotObject != null)
            {
                Destroy(hitDotObject);
            }
        }
    }
}
