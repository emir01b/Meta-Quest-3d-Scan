/*
 * MetaScan — MR Setup
 * Configures Mixed Reality passthrough environment at runtime.
 * Uses #if META_XR_SDK for OVR dependencies.
 */

using UnityEngine;

namespace MetaScan
{
    public class MRSetup : MonoBehaviour
    {
        [Header("Passthrough Settings")]
        [SerializeField] private bool enablePassthrough = true;

        private void Awake()
        {
            ConfigurePassthrough();
        }

        private void ConfigurePassthrough()
        {
#if META_XR_SDK
            // Configure OVRManager for passthrough
            OVRManager ovrManager = FindFirstObjectByType<OVRManager>();
            if (ovrManager == null)
            {
                Debug.LogError("[MetaScan-MR] OVRManager not found! Add OVRCameraRig to scene.");
                return;
            }

            ovrManager.isInsightPassthroughEnabled = enablePassthrough;

            // Enable camera access if supported by SDK version
#if META_XR_SDK
            // Note: In newer SDKs this might be property of OVRManager or OVRProjectConfig
            // We set it in Project Config, but some SDK versions allow runtime toggle
            // ovrManager.isPassthroughCameraAccessEnabled = true; 
#endif

            // Add passthrough layer (SDK handles layering automatically)
            OVRPassthroughLayer passthroughLayer = ovrManager.GetComponent<OVRPassthroughLayer>();
            if (passthroughLayer == null)
            {
                passthroughLayer = ovrManager.gameObject.AddComponent<OVRPassthroughLayer>();
            }

            // Configure camera for passthrough (transparent background)
            OVRCameraRig cameraRig = FindFirstObjectByType<OVRCameraRig>();
            if (cameraRig != null && cameraRig.centerEyeAnchor != null)
            {
                Camera centerCamera = cameraRig.centerEyeAnchor.GetComponent<Camera>();
                if (centerCamera != null)
                {
                    centerCamera.clearFlags = CameraClearFlags.SolidColor;
                    centerCamera.backgroundColor = new Color(0, 0, 0, 0);
                }
            }

            // Set tracking origin to stage (standing use)
            OVRManager.TrackingOrigin trackingOrigin = OVRManager.TrackingOrigin.Stage;
            ovrManager.trackingOriginType = trackingOrigin;

            Debug.Log("[MetaScan-MR] Passthrough configured: enabled=" + enablePassthrough);
#else
            Debug.LogWarning("[MetaScan-MR] META_XR_SDK not defined. Passthrough not available in Editor.");

            // Editor fallback: set normal skybox
            Camera cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.Skybox;
            }
#endif
        }
    }
}
