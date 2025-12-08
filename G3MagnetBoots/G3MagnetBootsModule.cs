using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using static EdyCommonTools.Spline;

// use HullTargeting for spherecast utility
using static G3MagnetBoots.HullTargeting;


/*  Working:
 *  Idle vs Walking state machine no issues with insecurity
 *  Heading update and walk animations same as stock
 *  Magnetization attachment
 *  Magnetization deattachment
 *  
 */

/*  To fix:
 *  Checks for when the player can control the heading of the kerbal, and when the kerbal is auto-aligned to the surface normal, and when the kerbal is made flush with the surface normal are inconsistent, resulting in sometimes being in the Idle hull state, but not being auto-attached.
 *  Add debug visualization for raycast results, surface normal, contact point, clearance distance, and debug value readout for state changes, events fired, and relevant variables and names. For the spherecast, use a billboarded ring at the hit point with radius equal to the spherecast radius, and a line from the ray origin to the hit point.
 *  Heading is bound to camera up, making it hard to walk on non-aligned surfaces
 *  Kerbals can walk on other kerbals, angering the Kraken. Needs a check to see if the target part is a kerbal and reject it.
 *  Kerbals walking on a surface often move off of the surface when the normal changes even slightly, but the state correctly transitions to idle floating when it does.
 *  Fix jump orientation issues when jumping off a surface (implement jump hull state from other implementation)
 *  Possible issue with multiple kerbals with magnet boots on the same vessel interfering with each other, phantom forces acting on the main vessel. Sometimes results in kerbals being flung off into space but ive yet to catch it occur to an active vessel kerbal.
 *  Kerbals surface-aligned forward direction (the way they face) doesn't turn alongside the plane they are attached to, resulting in clear misaligned facing direction when the vessel beneath rotates.
 *  Reverse-engineer the stock Rigidbody Anchoring logic used to stabalize kerbals on eva when packed, timewarping, or idling without movement input.
 *  It can be difficult to attach a kerbal to a surface when getting off a ladder, though it intuitively seems like this behavior should occur, the kerbal just goes into the floating idle state inches above the surface instead of attaching. Consider masking parts we just dismounted from like the ladder from the spherecast and making the cast longer for a moment after dismounting to allow easier reattachment on EVA without a Jetpack to close the gap.
 *  Kerbals can use their jetpack fully while magnetized, similarly to the stock floating idle state, but this should act like the stock grounded idle state where jetpack thrust is disabled except for upwards to lift off the surface, and doing so should weaken the magnetization to allow easy takeoff but not disable it completely yet.
 */

/*  For the future:
 *  Add live-toggle for the magnet boots in the PAW
 *  KSP2-style time-smoothed spherecasts and orientation changes with SMA Simple Moving Average filter to reduce jitter, consider a 2nd spherecast from a predicted future position to smooth major changes in surface normal by early detection and interpolation.
 *  Consider altering the hull jump behavior for a more useful disconnection motion, either weaken the stock jumping force, or remove the force and simply turn the magnets off for a moment, or turning them off and offsetting the kerbal away from the surface normal via a force + dampening force to negate quickly once offset enough.
 *  Consider adding a "let go" input and screen message like when on a ladder which either does the same as jumping off, or another useful behavior.
 *  Consider adding sound effects for magnet engage/disengage, walking on hull, jumping off hull.
 */



namespace G3MagnetBoots
{
    public class G3MagnetBootsModule : PartModule, IModuleInfo
    {
        public string GetModuleTitle() { return "EVA Magnetic Boots Module"; }
        public override string GetInfo() { return "Lorem ipsum dolor sit amet consequitor"; }
        public Callback<Rect> GetDrawModulePanelCallback() { return null; }
        public string GetPrimaryField() { return "Lorem ipsum"; }

        public KerbalEVA Kerbal { get; private set; }
        public KerbalFSM FSM { get { return Kerbal.fsm; } }
        public Part Part { get { return Kerbal.part; } }

