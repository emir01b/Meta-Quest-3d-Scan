/*
 * Meta3D Scanner - Depth Capture Module
 * Accesses Quest 3S Depth API for real-time depth maps.
 * 
 * Requires:
 * - Meta XR SDK with Depth API
 * - EnvironmentDepthManager component in scene
 */

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Meta3DScanner
{
    public class DepthCapture : MonoBehaviour
    {
        [Header("Depth Settings")]
        [SerializeField] private bool captureDepth = true;
        [SerializeField] private bool useEnvironmentDepth = true; // Uses Meta Depth API

        [Header("References (Auto-found if empty)")]
        [SerializeField] private OVRCameraRig ovrCameraRig;

        [Header("Debug")]
        [SerializeField] private bool showDepthVisualization = false;

        // Depth state
        private RenderTexture depthRenderTexture;
        private Texture2D depthTexture;
        private bool isDepthAvailable = false;
        private int depthWidth = 0;
        private int depthHeight = 0;

        // Events
        public event Action<byte[], int, int> OnDepthCaptured;

        public bool IsDepthAvailable => isDepthAvailable;

        private void Start()
        {
            if (ovrCameraRig == null)
            {
                ovrCameraRig = FindObjectOfType<OVRCameraRig>();
            }
            InitializeDepth();
        }

        private void InitializeDepth()
        {
            if (!captureDepth) return;

            if (useEnvironmentDepth)
            {
                InitializeEnvironmentDepth();
            }
            else
            {
                InitializeFallbackDepth();
            }
        }

        /// <summary>
        /// Initialize using Meta's Environment Depth API
        /// </summary>
        private void InitializeEnvironmentDepth()
        {
            // The Meta Depth API provides depth through the EnvironmentDepthManager
            // and EnvironmentDepthTextureProvider components.
            // These should be set up in the Unity scene.

            // Check if the OVRManager is available (present on OVRCameraRig)
            OVRManager depthManager = null;
            if (ovrCameraRig != null)
            {
                depthManager = ovrCameraRig.GetComponent<OVRManager>();
            }
            if (depthManager == null)
            {
                depthManager = FindObjectOfType<OVRManager>();
            }

            if (depthManager != null)
            {
                Debug.Log("[Meta3D] OVRManager found, Depth API should be available");
                isDepthAvailable = true;
            }
            else
            {
                Debug.LogWarning("[Meta3D] OVRManager not found. Add to scene for depth support.");
                isDepthAvailable = false;
            }
        }

        /// <summary>
        /// Initialize fallback depth estimation
        /// </summary>
        private void InitializeFallbackDepth()
        {
            // Fallback: Use the Unity depth buffer
            depthWidth = 640;
            depthHeight = 480;

            depthRenderTexture = new RenderTexture(depthWidth, depthHeight, 24, RenderTextureFormat.Depth);
            depthTexture = new Texture2D(depthWidth, depthHeight, TextureFormat.RFloat, false);

            isDepthAvailable = true;
            Debug.Log("[Meta3D] Fallback depth initialized (Unity depth buffer)");
        }

        /// <summary>
        /// Capture the current depth frame as a byte array of uint16 values.
        /// Returns null if depth is not available.
        /// </summary>
        public byte[] CaptureDepthFrame()
        {
            if (!captureDepth || !isDepthAvailable) return null;

            if (useEnvironmentDepth)
            {
                return CaptureEnvironmentDepth();
            }
            else
            {
                return CaptureFallbackDepth();
            }
        }

        /// <summary>
        /// Capture depth from Meta's Environment Depth API.
        /// The depth texture is available as a global shader variable: _EnvironmentDepthTexture
        /// </summary>
        private byte[] CaptureEnvironmentDepth()
        {
            try
            {
                // Access the environment depth texture through the shader global
                // This is set by Meta's EnvironmentDepthTextureProvider
                Texture depthTex = Shader.GetGlobalTexture("_EnvironmentDepthTexture");

                if (depthTex == null)
                {
                    Debug.LogWarning("[Meta3D] Environment depth texture not available");
                    return null;
                }

                if (depthTex is RenderTexture rt)
                {
                    depthWidth = rt.width;
                    depthHeight = rt.height;

                    // Read depth texture to CPU
                    if (depthTexture == null ||
                        depthTexture.width != depthWidth ||
                        depthTexture.height != depthHeight)
                    {
                        depthTexture = new Texture2D(depthWidth, depthHeight,
                            TextureFormat.RFloat, false);
                    }

                    RenderTexture.active = rt;
                    depthTexture.ReadPixels(new Rect(0, 0, depthWidth, depthHeight), 0, 0);
                    depthTexture.Apply();
                    RenderTexture.active = null;

                    // Convert float depth to uint16 (millimeters)
                    Color[] pixels = depthTexture.GetPixels();
                    byte[] depthBytes = new byte[depthWidth * depthHeight * 2];

                    for (int i = 0; i < pixels.Length; i++)
                    {
                        // Depth value in meters -> convert to millimeters (uint16)
                        float depthMeters = pixels[i].r;
                        ushort depthMM = (ushort)Mathf.Clamp(depthMeters * 1000f, 0f, 65535f);
                        depthBytes[i * 2] = (byte)(depthMM & 0xFF);
                        depthBytes[i * 2 + 1] = (byte)((depthMM >> 8) & 0xFF);
                    }

                    OnDepthCaptured?.Invoke(depthBytes, depthWidth, depthHeight);
                    return depthBytes;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Meta3D] Depth capture error: {e.Message}");
            }

            return null;
        }

        /// <summary>
        /// Capture depth from Unity's depth buffer (fallback)
        /// </summary>
        private byte[] CaptureFallbackDepth()
        {
            // Get camera from OVRCameraRig or fallback to Camera.main
            Camera cam = null;
            if (ovrCameraRig != null && ovrCameraRig.centerEyeAnchor != null)
            {
                cam = ovrCameraRig.centerEyeAnchor.GetComponent<Camera>();
            }
            if (cam == null)
            {
                cam = Camera.main;
            }
            if (cam == null) return null;

            // Enable depth texture generation
            cam.depthTextureMode = DepthTextureMode.Depth;

            // Note: Getting the actual depth buffer from the GPU to CPU is complex
            // In production, use a CommandBuffer or AsyncGPUReadback
            // This is a simplified version

            Debug.LogWarning("[Meta3D] Fallback depth capture is limited. Use Quest Depth API for best results.");
            return null;
        }

        /// <summary>
        /// Get depth dimensions
        /// </summary>
        public (int width, int height) GetDepthDimensions()
        {
            return (depthWidth, depthHeight);
        }

        private void OnDestroy()
        {
            if (depthRenderTexture != null)
            {
                depthRenderTexture.Release();
                Destroy(depthRenderTexture);
            }
            if (depthTexture != null)
            {
                Destroy(depthTexture);
            }
        }
    }
}
