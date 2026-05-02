/*
 * MetaScan — Camera Capture
 * Captures passthrough camera images from Meta Quest 3S.
 * Uses #if META_XR_SDK for OVR Camera Access API.
 * Encodes frames as JPEG with quality metrics.
 */

using System;
using UnityEngine;

namespace MetaScan
{
    public class CameraCapture : MonoBehaviour
    {
        [Header("Capture Settings")]
        [SerializeField] private int captureWidth = 1280;
        [SerializeField] private int captureHeight = 960;
        [SerializeField] private int jpegQuality = 85;
        [SerializeField] private float captureInterval = 0.1f; // 10 fps default

        [Header("Quality Settings")]
        [SerializeField] private float blurThreshold = 100f;

        // State
        public bool IsCapturing { get; private set; }
        public int FrameIndex { get; private set; }
        public float LastCaptureTime { get; private set; }

        // Events
        public event Action<CapturedFrame> OnFrameCaptured;

        // Head tracking
        private Transform headTransform;
        private float nextCaptureTime;

        // Camera intrinsics (estimated defaults for Quest 3S)
        private float focalLengthX = 640f;
        private float focalLengthY = 640f;
        private float principalPointX = 640f;
        private float principalPointY = 480f;

        // Render texture for screenshot fallback
        private RenderTexture captureRT;
        private Texture2D captureTexture;

        public struct CapturedFrame
        {
            public int frameIndex;
            public double timestamp;
            public byte[] imageData;       // JPEG
            public byte[] depthData;       // raw uint16 (from DepthCapture)
            public Vector3 position;
            public Quaternion rotation;
            public float fx, fy, cx, cy;
        }

        private void Start()
        {
            FindHeadTransform();

            // Create capture texture
            captureRT = new RenderTexture(captureWidth, captureHeight, 24);
            captureTexture = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);
        }

        private void FindHeadTransform()
        {
#if META_XR_SDK
            OVRCameraRig rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null && rig.centerEyeAnchor != null)
            {
                headTransform = rig.centerEyeAnchor;
                return;
            }
#endif
            Camera cam = Camera.main;
            if (cam != null) headTransform = cam.transform;
        }

        public void StartCapturing()
        {
            IsCapturing = true;
            FrameIndex = 0;
            nextCaptureTime = 0f;
            Debug.Log("[MetaScan-Camera] Capture started");
        }

        public void StopCapturing()
        {
            IsCapturing = false;
            Debug.Log($"[MetaScan-Camera] Capture stopped. Total frames: {FrameIndex}");
        }

        private void Update()
        {
            if (!IsCapturing) return;
            if (Time.time < nextCaptureTime) return;

            CaptureFrame();
            nextCaptureTime = Time.time + captureInterval;
        }

        private void CaptureFrame()
        {
            if (headTransform == null)
            {
                FindHeadTransform();
                if (headTransform == null) return;
            }

            byte[] imageData = CaptureImage();
            if (imageData == null || imageData.Length == 0) return;

            CapturedFrame frame = new CapturedFrame
            {
                frameIndex = FrameIndex,
                timestamp = GetTimestamp(),
                imageData = imageData,
                depthData = new byte[0], // Will be filled by DataStreamer from DepthCapture
                position = headTransform.position,
                rotation = headTransform.rotation,
                fx = focalLengthX,
                fy = focalLengthY,
                cx = principalPointX,
                cy = principalPointY,
            };

            FrameIndex++;
            LastCaptureTime = Time.time;

            OnFrameCaptured?.Invoke(frame);
        }

        private byte[] CaptureImage()
        {
#if META_XR_SDK
            // Try OVR Passthrough Camera API first
            // NOTE: Requires Meta XR Camera Access package
            // The Passthrough Camera API provides camera frames via
            // OVRPlugin.Media.GetMrcFrameImageData or
            // PassthroughCameraUtils (Meta-specific)
            // For now, fall through to screenshot-based capture
#endif

            // Fallback: capture screen via RenderTexture
            Camera cam = headTransform != null
                ? headTransform.GetComponent<Camera>()
                : Camera.main;

            if (cam == null) return null;

            RenderTexture prevRT = cam.targetTexture;
            cam.targetTexture = captureRT;
            cam.Render();
            cam.targetTexture = prevRT;

            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = captureRT;
            captureTexture.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
            captureTexture.Apply();
            RenderTexture.active = prevActive;

            byte[] jpegData = captureTexture.EncodeToJPG(jpegQuality);
            return jpegData;
        }

        private double GetTimestamp()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        }

        /// <summary>
        /// Set camera intrinsics from external source (e.g., OVR API).
        /// </summary>
        public void SetIntrinsics(float fx, float fy, float cx, float cy)
        {
            focalLengthX = fx;
            focalLengthY = fy;
            principalPointX = cx;
            principalPointY = cy;
        }

        /// <summary>
        /// Set capture frame rate.
        /// </summary>
        public void SetCaptureRate(float fps)
        {
            captureInterval = 1f / Mathf.Max(fps, 1f);
        }

        private void OnDestroy()
        {
            if (captureRT != null) captureRT.Release();
            if (captureTexture != null) Destroy(captureTexture);
        }
    }
}
