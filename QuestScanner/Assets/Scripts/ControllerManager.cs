/*
 * MetaScan — Controller Manager
 * Manages left/right controller input, button events, and haptic feedback.
 * Uses #if META_XR_SDK for OVR dependencies.
 */

using System;
using UnityEngine;

namespace MetaScan
{
    public class ControllerManager : MonoBehaviour
    {
        // Hand anchor references (set by SceneBootstrapper or found at runtime)
        [Header("References")]
        [SerializeField] private Transform leftHandAnchor;
        [SerializeField] private Transform rightHandAnchor;

        // Public accessors
        public Transform LeftHandAnchor => leftHandAnchor;
        public Transform RightHandAnchor => rightHandAnchor;

        // Controller state
        public Vector3 LeftPosition { get; private set; }
        public Quaternion LeftRotation { get; private set; }
        public Vector3 RightPosition { get; private set; }
        public Quaternion RightRotation { get; private set; }

        // Button events
        public event Action OnLeftGripDown;
        public event Action OnLeftGripUp;
        public event Action OnLeftTriggerDown;
        public event Action OnLeftTriggerUp;
        public event Action OnLeftPrimaryButtonDown;
        public event Action OnLeftSecondaryButtonDown;

        public event Action OnRightGripDown;
        public event Action OnRightGripUp;
        public event Action OnRightTriggerDown;
        public event Action OnRightTriggerUp;
        public event Action OnRightPrimaryButtonDown;
        public event Action OnRightSecondaryButtonDown;

        // Grip hold state
        public bool IsLeftGripHeld { get; private set; }
        public bool IsRightGripHeld { get; private set; }
        public bool IsRightTriggerHeld { get; private set; }
        public bool IsLeftTriggerHeld { get; private set; }

        private void Start()
        {
            FindAnchors();
        }

        private void FindAnchors()
        {
#if META_XR_SDK
            if (leftHandAnchor == null || rightHandAnchor == null)
            {
                OVRCameraRig cameraRig = FindFirstObjectByType<OVRCameraRig>();
                if (cameraRig != null)
                {
                    if (leftHandAnchor == null)
                        leftHandAnchor = cameraRig.leftHandAnchor;
                    if (rightHandAnchor == null)
                        rightHandAnchor = cameraRig.rightHandAnchor;
                }
            }
#endif

            if (leftHandAnchor == null || rightHandAnchor == null)
            {
                // Editor fallback — create dummy anchors
                Camera cam = Camera.main;
                if (cam != null)
                {
                    if (leftHandAnchor == null)
                    {
                        GameObject leftObj = new GameObject("FallbackLeftHand");
                        leftObj.transform.SetParent(cam.transform, false);
                        leftObj.transform.localPosition = new Vector3(-0.3f, -0.3f, 0.5f);
                        leftHandAnchor = leftObj.transform;
                    }
                    if (rightHandAnchor == null)
                    {
                        GameObject rightObj = new GameObject("FallbackRightHand");
                        rightObj.transform.SetParent(cam.transform, false);
                        rightObj.transform.localPosition = new Vector3(0.3f, -0.3f, 0.5f);
                        rightHandAnchor = rightObj.transform;
                    }
                }
            }

            Debug.Log("[MetaScan-Controller] Anchors initialized: " +
                $"Left={leftHandAnchor != null}, Right={rightHandAnchor != null}");
        }

        private void Update()
        {
            UpdateTransforms();
            UpdateInput();
        }

        private void UpdateTransforms()
        {
            if (leftHandAnchor != null)
            {
                LeftPosition = leftHandAnchor.position;
                LeftRotation = leftHandAnchor.rotation;
            }
            if (rightHandAnchor != null)
            {
                RightPosition = rightHandAnchor.position;
                RightRotation = rightHandAnchor.rotation;
            }
        }

        private void UpdateInput()
        {
#if META_XR_SDK
            // === Left Controller ===

            // Grip (hold detection)
            if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch))
            {
                IsLeftGripHeld = true;
                OnLeftGripDown?.Invoke();
            }
            if (OVRInput.GetUp(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch))
            {
                IsLeftGripHeld = false;
                OnLeftGripUp?.Invoke();
            }

            // Trigger
            if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch))
            {
                IsLeftTriggerHeld = true;
                OnLeftTriggerDown?.Invoke();
            }
            if (OVRInput.GetUp(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch))
            {
                IsLeftTriggerHeld = false;
                OnLeftTriggerUp?.Invoke();
            }

            // Buttons
            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch))
                OnLeftPrimaryButtonDown?.Invoke();
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch))
                OnLeftSecondaryButtonDown?.Invoke();

            // === Right Controller ===

            // Grip
            if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch))
            {
                IsRightGripHeld = true;
                OnRightGripDown?.Invoke();
            }
            if (OVRInput.GetUp(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch))
            {
                IsRightGripHeld = false;
                OnRightGripUp?.Invoke();
            }

            // Trigger
            if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
            {
                IsRightTriggerHeld = true;
                OnRightTriggerDown?.Invoke();
            }
            if (OVRInput.GetUp(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
            {
                IsRightTriggerHeld = false;
                OnRightTriggerUp?.Invoke();
            }

            // Buttons
            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
                OnRightPrimaryButtonDown?.Invoke();
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch))
                OnRightSecondaryButtonDown?.Invoke();
#else
            // Editor fallback: keyboard input
            if (Input.GetKeyDown(KeyCode.G))
            {
                IsLeftGripHeld = !IsLeftGripHeld;
                if (IsLeftGripHeld) OnLeftGripDown?.Invoke();
                else OnLeftGripUp?.Invoke();
            }
            if (Input.GetMouseButtonDown(0))
            {
                IsRightTriggerHeld = true;
                OnRightTriggerDown?.Invoke();
            }
            if (Input.GetMouseButtonUp(0))
            {
                IsRightTriggerHeld = false;
                OnRightTriggerUp?.Invoke();
            }
#endif
        }

        /// <summary>
        /// Send haptic feedback to a controller.
        /// </summary>
        public void SendHaptic(bool isLeft, float frequency = 0.5f, float amplitude = 0.5f, float duration = 0.1f)
        {
#if META_XR_SDK
            OVRInput.Controller ctrl = isLeft
                ? OVRInput.Controller.LTouch
                : OVRInput.Controller.RTouch;
            OVRInput.SetControllerVibration(frequency, amplitude, ctrl);

            // Stop vibration after duration
            if (duration > 0)
            {
                StartCoroutine(StopHapticAfter(ctrl, duration));
            }
#endif
        }

#if META_XR_SDK
        private System.Collections.IEnumerator StopHapticAfter(OVRInput.Controller ctrl, float delay)
        {
            yield return new WaitForSeconds(delay);
            OVRInput.SetControllerVibration(0, 0, ctrl);
        }
#endif

        /// <summary>
        /// Get the forward ray from a controller.
        /// </summary>
        public Ray GetControllerRay(bool isLeft)
        {
            Transform anchor = isLeft ? leftHandAnchor : rightHandAnchor;
            if (anchor != null)
                return new Ray(anchor.position, anchor.forward);
            return new Ray(Vector3.zero, Vector3.forward);
        }
    }
}
