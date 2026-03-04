/*
 * Meta3D Scanner - MR Setup
 * Initializes Mixed Reality Passthrough on Meta Quest.
 * Configures OVRManager, OVRPassthroughLayer, and camera transparency.
 * 
 * Attach this to the OVRCameraRig GameObject in your scene.
 */

using UnityEngine;

namespace Meta3DScanner
{
    [RequireComponent(typeof(OVRManager))]
    public class MRSetup : MonoBehaviour
    {
        [Header("Passthrough Settings")]
        [SerializeField] private bool enablePassthrough = true;
        [SerializeField] private float passthroughOpacity = 1.0f;

        [Header("References (Auto-found if empty)")]
        [SerializeField] private OVRCameraRig cameraRig;
        [SerializeField] private OVRPassthroughLayer passthroughLayer;

        private OVRManager ovrManager;

        private void Awake()
        {
            // Get OVRManager
            ovrManager = GetComponent<OVRManager>();
            if (ovrManager == null)
            {
                Debug.LogError("[Meta3D-MR] OVRManager not found on this GameObject!");
                return;
            }

            // Get OVRCameraRig
            if (cameraRig == null)
            {
                cameraRig = GetComponent<OVRCameraRig>();
            }
            if (cameraRig == null)
            {
                cameraRig = FindObjectOfType<OVRCameraRig>();
            }

            if (enablePassthrough)
            {
                ConfigurePassthrough();
            }
        }

        private void Start()
        {
            if (enablePassthrough)
            {
                ConfigureCameraBackground();
            }

            Debug.Log("[Meta3D-MR] MR Setup complete. Passthrough enabled: " + enablePassthrough);
        }

        /// <summary>
        /// Configure OVRManager and OVRPassthroughLayer for passthrough MR.
        /// </summary>
        private void ConfigurePassthrough()
        {
            // Enable Insight Passthrough on OVRManager
            OVRManager.isInsightPassthroughEnabled = true;

            // Tracking origin type: Stage (standing use)
            ovrManager.trackingOriginType = OVRManager.TrackingOrigin.Stage;

            // Add OVRPassthroughLayer if not present
            if (passthroughLayer == null)
            {
                passthroughLayer = GetComponent<OVRPassthroughLayer>();
            }
            if (passthroughLayer == null)
            {
                passthroughLayer = gameObject.AddComponent<OVRPassthroughLayer>();
            }

            // Configure passthrough layer: Underlay = real world behind virtual objects
            passthroughLayer.overlayType = OVROverlay.OverlayType.Underlay;
            passthroughLayer.compositionDepth = 0;

            // Set passthrough opacity
            passthroughLayer.SetStyleToDefault(); // ensure default style first

            Debug.Log("[Meta3D-MR] Passthrough configured: Underlay mode");
        }

        /// <summary>
        /// Make the camera background transparent so passthrough shows through.
        /// Must be called after OVRCameraRig initializes its cameras.
        /// </summary>
        private void ConfigureCameraBackground()
        {
            if (cameraRig == null)
            {
                Debug.LogWarning("[Meta3D-MR] OVRCameraRig not found, cannot configure camera background.");
                return;
            }

            // Set CenterEyeAnchor camera to solid color with alpha = 0
            Camera centerEye = cameraRig.centerEyeAnchor.GetComponent<Camera>();
            if (centerEye != null)
            {
                centerEye.clearFlags = CameraClearFlags.SolidColor;
                centerEye.backgroundColor = new Color(0f, 0f, 0f, 0f);
                Debug.Log("[Meta3D-MR] Camera background set to transparent");
            }

            // Also set left/right eye cameras if they exist
            SetCameraTransparent(cameraRig.leftEyeAnchor);
            SetCameraTransparent(cameraRig.rightEyeAnchor);
        }

        private void SetCameraTransparent(Transform eyeAnchor)
        {
            if (eyeAnchor == null) return;

            Camera cam = eyeAnchor.GetComponent<Camera>();
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            }
        }

        /// <summary>
        /// Toggle passthrough on/off at runtime.
        /// </summary>
        public void SetPassthroughEnabled(bool enabled)
        {
            enablePassthrough = enabled;
            OVRManager.isInsightPassthroughEnabled = enabled;

            if (passthroughLayer != null)
            {
                passthroughLayer.hidden = !enabled;
            }

            Debug.Log("[Meta3D-MR] Passthrough " + (enabled ? "enabled" : "disabled"));
        }

        /// <summary>
        /// Set passthrough opacity (0.0 to 1.0).
        /// </summary>
        public void SetPassthroughOpacity(float opacity)
        {
            passthroughOpacity = Mathf.Clamp01(opacity);
            if (passthroughLayer != null)
            {
                passthroughLayer.textureOpacity = passthroughOpacity;
            }
        }

        public OVRCameraRig GetCameraRig()
        {
            return cameraRig;
        }

        public OVRPassthroughLayer GetPassthroughLayer()
        {
            return passthroughLayer;
        }
    }
}
