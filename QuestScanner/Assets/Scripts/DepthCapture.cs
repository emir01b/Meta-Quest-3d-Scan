/*
 * MetaScan — Depth Capture
 * Captures depth maps from Meta Quest 3S using the Depth API.
 * Uses #if META_XR_SDK for OVR dependencies.
 * Outputs uint16 depth data in millimeters.
 */

using UnityEngine;

namespace MetaScan
{
    public class DepthCapture : MonoBehaviour
    {
        [Header("Depth Settings")]
        [SerializeField] private bool enableDepthCapture = true;

        // State
        public bool IsDepthAvailable { get; private set; }
        public int DepthWidth { get; private set; }
        public int DepthHeight { get; private set; }

        // Cached depth data
        private byte[] lastDepthData;
        private float lastDepthTime;

#if META_XR_SDK
        private OVRCameraRig cameraRig;
#endif

        private void Start()
        {
            InitializeDepth();
        }

        private void InitializeDepth()
        {
#if META_XR_SDK
            cameraRig = FindFirstObjectByType<OVRCameraRig>();
            if (cameraRig == null)
            {
                Debug.LogWarning("[MetaScan-Depth] OVRCameraRig not found. Depth capture disabled.");
                IsDepthAvailable = false;
                return;
            }

            // Check if depth API is supported
            OVRManager ovrManager = FindFirstObjectByType<OVRManager>();
            if (ovrManager != null)
            {
                // Enable environment depth
                // Note: This requires "Environment Depth" to be enabled in OVRManager
                // and the correct permissions in AndroidManifest
                Debug.Log("[MetaScan-Depth] Depth API initialization attempted");
                IsDepthAvailable = true;
            }
#else
            Debug.LogWarning("[MetaScan-Depth] Depth capture requires META_XR_SDK. Using fallback.");
            IsDepthAvailable = false;
#endif
        }

        /// <summary>
        /// Get the latest depth data as uint16 bytes (millimeters).
        /// Returns empty array if depth not available.
        /// </summary>
        public byte[] GetDepthData()
        {
            if (!enableDepthCapture || !IsDepthAvailable)
                return new byte[0];

#if META_XR_SDK
            return CaptureDepthOVR();
#else
            return new byte[0];
#endif
        }

#if META_XR_SDK
        private byte[] CaptureDepthOVR()
        {
            // Current Meta XR SDK uses EnvironmentDepthManager for depth access.
            // The depth texture is rendered automatically via the OVR compositor.
            // Direct pixel readback is not supported through the old API.
            // For scanning, we rely on camera frames + pose data primarily.
            // Depth data will be available when EnvironmentDepthManager is in the scene.

            // Try to find the global depth texture set by the SDK
            Texture depthTex = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
            if (depthTex == null || !(depthTex is RenderTexture depthRT))
            {
                return new byte[0];
            }

            DepthWidth = depthRT.width;
            DepthHeight = depthRT.height;

            // Read depth texture
            Texture2D readTex = new Texture2D(DepthWidth, DepthHeight, TextureFormat.RFloat, false);
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = depthRT;
            readTex.ReadPixels(new Rect(0, 0, DepthWidth, DepthHeight), 0, 0);
            readTex.Apply();
            RenderTexture.active = prevActive;

            // Convert float depth to uint16 millimeters
            Color[] pixels = readTex.GetPixels();
            byte[] depthBytes = new byte[DepthWidth * DepthHeight * 2];
            for (int i = 0; i < pixels.Length; i++)
            {
                float depthMeters = pixels[i].r;
                ushort depthMM = (ushort)Mathf.Clamp(depthMeters * 1000f, 0, 65535);
                depthBytes[i * 2] = (byte)(depthMM & 0xFF);
                depthBytes[i * 2 + 1] = (byte)((depthMM >> 8) & 0xFF);
            }

            Object.Destroy(readTex);

            lastDepthData = depthBytes;
            lastDepthTime = Time.time;

            return depthBytes;
        }
#endif

        /// <summary>
        /// Get the last captured depth data without recapturing.
        /// </summary>
        public byte[] GetLastDepthData()
        {
            return lastDepthData ?? new byte[0];
        }

        /// <summary>
        /// Check if depth data is reasonably fresh.
        /// </summary>
        public bool IsDepthFresh(float maxAge = 0.2f)
        {
            return lastDepthData != null && (Time.time - lastDepthTime) < maxAge;
        }
    }
}
