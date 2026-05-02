/*
 * MetaScan — Scene Bootstrapper
 * Creates the entire scene hierarchy at runtime.
 * Only requires OVRCameraRig prefab in the scene + this script.
 */

using UnityEngine;

namespace MetaScan
{
    public class SceneBootstrapper : MonoBehaviour
    {
        [Header("Prefab (optional — will be found at runtime if in scene)")]
        [SerializeField] private GameObject ovrCameraRigPrefab;

        private void Awake()
        {
            Debug.Log("[MetaScan-Boot] Bootstrapping scene...");
            BootstrapScene();
        }

        private void BootstrapScene()
        {
            // 1. Ensure OVRCameraRig exists
#if META_XR_SDK
            OVRCameraRig rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig == null)
            {
                if (ovrCameraRigPrefab != null)
                {
                    GameObject rigObj = Instantiate(ovrCameraRigPrefab);
                    rigObj.name = "OVRCameraRig";
                    rig = rigObj.GetComponent<OVRCameraRig>();
                }
                else
                {
                    Debug.LogError("[MetaScan-Boot] OVRCameraRig not found! " +
                        "Please add OVRCameraRig prefab to the scene.");
                }
            }
#endif

            // 2. Create ScanSystem root object
            GameObject scanSystem = new GameObject("ScanSystem");

            // 3. Add all components
            MRSetup mrSetup = scanSystem.AddComponent<MRSetup>();
            ControllerManager controllerMgr = scanSystem.AddComponent<ControllerManager>();
            HandUIManager handUI = scanSystem.AddComponent<HandUIManager>();
            ObjectSelector objSelector = scanSystem.AddComponent<ObjectSelector>();
            ScanPointer scanPtr = scanSystem.AddComponent<ScanPointer>();
            CameraCapture camCapture = scanSystem.AddComponent<CameraCapture>();
            DepthCapture depthCapture = scanSystem.AddComponent<DepthCapture>();
            DataStreamer streamer = scanSystem.AddComponent<DataStreamer>();
            PointCloudVisualizer visualizer = scanSystem.AddComponent<PointCloudVisualizer>();
            VRUIPointer vrPointer = scanSystem.AddComponent<VRUIPointer>();
            ScanManager scanMgr = scanSystem.AddComponent<ScanManager>();

            Debug.Log("[MetaScan-Boot] Scene bootstrapped successfully — all components attached");

            // 4. Log required permissions
            LogPermissions();
        }

        private void LogPermissions()
        {
            Debug.Log("[MetaScan-Boot] Required AndroidManifest permissions:");
            Debug.Log("  - android.permission.CAMERA");
            Debug.Log("  - horizonos.permission.HEADSET_CAMERA");
            Debug.Log("  - android.permission.INTERNET");
            Debug.Log("  - com.oculus.supportedDevices: quest3|quest3s");
        }
    }
}
