using System;
using System.Collections;
using UnityEngine;

#if META_XR_SDK
using Meta.XR;
#endif

namespace MetaScan
{
    // Captures one physical PCA image, its timestamp and its camera pose as a unit.
    public class CameraCapture : MonoBehaviour
    {
        [SerializeField] private int captureWidth = 1280;
        [SerializeField] private int captureHeight = 960;
        [SerializeField, Range(50, 100)] private int jpegQuality = 90;
        [SerializeField] private float captureInterval = 0.25f;
        [SerializeField] private float minimumTranslation = 0.02f;
        [SerializeField] private float minimumRotationDegrees = 5f;
        [SerializeField] private float maximumStepMetres = 0.4f;
        [SerializeField] private float maximumStepDegrees = 60f;

        public bool IsCapturing { get; private set; }
        public bool IsReady { get; private set; }
        public int FrameIndex { get; private set; }
        public float LastCaptureTime { get; private set; }
        public float FocalLengthX { get; private set; }
        public float FocalLengthY { get; private set; }
        public float PrincipalPointX { get; private set; }
        public float PrincipalPointY { get; private set; }
        public event Action<CapturedFrame> OnFrameCaptured;
        public event Action<string> OnCaptureError;

        private float nextCaptureTime;
        private bool havePreviousPose;
        private Vector3 previousPosition;
        private Quaternion previousRotation;
        private DateTime previousCameraTimestamp;
        private Texture2D captureTexture;

#if META_XR_SDK
        [SerializeField] private PassthroughCameraAccess cameraAccess;
#endif

        [Serializable]
        public struct CapturedFrame
        {
            public int frameIndex;
            public string cameraTimestamp;
            public byte[] imageData;
            public int imageWidth, imageHeight;
            public string cameraId;
            public Vector3 position;
            public Quaternion rotation;
            public float fx, fy, cx, cy;
        }

        private void Start()
        {
            StartCoroutine(InitializeCamera());
        }

        private IEnumerator InitializeCamera()
        {
#if UNITY_ANDROID && !UNITY_EDITOR && META_XR_SDK
            const string cameraPermission = "horizonos.permission.HEADSET_CAMERA";
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(cameraPermission))
            {
                UnityEngine.Android.Permission.RequestUserPermission(cameraPermission);
                float deadline = Time.realtimeSinceStartup + 30f;
                while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(cameraPermission)
                       && Time.realtimeSinceStartup < deadline)
                    yield return null;
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(cameraPermission))
                {
                    ReportError("Headset camera permission denied or timed out.");
                    yield break;
                }
            }
            if (cameraAccess == null)
                cameraAccess = FindFirstObjectByType<PassthroughCameraAccess>();
            if (cameraAccess == null)
            {
                // Configure source before enabling the component.
                var source = new GameObject("MetaScanPhysicalCamera");
                source.SetActive(false);
                cameraAccess = source.AddComponent<PassthroughCameraAccess>();
                cameraAccess.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
                cameraAccess.RequestedResolution = new Vector2Int(captureWidth, captureHeight);
                source.SetActive(true);
            }
            if (!PassthroughCameraAccess.IsSupported)
            {
                ReportError("Passthrough Camera Access is unsupported on this device.");
                yield break;
            }
            float readyDeadline = Time.realtimeSinceStartup + 30f;
            while (!cameraAccess.IsPlaying && Time.realtimeSinceStartup < readyDeadline)
                yield return null;
            IsReady = cameraAccess.IsPlaying && cameraAccess.GetTexture() != null;
            if (!IsReady)
                ReportError("Physical passthrough camera did not become ready.");
#else
            ReportError("Physical passthrough capture requires a Quest Android build with Meta XR SDK.");
            yield break;
