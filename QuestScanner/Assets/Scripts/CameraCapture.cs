/*
 * Meta3D Scanner - Camera Capture Module
 * Accesses Quest 3S Passthrough Camera API for high-quality frame capture.
 * 
 * Requires:
 * - Meta XR SDK (Passthrough Camera API)
 * - Horizon OS v74+
 * - android.permission.CAMERA or horizonos.permission.HEADSET_CAMERA
 */

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Meta3DScanner
{
    public class CameraCapture : MonoBehaviour
    {
        [Header("Camera Settings")]
        [SerializeField] private int targetWidth = 1280;
        [SerializeField] private int targetHeight = 960;
        [SerializeField] private int targetFPS = 30;

        [Header("Quality Settings")]
        [SerializeField] private int jpegQuality = 95;
        [SerializeField] private bool usePNG = false; // PNG = lossless but larger

        [Header("Debug")]
        [SerializeField] private bool showDebugPreview = false;
        [SerializeField] private RawImage debugPreviewImage;

        // Camera state
        private WebCamTexture webCamTexture;
        private Texture2D captureTexture;
        private bool isCapturing = false;
        private int frameIndex = 0;

        // OVR head tracking reference
        private OVRCameraRig ovrCameraRig;

        // Camera intrinsics (will be set from Quest API)
        private float focalLengthX = 600f;
        private float focalLengthY = 600f;
        private float principalPointX = 640f;
        private float principalPointY = 480f;

        // Events
        public event Action<byte[], CameraFrameData> OnFrameCaptured;
        public event Action<string> OnCameraError;

        public bool IsCapturing => isCapturing;
        public int FrameIndex => frameIndex;

        /// <summary>
        /// Camera frame metadata
        /// </summary>
        [Serializable]
        public class CameraFrameData
        {
            public float timestamp;
            public int frameIndex;
            public float[] poseMatrix; // 4x4 flattened
            public float focalLengthX;
            public float focalLengthY;
            public float principalPointX;
            public float principalPointY;
            public int width;
            public int height;
            public bool hasDepth;
            public float blurScore;
            public float brightness;
        }

        private void Start()
        {
            // Find OVRCameraRig for head pose tracking
            ovrCameraRig = FindObjectOfType<OVRCameraRig>();

            // Request camera permission on Quest
            StartCoroutine(RequestCameraPermission());
        }

        private IEnumerator RequestCameraPermission()
        {
            // Check if we have camera permission
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                UnityEngine.Android.Permission.Camera))
            {
                UnityEngine.Android.Permission.RequestUserPermission(
                    UnityEngine.Android.Permission.Camera);
                yield return new WaitForSeconds(1f);
            }

            // Also request Quest-specific permission
            try
            {
                UnityEngine.Android.Permission.RequestUserPermission(
                    "horizonos.permission.HEADSET_CAMERA");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Meta3D] Quest camera permission request: {e.Message}");
            }

            yield return new WaitForSeconds(0.5f);
            InitializeCamera();
        }

        private void InitializeCamera()
        {
            WebCamDevice[] devices = WebCamTexture.devices;

            if (devices.Length == 0)
            {
                Debug.LogError("[Meta3D] No cameras found!");
                OnCameraError?.Invoke("No cameras available");
                return;
            }

            // Log available cameras
            foreach (var device in devices)
            {
                Debug.Log($"[Meta3D] Camera found: {device.name}");
            }

            // Use the first available camera (Quest passthrough camera)
            webCamTexture = new WebCamTexture(devices[0].name, targetWidth, targetHeight, targetFPS);
            captureTexture = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);

            // Show debug preview if enabled
            if (showDebugPreview && debugPreviewImage != null)
            {
                debugPreviewImage.texture = webCamTexture;
            }

            Debug.Log($"[Meta3D] Camera initialized: {devices[0].name} ({targetWidth}x{targetHeight} @ {targetFPS}fps)");
        }

        /// <summary>
        /// Start capturing frames
        /// </summary>
        public void StartCapture()
        {
            if (webCamTexture == null)
            {
                OnCameraError?.Invoke("Camera not initialized");
                return;
            }

            if (!webCamTexture.isPlaying)
            {
                webCamTexture.Play();
            }

            isCapturing = true;
            frameIndex = 0;
            StartCoroutine(CaptureLoop());
            Debug.Log("[Meta3D] Capture started");
        }

        /// <summary>
        /// Stop capturing frames
        /// </summary>
        public void StopCapture()
        {
            isCapturing = false;
            if (webCamTexture != null && webCamTexture.isPlaying)
            {
                webCamTexture.Stop();
            }
            Debug.Log($"[Meta3D] Capture stopped. Total frames: {frameIndex}");
        }

        private IEnumerator CaptureLoop()
        {
            float captureInterval = 1f / targetFPS;
            WaitForSeconds wait = new WaitForSeconds(captureInterval);

            while (isCapturing)
            {
                if (webCamTexture.didUpdateThisFrame)
                {
                    CaptureFrame();
                }
                yield return wait;
            }
        }

        private void CaptureFrame()
        {
            if (webCamTexture == null || !webCamTexture.isPlaying) return;

            // Update texture dimensions if needed
            if (captureTexture.width != webCamTexture.width ||
                captureTexture.height != webCamTexture.height)
            {
                captureTexture.Reinitialize(webCamTexture.width, webCamTexture.height);
            }

            // Get pixels from webcam texture
            captureTexture.SetPixels(webCamTexture.GetPixels());
            captureTexture.Apply();

            // Encode to JPEG or PNG
            byte[] imageBytes;
            if (usePNG)
            {
                imageBytes = captureTexture.EncodeToPNG();
            }
            else
            {
                imageBytes = captureTexture.EncodeToJPG(jpegQuality);
            }

            // Get camera pose from head tracking
            float[] poseMatrix = GetHeadPoseMatrix();

            // Calculate image quality metrics
            float brightness = CalculateBrightness(captureTexture);
            float blurScore = CalculateBlurScore(captureTexture);

            // Create frame data
            CameraFrameData frameData = new CameraFrameData
            {
                timestamp = Time.realtimeSinceStartup,
                frameIndex = frameIndex,
                poseMatrix = poseMatrix,
                focalLengthX = focalLengthX,
                focalLengthY = focalLengthY,
                principalPointX = principalPointX,
                principalPointY = principalPointY,
                width = captureTexture.width,
                height = captureTexture.height,
                hasDepth = false, // Depth handled by DepthCapture module
                blurScore = blurScore,
                brightness = brightness
            };

            // Fire event
            OnFrameCaptured?.Invoke(imageBytes, frameData);
            frameIndex++;
        }

        /// <summary>
        /// Get the current headset pose as a 4x4 matrix (flattened row-major)
        /// </summary>
        private float[] GetHeadPoseMatrix()
        {
            // Get the center eye anchor / head transform
            // Prefer OVRCameraRig if available
            Transform headTransform = null;
            if (ovrCameraRig != null && ovrCameraRig.centerEyeAnchor != null)
            {
                headTransform = ovrCameraRig.centerEyeAnchor;
            }
            else
            {
                Camera mainCam = Camera.main;
                if (mainCam != null) headTransform = mainCam.transform;
            }

            if (headTransform == null)
            {
                Debug.LogWarning("[Meta3D] No camera transform found for head pose");
                return new float[16];
            }

            Matrix4x4 poseMatrix = Matrix4x4.TRS(
                headTransform.position,
                headTransform.rotation,
                Vector3.one
            );

            // Flatten to row-major array
            float[] matrix = new float[16];
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    matrix[row * 4 + col] = poseMatrix[row, col];
                }
            }

            return matrix;
        }

        /// <summary>
        /// Calculate average brightness of the frame (0-255)
        /// </summary>
        private float CalculateBrightness(Texture2D tex)
        {
            Color[] pixels = tex.GetPixels(0);
            float totalBrightness = 0;
            int sampleCount = Mathf.Min(pixels.Length, 10000); // Sample subset for performance

            int step = pixels.Length / sampleCount;
            for (int i = 0; i < pixels.Length; i += step)
            {
                totalBrightness += (pixels[i].r + pixels[i].g + pixels[i].b) / 3f;
            }

            return (totalBrightness / sampleCount) * 255f;
        }

        /// <summary>
        /// Estimate blur score using pixel variance (higher = sharper)
        /// Simple GPU-friendly approximation of Laplacian variance
        /// </summary>
        private float CalculateBlurScore(Texture2D tex)
        {
            Color[] pixels = tex.GetPixels(0);
            int width = tex.width;
            int height = tex.height;
            float sumDiff = 0;
            int samples = 0;

            // Sample from center region for speed
            int startX = width / 4;
            int endX = 3 * width / 4;
            int startY = height / 4;
            int endY = 3 * height / 4;
            int step = 4; // Sample every 4th pixel

            for (int y = startY; y < endY; y += step)
            {
                for (int x = startX; x < endX; x += step)
                {
                    if (x < 1 || y < 1 || x >= width - 1 || y >= height - 1) continue;

                    float center = pixels[y * width + x].grayscale;
                    float left = pixels[y * width + (x - 1)].grayscale;
                    float right = pixels[y * width + (x + 1)].grayscale;
                    float up = pixels[(y - 1) * width + x].grayscale;
                    float down = pixels[(y + 1) * width + x].grayscale;

                    // Laplacian
                    float laplacian = Mathf.Abs(left + right + up + down - 4 * center);
                    sumDiff += laplacian * laplacian;
                    samples++;
                }
            }

            return samples > 0 ? (sumDiff / samples) * 10000f : 0;
        }

        /// <summary>
        /// Update camera intrinsics if available from Quest API
        /// </summary>
        public void SetCameraIntrinsics(float fx, float fy, float cx, float cy)
        {
            focalLengthX = fx;
            focalLengthY = fy;
            principalPointX = cx;
            principalPointY = cy;
            Debug.Log($"[Meta3D] Intrinsics set: fx={fx}, fy={fy}, cx={cx}, cy={cy}");
        }

        private void OnDestroy()
        {
            StopCapture();
            if (webCamTexture != null)
            {
                Destroy(webCamTexture);
            }
            if (captureTexture != null)
            {
                Destroy(captureTexture);
            }
        }
    }
}