        private static G3MagnetBoots Config => G3MagnetBoots.Instance;

        // Custom FSM States & FSM Events
        private KFSMState st_idle_hull;
        private KFSMEvent On_attachToHull; // st_idle_fl -> st_idle_hull
        private KFSMEvent On_detachFromHull; // st_idle_hull -> st_idle_fl
        private KFSMEvent On_letGo; // st_idle_hull OR st_walk_hull -> st_idle_fl

        private KFSMState st_walk_hull;
        private KFSMEvent On_MoveHull; // st_idle_hull -> st_walk_hull
        private KFSMEvent On_stopHull; // st_walk_hull -> st_idle_hull

        private KFSMState st_jump_hull;
        private KFSMEvent On_jump_hull; // st_idle_hull OR st_walk_hull -> st_jump_hull
        private KFSMTimedEvent On_jump_hull_completed;

        // KSP2 styled tuning
        [KSPField] public float GroundSpherecastUpOffset = 0.15f;
        [KSPField] public float GroundSpherecastRadius = 0.25f;
        [KSPField] public float GroundSpherecastLength = 0.23f;
        [KSPField] public int ContactNormalSmoothingSamples = 20;

        [KSPField] public float EngageRadius = 0.55f; // snap distance (feet -> hull)
        [KSPField] public float ReleaseRadius = 0.85f; // must be > EngageRadius
        [KSPField] public float MaxRelSpeedToEngage = 1.5f;
        [KSPField] public float MaxRelSpeedToStay = 3.0f;

        private HullTarget _hullTarget;
        private Vector3 _localHullForward;
        private Transform _hullTransform;

        private bool _inLetGoCooldown;

        // Accessors for protected KerbalEVA fields (via EVAAccess utility)
        public float currentSpd
        {
            get => Kerbal != null ? KerbalEVAAccess.CurrentSpd(Kerbal) : 0f;
            set { if (Kerbal != null) KerbalEVAAccess.CurrentSpd(Kerbal) = value; }
        }
        public float tgtSpeed
        {
            get => Kerbal != null ? KerbalEVAAccess.TgtSpeed(Kerbal) : 0f;
            set { if (Kerbal != null) KerbalEVAAccess.TgtSpeed(Kerbal) = value; }
        }
        public float lastTgtSpeed
        {
            get => Kerbal != null ? KerbalEVAAccess.LastTgtSpeed(Kerbal) : 0f;
            set { if (Kerbal != null) KerbalEVAAccess.LastTgtSpeed(Kerbal) = value; }
        }
        public Vector3 cmdDir
        {
            get => Kerbal != null ? KerbalEVAAccess.CmdDir(Kerbal) : Vector3.zero;
            set { if (Kerbal != null) KerbalEVAAccess.CmdDir(Kerbal) = value; }
        }
        public Vector3 fUp
        {
            get => Kerbal != null ? KerbalEVAAccess.FUp(Kerbal) : Vector3.up;
            set { if (Kerbal != null) KerbalEVAAccess.FUp(Kerbal) = value; }
        }
        public Vector3 tgtRpos
        {
            get => Kerbal != null ? KerbalEVAAccess.TgtRpos(Kerbal) : Vector3.zero;
            set { if (Kerbal != null) KerbalEVAAccess.TgtRpos(Kerbal) = value; }
        }
        public Vector3 tgtFwd
        {
            get => Kerbal != null ? KerbalEVAAccess.TgtFwd(Kerbal) : Vector3.forward;
            set { if (Kerbal != null) KerbalEVAAccess.TgtFwd(Kerbal) = value; }
        }
        public float deltaHdg
        {
            get => Kerbal != null ? KerbalEVAAccess.DeltaHdg(Kerbal) : 0f;
            set { if (Kerbal != null) KerbalEVAAccess.DeltaHdg(Kerbal) = value; }
        }
        public float turnRate
        {
            get => Kerbal != null ? KerbalEVAAccess.TurnRate(Kerbal) : 0f;
            set { if (Kerbal != null) KerbalEVAAccess.TurnRate(Kerbal) = value; }
        }
        public Animation _animation
        {
            get => Kerbal != null ? KerbalEVAAccess.Animation(Kerbal) : null;
            set { if (Kerbal != null) KerbalEVAAccess.Animation(Kerbal) = value; }
        }
        public List<Collider> currentLadderTriggers
        {
            get => Kerbal != null ? KerbalEVAAccess.CurrentLadderTriggers(Kerbal) : null;
            set { if (Kerbal != null) KerbalEVAAccess.CurrentLadderTriggers(Kerbal) = value; }
        }
        public Vector3 packLinear
        {
            get => Kerbal != null ? KerbalEVAAccess.PackLinear(Kerbal) : Vector3.zero;
            set { if (Kerbal != null) KerbalEVAAccess.PackLinear(Kerbal) = value; }
        }
        public Vector3 packTgtRPos
        {
            get => Kerbal != null ? KerbalEVAAccess.PackTgtRPos(Kerbal) : Vector3.zero;
            set { if (Kerbal != null) KerbalEVAAccess.PackTgtRPos(Kerbal) = value; }
        }
        public float fuelFlowRate
        {
            get => Kerbal != null ? KerbalEVAAccess.FuelFlowRate(Kerbal) : 0f;
            set { if (Kerbal != null) KerbalEVAAccess.FuelFlowRate(Kerbal) = value; }
        }
        public bool VesselUnderControl =>
            Kerbal != null &&
            Kerbal.vessel != null &&
            FlightGlobals.ActiveVessel == Kerbal.vessel;

