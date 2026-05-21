/*
 * MetaScan — Depth Capture
 * Captures depth maps from Meta Quest 3S using the Depth API.
 * Uses #if META_XR_SDK for OVR dependencies.
 * Outputs uint16 depth data in millimeters.
 */

using UnityEngine;

#if META_XR_SDK
using Meta.XR;
using Meta.XR.EnvironmentDepth;
#endif

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

            OVRManager ovrManager = FindFirstObjectByType<OVRManager>();
            if (ovrManager != null)
            {
                // In Meta SDK v60+, Depth is handled via EnvironmentDepthManager component
                EnvironmentDepthManager depthManager = ovrManager.GetComponent<EnvironmentDepthManager>();
                if (depthManager == null) {
                    depthManager = ovrManager.gameObject.AddComponent<EnvironmentDepthManager>();
                }
                depthManager.RemoveHands = true;
                depthManager.enabled = true;
                
                Debug.Log("[MetaScan-Depth] EnvironmentDepthManager attached and enabled.");
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
            // Try to find the global depth texture set by the SDK
            Texture depthTex = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
            if (depthTex == null)
            {
                return new byte[0];
            }

            DepthWidth = depthTex.width;
            DepthHeight = depthTex.height;

            // Create a temporary readable RenderTexture (RFloat)
            // Depth map reading requires blitting to a color texture first on Android/Quest
            RenderTexture tempRT = RenderTexture.GetTemporary(DepthWidth, DepthHeight, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            
            // Blit depth texture to our temporary RT
            Graphics.Blit(depthTex, tempRT);

            // Read pixels from the RT
            Texture2D readTex = new Texture2D(DepthWidth, DepthHeight, TextureFormat.RFloat, false);
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = tempRT;
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

            // Cleanup memory
            Object.Destroy(readTex);
            RenderTexture.ReleaseTemporary(tempRT);

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
