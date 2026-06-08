using CameraFXModules;
using HarmonyLib;
using KSP.UI;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

namespace G3MagnetBoots
{
    // NOTE: This intentionally does NOT patch FlightCamera.setMode.
    //
    // A previous version rewrote a requested LOCKED -> FREE for every vessel when
    // not on a hull. Because stock SetNextMode() cycles (int)mode+1 and only wraps
    // LOCKED(4) -> AUTO(0), never letting mode become LOCKED broke the cycle: both
    // LOCKED *and* AUTO became unreachable on every vessel. The boom camera does
    // not need that interception - engagement is decided solely by ShouldOwnCamera
    // (mode == LOCKED && on hull && feature enabled) in the LateUpdate patch, so
    // stock camera modes are left completely alone here.
    public static class EvaBootsCameraHelper
    {
        internal static bool TryGetBoots(out ModuleG3MagnetBoots boots)
        {
            boots = null;

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || !v.isEVA || v.rootPart == null)
                return false;

            boots = v.rootPart.FindModuleImplementing<ModuleG3MagnetBoots>();
            return boots != null;
        }
    }

    [HarmonyPatch(typeof(FlightCamera), "LateUpdate")]
    internal static class Patch_FlightCamera_LateUpdate_EvaLockedBoom
    {
        [HarmonyPrefix]
        private static bool Prefix(FlightCamera __instance)
        {
            if (EvaLockedBoomCamera.ShouldOwnCamera(__instance))
            {
                EvaLockedBoomCamera.UpdateCamera(__instance);
                return false;
            }

            // release before stock LateUpdate so it can compute target pose and blend camera
            EvaLockedBoomCamera.ReleaseForStockWithExitTransition(__instance);
            return true;
        }

        [HarmonyPostfix]
        private static void Postfix(FlightCamera __instance)
        {
            EvaLockedBoomCamera.ApplyExitTransition(__instance);
        }
    }

    internal static class EvaLockedBoomCamera
    {
        internal static bool InCustomCameraUpdate;

        private const string BoomRootName = "G3MagnetBoots LOCKED-Mode Camera Boom Root";

        private static readonly FieldInfo DistanceField = AccessTools.Field(typeof(FlightCamera), "distance");
        private static readonly FieldInfo OffsetPitchField = AccessTools.Field(typeof(FlightCamera), "offsetPitch");
        private static readonly FieldInfo OffsetHdgField = AccessTools.Field(typeof(FlightCamera), "offsetHdg");
        private static readonly FieldInfo TIRPitchField = AccessTools.Field(typeof(FlightCamera), "tIRpitch");
        private static readonly FieldInfo TIRYawField = AccessTools.Field(typeof(FlightCamera), "tIRyaw");
        private static readonly FieldInfo TIRRollField = AccessTools.Field(typeof(FlightCamera), "tIRroll");
        private static readonly FieldInfo LastLocalPitchField = AccessTools.Field(typeof(FlightCamera), "lastLocalPitch");
        private static readonly FieldInfo LocalPitchField = AccessTools.Field(typeof(FlightCamera), "localPitch");
        private static readonly FieldInfo EndPitchField = AccessTools.Field(typeof(FlightCamera), "endPitch");

        private static readonly MethodInfo TrackIRIsActive = AccessTools.Method(typeof(FlightCamera), "TrackIRisActive");

        private static Transform _boom;
        private static FlightCamera _owner;
        private static Vessel _ownerVessel;
        private static bool _active;
        private static bool _savedUpdateActive;
        private static Transform _savedPivotParent;
        private static Transform _savedCameraParent;

        private static Transform _referenceTransform;
        private static Quaternion _lastReferenceRotation = Quaternion.identity;
        private static bool _lastReferenceRotationValid;

        private static Quaternion _boomFrame = Quaternion.identity;
        private static bool _boomFrameValid;
        private static Vector3 _lastUp = Vector3.up;

        private static bool _entryTransitionActive;
        private static float _entryTransitionT;
        private static Vector3 _entryCameraPos;
        private static Quaternion _entryCameraRot;

        private static bool _exitTransitionActive;
        private static float _exitTransitionT;
        private static Vector3 _exitCameraPos;
        private static Quaternion _exitCameraRot;

        private const float BOOM_ENTRY_TRANSITION_TIME = 0.35f;
        private const float BOOM_EXIT_TRANSITION_TIME = 0.35f;

        private const float FRAME_SMOOTH_TAU_BASE = 0.08f;
        private const float FRAME_SMOOTH_TAU_FAST = 0.025f;
        private const float SNAP_ANGLE = 175f;
        private const float ADAPTIVE_ANGLE_MIN = 4f;
        private const float ADAPTIVE_ANGLE_MAX = 90f;

        private const float SEAM_FRAME_SMOOTH_TAU_BASE = 0.18f;
        private const float SEAM_FRAME_SMOOTH_TAU_FAST = 0.075f;

        internal static bool ShouldOwnCamera(FlightCamera cam)
        {
            if (cam == null)
                return false;

            Vessel activeVessel = FlightGlobals.ActiveVessel;

            if (_active && _owner == cam && _ownerVessel != null && activeVessel != _ownerVessel)
                return false;

            if (!G3MagnetBoots.lockedCameraModeEnabled)
                return false;

            if (cam.mode != FlightCamera.Modes.LOCKED)
                return false;

            ModuleG3MagnetBoots boots;
            return EvaBootsCameraHelper.TryGetBoots(out boots) && boots.IsOnHull;
        }

        internal static void UpdateCamera(FlightCamera cam)
        {
            InCustomCameraUpdate = true;
            try
            {
                ModuleG3MagnetBoots boots;
                if (!EvaBootsCameraHelper.TryGetBoots(out boots) || !boots.IsOnHull)
                {
                    ReleaseIfActive(cam);
                    return;
                }

                ActivateIfNeeded(cam, boots);

                if (!_active || _owner != cam)
                    return;

                if (FlightDriver.Pause && UIMasterController.Instance != null && UIMasterController.Instance.IsUIShowing)
                    return;

                UpdateBoomTransform(cam, boots);

                Vector3 nextMove = Vector3.zero;

                if (_entryTransitionActive)
                {
                    ClearTrackIR(cam);
                }
                else if (CameraControlsUnlocked())
                {
                    ReadStockCameraInput(cam, ref nextMove);
                }
                else
                {
                    ClearTrackIR(cam);
                }

                float distance = GetDistance(cam);
                cam.camPitch -= nextMove.x;
                cam.camHdg += nextMove.y;
                SetDistance(cam, Mathf.Clamp(distance + nextMove.z, cam.minDistance, cam.maxDistance));

                UpdateCameraTransformStockAlike(cam);
                ApplyEntryTransition(cam);
            }
            finally
            {
                InCustomCameraUpdate = false;
            }
        }

        private static void ApplyEntryTransition(FlightCamera cam)
        {
            if (!_entryTransitionActive || cam == null)
                return;

            _entryTransitionT += Time.unscaledDeltaTime / BOOM_ENTRY_TRANSITION_TIME;
            float t = Mathf.Clamp01(_entryTransitionT);
            t = t * t * (3f - 2f * t);

            Vector3 targetPos = cam.transform.position;
            Quaternion targetRot = cam.transform.rotation;

            cam.transform.position = Vector3.Lerp(_entryCameraPos, targetPos, t);
            cam.transform.rotation = Quaternion.Slerp(_entryCameraRot, targetRot, t);

            if (_entryTransitionT >= 1f)
                _entryTransitionActive = false;
        }

        internal static void ReleaseForStockWithExitTransition(FlightCamera cam)
        {
            if (!_active)
                return;

            if (cam == null)
                cam = _owner != null ? _owner : FlightCamera.fetch;

            if (cam == null)
            {
                ReleaseIfActive(null);
                return;
            }

            Vector3 startPos = cam.transform.position;
            Quaternion startRot = cam.transform.rotation;

            ReleaseIfActive(cam);

            _exitCameraPos = startPos;
            _exitCameraRot = startRot;
            _exitTransitionT = 0f;
            _exitTransitionActive = true;
        }

        internal static void ApplyExitTransition(FlightCamera cam)
        {
            if (!_exitTransitionActive || cam == null)
                return;

            _exitTransitionT += Time.unscaledDeltaTime / BOOM_EXIT_TRANSITION_TIME;
            float t = Mathf.Clamp01(_exitTransitionT);
            t = t * t * (3f - 2f * t);

            Vector3 stockTargetPos = cam.transform.position;
            Quaternion stockTargetRot = cam.transform.rotation;

            cam.transform.position = Vector3.Lerp(_exitCameraPos, stockTargetPos, t);
            cam.transform.rotation = Quaternion.Slerp(_exitCameraRot, stockTargetRot, t);

            if (_exitTransitionT >= 1f)
                ClearExitTransition();
        }

        private static void ClearExitTransition()
        {
            _exitTransitionActive = false;
            _exitTransitionT = 0f;
            _exitCameraPos = Vector3.zero;
            _exitCameraRot = Quaternion.identity;
        }

        internal static void ForceReleaseToStock(Vessel preferredVessel)
        {
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null)
                return;

            Transform preferredParent = preferredVessel != null ? preferredVessel.transform : null;

            ReleaseIfActive(cam, preferredParent);
            ClearExitTransition();

            if (cam.mode == FlightCamera.Modes.LOCKED)
            {
                cam.setModeImmediate(FlightCamera.Modes.FREE);
            }
        }

        internal static void ReleaseIfActive(FlightCamera cam)
        {
            ReleaseIfActive(cam, null);
        }

        private static void ReleaseIfActive(FlightCamera cam, Transform preferredPivotParent)
        {
            if (!_active)
                return;

            if (cam == null)
                cam = _owner != null ? _owner : FlightCamera.fetch;

            if (cam == null)
            {
                ClearStateOnly();
                return;
            }

            if (_owner != null && _owner != cam)
                return;

            InCustomCameraUpdate = true;
            try
            {
                Transform pivot = null;
                try { pivot = cam.GetPivot(); }
                catch { pivot = null; }

                if (pivot != null)
                {
                    Transform restorePivotParent = preferredPivotParent != null
                        ? preferredPivotParent
                        : (_savedPivotParent != null ? _savedPivotParent : null);

                    if (restorePivotParent == null && FlightGlobals.ActiveVessel != null)
                        restorePivotParent = FlightGlobals.ActiveVessel.transform;

                    pivot.SetParent(restorePivotParent, true);

                    Transform restoreCameraParent = _savedCameraParent != null ? _savedCameraParent : pivot;
                    if (restoreCameraParent != null)
                        cam.transform.SetParent(restoreCameraParent, true);

                    try { cam.SetCamCoordsFromPosition(cam.transform.position); }
                    catch { }
                }
                else if (_savedCameraParent != null)
                {
                    cam.transform.SetParent(_savedCameraParent, true);
                }

                cam.updateActive = _savedUpdateActive;
                if (_savedUpdateActive)
                    cam.ActivateUpdate();
                else
                    cam.DeactivateUpdate();
            }
            finally
            {
                ClearStateOnly();
                InCustomCameraUpdate = false;
            }
        }

        private static void ClearStateOnly()
        {
            _active = false;
            _owner = null;
            _ownerVessel = null;
            _savedPivotParent = null;
            _savedCameraParent = null;
            _referenceTransform = null;
            _lastReferenceRotation = Quaternion.identity;
            _lastReferenceRotationValid = false;
            _boomFrame = Quaternion.identity;
            _boomFrameValid = false;
            _lastUp = Vector3.up;
            _entryTransitionActive = false;
            _entryTransitionT = 0f;
            _entryCameraPos = Vector3.zero;
            _entryCameraRot = Quaternion.identity;
        }

        private static void ActivateIfNeeded(FlightCamera cam, ModuleG3MagnetBoots boots)
        {
            if (_active && _owner == cam)
                return;

            EnsureBoom();

            Transform pivot = cam.GetPivot();
            if (pivot == null)
                return;

            ClearExitTransition();
            _entryCameraPos = cam.transform.position;
            _entryCameraRot = cam.transform.rotation;
            _entryTransitionT = 0f;
            _entryTransitionActive = true;

            _owner = cam;
            _ownerVessel = FlightGlobals.ActiveVessel;
            _savedUpdateActive = cam.updateActive;
            _savedPivotParent = pivot.parent;
            _savedCameraParent = cam.transform.parent;

            cam.DeactivateUpdate();

            Vector3 up = GetCameraUp(boots);
            Transform reference = boots.CameraLockedReferenceTransform;
            Vector3 anchor = GetCameraAnchor(boots, cam);

            _boom.SetParent(null, true);
            _boom.SetPositionAndRotation(anchor, CaptureInitialBoomFrame(cam, up, reference));
            ParentBoomToReference(reference, true);

            Vector3 fromAnchor = cam.transform.position - anchor;
            float distance = fromAnchor.magnitude;
            if (distance < cam.minDistance) distance = cam.minDistance;
            if (distance > cam.maxDistance) distance = cam.maxDistance;

            Vector3 localDir = Quaternion.Inverse(_boom.rotation) * fromAnchor;
            if (localDir.sqrMagnitude > ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD)
            {
                localDir.Normalize();
                cam.camHdg = Mathf.Atan2(-localDir.z, localDir.x) - Mathf.PI * 0.5f;
                cam.camPitch = Mathf.Atan2(localDir.y, Mathf.Sqrt(localDir.x * localDir.x + localDir.z * localDir.z));
            }

            SetDistance(cam, distance);

            pivot.SetParent(_boom, false);
            pivot.localPosition = Vector3.zero;
            cam.transform.SetParent(pivot, false);

            _active = true;
        }

        private static void EnsureBoom()
        {
            if (_boom != null)
                return;

            GameObject go = new GameObject(BoomRootName);
            UnityEngine.Object.DontDestroyOnLoad(go);
            _boom = go.transform;
        }

        private static void ParentBoomToReference(Transform reference, bool keepWorld)
        {
            if (_boom == null || _boom.parent == reference)
                return;

            _boom.SetParent(reference, keepWorld);
        }

        private static Quaternion CaptureInitialBoomFrame(FlightCamera cam, Vector3 up, Transform reference)
        {
            _referenceTransform = reference;
            _lastReferenceRotation = reference != null ? reference.rotation : Quaternion.identity;
            _lastReferenceRotationValid = reference != null;

            Vector3 fwd = Vector3.ProjectOnPlane(cam.GetPivot().forward, up);
            if (fwd.sqrMagnitude < ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD)
                fwd = Vector3.ProjectOnPlane(cam.transform.forward, up);
            if (fwd.sqrMagnitude < ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD && reference != null)
                fwd = Vector3.ProjectOnPlane(reference.forward, up);
            fwd = SafeTangentForward(fwd, up, reference);

            _boomFrame = NormalizeSafe(Quaternion.LookRotation(fwd, up));
            _boomFrameValid = true;
            _lastUp = up;
            return _boomFrame;
        }

        private static void UpdateBoomTransform(FlightCamera cam, ModuleG3MagnetBoots boots)
        {
            if (_boom == null || boots == null)
                return;

            float dt = Time.deltaTime > 0f ? Time.deltaTime : Time.fixedDeltaTime;
            Vector3 up = GetCameraUp(boots);
            Transform reference = boots.CameraLockedReferenceTransform;
            Vector3 anchor = GetCameraAnchor(boots, cam);

            Quaternion desiredWorldFrame;
            bool referenceChanged = reference != _referenceTransform;

            if (!_boomFrameValid)
            {
                desiredWorldFrame = CaptureInitialBoomFrame(cam, up, reference);
                ParentBoomToReference(reference, true);
            }
            else if (referenceChanged)
            {
                Quaternion rebasedFrame = RebaseReferencePreservingFrame(up, reference);
                desiredWorldFrame = SmoothFrame(_boom.rotation, rebasedFrame, dt, true);
                ParentBoomToReference(reference, true);
            }
            else
            {
                desiredWorldFrame = UpdateTransportedFrame(up, reference);
                desiredWorldFrame = SmoothFrame(_boom.rotation, desiredWorldFrame, dt);
            }

            _boomFrame = desiredWorldFrame;

            Vector3 actualUp = _boomFrame * Vector3.up;
            if (actualUp.sqrMagnitude > ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD)
                _lastUp = actualUp.normalized;

            if (reference != null)
            {
                ParentBoomToReference(reference, true);
                _boom.localPosition = reference.InverseTransformPoint(anchor);
                _boom.localRotation = NormalizeSafe(Quaternion.Inverse(reference.rotation) * desiredWorldFrame);
            }
            else
            {
                if (_boom.parent != null)
                    _boom.SetParent(null, true);

                _boom.SetPositionAndRotation(anchor, desiredWorldFrame);
            }
        }

        private static Quaternion RebaseReferencePreservingFrame(Vector3 up, Transform newReference)
        {
            Quaternion currentWorldFrame = _boom != null ? _boom.rotation : _boomFrame;

            Vector3 currentUp = currentWorldFrame * Vector3.up;
            if (currentUp.sqrMagnitude < ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD)
                currentUp = _lastUp;
            currentUp.Normalize();

            Quaternion upSwing = Quaternion.FromToRotation(currentUp, up);
            Vector3 fwd = upSwing * (currentWorldFrame * Vector3.forward);
            fwd = SafeTangentForward(fwd, up, newReference);

            _referenceTransform = newReference;
            _lastReferenceRotation = newReference != null ? newReference.rotation : Quaternion.identity;
            _lastReferenceRotationValid = newReference != null;
            _lastUp = up;

            return NormalizeSafe(Quaternion.LookRotation(fwd, up));
        }

        private static Quaternion UpdateTransportedFrame(Vector3 up, Transform reference)
        {
            Vector3 previousFwd = _boomFrame * Vector3.forward;
            Vector3 previousUp = _lastUp;

            Quaternion refDelta = Quaternion.identity;
            if (reference != null && reference == _referenceTransform && _lastReferenceRotationValid)
            {
                Quaternion currentRefRotation = reference.rotation;
                refDelta = NormalizeSafe(currentRefRotation * Quaternion.Inverse(_lastReferenceRotation));
                _lastReferenceRotation = currentRefRotation;
            }
            else
            {
                _lastReferenceRotation = reference != null ? reference.rotation : Quaternion.identity;
                _lastReferenceRotationValid = reference != null;
            }

            Vector3 fwdAfterHullRotation = refDelta * previousFwd;
            Vector3 upAfterHullRotation = refDelta * previousUp;

            if (upAfterHullRotation.sqrMagnitude < ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD)
                upAfterHullRotation = previousUp;
            upAfterHullRotation.Normalize();

            Quaternion upSwing = Quaternion.FromToRotation(upAfterHullRotation, up);
            Vector3 fwd = upSwing * fwdAfterHullRotation;
            fwd = SafeTangentForward(fwd, up, reference);

            _lastUp = up;
            _referenceTransform = reference;
            return NormalizeSafe(Quaternion.LookRotation(fwd, up));
        }

        private static Quaternion SmoothFrame(Quaternion current, Quaternion desired, float dt, bool seam = false)
        {
            current = NormalizeSafe(current);
            desired = NormalizeSafe(desired);

            float angleDiff = Quaternion.Angle(current, desired);

            if (!seam && angleDiff >= SNAP_ANGLE)
                return desired;

            float t = Mathf.InverseLerp(ADAPTIVE_ANGLE_MIN, ADAPTIVE_ANGLE_MAX, angleDiff);
            float baseTau = seam ? SEAM_FRAME_SMOOTH_TAU_BASE : FRAME_SMOOTH_TAU_BASE;
            float fastTau = seam ? SEAM_FRAME_SMOOTH_TAU_FAST : FRAME_SMOOTH_TAU_FAST;
            float tau = Mathf.Lerp(baseTau, fastTau, t);
            float alpha = 1f - Mathf.Exp(-dt / tau);
            return NormalizeSafe(Quaternion.Slerp(current, desired, alpha));
        }

        private static Vector3 SafeTangentForward(Vector3 fwd, Vector3 up, Transform reference)
        {
            fwd = Vector3.ProjectOnPlane(fwd, up);

            if (fwd.sqrMagnitude < ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD && _boomFrameValid)
                fwd = Vector3.ProjectOnPlane(_boomFrame * Vector3.forward, up);

            if (fwd.sqrMagnitude < ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD && reference != null)
                fwd = Vector3.ProjectOnPlane(reference.forward, up);

            if (fwd.sqrMagnitude < ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD)
                fwd = Vector3.Cross(up, Vector3.right);

            if (fwd.sqrMagnitude < ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD)
                fwd = Vector3.Cross(up, Vector3.forward);

            return fwd.normalized;
        }

        private static Vector3 GetCameraUp(ModuleG3MagnetBoots boots)
        {
            Vector3 up = boots != null ? boots.CameraLockedUp : Vector3.up;

            if (up.sqrMagnitude < ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD && boots != null && boots.Kerbal != null)
                up = boots.Kerbal.transform.up;

            if (up.sqrMagnitude < ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD)
                up = Vector3.up;

            return up.normalized;
        }

        private static Vector3 GetCameraAnchor(ModuleG3MagnetBoots boots, FlightCamera cam)
        {
            if (boots != null)
            {
                Vector3 anchor = boots.CameraLockedAnchorWorld;
                if (anchor.sqrMagnitude > ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD)
                    return anchor;

                if (boots.Part != null && boots.Part.rb != null && boots.Part.rb.transform != null)
                    return boots.Part.rb.transform.TransformPoint(boots.Part.rb.centerOfMass);

                if (boots.Kerbal != null && boots.Kerbal.transform != null)
                    return boots.Kerbal.transform.position;
            }

            if (cam != null && cam.GetPivot() != null)
                return cam.GetPivot().position;

            Vessel v = FlightGlobals.ActiveVessel;
            return v != null ? v.transform.position : Vector3.zero;
        }

        private static void ReadStockCameraInput(FlightCamera cam, ref Vector3 nextMove)
        {
            EventSystem eventSystem = EventSystem.current;
            bool pointerOverUI = eventSystem != null && eventSystem.IsPointerOverGameObject();

            float wheel = GameSettings.AXIS_MOUSEWHEEL.GetAxis();
            if (wheel != 0f && !pointerOverUI)
            {
                if (GameSettings.MODIFIER_KEY.GetKey())
                {
                    cam.FieldOfView = Mathf.Clamp(cam.FieldOfView + wheel * 5f, cam.fovMin, cam.fovMax);
                    cam.SetFoV(cam.FieldOfView);
                }
                else
                {
                    float distance = GetDistance(cam);
                    nextMove.z = Mathf.Clamp(distance * (1f - wheel), cam.minDistance, cam.maxDistance) - distance;
                }
            }

            if (GameSettings.ZOOM_IN.GetKey())
            {
                if (GameSettings.MODIFIER_KEY.GetKey())
                {
                    cam.FieldOfView = Mathf.Clamp(cam.FieldOfView + 5f * Time.unscaledDeltaTime, cam.fovMin, cam.fovMax);
                    cam.SetFoV(cam.FieldOfView);
                }
                else
                {
                    float distance = GetDistance(cam);
                    nextMove.z = Mathf.Clamp(distance / (1f + cam.zoomScaleFactor * 0.04f), cam.minDistance, cam.maxDistance) - distance;
                }
            }

            if (GameSettings.ZOOM_OUT.GetKey())
            {
                if (GameSettings.MODIFIER_KEY.GetKey())
                {
                    cam.FieldOfView = Mathf.Clamp(cam.FieldOfView - 5f * Time.unscaledDeltaTime, cam.fovMin, cam.fovMax);
                    cam.SetFoV(cam.FieldOfView);
                }
                else
                {
                    float distance = GetDistance(cam);
                    nextMove.z = Mathf.Clamp(distance * (1f + cam.zoomScaleFactor * 0.04f), cam.minDistance, cam.maxDistance) - distance;
                }
            }

            if (CameraMouseLook.GetMouseLook())
            {
                nextMove.y = Input.GetAxis("Mouse X") * cam.orbitSensitivity;
                nextMove.x = Input.GetAxis("Mouse Y") * cam.orbitSensitivity;
            }

            nextMove.y += GameSettings.AXIS_CAMERA_HDG.GetAxis() * cam.orbitSensitivity;
            nextMove.x += GameSettings.AXIS_CAMERA_PITCH.GetAxis() * cam.orbitSensitivity;

            if (Input.GetMouseButton(2))
            {
                SetOffsetHdg(cam, GetOffsetHdg(cam) + Input.GetAxis("Mouse X") * cam.orbitSensitivity * 0.5f);
                SetOffsetPitch(cam, GetOffsetPitch(cam) - Input.GetAxis("Mouse Y") * cam.orbitSensitivity * 0.5f);
            }

            if (Mouse.Middle.GetDoubleClick())
            {
                SetOffsetHdg(cam, 0f);
                SetOffsetPitch(cam, 0f);
                cam.SetDefaultFoV();
            }

            float halfFovRad = cam.mainCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            SetOffsetPitch(cam, Mathf.Clamp(GetOffsetPitch(cam), -halfFovRad, halfFovRad));
            SetOffsetHdg(cam, Mathf.Clamp(GetOffsetHdg(cam), -halfFovRad, halfFovRad));

            UpdateTrackIR(cam);

            if (GameSettings.CAMERA_ORBIT_UP.GetKey())
                nextMove.x = -1f * Time.unscaledDeltaTime;
            if (GameSettings.CAMERA_ORBIT_DOWN.GetKey())
                nextMove.x = 1f * Time.unscaledDeltaTime;
            if (GameSettings.CAMERA_ORBIT_LEFT.GetKey())
                nextMove.y = 1f * Time.unscaledDeltaTime;
            if (GameSettings.CAMERA_ORBIT_RIGHT.GetKey())
                nextMove.y = -1f * Time.unscaledDeltaTime;
        }

        private static bool CameraControlsUnlocked()
        {
            if (InputLockManager.IsUnlocked(ControlTypes.CAMERACONTROLS))
                return true;

            return FlightDriver.Pause && UIMasterController.Instance != null && !UIMasterController.Instance.IsUIShowing;
        }

        private static void UpdateCameraTransformStockAlike(FlightCamera cam)
        {
            Transform pivot = cam.GetPivot();
            if (pivot == null || _boom == null)
                return;

            float distance = GetDistance(cam);
            Vector3 upAxis = FlightGlobals.upAxis;
            cam.upAxis = upAxis;

            cam.minHeight = Mathf.Lerp(
                cam.minHeightAtMinDist,
                cam.minHeightAtMaxDist,
                Mathf.InverseLerp(cam.minDistance, cam.maxDistance, distance));

            cam.camPitch = Mathf.Min(cam.maxPitch, cam.camPitch);

            float terrainPitch = cam.minPitch;
            RaycastHit hit;
            if (Physics.Raycast(pivot.position + upAxis * distance, -upAxis, out hit, distance * 2f, 32768, QueryTriggerInteraction.Ignore))
            {
                float hitAlt = FlightGlobals.getAltitudeAtPos(hit.point);
                float pivotAlt = FlightGlobals.getAltitudeAtPos(pivot.position);
                float denom = distance * Mathf.Cos(cam.camPitch);
                if (Mathf.Abs(denom) > 0.001f)
                    terrainPitch = Mathf.Max(terrainPitch, Mathf.Atan2(hitAlt + cam.minHeight - pivotAlt, denom));
            }

            Vessel v = FlightGlobals.ActiveVessel;
            CelestialBody body = v != null ? v.mainBody : null;
            float targetAlt = body != null ? FlightGlobals.getAltitudeAtPos(pivot.position, body) : FlightGlobals.getAltitudeAtPos(pivot.position);

            float endPitch;
            if (targetAlt > PhysicsGlobals.CameraDepthToUnlock)
                endPitch = Mathf.Max(cam.camPitch, Mathf.Max(terrainPitch, Mathf.Atan2(cam.minHeight - targetAlt, distance * Mathf.Cos(cam.camPitch))));
            else
                endPitch = Mathf.Max(cam.camPitch, terrainPitch);

            SetEndPitch(cam, endPitch);

            pivot.localPosition = Vector3.zero;
            pivot.localRotation = Quaternion.AngleAxis(cam.camHdg * Mathf.Rad2Deg, Vector3.up) *
                                  Quaternion.AngleAxis(endPitch * Mathf.Rad2Deg, Vector3.right);

            if (cam.transform.parent != pivot)
                cam.transform.SetParent(pivot, false);

            Vector3 camFXPos = Vector3.back * distance;
            Quaternion camFXRot = Quaternion.LookRotation(-cam.transform.localPosition, Vector3.up);

            if (CameraFX.Instance != null && CameraFX.Instance.FX != null && CameraFX.Instance.FX.Count > 0)
            {
                camFXPos = CameraFX.Instance.FX.GetLocalPositionFX(camFXPos, 1f * GameSettings.CAMERA_FX_EXTERNAL, Views.FlightExternal);
                camFXRot = CameraFX.Instance.FX.GetLocalRotationFX(camFXRot, 0.06f * GameSettings.CAMERA_FX_EXTERNAL, Views.FlightExternal);
            }

            cam.transform.localPosition = Vector3.Lerp(cam.transform.localPosition, camFXPos, cam.sharpness * Time.unscaledDeltaTime);
            cam.transform.localRotation = camFXRot;

            if (terrainPitch > cam.minPitch && cam.camPitch < terrainPitch)
            {
                float localPitch = Mathf.Max(Mathf.Min(cam.camPitch - endPitch, 0f), -0.87964594f);
                SetLocalPitch(cam, localPitch);

                if (localPitch <= -0.87964594f)
                    cam.camPitch = GetLastLocalPitch(cam);
                else
                    SetLastLocalPitch(cam, cam.camPitch);

                cam.transform.Rotate(Vector3.right, localPitch * Mathf.Rad2Deg, Space.Self);
            }
            else
            {
                cam.camPitch = endPitch;
                SetLocalPitch(cam, 0f);
                SetLastLocalPitch(cam, 0f);
            }

            cam.transform.Rotate(pivot.up, GetOffsetHdg(cam) * Mathf.Rad2Deg, Space.World);
            cam.transform.Rotate(Vector3.right, GetOffsetPitch(cam) * Mathf.Rad2Deg, Space.Self);

            if (GameSettings.TRACKIR_ENABLED && TrackIRActive(cam))
            {
                cam.transform.Rotate(Vector3.up, GetTIRYaw(cam) * Mathf.Rad2Deg, Space.Self);
                cam.transform.Rotate(Vector3.right, GetTIRPitch(cam) * Mathf.Rad2Deg, Space.Self);
                cam.transform.Rotate(Vector3.forward, GetTIRRoll(cam) * Mathf.Rad2Deg, Space.Self);
            }
        }

        private static void UpdateTrackIR(FlightCamera cam)
        {
            if (GameSettings.TRACKIR_ENABLED && TrackIRActive(cam))
            {
                SetTIRYaw(cam, TrackIR.Instance.Yaw.GetAxis());
                SetTIRPitch(cam, TrackIR.Instance.Pitch.GetAxis());
                SetTIRRoll(cam, TrackIR.Instance.Roll.GetAxis());
            }
            else
            {
                ClearTrackIR(cam);
            }
        }

        private static bool TrackIRActive(FlightCamera cam)
        {
            if (TrackIRIsActive == null || cam == null)
                return false;

            object value = TrackIRIsActive.Invoke(cam, null);
            return value is bool && (bool)value;
        }

        private static void ClearTrackIR(FlightCamera cam)
        {
            SetTIRYaw(cam, 0f);
            SetTIRPitch(cam, 0f);
            SetTIRRoll(cam, 0f);
        }

        private static Quaternion NormalizeSafe(Quaternion q)
        {
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (mag < ModuleG3MagnetBoots.VECTOR_ZERO_THRESHOLD)
                return Quaternion.identity;

            float inv = 1f / mag;
            q.x *= inv;
            q.y *= inv;
            q.z *= inv;
            q.w *= inv;
            return q;
        }

        private static float GetDistance(FlightCamera cam)
        {
            return DistanceField != null ? (float)DistanceField.GetValue(cam) : cam.Distance;
        }

        private static void SetDistance(FlightCamera cam, float value)
        {
            value = Mathf.Clamp(value, cam.minDistance, cam.maxDistance);
            if (DistanceField != null) DistanceField.SetValue(cam, value);
            else cam.SetDistance(value);
        }

        private static float GetOffsetPitch(FlightCamera cam) { return OffsetPitchField != null ? (float)OffsetPitchField.GetValue(cam) : 0f; }
        private static float GetOffsetHdg(FlightCamera cam) { return OffsetHdgField != null ? (float)OffsetHdgField.GetValue(cam) : 0f; }
        private static void SetOffsetPitch(FlightCamera cam, float value) { if (OffsetPitchField != null) OffsetPitchField.SetValue(cam, value); }
        private static void SetOffsetHdg(FlightCamera cam, float value) { if (OffsetHdgField != null) OffsetHdgField.SetValue(cam, value); }

        private static float GetTIRPitch(FlightCamera cam) { return TIRPitchField != null ? (float)TIRPitchField.GetValue(cam) : 0f; }
        private static float GetTIRYaw(FlightCamera cam) { return TIRYawField != null ? (float)TIRYawField.GetValue(cam) : 0f; }
        private static float GetTIRRoll(FlightCamera cam) { return TIRRollField != null ? (float)TIRRollField.GetValue(cam) : 0f; }
        private static void SetTIRPitch(FlightCamera cam, float value) { if (TIRPitchField != null) TIRPitchField.SetValue(cam, value); }
        private static void SetTIRYaw(FlightCamera cam, float value) { if (TIRYawField != null) TIRYawField.SetValue(cam, value); }
        private static void SetTIRRoll(FlightCamera cam, float value) { if (TIRRollField != null) TIRRollField.SetValue(cam, value); }

        private static float GetLastLocalPitch(FlightCamera cam) { return LastLocalPitchField != null ? (float)LastLocalPitchField.GetValue(cam) : 0f; }
        private static void SetLastLocalPitch(FlightCamera cam, float value) { if (LastLocalPitchField != null) LastLocalPitchField.SetValue(cam, value); }
        private static void SetLocalPitch(FlightCamera cam, float value) { if (LocalPitchField != null) LocalPitchField.SetValue(cam, value); }
        private static void SetEndPitch(FlightCamera cam, float value) { if (EndPitchField != null) EndPitchField.SetValue(cam, value); }
    }
}