        public bool Enabled = true;
        [KSPEvent(guiActive = true, guiName = "Toggle Magnet Boots")]
        public void ToggleEnabled()
        {
            Enabled = !Enabled;
            UpdatePaw();
        }

        private void UpdatePaw()
        {
            Events[nameof(ToggleEnabled)].guiName = Enabled ? "Disable Magnet Boots" : "Enable Magnet Boots";
        }

        // MonoBehaviour... behavior?
        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (!HighLogic.LoadedSceneIsFlight) return;
            UpdatePaw();
        }

        // EVA Hookup via Harmony Patch
        private int _lastFsmHash;
        private bool _installed;
        //private HullContactTracker _contact;
        internal void HookIntoEva(KerbalEVA eva)
        {
            Kerbal = eva;
            if (FSM == null) return;

            //_contact = HullContactTracker.GetOrAdd(Kerbal);

            int fsmHash = RuntimeHelpers.GetHashCode(FSM);
            if (_installed && fsmHash == _lastFsmHash) return;

            _lastFsmHash = fsmHash;
            _installed = false;

            try
            {
                SetupFSM();
            }
            catch (Exception ex)
            {
                Logger.Error($"SetupFSM failed for {Kerbal?.name}: {ex}");
                _installed = false;
            }
        }

        protected virtual void SetupFSM()
        {
            if (_installed) return;
            _installed = true;
            Logger.Trace();

            // Idle (On Hull) State
            st_idle_hull = new("Idle (On Hull)");
            st_idle_hull.updateMode = KFSMUpdateMode.FIXEDUPDATE;
            st_idle_hull.OnEnter = idle_hull_OnEnter;
            st_idle_hull.OnFixedUpdate = RefreshHullTarget;
            st_idle_hull.OnFixedUpdate += OrientToSurfaceNormal;
            st_idle_hull.OnFixedUpdate += UpdateMovementOnVessel;
            st_idle_hull.OnFixedUpdate += UpdateHeading;
            st_idle_hull.OnFixedUpdate += UpdatePackLinear;
            st_idle_hull.OnFixedUpdate += updateRagdollVelocities;
            st_idle_hull.OnLeave = idle_hull_OnLeave;
            FSM.AddState(st_idle_hull);

            FSM.AddEvent(Kerbal.On_packToggle, st_idle_hull);
            FSM.AddEvent(Kerbal.On_stumble, st_idle_hull);
            FSM.AddEvent(Kerbal.On_ladderGrabStart, st_idle_hull);
            FSM.AddEvent(Kerbal.On_constructionModeEnter, st_idle_hull);
            FSM.AddEvent(Kerbal.On_constructionModeExit, st_idle_hull);
            FSM.AddEvent(Kerbal.On_flagPlantStart, st_idle_hull);
            FSM.AddEvent(Kerbal.On_seatBoard, st_idle_hull);
            FSM.AddEvent(Kerbal.On_weldStart, st_idle_hull);

            // Attach / Detach Events
            On_attachToHull = new("Attach to Hull");
            On_attachToHull.updateMode = KFSMUpdateMode.FIXEDUPDATE;
            On_attachToHull.GoToStateOnEvent = st_idle_hull;
            On_attachToHull.OnCheckCondition = currentState => ShouldEnterHullIdle();
            FSM.AddEvent(On_attachToHull, Kerbal.st_idle_fl);

            On_detachFromHull = new("Detach from Hull");
            On_detachFromHull.updateMode = KFSMUpdateMode.FIXEDUPDATE;
            On_detachFromHull.GoToStateOnEvent = Kerbal.st_idle_fl;
            On_detachFromHull.OnCheckCondition = _ => ShouldExitHullIdle();
            FSM.AddEvent(On_detachFromHull, st_idle_hull);

            On_letGo = new("Let go from Hull");
            On_letGo.updateMode = KFSMUpdateMode.FIXEDUPDATE;
            On_letGo.GoToStateOnEvent = Kerbal.st_idle_fl;
            On_letGo.OnCheckCondition = currentState =>
                GameSettings.EVA_Jump.GetKey() && !GameSettings.EVA_Run.GetKey()
                && VesselUnderControl
                && !Kerbal.PartPlacementMode
                && !EVAConstructionModeController.MovementRestricted
                && _hullTarget.IsValid()
                && !Kerbal.vessel.packed;
            On_letGo.OnEvent = On_letGoFromHull;
            FSM.AddEvent(On_letGo, st_idle_hull);

            // Walk (On Hull) State
            st_walk_hull = new("Walk (On Hull)");
            st_walk_hull.updateMode = KFSMUpdateMode.FIXEDUPDATE;
            st_walk_hull.OnUpdate = walk_hull_OnUpdate;
            st_walk_hull.OnFixedUpdate = RefreshHullTarget;
            st_walk_hull.OnFixedUpdate += OrientToSurfaceNormal;
            st_walk_hull.OnFixedUpdate += UpdateMovementOnVessel;
            st_walk_hull.OnFixedUpdate += UpdateHeading;
            st_walk_hull.OnFixedUpdate += UpdatePackLinear;
            st_walk_hull.OnFixedUpdate += updateRagdollVelocities;
            st_walk_hull.OnLeave = walk_hull_OnLeave;
            FSM.AddState(st_walk_hull);

            FSM.AddEvent(On_detachFromHull, st_walk_hull);
            FSM.AddEvent(On_letGo, st_walk_hull);
            FSM.AddEvent(Kerbal.On_packToggle, st_walk_hull);
            FSM.AddEvent(Kerbal.On_stumble, st_walk_hull);
            FSM.AddEvent(Kerbal.On_ladderGrabStart, st_walk_hull);
            FSM.AddEvent(Kerbal.On_constructionModeEnter, st_walk_hull);
            FSM.AddEvent(Kerbal.On_constructionModeExit, st_walk_hull);
            FSM.AddEvent(Kerbal.On_flagPlantStart, st_walk_hull);
            FSM.AddEvent(Kerbal.On_seatBoard, st_walk_hull);
            FSM.AddEvent(Kerbal.On_weldStart, st_walk_hull);

            // Move (Hull) Event
            On_MoveHull = new("Move (Hull / FPS)");
            On_MoveHull.updateMode = KFSMUpdateMode.FIXEDUPDATE;
            On_MoveHull.GoToStateOnEvent = st_walk_hull;
            On_MoveHull.OnCheckCondition = currentState =>
                tgtRpos != Vector3.zero
                && _hullTarget.IsValid()
                && !EVAConstructionModeController.MovementRestricted
                && VesselUnderControl
                && !Kerbal.vessel.packed;
            FSM.AddEvent(On_MoveHull, st_idle_hull);

            // Stop (Hull) Event
            On_stopHull = new("Stop (Hull)");
            On_stopHull.updateMode = KFSMUpdateMode.FIXEDUPDATE;
            On_stopHull.GoToStateOnEvent = st_idle_hull;
            On_stopHull.OnCheckCondition = currentState =>
                tgtRpos == Vector3.zero
                || EVAConstructionModeController.MovementRestricted
                || !VesselUnderControl
                || Kerbal.vessel.packed;
            FSM.AddEvent(On_stopHull, st_walk_hull);

            // Jumping (On Hull) State
            st_jump_hull = new KFSMState("Jumping (On Hull)");
            st_jump_hull.updateMode = KFSMUpdateMode.FIXEDUPDATE;
            st_jump_hull.OnEnter = jump_hull_OnEnter;
            st_jump_hull.OnFixedUpdate = OrientToSurfaceNormal;
            st_jump_hull.OnFixedUpdate += UpdateMovementOnVessel;
            st_jump_hull.OnFixedUpdate += UpdateHeading;
            st_jump_hull.OnFixedUpdate += updateRagdollVelocities;
            FSM.AddState(st_jump_hull);

            On_jump_hull = new KFSMEvent("Jump (Hull)");
            On_jump_hull.updateMode = KFSMUpdateMode.FIXEDUPDATE;
            On_jump_hull.GoToStateOnEvent = st_jump_hull;
            On_jump_hull.OnCheckCondition = currentState =>
                GameSettings.EVA_Jump.GetKey() && GameSettings.EVA_Run.GetKey() &&
                VesselUnderControl &&
                !Kerbal.PartPlacementMode &&
                !EVAConstructionModeController.MovementRestricted &&
                _hullTarget.IsValid() &&
                !Kerbal.vessel.packed;
            FSM.AddEvent(On_jump_hull, st_idle_hull, st_walk_hull);

            On_jump_hull_completed = new KFSMTimedEvent("Jump (Hull) Complete", 0.3);
            On_jump_hull_completed.GoToStateOnEvent = Kerbal.st_idle_fl;
            On_jump_hull_completed.OnEvent = jump_hull_Completed;
            FSM.AddEvent(On_jump_hull_completed, st_jump_hull);

            // Stock Ladder Let Go Event augment
            Kerbal.On_ladderLetGo.OnEvent += delegate
            {
                // Temporarily increase spherecast radius to help reattach when getting off ladder
                Kerbal.StartCoroutine(On_ladderLetGo_Coroutine());
            };

        }

