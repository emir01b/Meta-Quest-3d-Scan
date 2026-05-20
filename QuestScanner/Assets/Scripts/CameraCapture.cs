/*
 * MetaScan — Camera Capture
 * Captures passthrough camera images from Meta Quest 3S.
 * Uses MRUK PassthroughCameraAccess for direct Passthrough capture.
 * Falls back to WebCamTexture or RenderTexture.
 */

using System;
using UnityEngine;

#if META_XR_SDK
using Meta.XR;
#endif

namespace MetaScan
{
    public class CameraCapture : MonoBehaviour
    {
        [Header("Capture Settings")]
        [SerializeField] private int captureWidth = 1280;
        [SerializeField] private int captureHeight = 960;
        [SerializeField] private int jpegQuality = 85;
        [SerializeField] private float captureInterval = 0.1f; // 10 fps default

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

#if META_XR_SDK
        // Official Meta Passthrough Camera Access API
        private PassthroughCameraAccess mrukCameraAccess;
#endif

        // WebCamTexture fallback for real-world capture
        private WebCamTexture webcamTexture;
        private bool useWebCamCapture = false;

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

            // Create capture texture for fallback
            captureRT = new RenderTexture(captureWidth, captureHeight, 24);
            captureTexture = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);

            StartCoroutine(InitializeCameraRoutine());
        }

        private System.Collections.IEnumerator InitializeCameraRoutine()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            string camPermission = UnityEngine.Android.Permission.Camera;
            
            // Request permissions if not already granted
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(camPermission))
            {
                UnityEngine.Android.Permission.RequestUserPermission(camPermission);
                
                // Wait until the user either grants or denies the permission
                float timeout = Time.time + 30f; // 30 seconds timeout
                while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(camPermission))
                {
                    if (Time.time > timeout)
                    {
                        Debug.LogWarning("[MetaScan-Camera] Permission request timed out.");
                        yield break;
                    }
                    yield return null;
                }
            }

            // Wait a little bit extra to ensure OS is ready
            yield return new WaitForSeconds(0.5f);

#if META_XR_SDK
            // Try to initialize MRUK PassthroughCameraAccess
            mrukCameraAccess = FindFirstObjectByType<PassthroughCameraAccess>();
            if (mrukCameraAccess == null)
            {
                Debug.Log("[MetaScan-Camera] PassthroughCameraAccess not found in scene. Creating one...");
                mrukCameraAccess = gameObject.AddComponent<PassthroughCameraAccess>();
                yield return new WaitForSeconds(0.5f); // Wait for initialization
            }

            if (mrukCameraAccess != null)
            {
                Debug.Log("[MetaScan-Camera] Official MRUK PassthroughCameraAccess enabled.");
            }
#endif

            // Fallback: WebCamTexture
            if (WebCamTexture.devices.Length > 0)
            {
                webcamTexture = new WebCamTexture(WebCamTexture.devices[0].name, captureWidth, captureHeight, 30);
                
                // Force WebCamTexture to update by applying it to a dummy renderer
                GameObject dummyObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
                dummyObj.name = "WebCamDummyRenderer";
                dummyObj.transform.SetParent(this.transform);
                // Hide it behind the camera or make it very small/disabled visual but active object
                dummyObj.transform.localPosition = new Vector3(0, -1000, 0); 
                dummyObj.transform.localScale = Vector3.zero;
                MeshRenderer renderer = dummyObj.GetComponent<MeshRenderer>();
                renderer.material.mainTexture = webcamTexture;

                webcamTexture.Play();
                useWebCamCapture = true;
                Debug.Log("[MetaScan-Camera] WebCamTexture fallback initialized on: " + WebCamTexture.devices[0].name);
            }
            else
            {
                Debug.LogWarning("[MetaScan-Camera] No WebCam devices found.");
            }
#else
            yield return null;
#endif
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

            if (useWebCamCapture && webcamTexture != null && !webcamTexture.isPlaying)
            {
                webcamTexture.Play();
            }

            Debug.Log("[MetaScan-Camera] Capture started.");
        }

        public void StopCapturing()
        {
            IsCapturing = false;
            if (useWebCamCapture && webcamTexture != null)
            {
                webcamTexture.Stop();
            }
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
            // 1. Try Official MRUK Passthrough Camera Access API (Meta Quest 3/3S V74+)
            if (mrukCameraAccess != null)
            {
                Texture ptTexture = mrukCameraAccess.GetTexture();
                if (ptTexture != null && ptTexture.width > 16)
                {
                    // Copy to avoid modifying the original buffer directly and support encoding
                    Texture2D texCopy = new Texture2D(ptTexture.width, ptTexture.height, TextureFormat.RGB24, false);
                    
                    // Safely convert any Texture to Texture2D using a temporary RenderTexture
                    RenderTexture prevRT2 = RenderTexture.active;
                    RenderTexture tempRT = RenderTexture.GetTemporary(ptTexture.width, ptTexture.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Default);
                    Graphics.Blit(ptTexture, tempRT);
                    RenderTexture.active = tempRT;
                    texCopy.ReadPixels(new Rect(0, 0, tempRT.width, tempRT.height), 0, 0);
                    texCopy.Apply();
                    RenderTexture.active = prevRT2;
                    RenderTexture.ReleaseTemporary(tempRT);

                    byte[] data = texCopy.EncodeToJPG(jpegQuality);
                    Destroy(texCopy);
                    return data;
                }
            }
#endif

            // 2. Try WebCamTexture API (Fallback for standard Android or earlier SDK)
            if (useWebCamCapture && webcamTexture != null && webcamTexture.isPlaying && webcamTexture.width > 16)
            {
                Texture2D tempTex = new Texture2D(webcamTexture.width, webcamTexture.height, TextureFormat.RGB24, false);
                tempTex.SetPixels(webcamTexture.GetPixels());
                tempTex.Apply();
                byte[] jpegData = tempTex.EncodeToJPG(jpegQuality);
                Destroy(tempTex);
                return jpegData;
            }

            // 3. Fallback: capture screen via RenderTexture (Unity UI + Camera Render - NO REAL WORLD)
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

            return captureTexture.EncodeToJPG(jpegQuality);
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
