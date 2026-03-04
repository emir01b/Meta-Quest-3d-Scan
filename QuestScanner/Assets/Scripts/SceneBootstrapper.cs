/*
 * Meta3D Scanner - Scene Bootstrapper
 * Sets up the entire MR scanning scene at runtime.
 * 
 * Usage: Add this script to an empty GameObject in the scene.
 * It will create the OVRCameraRig, add all components, and wire everything together.
 * 
 * Alternatively, you can set up OVRCameraRig in the editor and just use
 * this script to add the custom scanner components.
 */

using UnityEngine;

namespace Meta3DScanner
{
    public class SceneBootstrapper : MonoBehaviour
    {
        [Header("Setup Mode")]
        [Tooltip("If true, creates OVRCameraRig from prefab. If false, expects it to already be in scene.")]
        [SerializeField] private bool createOVRCameraRig = false;

        [Header("References (Auto-assigned if empty)")]
        [SerializeField] private OVRCameraRig cameraRig;

        [Header("Component Settings")]
        [SerializeField] private string defaultServerIP = "192.168.1.100";
        [SerializeField] private int defaultServerPort = 8765;
        [SerializeField] private int targetFrames = 200;
        [SerializeField] private int minFramesRequired = 30;

        // Created components
        private MRSetup mrSetup;
        private ControllerManager controllerManager;
        private HandUIManager handUIManager;
        private ScanPointer scanPointer;
        private PointCloudVisualizer pointCloudVisualizer;
        private ScanManager scanManager;
        private CameraCapture cameraCapture;
        private DepthCapture depthCapture;
        private DataStreamer dataStreamer;

        private void Awake()
        {
            Debug.Log("[Meta3D-Bootstrap] Starting scene setup...");

            // Step 1: Ensure OVRCameraRig exists
            EnsureCameraRig();

            // Step 2: Add MR Setup (passthrough)
            SetupMR();

            // Step 3: Add controller management
            SetupControllers();

            // Step 4: Add scanning components
            SetupScanner();

            // Step 5: Add UI
            SetupUI();

            // Step 6: Wire everything together
            WireComponents();

            Debug.Log("[Meta3D-Bootstrap] Scene setup complete!");
            LogSetupInfo();
        }

        private void EnsureCameraRig()
        {
            if (cameraRig == null)
            {
                cameraRig = FindObjectOfType<OVRCameraRig>();
            }

            if (cameraRig == null && createOVRCameraRig)
            {
                // Try to instantiate OVRCameraRig from Resources
                GameObject rigPrefab = Resources.Load<GameObject>("OVRCameraRig");
                if (rigPrefab != null)
                {
                    GameObject rigObj = Instantiate(rigPrefab);
                    rigObj.name = "OVRCameraRig";
                    cameraRig = rigObj.GetComponent<OVRCameraRig>();
                }
                else
                {
                    // Create minimal camera rig manually
                    Debug.LogWarning("[Meta3D-Bootstrap] OVRCameraRig prefab not found in Resources. " +
                        "Please add the OVRCameraRig prefab to your scene manually.");
                }
            }

            if (cameraRig == null)
            {
                Debug.LogError("[Meta3D-Bootstrap] OVRCameraRig not found! " +
                    "Add the OVRCameraRig prefab from Meta XR SDK to your scene.");
                return;
            }

            // Remove any default Main Camera that's not part of OVRCameraRig
            Camera[] allCameras = FindObjectsOfType<Camera>();
            foreach (Camera cam in allCameras)
            {
                if (cam.gameObject.name == "Main Camera" &&
                    cam.transform.parent == null)
                {
                    Debug.Log("[Meta3D-Bootstrap] Removing default Main Camera");
                    Destroy(cam.gameObject);
                }
            }
        }