        protected virtual bool ShouldEnterHullIdle()
        {
            if (!Enabled || _inLetGoCooldown) return false;
            RefreshHullTarget();
            if (!_hullTarget.IsValid()) return false;
            if (HullTargeting.GetRelativeSpeedToHullPoint(_hullTarget, base.part) > MaxRelSpeedToEngage) return false;
            return true;
        }

        protected virtual bool ShouldExitHullIdle()
        {
            //if (GameSettings.EVA_Jump.GetKeyDown() && !GameSettings.EVA_Run.GetKey()) return true;
            if (!_hullTarget.IsValid()) return true;
            if (_hullTarget.hitDistance > ReleaseRadius) return true;
            if (HullTargeting.GetRelativeSpeedToHullPoint(_hullTarget, base.part) > MaxRelSpeedToStay) return true;
            if (Kerbal.JetpackDeployed && Kerbal.JetpackIsThrusting) return true;

            return false;
        }

        protected virtual void idle_hull_OnEnter(KFSMState s)
        {
            Kerbal.Events["PlantFlag"].active = true;
            tgtSpeed = 0f;
            currentSpd = 0f;

            // disabled for now because they are intrusive
            //KerbalEVAAccess.PostInteractionScreenMessage(Kerbal, $"[{GameSettings.EVA_Jump.primary.ToString()}]: Let go", 0.1f); // same as leaving ladder
            //KerbalEVAAccess.PostInteractionScreenMessage(Kerbal, $"[{GameSettings.EVA_Jump.primary.ToString()} + {GameSettings.EVA_Run.primary.ToString()}]: Jump off", 0.1f);

            _animation.CrossFade(Kerbal.Animations.idle, 1.2f, PlayMode.StopSameLayer);
            if (_hullTarget.part != null)
            {
                _hullTransform = _hullTarget.part.transform;
                _localHullForward = _hullTransform.InverseTransformDirection(Kerbal.transform.forward);
            }
        }