#endif
        }

        public bool StartCapturing()
        {
            if (!IsReady)
            {
                ReportError("Physical camera is not ready.");
                return false;
            }
            IsCapturing = true;
            FrameIndex = 0;
            havePreviousPose = false;
            previousCameraTimestamp = default;
            nextCaptureTime = 0f;
            return true;
        }

        public void StopCapturing() => IsCapturing = false;

        private void Update()
        {
            if (!IsCapturing || Time.unscaledTime < nextCaptureTime)
                return;
#if META_XR_SDK
            if (cameraAccess == null || !cameraAccess.IsPlaying || !cameraAccess.IsUpdatedThisFrame)
                return;

            DateTime timestamp = cameraAccess.Timestamp;
            if (timestamp == previousCameraTimestamp)
                return;
            Pose pose = cameraAccess.GetCameraPose();
            if (havePreviousPose &&
                (Vector3.Distance(previousPosition, pose.position) > maximumStepMetres ||
                 Quaternion.Angle(previousRotation, pose.rotation) > maximumStepDegrees))
            {
                StopCapturing();
                ReportError("Physical camera pose jumped; tracking may have reset. Start a new scan.");
                return;
            }
            if (havePreviousPose
                && Vector3.Distance(previousPosition, pose.position) < minimumTranslation
                && Quaternion.Angle(previousRotation, pose.rotation) < minimumRotationDegrees)
                return;
            var resolution = cameraAccess.CurrentResolution;
            if (resolution.x < 16 || resolution.y < 16)
                return;

            // The SDK viewport uses a bottom-left origin; JPEG/OpenCV use top-left.
            // Derive the effective pinhole intrinsics from SDK rays in camera space.
            if (!TryGetTopLeftIntrinsics(pose, resolution, out float fx, out float fy, out float cx, out float cy))
            {
                ReportError("PCA calibration could not be resolved.");
                StopCapturing();
                return;
            }

            // GetColors is the documented current-image CPU API. GetTexture +
            // Graphics.Blit can return the previous frame on the render thread.
            var pixels = cameraAccess.GetColors();
            if (pixels.Length != resolution.x * resolution.y)
            {
                StopCapturing();
                ReportError($"PCA pixel count mismatch: {pixels.Length} for {resolution.x}x{resolution.y}.");
                return;
            }
            if (cameraAccess.Timestamp != timestamp)
                return;
            if (captureTexture == null || captureTexture.width != resolution.x || captureTexture.height != resolution.y)
            {
                if (captureTexture != null) Destroy(captureTexture);
                captureTexture = new Texture2D(resolution.x, resolution.y, TextureFormat.RGBA32, false);
            }
            captureTexture.SetPixelData(pixels, 0);
            captureTexture.Apply(false, false);
            byte[] image = captureTexture.EncodeToJPG(jpegQuality);
            if (cameraAccess.Timestamp != timestamp || image == null || image.Length == 0)
                return;

            FocalLengthX = fx;
            FocalLengthY = fy;
            PrincipalPointX = cx;
            PrincipalPointY = cy;
            var frame = new CapturedFrame
            {
                frameIndex = FrameIndex++,
                cameraTimestamp = timestamp.ToString("O"),
                imageData = image,
                imageWidth = resolution.x,
                imageHeight = resolution.y,
                cameraId = cameraAccess.CameraPosition == PassthroughCameraAccess.CameraPositionType.Left ? "left" : "right",
                position = pose.position,
                rotation = pose.rotation,
                fx = fx, fy = fy, cx = cx, cy = cy
            };
            previousCameraTimestamp = timestamp;
            previousPosition = pose.position;
            previousRotation = pose.rotation;
            havePreviousPose = true;
            LastCaptureTime = Time.unscaledTime;
            nextCaptureTime = LastCaptureTime + captureInterval;
            OnFrameCaptured?.Invoke(frame);
#endif
        }

#if META_XR_SDK
        private bool TryGetTopLeftIntrinsics(Pose pose, Vector2Int size,
            out float fx, out float fy, out float cx, out float cy)
        {
            fx = fy = cx = cy = 0f;
            Vector3 left = Quaternion.Inverse(pose.rotation) *
                cameraAccess.ViewportPointToRay(new Vector2(0f, 0.5f), pose).direction;
            Vector3 right = Quaternion.Inverse(pose.rotation) *
                cameraAccess.ViewportPointToRay(new Vector2(1f, 0.5f), pose).direction;
            Vector3 bottom = Quaternion.Inverse(pose.rotation) *
                cameraAccess.ViewportPointToRay(new Vector2(0.5f, 0f), pose).direction;
            Vector3 top = Quaternion.Inverse(pose.rotation) *
                cameraAccess.ViewportPointToRay(new Vector2(0.5f, 1f), pose).direction;
            if (left.z <= 0 || right.z <= 0 || bottom.z <= 0 || top.z <= 0)
                return false;
            float lx = left.x / left.z, rx = right.x / right.z;
            float by = bottom.y / bottom.z, ty = top.y / top.z;
            if (rx - lx < 0.01f || ty - by < 0.01f)
                return false;
            fx = size.x / (rx - lx);
            fy = size.y / (ty - by);
            cx = -fx * lx;
            cy = fy * ty;
            return fx > 0 && fy > 0 && cx >= 0 && cy >= 0 && cx <= size.x && cy <= size.y;
        }
#endif

        public void SetCaptureRate(float fps) => captureInterval = 1f / Mathf.Max(fps, 1f);

        private void ReportError(string message)
        {
            Debug.LogError("[MetaScan-Camera] " + message);
            OnCaptureError?.Invoke(message);
        }

        private void OnDestroy()
        {
            if (captureTexture != null) Destroy(captureTexture);
        }
    }
}