        private void SetupMR()
        {
            if (cameraRig == null) return;

            // Add MRSetup to the CameraRig
            mrSetup = cameraRig.GetComponent<MRSetup>();
            if (mrSetup == null)
            {
                mrSetup = cameraRig.gameObject.AddComponent<MRSetup>();
            }

            Debug.Log("[Meta3D-Bootstrap] MR Setup added");
        }

        private void SetupControllers()
        {
            if (cameraRig == null) return;

            // Add ControllerManager to the CameraRig
            controllerManager = cameraRig.GetComponent<ControllerManager>();
            if (controllerManager == null)
            {
                controllerManager = cameraRig.gameObject.AddComponent<ControllerManager>();
            }

            Debug.Log("[Meta3D-Bootstrap] Controller manager added");
        }

        private void SetupScanner()
        {
            // Create a scanner manager object
            GameObject scannerObj = new GameObject("Meta3D_Scanner");

            // Core scanning components
            cameraCapture = scannerObj.AddComponent<CameraCapture>();
            depthCapture = scannerObj.AddComponent<DepthCapture>();
            dataStreamer = scannerObj.AddComponent<DataStreamer>();
            scanManager = scannerObj.AddComponent<ScanManager>();

            // Point cloud visualizer
            pointCloudVisualizer = scannerObj.AddComponent<PointCloudVisualizer>();

            // Scan pointer (right controller ray)
            GameObject pointerObj = new GameObject("ScanPointer");
            pointerObj.transform.SetParent(scannerObj.transform);
            scanPointer = pointerObj.AddComponent<ScanPointer>();

            Debug.Log("[Meta3D-Bootstrap] Scanner components added");
        }

        private void SetupUI()
        {
            // Create HandUIManager on a separate object
            GameObject uiManagerObj = new GameObject("Meta3D_HandUI");
            handUIManager = uiManagerObj.AddComponent<HandUIManager>();

            Debug.Log("[Meta3D-Bootstrap] Hand UI manager added");
        }

        private void WireComponents()
        {
            // The ScanManager needs to know about all components
            // It will find them via FindObjectOfType in its Start() method

            Debug.Log("[Meta3D-Bootstrap] Components wired together");
        }

        private void LogSetupInfo()
        {
            Debug.Log("==================================================");
            Debug.Log("[Meta3D-Bootstrap] Setup Summary:");
            Debug.Log($"  OVRCameraRig: {(cameraRig != null ? "OK" : "MISSING")}");
            Debug.Log($"  MR Setup: {(mrSetup != null ? "OK" : "MISSING")}");
            Debug.Log($"  Controller Manager: {(controllerManager != null ? "OK" : "MISSING")}");
            Debug.Log($"  Camera Capture: {(cameraCapture != null ? "OK" : "MISSING")}");
            Debug.Log($"  Depth Capture: {(depthCapture != null ? "OK" : "MISSING")}");
            Debug.Log($"  Data Streamer: {(dataStreamer != null ? "OK" : "MISSING")}");
            Debug.Log($"  Scan Manager: {(scanManager != null ? "OK" : "MISSING")}");
            Debug.Log($"  Hand UI: {(handUIManager != null ? "OK" : "MISSING")}");
            Debug.Log($"  Scan Pointer: {(scanPointer != null ? "OK" : "MISSING")}");
            Debug.Log($"  Point Cloud: {(pointCloudVisualizer != null ? "OK" : "MISSING")}");
            Debug.Log("==================================================");
            Debug.Log("[Meta3D-Bootstrap] IMPORTANT: Ensure you have the following in your Unity project:");
            Debug.Log("  1. Meta XR Core SDK (or All-in-One SDK) imported");
            Debug.Log("  2. OVRCameraRig prefab in scene (or Resources folder)");
            Debug.Log("  3. Build target set to Android");
            Debug.Log("  4. XR Plug-in Management → Oculus enabled");
            Debug.Log("  5. Passthrough Support set to 'Required' in OVRManager");
            Debug.Log("==================================================");
        }
    }
}