        protected virtual void idle_hull_OnLeave(KFSMState s)
        {
            _hullTarget = default;
        }

        // Stock-alike but using the Kerbal-relative coordinate frame
        protected virtual void walk_hull_OnUpdate()
        {
            Vector3 fwd = Kerbal.transform.forward;
            Vector3 right = Kerbal.transform.right;

            float fwdDot = Vector3.Dot(tgtRpos, fwd);
            float fwdPos = Mathf.Clamp01(fwdDot);
            float fwdNeg = Mathf.Clamp01(-fwdDot);

            float rightDot = Vector3.Dot(tgtRpos, right);
            float rightPos = Mathf.Clamp01(rightDot);
            float rightNeg = Mathf.Clamp01(-rightDot);

            tgtSpeed = Kerbal.walkSpeed * (fwdPos + fwdNeg) + Kerbal.strafeSpeed * (rightPos + rightNeg);

            if (fwdPos > 0.01f)
            {
                _animation.CrossFade(Kerbal.Animations.walkFwd, 0.2f);
                _animation.Blend(Kerbal.Animations.walkLowGee,
                    Mathf.InverseLerp(1f, Kerbal.minWalkingGee, (float)Kerbal.vessel.mainBody.GeeASL));
                Kerbal.Animations.walkLowGee.State.speed = 2.7f;

                if (rightPos > 0f)
                    _animation.Blend(Kerbal.Animations.strafeRight, rightPos);
                else if (rightNeg > 0f)
                    _animation.Blend(Kerbal.Animations.strafeLeft, rightNeg);
            }
            else if (fwdNeg > 0.01f)
            {
                _animation.CrossFade(Kerbal.Animations.walkBack, 0.2f);

                if (rightPos > 0f)
                    _animation.Blend(Kerbal.Animations.strafeRight, rightPos);
                else if (rightNeg > 0f)
                    _animation.Blend(Kerbal.Animations.strafeLeft, rightNeg);
            }
            else if (rightPos > 0.01f)
            {
                _animation.CrossFade(Kerbal.Animations.strafeRight, 0.2f);
            }
            else if (rightNeg > 0.01f)
            {
                _animation.CrossFade(Kerbal.Animations.strafeLeft, 0.2f);
            }
        }

