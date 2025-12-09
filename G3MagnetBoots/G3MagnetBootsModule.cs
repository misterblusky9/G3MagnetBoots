using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

using static G3MagnetBoots.HullTargeting;
using static KerbalEVA;

// Up next:  
// *Kerbals surface - aligned forward direction(the way they face) doesn't turn alongside the plane they are attached to, resulting in clear misaligned facing direction when the vessel beneath rotates.

/*  To fix:
 *  Multiple kerbals loading seem to interfere, only one is allowed to attach at a time, others cant.
 *  Reverse-engineer the stock Rigidbody Anchoring logic used to stabalize kerbals on eva when packed, timewarping, or idling without movement input.
 *  Note: Not sure if this still occurs...  Possible issue with multiple kerbals with magnet boots on the same vessel interfering with each other, phantom forces acting on the main vessel. Sometimes results in kerbals being flung off into space but ive yet to catch it occur to an active vessel kerbal.
 *  Kinda fixed: Heading is bound to camera up, making it hard to walk on non-aligned surfaces
 */

/*  For the future:
 *  KSP2-style time-smoothed spherecasts and orientation changes with SMA Simple Moving Average filter to reduce jitter, consider a 2nd spherecast from a predicted future position to smooth major changes in surface normal by early detection and interpolation.
 *  Add config and difficulty settings to tune magnet boot behavior in-game.
 *  Consider adding a "let go" input and screen message like when on a ladder which either does the same as jumping off, or another useful behavior. Stock implementation is intrusive so find a different way to communicate this.
 *  Consider adding sound effects for magnet engage/disengage, walking on hull, jumping off hull.
 *  Consider adding an LED indicator on the Kerbal suit boot model to show magnet status and provide slight illumination.3
 *  Support asteroid surfaces.
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

        private static G3MagnetBootsSettings Settings => G3MagnetBootsSettings.Current;

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
        [KSPField] public float GroundSpherecastUpOffset = 0.15f; //0.15f;
        [KSPField] public float GroundSpherecastRadius = 0.25f;
        [KSPField] public float GroundSpherecastLength = 0.23f; //0.23f;
        [KSPField] public int ContactNormalSmoothingSamples = 20;

        [KSPField] public float EngageRadius = 0.55f; // snap distance (feet -> hull)
        [KSPField] public float ReleaseRadius = 0.85f; // must be > EngageRadius
        [KSPField] public float MaxRelSpeedToEngage = 1.5f;
        //MagnetBootsSettings.Current.maxRelSpeedToStay // [KSPField] public float MaxRelSpeedToStay = 3.0f;

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
            _inLetGoCooldown = false;
            UpdatePaw();
        }

        // EVA Hookup via Harmony Patch
        private int _lastFsmHash;
        private bool _installed;
        internal void HookIntoEva(KerbalEVA eva)
        {
            Kerbal = eva;
            if (FSM == null) return;

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
            FSM.AddEvent(Kerbal.On_boardPart, st_idle_hull);
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
            st_walk_hull.OnFixedUpdate += UpdateHullInputTargets;
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
            FSM.AddEvent(Kerbal.On_boardPart, st_walk_hull);
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
            On_jump_hull.OnEvent += delegate
            {
                Kerbal.StartCoroutine(AutoDeployJetpack_Coroutine(1.0f));
            };
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

            // Stock Weld State OnEnter augment
            Kerbal.st_weld.OnEnter = weld_OnEnter;

        }

        public bool IsAboveHighAltitude()
        {
            if (Part.rb != null)
            {
                float groundAndSeaHighAltitude = 3500f;
                return vessel.altitude > (double)groundAndSeaHighAltitude && vessel.heightFromTerrain > (double)groundAndSeaHighAltitude;
            }
            return true;
        }

        protected virtual bool ShouldEnterHullIdle()
        {
            if (!Settings.modEnabled) return false;
            if (!Enabled || _inLetGoCooldown) return false;
            RefreshHullTarget();
            if (!_hullTarget.IsValid()) return false;
            if (Settings.disableInAtmosphere && !IsAboveHighAltitude()) return false;
            if (HullTargeting.GetRelativeSpeedToHullPoint(_hullTarget, base.part) > MaxRelSpeedToEngage) return false;
            return true;
        }

        protected virtual bool ShouldExitHullIdle()
        {
            if (!_hullTarget.IsValid()) return true;
            if (_hullTarget.hitDistance > ReleaseRadius) return true;
            if (Settings.disableInAtmosphere && !IsAboveHighAltitude()) return true;
            if (HullTargeting.GetRelativeSpeedToHullPoint(_hullTarget, base.part) > Settings.maxRelSpeedToStay) return true;

            return false;
        }

        protected virtual void idle_hull_OnEnter(KFSMState s)
        {
            Kerbal.Events["PlantFlag"].active = Settings.allowPlantFlag;
            tgtSpeed = 0f;
            currentSpd = 0f;

            // Allow repacking chute while on hull
            if (KerbalEVAAccess.EvaChute(Kerbal) != null)
            {
                KerbalEVAAccess.EvaChute(Kerbal).AllowRepack(allowRepack: Settings.allowPackChute);
            }

            SetToggleJetpack(false);

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
            GroundSpherecastRadius += 4.0f;
            GroundSpherecastLength += 2.0f;
            yield return new WaitForSeconds(2.0f);
            GroundSpherecastRadius -= 4.0f;
            GroundSpherecastLength -= 2.0f;
        }

        private void On_letGoFromHull()
        {

            _inLetGoCooldown = true;
            Enabled = false;
            _hullTarget = default;

            Part.rb.velocity += Kerbal.transform.up * 0.5f;
            Kerbal.StartCoroutine(On_letGo_Coroutine(2.0f));
            Kerbal.StartCoroutine(AutoDeployJetpack_Coroutine(0.5f));
        }

        private IEnumerator On_letGo_Coroutine(float delay = 2.0f)
        {
            yield return new WaitForSeconds(delay);
            Enabled = true;
            _inLetGoCooldown = false;
        }

        private IEnumerator AutoDeployJetpack_Coroutine(float delay = 1.0f)
        {
            yield return new WaitForSeconds(delay);
            if (Kerbal.HasJetpack && !Kerbal.JetpackDeployed)
            {
                SetToggleJetpack(true);
            }
        }

        private void SetToggleJetpack(bool enable)
        {
            if (Settings.autoToggleJetpack && Kerbal.HasJetpack)
            {
                if (enable && !Kerbal.JetpackDeployed)
                {
                    Kerbal.ToggleJetpack();
                }
                else if (!enable && Kerbal.JetpackDeployed)
                {
                    Kerbal.ToggleJetpack();
                }
            }
        }

        protected virtual void weld_OnEnter(KFSMState st)
        {
            // identical to stock weld_OnEnter just including the hulltarget surface check alongside the stock surface check
            Kerbal.Animations.weld.State.time = Kerbal.Animations.weld.start;
            Kerbal.Animations.weldSuspended.State.time = Kerbal.Animations.weldSuspended.start;
            if (KerbalEVAAccess.HasWeldLineOfSight(Kerbal))
            {
                if (KerbalEVAAccess.SurfaceContact(Kerbal) || _hullTarget.IsValid())
                {
                    _animation.CrossFade(Kerbal.Animations.weld, 0.2f, PlayMode.StopSameLayer);
                }
                else
                {
                    _animation.CrossFade(Kerbal.Animations.weldSuspended, 0.2f, PlayMode.StopSameLayer);
                }
                KerbalEVAAccess.WasVisorEnabledBeforeWelding(Kerbal) = KerbalEVAAccess.VisorState(Kerbal) == VisorStates.Lowered;
                Kerbal.LowerVisor(forceHelmet: true);
                if (Kerbal.WeldFX != null)
                {
                    Kerbal.WeldFX.Play();
                }
            }
            else
            {
                FSM.RunEvent(Kerbal.On_weldComplete);
            }
        }


        protected virtual void RefreshHullTarget()
        {
            /*
            if (!Enabled) { _hullTarget = default; return; }

            if (!HullTargeting.TryAcquireHullSpherecast(
                Kerbal, GroundSpherecastUpOffset, GroundSpherecastRadius, GroundSpherecastLength, EngageRadius,
                out HullTarget target))
            {
                _hullTarget = default;
                return;
            }
            */

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
            if (base.vessel.packed || !_hullTarget.IsValid()) return;

            var rb = Part.rb;
            if (rb == null) return;

            // Choose desired facing: movement when moving, else tgtFwd (mouse-look etc.)
            Vector3 desired = (tgtRpos != Vector3.zero) ? (tgtRpos - base.transform.position) : tgtFwd;

            // Project both onto surface plane
            Vector3 curFwd = Vector3.ProjectOnPlane(base.transform.forward, fUp);
            Vector3 desFwd = Vector3.ProjectOnPlane(desired, fUp);

            if (curFwd.sqrMagnitude < 1e-6f || desFwd.sqrMagnitude < 1e-6f) return;

            curFwd.Normalize();
            desFwd.Normalize();

            // Signed shortest angle around surface up (degrees)
            deltaHdg = Vector3.SignedAngle(curFwd, desFwd, fUp);
            float sign = Mathf.Sign(deltaHdg);

            // Stock-style angularVelocity drive (do NOT convert to radians here)
            if (Mathf.Abs(deltaHdg) < turnRate * 2f)
                rb.angularVelocity = deltaHdg * 0.5f * fUp;
            else
                rb.angularVelocity = turnRate * sign * fUp;
        }

        protected virtual void UpdatePackLinear()
        {
            // Same as stock but blocked if on hull
            if (Kerbal.JetpackDeployed && !base.vessel.packed && !Kerbal.isRagdoll && !EVAConstructionModeController.MovementRestricted && !_hullTarget.IsValid())
            {
                if (!Settings.canJetpackOnHull) return;
                    //if (FSM.CurrentState == st_idle_hull || FSM.CurrentState == st_walk_hull || FSM.CurrentState == st_jump_hull) return;

                    packLinear = packTgtRPos * (Kerbal.thrustPercentage * 0.01f);
                if (packLinear != Vector3.zero && Kerbal.Fuel > 0.0)
                {
                    base.part.AddForce(packLinear * Kerbal.linPower);
                    fuelFlowRate += packLinear.magnitude * Time.fixedDeltaTime;
                }
            }
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
            if (base.vessel.packed || !_hullTarget.IsValid() || !Settings.modEnabled) return;

            var rb = Part.rb;
            if (rb == null) return;

            Vector3 upN = _hullTarget.hitNormal;
            fUp = upN;

            // Preserve current heading, only tilt to match the surface normal
            Vector3 fwd = Vector3.ProjectOnPlane(rb.rotation * Vector3.forward, fUp);
            if (fwd.sqrMagnitude < 1e-6f)
                fwd = Vector3.ProjectOnPlane(rb.rotation * Vector3.right, fUp);
            fwd.Normalize();

            Quaternion targetRot = Quaternion.LookRotation(fwd, fUp);
            Quaternion newRot = Quaternion.RotateTowards(
                rb.rotation,
                targetRot,
                360f * Time.fixedDeltaTime
            );

            Vector3 pivot = Kerbal.footPivot != null ? Kerbal.footPivot.position : rb.worldCenterOfMass;
            Quaternion delta = newRot * Quaternion.Inverse(rb.rotation);

            rb.MoveRotation(newRot);
            rb.MovePosition(pivot + (delta * (rb.position - pivot)));

            if (Kerbal.footPivot == null) return;

            Vector3 targetPos = _hullTarget.hitPoint + (fUp * 0.08f);

            Vector3 foot = Kerbal.footPivot != null
                ? Kerbal.footPivot.position
                : rb.worldCenterOfMass;

            Vector3 newPos = Vector3.Lerp(rb.position, rb.position + (targetPos - foot), 0.35f);
            rb.MovePosition(newPos);
        }

        // Stock-alike custom implementation replacing updateMovementOnVessel() which uses the scene-fixed coordinate frame
        protected virtual void UpdateMovementOnVessel()
        {
            if (base.vessel.packed || !_hullTarget.IsValid() || !Settings.modEnabled) return;

            float num = (float)FSM.TimeAtCurrentState;
            num = ((num >= 0.3f) ? 1f : ((!(num > 0f)) ? 0f : (num * 3.3333333f)));
            currentSpd = Mathf.Lerp(lastTgtSpeed, tgtSpeed, num);

            //Vector3 desiredDir = (tgtRpos != Vector3.zero)
            //    ? (Kerbal.CharacterFrameMode ? tgtRpos : Kerbal.transform.forward)
            //? (Vector3.Lerp(cmdDir, (Kerbal.CharacterFrameMode ? tgtRpos : Kerbal.transform.forward), num))
            //     : Vector3.zero;

            Vector3 desiredDir = (tgtRpos != Vector3.zero) ? tgtRpos : Vector3.zero;
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
            float maxNormalDv = Settings.strengthSurfaceSnap * dt;
            float vnNew = Mathf.MoveTowards(vn, 0f, maxNormalDv);
            Vector3 vRelNNew = fUp * vnNew;

            // control tangential relative velocity toward desiredTangentVel. desiredTangentVel is already in world space tangent to the surface
            Vector3 slip = vRelT - desiredTangentVel;
            float slipMag = slip.magnitude;

            if (slipMag > 1e-5f)
            {
                float reduce = Mathf.Min(slipMag, Settings.strengthSurfaceSlide * dt);
                Vector3 slipNew = slip - slip.normalized * reduce;
                Vector3 vRelTNew = desiredTangentVel + slipNew;

                vRelT = vRelTNew;
            }

            // set new world velocity
            Vector3 vRelNew = vRelT + vRelNNew;
            rb.velocity = surfaceVel + vRelNew;
        }

        /*
        private void UpdateHullInputTargets()
        {
            if (!_hullTarget.IsValid() || !VesselUnderControl) return;

            GetCameraTangentBasis(fUp, out var camFwdT, out var camRightT);
            if (camFwdT == Vector3.zero || camRightT == Vector3.zero) return;

            float v = 0f, h = 0f;
            if (GameSettings.EVA_forward.GetKey()) v += 1f;
            if (GameSettings.EVA_back.GetKey()) v -= 1f;
            if (GameSettings.EVA_right.GetKey()) h += 1f;
            if (GameSettings.EVA_left.GetKey()) h -= 1f;

            Vector3 move = camFwdT * v + camRightT * h;
            if (move.sqrMagnitude > 1f) move.Normalize();

            tgtRpos = move;                         // used for movement + anim blend
            tgtFwd = (move.sqrMagnitude > 1e-6f)   // used for facing when idle
                ? move
                : camFwdT;
        }
        */

        private void UpdateHullInputTargets()
        {
            if (!_hullTarget.IsValid() || !VesselUnderControl) return;

            GetCameraTangentBasis(fUp, out var camFwdT, out var camRightT);
            if (camFwdT == Vector3.zero || camRightT == Vector3.zero) return;

            // 1. Measure how "straight down" the camera is relative to the surface normal
            Transform camT = null;
            if (FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null)
                camT = FlightCamera.fetch.mainCamera.transform;
            else if (Camera.main != null)
                camT = Camera.main.transform;

            bool useKerbalFrame = false;
            if (camT != null)
            {
                // +1: camera looking along +fUp, -1: along -fUp (straight into the surface)
                float align = Vector3.Dot(camT.forward, fUp);

                // Threshold: when looking too close to ±surface normal, stop using camera basis
                // tweak 0.85f as needed (≈30° cone around the normal)
                if (Mathf.Abs(align) > 0.85f)
                    useKerbalFrame = true;
            }

            // 2. Choose movement basis: camera vs kerbal
            Vector3 basisFwd, basisRight;

            if (useKerbalFrame)
            {
                // Everything relative to kerbal forward/right projected on the surface,
                // i.e. "normal" behavior you want in the straight-down case.
                basisFwd = Vector3.ProjectOnPlane(Kerbal.transform.forward, fUp);
                if (basisFwd.sqrMagnitude < 1e-6f)
                    basisFwd = Vector3.ProjectOnPlane(Kerbal.transform.right, fUp);
                basisFwd.Normalize();

                basisRight = Vector3.ProjectOnPlane(Kerbal.transform.right, fUp);
                if (basisRight.sqrMagnitude < 1e-6f)
                    basisRight = Vector3.Cross(fUp, basisFwd);
                basisRight.Normalize();
            }
            else
            {
                basisFwd = camFwdT;
                basisRight = camRightT;
            }

            float v = 0f, h = 0f;
            if (GameSettings.EVA_forward.GetKey()) v += 1f;
            if (GameSettings.EVA_back.GetKey()) v -= 1f;
            if (GameSettings.EVA_right.GetKey()) h += 1f;
            if (GameSettings.EVA_left.GetKey()) h -= 1f;

            Vector3 move = basisFwd * v + basisRight * h;
            if (move.sqrMagnitude > 1f) move.Normalize();

            // 3. Targets:
            // - movement always uses the chosen basis
            // - facing uses kerbal-forward frame when camera is too vertical
            tgtRpos = move;

            if (move.sqrMagnitude > 1e-6f)
            {
                // Moving: face movement direction (already tangent to surface)
                tgtFwd = move;
            }
            else
            {
                // Idle: either face camera-tangent forward or kerbal-forward-on-surface,
                // depending on whether we're "too vertical" or not.
                tgtFwd = useKerbalFrame ? basisFwd : camFwdT;
            }
        }

        private void GetCameraTangentBasis(Vector3 surfaceUp, out Vector3 tFwd, out Vector3 tRight)
        {
            tFwd = Vector3.zero;
            tRight = Vector3.zero;

            Transform camT = null;
            if (FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null)
                camT = FlightCamera.fetch.mainCamera.transform;
            else if (Camera.main != null)
                camT = Camera.main.transform;

            if (camT == null) return;

            // Primary: camera forward projected onto surface plane
            Vector3 f = Vector3.ProjectOnPlane(camT.forward, surfaceUp);

            // If looking nearly straight up/down at the surface, forward projection collapses.
            // Fallback: use camera UP projected onto the surface plane (screen-up becomes "forward").
            if (f.sqrMagnitude < 1e-6f)
                f = Vector3.ProjectOnPlane(camT.up, surfaceUp);

            // Last resort fallback
            if (f.sqrMagnitude < 1e-6f)
                f = Vector3.ProjectOnPlane(base.transform.forward, surfaceUp);

            f.Normalize();

            // Right: prefer camera right projected; fallback to cross
            Vector3 r = Vector3.ProjectOnPlane(camT.right, surfaceUp);
            if (r.sqrMagnitude < 1e-6f)
                r = Vector3.Cross(surfaceUp, f);

            r.Normalize();

            tFwd = f;
            tRight = r;
        }

    }
}