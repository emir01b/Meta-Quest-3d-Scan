/*
 * Meta3D Scanner - Controller Manager
 * Manages left and right Quest controller input and state.
 * Provides events for button presses and tracking data.
 * 
 * Left controller = UI interaction
 * Right controller = Scanning
 */

using System;
using UnityEngine;

namespace Meta3DScanner
{
    public class ControllerManager : MonoBehaviour
    {
        [Header("References (Auto-found if empty)")]
        [SerializeField] private OVRCameraRig cameraRig;

        // Controller anchors from OVRCameraRig
        private Transform leftHandAnchor;
        private Transform rightHandAnchor;
        private Transform leftControllerAnchor;
        private Transform rightControllerAnchor;

        // Controller state
        private bool leftControllerConnected;
        private bool rightControllerConnected;

        // Events - Right Controller (Scanning)
        public event Action OnRightTriggerDown;
        public event Action OnRightTriggerUp;
        public event Action OnRightGripDown;
        public event Action OnRightGripUp;
        public event Action OnRightPrimaryButtonDown;   // A button
        public event Action OnRightSecondaryButtonDown;  // B button

        // Events - Left Controller (UI)
        public event Action OnLeftTriggerDown;
        public event Action OnLeftTriggerUp;
        public event Action OnLeftPrimaryButtonDown;    // X button
        public event Action OnLeftSecondaryButtonDown;   // Y button

        // Public accessors
        public Transform LeftHandAnchor => leftHandAnchor;
        public Transform RightHandAnchor => rightHandAnchor;
        public Transform LeftControllerAnchor => leftControllerAnchor;
        public Transform RightControllerAnchor => rightControllerAnchor;
        public bool IsLeftConnected => leftControllerConnected;
        public bool IsRightConnected => rightControllerConnected;

        private void Start()
        {
            FindCameraRig();
        }

        private void FindCameraRig()
        {
            if (cameraRig == null)
            {
                cameraRig = FindObjectOfType<OVRCameraRig>();
            }

            if (cameraRig == null)
            {
                Debug.LogError("[Meta3D-Controller] OVRCameraRig not found!");
                return;
            }

            // Get anchors from OVRCameraRig hierarchy
            leftHandAnchor = cameraRig.leftHandAnchor;
            rightHandAnchor = cameraRig.rightHandAnchor;
            leftControllerAnchor = cameraRig.leftControllerAnchor;
            rightControllerAnchor = cameraRig.rightControllerAnchor;

            Debug.Log("[Meta3D-Controller] Controller anchors initialized");
        }

        private void Update()
        {
            // Check controller connection
            leftControllerConnected = OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
            rightControllerConnected = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);

            // Process right controller input (scanning)
            ProcessRightController();

            // Process left controller input (UI)
            ProcessLeftController();
        }

        private void ProcessRightController()
        {
            if (!rightControllerConnected) return;

            // Right Index Trigger
            if (OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger))
            {
                OnRightTriggerDown?.Invoke();
            }
            if (OVRInput.GetUp(OVRInput.Button.SecondaryIndexTrigger))
            {
                OnRightTriggerUp?.Invoke();
            }

            // Right Hand Trigger (Grip)
            if (OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger))
            {
                OnRightGripDown?.Invoke();
            }
            if (OVRInput.GetUp(OVRInput.Button.SecondaryHandTrigger))
            {
                OnRightGripUp?.Invoke();
            }

            // A Button
            if (OVRInput.GetDown(OVRInput.Button.One))
            {
                OnRightPrimaryButtonDown?.Invoke();
            }

            // B Button
            if (OVRInput.GetDown(OVRInput.Button.Two))
            {
                OnRightSecondaryButtonDown?.Invoke();
            }
        }

        private void ProcessLeftController()
        {
            if (!leftControllerConnected) return;

            // Left Index Trigger
            if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger))
            {
                OnLeftTriggerDown?.Invoke();
            }
            if (OVRInput.GetUp(OVRInput.Button.PrimaryIndexTrigger))
            {
                OnLeftTriggerUp?.Invoke();
            }

            // X Button
            if (OVRInput.GetDown(OVRInput.Button.Three))
            {
                OnLeftPrimaryButtonDown?.Invoke();
            }

            // Y Button
            if (OVRInput.GetDown(OVRInput.Button.Four))
            {
                OnLeftSecondaryButtonDown?.Invoke();
            }
        }

        /// <summary>
        /// Get the world-space ray from the right controller (for scanning).
        /// </summary>
        public Ray GetRightControllerRay()
        {
            if (rightControllerAnchor != null)
            {
                return new Ray(rightControllerAnchor.position, rightControllerAnchor.forward);
            }
            if (rightHandAnchor != null)
            {
                return new Ray(rightHandAnchor.position, rightHandAnchor.forward);
            }
            return new Ray(Vector3.zero, Vector3.forward);
        }

        /// <summary>
        /// Get the world-space ray from the left controller (for UI interaction).
        /// </summary>
        public Ray GetLeftControllerRay()
        {
            if (leftControllerAnchor != null)
            {
                return new Ray(leftControllerAnchor.position, leftControllerAnchor.forward);
            }
            if (leftHandAnchor != null)
            {
                return new Ray(leftHandAnchor.position, leftHandAnchor.forward);
            }
            return new Ray(Vector3.zero, Vector3.forward);
        }

        /// <summary>
        /// Get the right controller velocity (useful for motion blur detection).
        /// </summary>
        public Vector3 GetRightControllerVelocity()
        {
            return OVRInput.GetLocalControllerVelocity(OVRInput.Controller.RTouch);
        }

        /// <summary>
        /// Get the right controller angular velocity.
        /// </summary>
        public Vector3 GetRightControllerAngularVelocity()
        {
            return OVRInput.GetLocalControllerAngularVelocity(OVRInput.Controller.RTouch);
        }

        /// <summary>
        /// Trigger haptic feedback on a controller.
        /// </summary>
        public void TriggerHaptic(OVRInput.Controller controller, float frequency, float amplitude, float duration)
        {
            OVRInput.SetControllerVibration(frequency, amplitude, controller);

            // Auto-stop haptic after duration
            if (duration > 0f)
            {
                StartCoroutine(StopHapticAfter(controller, duration));
            }
        }

        private System.Collections.IEnumerator StopHapticAfter(OVRInput.Controller controller, float delay)
        {
            yield return new WaitForSeconds(delay);
            OVRInput.SetControllerVibration(0f, 0f, controller);
        }

        /// <summary>
        /// Quick haptic pulse on right controller (scan feedback).
        /// </summary>
        public void PulseScanHaptic()
        {
            TriggerHaptic(OVRInput.Controller.RTouch, 0.5f, 0.3f, 0.05f);
        }

        /// <summary>
        /// Quick haptic pulse on left controller (UI feedback).
        /// </summary>
        public void PulseUIHaptic()
        {
            TriggerHaptic(OVRInput.Controller.LTouch, 0.3f, 0.2f, 0.03f);
        }
    }
}