        protected virtual void walk_hull_OnLeave(KFSMState s)
        {
            lastTgtSpeed = tgtSpeed;
        }
        protected virtual void jump_hull_OnEnter(KFSMState st)
        {
            if (tgtSpeed < 0.2f)
            {
                // standing jump
                On_jump_hull_completed.TimerDuration = 0.2f;
                Kerbal.Animations.JumpStillStart.State.time = -0.2f;
                _animation.CrossFade(Kerbal.Animations.JumpStillStart, 0.2f, PlayMode.StopAll);
            }
            else
            {
                // running jump
                On_jump_hull_completed.TimerDuration = Kerbal.Animations.JumpFwdStart.end;
                Kerbal.Animations.JumpFwdStart.State.time = Kerbal.Animations.JumpFwdStart.start;
                _animation.CrossFade(Kerbal.Animations.JumpFwdStart, 0.2f, PlayMode.StopAll);
            }
        }
        protected virtual void jump_hull_Completed()
        {
            Vector3 impulse = (Kerbal.transform.up * Mathf.Pow(Part.mass / PhysicsGlobals.PerCommandSeatReduction, Kerbal.jumpMultiplier) * Kerbal.maxJumpForce) + (Kerbal.transform.forward * tgtSpeed * Kerbal.massMultiplier);
            Part.AddImpulse(impulse);

            var endAnim = (tgtSpeed < 0.2f)
                ? Kerbal.Animations.JumpStillEnd
                : Kerbal.Animations.JumpFwdEnd;
            _animation.CrossFade(endAnim, 0.1f, PlayMode.StopAll);
        }

        private IEnumerator On_ladderLetGo_Coroutine()
        {
            GroundSpherecastRadius += 1.0f;
            yield return new WaitForSeconds(2.0f);
            GroundSpherecastRadius -= 1.0f;
        }

        private void On_letGoFromHull()
        {

            _inLetGoCooldown = true;
            Enabled = false;
            _hullTarget = default;

            Part.rb.velocity += Kerbal.transform.up * 0.5f;
            Kerbal.StartCoroutine(On_letGo_Coroutine());
        }

        private IEnumerator On_letGo_Coroutine()
        {
            yield return new WaitForSeconds(2.0f);
            Enabled = true;
            _inLetGoCooldown = false;
        }

        protected virtual void RefreshHullTarget()
        {
            if (!Enabled)
            {
                _hullTarget = default;
                return;
            }
            if (!HullTargeting.TryAcquireHullSpherecast(
                Kerbal,
                GroundSpherecastUpOffset,
                GroundSpherecastRadius,
                GroundSpherecastLength,
                EngageRadius,
                out HullTarget target))
            {
                _hullTarget = default;
                return;
            }
            _hullTarget = target;
        }

        protected virtual void UpdateHeading()
        {
            if (base.vessel.packed) return;

            if (tgtRpos != Vector3.zero)
            {
                float num = Vector3.Dot(base.transform.forward, tgtFwd);
                if (num <= -1f || !(num < 1f))
                {
                    return;
                }
                deltaHdg = Mathf.Acos(num) * 57.29578f;
            }
            float num2 = Mathf.Sign((Quaternion.Inverse(base.transform.rotation) * tgtFwd).x);
            deltaHdg *= num2;

            if (Mathf.Abs(deltaHdg) < turnRate * 2f)
            {
                Part.rb.angularVelocity = deltaHdg * 0.5f * fUp;
            }
            else
            {
                Part.rb.angularVelocity = turnRate * num2 * fUp;
            }
        }
        public void UpdatePackLinear()
        {
            if (Kerbal != null)
                KerbalEVAAccess.UpdatePackLinear(Kerbal);
        }
        protected virtual void updateRagdollVelocities()
        {
            if (!base.vessel.packed)
            {
                int num = Kerbal.ragdollNodes.Length;
                while (num-- > 0)
                {
                    Kerbal.ragdollNodes[num].updateVelocity(base.transform.position, base.part.rb.velocity, 1f / Time.fixedDeltaTime);
                }
            }
        }

        // Stock-alike custom implementation replacing correctGroundedRotation() which uses the scene-fixed coordinate frame
        private void OrientToSurfaceNormal()
        {
            if (base.vessel.packed || !_hullTarget.IsValid()) return;

            Vector3 upN = _hullTarget.hitNormal;
            var rb = Part.rb;
            if (rb == null) return;

            fUp = upN;

            //
            Vector3 fwd;
            //if (_hullTransform != null && _localHullForward != Vector3.zero)
            //    fwd = _hullTransform.TransformDirection(_localHullForward);
            //else
            fwd = Kerbal.transform.forward;

            fwd = Vector3.ProjectOnPlane(fwd, fUp);
            if (fwd.sqrMagnitude < 1e-6f)
                fwd = Vector3.ProjectOnPlane(Kerbal.transform.right, fUp);
            fwd.Normalize();



            Quaternion targetRot = Quaternion.LookRotation(fwd, fUp);
            Quaternion newRot = Quaternion.RotateTowards(
                rb.rotation,
                targetRot,
                360f * Time.fixedDeltaTime
            );

            // Rotate around foot pivot as before
            Vector3 pivot = Kerbal.footPivot != null ? Kerbal.footPivot.position : rb.worldCenterOfMass;
            Quaternion delta = newRot * Quaternion.Inverse(rb.rotation);

            rb.MoveRotation(newRot);
            rb.MovePosition(pivot + (delta * (rb.position - pivot)));

            if (Kerbal.footPivot == null) return;
            rb.MovePosition(_hullTarget.hitPoint - Kerbal.footPivot.position + (fUp * 0.1f)); // small gap
        }

        // Stock-alike custom implementation replacing updateMovementOnVessel() which uses the scene-fixed coordinate frame
        protected virtual void UpdateMovementOnVessel()
        {
            if (base.vessel.packed || !_hullTarget.IsValid()) return;

            float num = (float)FSM.TimeAtCurrentState;
            num = ((num >= 0.3f) ? 1f : ((!(num > 0f)) ? 0f : (num * 3.3333333f)));
            currentSpd = Mathf.Lerp(lastTgtSpeed, tgtSpeed, num);

            Vector3 desiredDir = (tgtRpos != Vector3.zero)
                ? (Kerbal.CharacterFrameMode ? tgtRpos : Kerbal.transform.forward)
                //? (Vector3.Lerp(cmdDir, (Kerbal.CharacterFrameMode ? tgtRpos : Kerbal.transform.forward), num))
                : Vector3.zero;

            desiredDir = Vector3.ProjectOnPlane(desiredDir, fUp);
            if (desiredDir.sqrMagnitude > 1e-6f) desiredDir.Normalize();

            //cmdDir = desiredDir;
            SetKinematicStickVelocity(desiredDir * currentSpd);
        }

        private void SetKinematicStickVelocity(Vector3 desiredTangentVel)
        {
            var rb = Part.rb;
            if (rb == null) return;

            Vector3 surfaceVel = HullTargeting.GetSurfacePointVelocity(_hullTarget);
            Vector3 v = rb.velocity;
            Vector3 vRel = v - surfaceVel;

            // Split current relative velocity into normal + tangential
            float vn = Vector3.Dot(vRel, fUp);
            Vector3 vRelN = fUp * vn;
            Vector3 vRelT = vRel - vRelN;

            float dt = Time.fixedDeltaTime;

            // control normal relative velocity (vn -> 0)
            var StickSnap = 10.0f;
            float maxNormalDv = StickSnap * dt;
            float vnNew = Mathf.MoveTowards(vn, 0f, maxNormalDv);
            Vector3 vRelNNew = fUp * vnNew;

            // control tangential relative velocity toward desiredTangentVel. desiredTangentVel is already in world space tangent to the surface
            Vector3 slip = vRelT - desiredTangentVel;
            float slipMag = slip.magnitude;

            if (slipMag > 1e-5f)
            {
                var StickSlide = 6.0f;
                float reduce = Mathf.Min(slipMag, StickSlide * dt);
                Vector3 slipNew = slip - slip.normalized * reduce;
                Vector3 vRelTNew = desiredTangentVel + slipNew;

                vRelT = vRelTNew;
            }

            // set new world velocity
            Vector3 vRelNew = vRelT + vRelNNew;
            rb.velocity = surfaceVel + vRelNew;
        }
    }
}