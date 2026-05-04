using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Linq;
using Expansions;
using FinePrint.Utilities;
using KSP.Localization;
using KSP.UI.Screens.Flight;
using UnityEngine;
using static KerbalEVA;

namespace G3MagnetBoots
{
    public partial class ModuleG3MagnetBoots
    {
        public Vector3 HullNormal
        {
            get
            {
                if (_hullTarget.IsValid() && _hullTarget.hitNormal.sqrMagnitude > VECTOR_ZERO_THRESHOLD)
                    return _hullTarget.hitNormal.normalized;
                return Kerbal != null ? Kerbal.transform.up : Vector3.up;
            }
        }

        private Vector3 _smoothedHullNormalLocal = Vector3.up;
        private bool _smoothedHullNormalLocalValid = false;
        public Vector3 CameraLockedUp
        {
            get
            {
                if (_smoothedHullNormalLocalValid && _hullTransform != null)
                    return _hullTransform.TransformDirection(_smoothedHullNormalLocal).normalized;

                if (_hullSmoothingValid && _smoothedHullNormal.sqrMagnitude > VECTOR_ZERO_THRESHOLD)
                    return _smoothedHullNormal.normalized;

                return HullNormal;
            }
        }

        public Vector3 SmoothedHullNormal
        {
            get
            {
                if (_hullSmoothingValid && _smoothedHullNormal.sqrMagnitude > VECTOR_ZERO_THRESHOLD)
                    return _smoothedHullNormal.normalized;

                return HullNormal;
            }
        }

        public Transform CameraLockedReferenceTransform
        {
            get
            {
                if (_hullTransform != null)
                    return _hullTransform;

                if (_hullTarget.rigidbody != null)
                    return _hullTarget.rigidbody.transform;

                if (_hullTarget.part != null)
                    return _hullTarget.part.transform;

                return null;
            }
        }

        public Vector3 CameraLockedAnchorWorld
        {
            get
            {
                // Render-current part COM, using transform interpolation if available.
                if (Part != null && Part.rb != null && Part.rb.transform != null)
                    return Part.rb.transform.TransformPoint(Part.rb.centerOfMass);

                if (Kerbal != null && Kerbal.transform != null)
                    return Kerbal.transform.position;

                if (Part != null)
                    return Part.transform.position;

                return transform.position;
            }
        }


        // Physics Constants
        internal const float SPHERECAST_UP_OFFSET_DEFAULT = 0.15f;
        internal const float SPHERECAST_RADIUS_DEFAULT = 0.25f;
        internal const float SPHERECAST_LENGTH_DEFAULT = 0.23f;
        internal const float ENGAGE_RADIUS_DEFAULT = 0.55f;
        internal const float ENGAGE_RADIUS_JETPACK_THRUSTING = 0.1f;
        internal const float RELEASE_RADIUS = 0.65f;
        internal const float FOOT_HULL_PAD = 0.05f;
        internal const float JETPACK_UP_THRUST_THRESHOLD = 0.15f;
        internal const float JETPACK_UP_THRUST_ATTACH_THRESHOLD = 0.05f;

        // Timing Constants
        internal const float ANIMATION_CROSSFADE_TIME = 0.2f;
        internal const float ANIMATION_CROSSFADE_TIME_LONG = 1.2f;
        internal const float IDLE_ANIMATION_CROSSFADE_TIME = 0.1f;
        internal const float JUMP_STILL_TIME_OFFSET = -0.2f;
        internal const float JUMP_STILL_DURATION = 0.2f;
        internal const float LOW_GEE_ANIMATION_SPEED = 2.7f;

        // Impulse & Force Constants
        internal const float LET_GO_IMPULSE_STRENGTH = 1.0f;
        internal const float ROTATION_RATE_MULTIPLIER = 360f;

        // Cooldown & Delay Constants
        internal const float LET_GO_COOLDOWN_TIME = 1.0f;
        internal const float JETPACK_DEPLOY_DELAY_JUMP = 1.0f;
        internal const float JETPACK_DEPLOY_DELAY_LETGO = 0.5f;
        internal const float LADDER_LETGO_SPHERECAST_BOOST_TIME = 1.0f;
        internal const float LADDER_LETGO_SPHERECAST_RADIUS_BOOST = 10.0f;
        internal const float LADDER_LETGO_SPHERECAST_LENGTH_BOOST = 4.0f;

        // Speed & Movement Constants
        internal const float MIN_MOVEMENT_THRESHOLD = 0.01f;
        internal const float MIN_SPEED_THRESHOLD = 0.2f;
        internal const float RELATIVE_VELOCITY_DISPLAY_SCALE = 0.4f;

        // EMA Smoothing Constants
        internal const float SURFACE_VELOCITY_SMOOTHING_TAU = 0.05f;

        // Vector Magnitude Thresholds
        internal const float VECTOR_ZERO_THRESHOLD = 1e-6f;

        // Quaternion Dot Product Thresholds
        internal const float QUAT_DOT_NEARLY_SAME = 0.9999f;
        internal const float QUAT_DOT_OPPOSITE = -0.9999f;
        internal const float QUAT_FLIP_ANGLE = 180f;

        // Camera Lock Constants
        internal const int OFF_HULL_FRAMES_TO_UNLOCK = 5;

        // Hull Anchor Joint
        private float _hullAnchorTimer;
        private const float HULL_ANCHOR_DELAY = 0.5f;
        private const float HULL_ANCHOR_MAX_NORMAL_DELTA = 3.0f; // degrees

        private static G3MagnetBootsSettings Settings => G3MagnetBootsSettings.Current;
        private static G3MagnetBootsConstants Constants => G3MagnetBootsConstants.Current;

        public KerbalEVA Kerbal { get; private set; }
        public KerbalFSM FSM { get { return Kerbal.fsm; } }
        public Part Part { get { return Kerbal.part; } }
        public ProtoCrewMember Crew { get { return Part.protoModuleCrew?.FirstOrDefault(); } }

        // Tech tree requirement check
        [KSPField] public string unlockTech = "";

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
        private KFSMTimedEvent On_jump_hull_completed; // st_jump_hull -> st_idle_fl

        // EVA science on Hull – tracks whether golf/flag-plant/weld was triggered from a hull state
        public bool _golfStartedFromHull;
        public bool _flagStartedFromHull;
        public bool _constructionFromHull;
        public bool _weldStartedFromHull;

        // KSP2 styled tuning
        public float GroundSpherecastUpOffset = SPHERECAST_UP_OFFSET_DEFAULT;
        public float GroundSpherecastRadius = SPHERECAST_RADIUS_DEFAULT;
        public float GroundSpherecastLength = SPHERECAST_LENGTH_DEFAULT;
        public int ContactNormalSmoothingSamples = 20;

        public float EngageRadius = ENGAGE_RADIUS_DEFAULT; // snap distance (feet -> hull)
        public float ReleaseRadius = RELEASE_RADIUS; // must be > EngageRadius
        public float FootHullPad = FOOT_HULL_PAD; // extra distance from foot to hull surface

        ScreenMessage _magMsg;

        private HullTarget _hullTarget;
        private Vector3 _localHullForward;       // kerbal forward stored in hull local space
        private Transform _hullTransform;        // hull part transform
        private bool _inLetGoCooldown;
        public bool _lastGear;
        public bool _lastOnHull;
        public bool _lastHadBoots = false;

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

        bool IsAGOn(KSPActionGroup g) => VesselUtils.IsAGOn(vessel, g);
        void SetAG(KSPActionGroup g, bool active) => VesselUtils.SetAG(vessel, g, active);
        void ToggleAG(KSPActionGroup g) => VesselUtils.ToggleAG(vessel, g);

        public bool IsGearOn => IsAGOn(KSPActionGroup.Gear);
        public bool IsOnHull
        {
            get
            {
                if (!this.enabled) return false;
                if (Kerbal == null || Kerbal.fsm == null) return false;
                if (!_hullTarget.IsValid()) return false;

                KFSMState current = Kerbal.fsm.CurrentState;

                return
                    current == st_idle_hull ||
                    current == st_walk_hull ||
                    current == st_jump_hull ||
                    (current == Kerbal.st_playing_golf && _golfStartedFromHull) ||
                    (current == Kerbal.st_flagAcquireHeading && _flagStartedFromHull) ||
                    (current == Kerbal.st_flagPlant && _flagStartedFromHull) ||
                    (current == Kerbal.st_enteringConstruction && _constructionFromHull) ||
                    (current == Kerbal.st_exitingConstruction && _constructionFromHull) ||
                    (current == Kerbal.st_weldAcquireHeading && _constructionFromHull) ||
                    (current == Kerbal.st_weld && _constructionFromHull);
            }
        }

        public bool FlagStartedFromHull => _flagStartedFromHull;
        public bool HullTargetIsValid => _hullTarget.IsValid();
        public string CurrentFSMStateName => FSM?.CurrentState?.name ?? "null";
        public Rigidbody HullTargetRigidbody => _hullTarget.rigidbody;
        public bool VesselUnderControl => VesselUtils.VesselUnderControl(Kerbal);
        public bool IsJetpackThrustingUp = false;

        [KSPEvent(guiActive = true, guiName = "#autoLOC_6003095", active = false)]
        public void PlantFlagOnHull()
        {
            if (!Settings.magbootsPlantFlagEnabled) return;
            if (Kerbal == null || !IsOnHull) return;
            Kerbal.flagItems--;
            Kerbal.fsm.RunEvent(Kerbal.On_flagPlantStart);
        }

        public void UpdatePlantFlagOnHullButton()
        {
            if (Kerbal == null) return;
            Events["PlantFlagOnHull"].active =
                Settings.magbootsPlantFlagEnabled
                && IsOnHull
                && Kerbal.vessel.state == Vessel.State.ACTIVE
                && Kerbal.flagItems > 0
                && !Kerbal.isRagdoll
                && GameVariables.Instance.UnlockedEVAFlags(
                    ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.AstronautComplex))
                && !Kerbal.InConstructionMode;
        }

        private bool HasMagnetBootsInInventory()
        {
            if (Kerbal == null || Kerbal.part == null) return false;
            var inventory = Kerbal.ModuleInventoryPartReference;
            if (inventory == null || inventory.InventoryItemCount == 0) return false;

            foreach (int slotIndex in inventory.storedParts.Keys)
            {
                StoredPart storedPart = inventory.storedParts[slotIndex];
                if (storedPart != null && storedPart.partName.Equals("G3.EVAMagnetBoots"))
                    return true;
            }
            return false;
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

        public void SetEnabled(bool enabled)
        {
            if (HasMagnetBootsInInventory())
            {
                this.enabled = enabled;
                SetAG(KSPActionGroup.Gear, enabled);
            }
            else
            {
                this.enabled = false;
                SetAG(KSPActionGroup.Gear, false);
            }
        }


        // MonoBehaviour... behavior?
        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (!HighLogic.LoadedSceneIsFlight) return;

            _inLetGoCooldown = false;

            HookAGGearButton();

            GameEvents.onKerbalPassedOutFromGeeForce.Add(OnKerbalBlackedOut);
            GameEvents.onKerbalInactiveChange.Add(OnKerbalInactiveChanged);
            GameEvents.onVesselLoaded.Add(OnVesselUnpacked);
            GameEvents.onVesselGoOffRails.Add(OnVesselUnpacked);
        }

        public void OnDestroy()
        {
            GameEvents.onKerbalPassedOutFromGeeForce.Remove(OnKerbalBlackedOut);
            GameEvents.onKerbalInactiveChange.Remove(OnKerbalInactiveChanged);
            GameEvents.onVesselLoaded.Remove(OnVesselUnpacked);
            GameEvents.onVesselGoOffRails.Remove(OnVesselUnpacked);
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            UpdatePlantFlagOnHullButton();
            UpdateUI();

            if (!HasMagnetBootsInInventory())
            {
                if (_lastHadBoots)
                {
                    if (_lastOnHull)
                    {
                        PostMagMsg(false);
                        _lastOnHull = false;
                    }

                    // Force detach cleanly
                    if (IsOnHull || _hullTarget.IsValid())
                    {
                        ClearHullTarget();
                        RemoveHullAnchor();
                        if (Kerbal?.fsm != null)
                            Kerbal.fsm.RunEvent(On_detachFromHull);
                    }
                }

                _lastHadBoots = false;
                this.enabled = false;
                return;
            }

            // Boots just re-added
            if (!_lastHadBoots)
            {
                _lastHadBoots = true;
                SetEnabled(IsGearOn);
                _lastGear = IsGearOn;
            }

            IsJetpackThrustingUp = Vector3.Dot(Vector3.Project(packTgtRPos, Kerbal.transform.up), Kerbal.transform.up) > JETPACK_UP_THRUST_THRESHOLD;
            EngageRadius = (Kerbal != null && Kerbal.JetpackDeployed && Kerbal.Fuel > 0.0 && Kerbal.thrustPercentage > 0f && IsJetpackThrustingUp) ? ENGAGE_RADIUS_JETPACK_THRUSTING : ENGAGE_RADIUS_DEFAULT;
        }

        // EVA Hookup via Harmony Patch
        private int _lastFsmHash;
        private bool _installed;
        internal void HookIntoEva(KerbalEVA eva)
        {
            Kerbal = eva;

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
                Logger.Error($"Init failed for {Kerbal?.name}: {ex}");
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
            st_idle_hull.OnFixedUpdate += UpdateSmoothedHullSurface;
            st_idle_hull.OnFixedUpdate += OrientToSurfaceNormal;
            st_idle_hull.OnFixedUpdate += UpdateMovementOnVessel;
            st_idle_hull.OnFixedUpdate += UpdateHeading;
            st_idle_hull.OnFixedUpdate += TryAddHullAnchor;
            st_idle_hull.OnFixedUpdate += UpdatePackLinear;
            st_idle_hull.OnFixedUpdate += UpdateConstructionFromHullFlag;
            st_idle_hull.OnFixedUpdate += ValidateHullAnchor;
            //st_idle_hull.OnFixedUpdate += Kerbal.CheckLadderTriggers;
            st_idle_hull.OnLeave = nextState =>
            {
                bool keepAnchor =
                    nextState == st_idle_hull ||
                    (nextState == Kerbal.st_enteringConstruction && _constructionFromHull) ||
                    (nextState == Kerbal.st_exitingConstruction && _constructionFromHull); //||
                    //(nextState == Kerbal.st_weldAcquireHeading && _constructionFromHull) ||
                    //(nextState == Kerbal.st_weld && _constructionFromHull) ||
                    //(nextState == Kerbal.st_playing_golf && _golfStartedFromHull) ||
                    //(nextState == Kerbal.st_flagAcquireHeading && _flagStartedFromHull) ||
                    //(nextState == Kerbal.st_flagPlant && _flagStartedFromHull);

                if (!keepAnchor)
                    RemoveHullAnchor();
            };
            FSM.AddState(st_idle_hull);

            FSM.AddEvent(Kerbal.On_packToggle, st_idle_hull);
            FSM.AddEvent(Kerbal.On_stumble, st_idle_hull);
            FSM.AddEvent(Kerbal.On_ladderGrabStart, st_idle_hull);
            FSM.AddEvent(Kerbal.On_flagPlantStart, st_idle_hull);
            FSM.AddEvent(Kerbal.On_boardPart, st_idle_hull);

            // Attach / Detach Events
            On_attachToHull = new("Attach to Hull");
            On_attachToHull.updateMode = KFSMUpdateMode.FIXEDUPDATE;
            On_attachToHull.GoToStateOnEvent = st_idle_hull;
            On_attachToHull.OnCheckCondition = currentState => ShouldEnterHullIdle();
            FSM.AddEvent(On_attachToHull, Kerbal.st_idle_fl);
            FSM.AddEvent(On_attachToHull, Kerbal.st_idle_gr);
            FSM.AddEvent(On_attachToHull, Kerbal.st_idle_b_gr);

            On_detachFromHull = new("Detach from Hull");
            On_detachFromHull.updateMode = KFSMUpdateMode.FIXEDUPDATE;
            On_detachFromHull.GoToStateOnEvent = Kerbal.st_idle_fl;
            On_detachFromHull.OnCheckCondition = _ => ShouldExitHullIdle();
            On_detachFromHull.OnEvent = () =>
            {
                Kerbal.StartCoroutine(AutoDeployJetpack_Coroutine(JETPACK_DEPLOY_DELAY_LETGO));
            };
            FSM.AddEvent(On_detachFromHull, st_idle_hull);


            On_letGo = new("Let go from Hull");
            On_letGo.updateMode = KFSMUpdateMode.FIXEDUPDATE;
            On_letGo.GoToStateOnEvent = Kerbal.st_idle_fl;
            On_letGo.OnCheckCondition = currentState =>
                GameSettings.EVA_Jump.GetKey()
                && !GameSettings.EVA_Run.GetKey()
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
            st_walk_hull.OnFixedUpdate = RefreshHullTarget;
            st_walk_hull.OnFixedUpdate += UpdateSmoothedHullSurface;
            st_walk_hull.OnFixedUpdate += OrientToSurfaceNormal;
            st_walk_hull.OnFixedUpdate += UpdateHullInputTargets;
            st_walk_hull.OnFixedUpdate += walk_hull_OnUpdate;
            st_walk_hull.OnFixedUpdate += UpdateMovementOnVessel;
            st_walk_hull.OnFixedUpdate += UpdateHeading;
            st_walk_hull.OnFixedUpdate += UpdatePackLinear;
            st_walk_hull.OnFixedUpdate += UpdateConstructionFromHullFlag;
            //st_walk_hull.OnFixedUpdate += Kerbal.CheckLadderTriggers;
            st_walk_hull.OnEnter = _ =>
            {
                SnapToHullPad();
                RemoveHullAnchor();
            };
            st_walk_hull.OnLeave = walk_hull_OnLeave;
            FSM.AddState(st_walk_hull);

            FSM.AddEvent(On_detachFromHull, st_walk_hull);
            FSM.AddEvent(On_letGo, st_walk_hull);
            FSM.AddEvent(Kerbal.On_packToggle, st_walk_hull);
            FSM.AddEvent(Kerbal.On_stumble, st_walk_hull);
            FSM.AddEvent(Kerbal.On_ladderGrabStart, st_walk_hull);
            FSM.AddEvent(Kerbal.On_flagPlantStart, st_walk_hull);
            FSM.AddEvent(Kerbal.On_boardPart, st_walk_hull);
            FSM.AddEvent(Kerbal.On_stumble, st_walk_hull);
            FSM.AddEvent(Kerbal.On_ladderGrabStart, st_walk_hull);
            FSM.AddEvent(Kerbal.On_flagPlantStart, st_walk_hull);
            FSM.AddEvent(Kerbal.On_boardPart, st_walk_hull);

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
            st_jump_hull.OnFixedUpdate = UpdateMovementOnVessel;
            st_jump_hull.OnFixedUpdate += UpdateHeading;
            //st_jump_hull.OnFixedUpdate += Kerbal.CheckLadderTriggers;
            FSM.AddState(st_jump_hull);

            On_jump_hull = new KFSMEvent("Jump (On Hull)");
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
                Kerbal.StartCoroutine(AutoDeployJetpack_Coroutine(JETPACK_DEPLOY_DELAY_JUMP));
            };
            FSM.AddEvent(On_jump_hull, st_idle_hull, st_walk_hull);

            On_jump_hull_completed = new KFSMTimedEvent("Jump (On Hull) Complete", 0.3);
            On_jump_hull_completed.GoToStateOnEvent = Kerbal.st_idle_fl;
            On_jump_hull_completed.OnEvent = jump_hull_Completed;
            FSM.AddEvent(On_jump_hull_completed, st_jump_hull);

            // Stock Ladder Let Go Event augment
            Kerbal.On_ladderLetGo.OnEvent += delegate
            {
                if (Settings.magbootsAutoToggleEnabled)
                {
                    SetAG(KSPActionGroup.Gear, true);
                }
                Kerbal.StartCoroutine(On_ladderLetGo_Coroutine());
            };

            // Zero movement and record origin when flag plant starts from hull
            Kerbal.On_flagPlantStart.OnEvent -= On_flagPlantStart_Hull_Hook;
            Kerbal.On_flagPlantStart.OnEvent += On_flagPlantStart_Hull_Hook;

            // Run hull grip physics during both flag FSM states
            Kerbal.st_flagAcquireHeading.OnFixedUpdate -= flag_hull_OnFixedUpdate;
            Kerbal.st_flagAcquireHeading.OnFixedUpdate += flag_hull_OnFixedUpdate;
            Kerbal.st_flagPlant.OnFixedUpdate -= flag_hull_OnFixedUpdate;
            Kerbal.st_flagPlant.OnFixedUpdate += flag_hull_OnFixedUpdate;

            // Redirect flag-plant completion back to st_idle_hull
            Kerbal.On_flagPlantComplete.OnEvent -= On_flagPlantComplete_Hull_Redirect;
            Kerbal.On_flagPlantComplete.OnEvent += On_flagPlantComplete_Hull_Redirect;

            // EVA Science on Hull – allow golf to trigger from hull states
            FSM.AddEvent(Kerbal.On_Playing_Golf, st_idle_hull);
            FSM.AddEvent(Kerbal.On_Playing_Golf, st_walk_hull);

            // Unsubscribe before subscribing so FSM recreation never double-registers.

            // Zero movement when golf is triggered from hull
            Kerbal.On_Playing_Golf.OnEvent -= On_Playing_Golf_Hull_Hook;
            Kerbal.On_Playing_Golf.OnEvent += On_Playing_Golf_Hull_Hook;

            // Run hull grip physics during the golf animation
            Kerbal.st_playing_golf.OnFixedUpdate -= playing_golf_hull_OnFixedUpdate;
            Kerbal.st_playing_golf.OnFixedUpdate += playing_golf_hull_OnFixedUpdate;

            // Redirect golf completion back to st_idle_hull instead of the vanilla ground state
            Kerbal.On_Golf_Complete.OnEvent -= On_Golf_Complete_Hull_Redirect;
            Kerbal.On_Golf_Complete.OnEvent += On_Golf_Complete_Hull_Redirect;

            // EVA Construction pipeline
            FSM.AddEvent(Kerbal.On_constructionModeEnter, st_idle_hull);
            FSM.AddEvent(Kerbal.On_constructionModeEnter, st_walk_hull);
            FSM.AddEvent(Kerbal.On_constructionModeExit, st_idle_hull);
            FSM.AddEvent(Kerbal.On_constructionModeExit, st_walk_hull);

            Kerbal.st_enteringConstruction.OnFixedUpdate -= construction_hull_OnFixedUpdate;
            Kerbal.st_enteringConstruction.OnFixedUpdate += construction_hull_OnFixedUpdate;
            Kerbal.st_exitingConstruction.OnFixedUpdate -= construction_hull_OnFixedUpdate;
            Kerbal.st_exitingConstruction.OnFixedUpdate += construction_hull_OnFixedUpdate;

            Kerbal.On_constructionModeEnter.OnEvent -= On_ConstructionEnter_Hull_Hook;
            Kerbal.On_constructionModeEnter.OnEvent += On_ConstructionEnter_Hull_Hook;
            Kerbal.On_constructionModeExit.OnEvent -= On_ConstructionExit_Hull_Hook;
            Kerbal.On_constructionModeExit.OnEvent += On_ConstructionExit_Hull_Hook;
            Kerbal.On_constructionModeTrigger_fl_Complete.OnEvent -= On_ConstructionComplete_Hull_Redirect;
            Kerbal.On_constructionModeTrigger_fl_Complete.OnEvent += On_ConstructionComplete_Hull_Redirect;
            Kerbal.On_constructionModeTrigger_gr_Complete.OnEvent -= On_ConstructionComplete_Hull_Redirect;
            Kerbal.On_constructionModeTrigger_gr_Complete.OnEvent += On_ConstructionComplete_Hull_Redirect;

            // Welding on hull – allow weld to trigger from hull states
            FSM.AddEvent(Kerbal.On_weldStart, st_idle_hull);
            FSM.AddEvent(Kerbal.On_weldStart, st_walk_hull);

            // Zero movement and set flag when weld is triggered from hull
            Kerbal.On_weldStart.OnEvent -= On_weldStart_Hull_Hook;
            Kerbal.On_weldStart.OnEvent += On_weldStart_Hull_Hook;

            // Run hull grip physics during weld acquire-heading and weld animation
            Kerbal.st_weldAcquireHeading.OnFixedUpdate -= weld_hull_OnFixedUpdate;
            Kerbal.st_weldAcquireHeading.OnFixedUpdate += weld_hull_OnFixedUpdate;
            Kerbal.st_weld.OnFixedUpdate -= weld_hull_OnFixedUpdate;
            Kerbal.st_weld.OnFixedUpdate += weld_hull_OnFixedUpdate;

            // Hull-relative heading update during weld acquire-heading
            Kerbal.st_weldAcquireHeading.OnLateUpdate -= weldAcquireHeading_hull_OnLateUpdate;
            Kerbal.st_weldAcquireHeading.OnLateUpdate += weldAcquireHeading_hull_OnLateUpdate;

            // Redirect weld completion back to st_idle_hull
            Kerbal.On_weldComplete.OnEvent -= On_weldComplete_Hull_Redirect;
            Kerbal.On_weldComplete.OnEvent += On_weldComplete_Hull_Redirect;
        }

        private ConfigurableJoint _hullAnchorJoint;
        private bool ShouldUseHullAnchor()
        {
            if (_hullAnchorJoint != null) return false;
            //if (FSM.CurrentState != st_idle_hull) return false;
            if (!_hullTarget.IsValid()) return false;
            if (Part?.rb == null || _hullTarget.rigidbody == null) return false;

            // check if kerbal is trying to move via inputs (zero)
            if (tgtRpos.sqrMagnitude > VECTOR_ZERO_THRESHOLD)
            {
                return false;
            }

            if (PartHasMovingColliderRisk(_hullTarget.part)) return false;

            // Normal stability only. Keep this loose.
            if (_hullSmoothingValid && _hullTarget.hitNormal.sqrMagnitude > VECTOR_ZERO_THRESHOLD)
            {
                float normalDelta = Vector3.Angle(_smoothedHullNormal, _hullTarget.hitNormal.normalized);
                if (normalDelta > HULL_ANCHOR_MAX_NORMAL_DELTA)
                {
                    return false;
                }
            }
            return true;
        }

        private bool TryGetExactPadRelativeVelocity(out Vector3 relVel, out float relN, out float relT)
        {
            relVel = Vector3.zero;
            relN = 0f;
            relT = 0f;

            if (!_hullTarget.IsValid() || Part?.rb == null || _hullTarget.rigidbody == null)
                return false;

            Vector3 n = _hullSmoothingValid && _smoothedHullNormal.sqrMagnitude > VECTOR_ZERO_THRESHOLD
                ? _smoothedHullNormal.normalized
                : _hullTarget.hitNormal.normalized;

            Vector3 contactPoint = Part.rb.position - n * FootHullPad;

            Vector3 kerbalVel = Part.rb.velocity;
            Vector3 surfaceVel = _hullTarget.rigidbody.GetPointVelocity(contactPoint);

            relVel = kerbalVel - surfaceVel;
            relN = Mathf.Abs(Vector3.Dot(relVel, n));
            relT = Vector3.ProjectOnPlane(relVel, n).magnitude;

            return true;
        }

        private void TryAddHullAnchor()
        {
            if (_hullAnchorJoint != null)
                return;

            if (!ShouldUseHullAnchor())
            {
                _hullAnchorTimer = 0f;
                return;
            }

            _hullAnchorTimer += Time.fixedDeltaTime;
            if (_hullAnchorTimer < HULL_ANCHOR_DELAY)
                return;

            SnapToHullPad();
            AddHullAnchor();
        }

        private bool _anchorBrokenByPhysics = false;

        private void AddHullAnchor()
        {
            if (_hullAnchorJoint != null) return;
            if (!_hullTarget.IsValid()) return;
            if (Part.rb == null || _hullTarget.rigidbody == null) return;

            var j = Part.rb.gameObject.AddComponent<ConfigurableJoint>();
            j.connectedBody = _hullTarget.rigidbody;
            j.autoConfigureConnectedAnchor = true;

            j.autoConfigureConnectedAnchor = false;
            //Vector3 contactPointWorld = Part.rb.position - (Part.rb.rotation * (Part.rb.centerOfMass));
            Vector3 contactPointWorld = Part.rb.position - fUp.normalized * FootHullPad;
            j.anchor = Part.rb.transform.InverseTransformPoint(contactPointWorld);
            j.connectedAnchor = _hullTarget.rigidbody.transform.InverseTransformPoint(contactPointWorld);

            j.xMotion = ConfigurableJointMotion.Locked;
            j.yMotion = ConfigurableJointMotion.Locked;
            j.zMotion = ConfigurableJointMotion.Locked;

            j.angularXMotion = ConfigurableJointMotion.Locked;
            j.angularYMotion = ConfigurableJointMotion.Locked;
            j.angularZMotion = ConfigurableJointMotion.Locked;

            j.enableCollision = true;
            
            if (Constants.parkingBrakeMaxG == 0)
            {
                j.breakForce = Mathf.Infinity;
                j.breakTorque = Mathf.Infinity;
            }
            else
            {
                j.breakForce = Part.rb.mass * Constants.parkingBrakeMaxG * 9.81f; // F = m * a; a = maxG * gravity
                j.breakTorque = j.breakForce;
            }

            j.projectionMode = JointProjectionMode.PositionAndRotation;
            j.projectionDistance = 0.02f;
            j.projectionAngle = 3f;

            j.massScale = 10f;
            j.connectedMassScale = 1f;

            _hullAnchorJoint = j;

            Logger.Debug($"Parking brake joint created with break force {_hullAnchorJoint.breakForce} and break torque {_hullAnchorJoint.breakTorque}");

            _anchorBrokenByPhysics = false;
            var detector = Part.rb.gameObject.AddComponent<HullAnchorBreakDetector>();
            detector.OnBroken = () =>
            {
                _anchorBrokenByPhysics = true;
                _hullAnchorJoint = null;
            };
        }


        private void SnapToHullPad()
        {
            if (!_hullTarget.IsValid() || Part.rb == null) return;

            Vector3 normal = _hullSmoothingValid ? _smoothedHullNormal.normalized : _hullTarget.hitNormal.normalized;
            float dist = _hullSmoothingValid ? _smoothedHitDistance : _hullTarget.hitDistance;
            float error = FootHullPad - dist;

            if (error <= 0f)
                return;

            Part.rb.position += normal * error;

            Vector3 contactPoint = Part.rb.position - normal * FootHullPad;
            Vector3 surfaceVel = _hullTarget.rigidbody != null
                ? _hullTarget.rigidbody.GetPointVelocity(contactPoint)
                : Vector3.zero;

            Vector3 relVel = Part.rb.velocity - surfaceVel;
            float vn = Vector3.Dot(relVel, normal);

            // Kill only inward motion.
            if (vn < 0f)
                Part.rb.velocity -= normal * vn;

            Physics.SyncTransforms();
        }

        private bool PartHasMovingColliderRisk(Part p)
        {
            if (p == null) return false;

            for (int i = 0; i < p.Modules.Count; i++)
            {
                PartModule m = p.Modules[i];

                if (m is IScalarModule scalar && scalar.IsMoving())
                    return true;

                if (m is ModuleAnimateGeneric anim &&
                    anim.aniState == ModuleAnimateGeneric.animationStates.MOVING)
                    return true;
            }

            return false;
        }

        private void RemoveHullAnchor()
        {
            _anchorBrokenByPhysics = false;
            if (Part?.rb != null)
            {
                var detector = Part.rb.gameObject.GetComponent<HullAnchorBreakDetector>();
                if (detector != null) Destroy(detector);
            }
            if (_hullAnchorJoint != null)
                Destroy(_hullAnchorJoint);
            _hullAnchorJoint = null;
            _hullAnchorTimer = 0f;
        }

        private void ValidateHullAnchor()
        {
            if (_anchorBrokenByPhysics)
            {
                PostAnchorBrakeMsg();
                _anchorBrokenByPhysics = false;
                _hullAnchorTimer = 0f;
                return;
            }


            if (_hullAnchorJoint == null) return;

            if (!_hullTarget.IsValid())
            {
                RemoveHullAnchor();
                return;
            }

            if (tgtRpos != Vector3.zero)
            {
                RemoveHullAnchor();
                return;
            }

            if (PartHasMovingColliderRisk(_hullTarget.part))
            {
                RemoveHullAnchor();
                return;
            }
        }

        protected virtual bool ShouldEnterHullIdle()
        {
            if (!this.enabled || _inLetGoCooldown) return false;

            RefreshHullTarget_DoProbe();

            if (!_hullTarget.IsValid()) return false;

            if (Settings.magbootsRequireMicrogravity && !IsAboveHighAltitude()) return false;
            if (HullTargeting.GetRelativeSpeedToHullPoint(_hullTarget, base.part) > Kerbal.stumbleThreshold) return false;

            // Don't attach while commanding upward jetpack thrust (relative to Kerbal up)
            if (Kerbal != null && Kerbal.JetpackDeployed && Kerbal.Fuel > 0.0 && Kerbal.thrustPercentage > 0f)
            {
                if (Vector3.Dot(packTgtRPos, Kerbal.transform.up) > JETPACK_UP_THRUST_ATTACH_THRESHOLD)
                    return false;
            }

            return true;
        }

        public void ClearHullTarget()
        {
            _hullTarget = default;
            _hullTransform = null;
            _surfaceVelCount = 0;
            _hullSmoothingValid = false;
            _smoothedHullNormalLocalValid = false;
            _smoothedHullNormalLocal = Vector3.up;
            RemoveHullAnchor();
        }

        protected virtual bool ShouldExitHullIdle()
        {
            if (!_hullTarget.IsValid()) { ClearHullTarget(); return true; }

            if (Settings.magbootsRequireMicrogravity && !IsAboveHighAltitude()) { ClearHullTarget(); return true; }
            if (HullTargeting.GetRelativeSpeedToHullPoint(_hullTarget, base.part) > Kerbal.stumbleThreshold) { ClearHullTarget(); return true; }

            return false;
        }

        protected virtual void idle_hull_OnEnter(KFSMState s)
        {
            Kerbal.Events["PlantFlag"].active = false;
            tgtSpeed = 0f;
            currentSpd = 0f;
            KerbalEVAAccess.KerbalAnchorTimeCounter(Kerbal) = 0f;
            RefreshHullTarget();
            OrientToSurfaceNormal();

            // Allow repacking chute while on hull
            if (KerbalEVAAccess.EvaChute(Kerbal) != null)
            {
                KerbalEVAAccess.EvaChute(Kerbal).AllowRepack(allowRepack: Settings.magbootsRepackChuteEnabled);
            }

            _animation.CrossFade(Kerbal.Animations.idle, ANIMATION_CROSSFADE_TIME_LONG, PlayMode.StopSameLayer);
            if (_hullTarget.part != null)
            {
                _hullTransform = _hullTarget.part.transform;
                _localHullForward = _hullTransform.InverseTransformDirection(Part.rb.transform.forward); //
            }

            RemoveHullAnchor();
            _hullAnchorTimer = 0f;
        }

        // Stock-alike but using the Kerbal-relative coordinate frame
        protected virtual void walk_hull_OnUpdate()
        {
            float fwdDot = Vector3.Dot(tgtRpos, Kerbal.transform.forward);
            float fwdPos = Mathf.Clamp01(fwdDot);
            float fwdNeg = Mathf.Clamp01(-fwdDot);

            float rightDot = Vector3.Dot(tgtRpos, Kerbal.transform.right);
            float rightPos = Mathf.Clamp01(rightDot);
            float rightNeg = Mathf.Clamp01(-rightDot);

            tgtSpeed = Kerbal.walkSpeed * (fwdPos + fwdNeg) + Kerbal.strafeSpeed * (rightPos + rightNeg);

            if (fwdPos > MIN_MOVEMENT_THRESHOLD)
            {
                _animation.CrossFade(Kerbal.Animations.walkFwd, ANIMATION_CROSSFADE_TIME);
                _animation.Blend(Kerbal.Animations.walkLowGee,
                    Mathf.InverseLerp(1f, Kerbal.minWalkingGee, (float)Kerbal.vessel.mainBody.GeeASL));
                Kerbal.Animations.walkLowGee.State.speed = LOW_GEE_ANIMATION_SPEED;

                if (rightPos > 0f)
                    _animation.Blend(Kerbal.Animations.strafeRight, rightPos);
                else if (rightNeg > 0f)
                    _animation.Blend(Kerbal.Animations.strafeLeft, rightNeg);
            }
            else if (fwdNeg > MIN_MOVEMENT_THRESHOLD)
            {
                _animation.CrossFade(Kerbal.Animations.walkBack, ANIMATION_CROSSFADE_TIME);

                if (rightPos > 0f)
                    _animation.Blend(Kerbal.Animations.strafeRight, rightPos);
                else if (rightNeg > 0f)
                    _animation.Blend(Kerbal.Animations.strafeLeft, rightNeg);
            }
            else if (rightPos > MIN_MOVEMENT_THRESHOLD)
            {
                _animation.CrossFade(Kerbal.Animations.strafeRight, ANIMATION_CROSSFADE_TIME);
            }
            else if (rightNeg > MIN_MOVEMENT_THRESHOLD)
            {
                _animation.CrossFade(Kerbal.Animations.strafeLeft, ANIMATION_CROSSFADE_TIME);
            }
        }

        protected virtual void walk_hull_OnLeave(KFSMState s)
        {
            lastTgtSpeed = tgtSpeed;
        }
        protected virtual void jump_hull_OnEnter(KFSMState st)
        {
            if (tgtSpeed < MIN_SPEED_THRESHOLD)
            {
                // standing jump
                On_jump_hull_completed.TimerDuration = JUMP_STILL_DURATION;
                Kerbal.Animations.JumpStillStart.State.time = JUMP_STILL_TIME_OFFSET;
                _animation.CrossFade(Kerbal.Animations.JumpStillStart, ANIMATION_CROSSFADE_TIME, PlayMode.StopAll);
            }
            else
            {
                // running jump
                On_jump_hull_completed.TimerDuration = Kerbal.Animations.JumpFwdStart.end;
                Kerbal.Animations.JumpFwdStart.State.time = Kerbal.Animations.JumpFwdStart.start;
                _animation.CrossFade(Kerbal.Animations.JumpFwdStart, ANIMATION_CROSSFADE_TIME, PlayMode.StopAll);
            }
            RemoveHullAnchor();
        }
        protected virtual void jump_hull_Completed()
        {
            Vector3 impulse = (Kerbal.transform.up * Mathf.Pow(Part.mass / PhysicsGlobals.PerCommandSeatReduction, Kerbal.jumpMultiplier) * Kerbal.maxJumpForce) + (Kerbal.transform.forward * tgtSpeed * Kerbal.massMultiplier);
            Part.AddImpulse(impulse);

            var endAnim = (tgtSpeed < MIN_SPEED_THRESHOLD) ? Kerbal.Animations.JumpStillEnd : Kerbal.Animations.JumpFwdEnd;
            _animation.CrossFade(endAnim, IDLE_ANIMATION_CROSSFADE_TIME, PlayMode.StopAll);
        }


        private IEnumerator On_ladderLetGo_Coroutine()
        {
            GroundSpherecastRadius += LADDER_LETGO_SPHERECAST_RADIUS_BOOST;
            GroundSpherecastLength += LADDER_LETGO_SPHERECAST_LENGTH_BOOST;
            yield return new WaitForSeconds(LADDER_LETGO_SPHERECAST_BOOST_TIME);
            GroundSpherecastRadius -= LADDER_LETGO_SPHERECAST_RADIUS_BOOST;
            GroundSpherecastLength -= LADDER_LETGO_SPHERECAST_LENGTH_BOOST;
        }

        // Helper: zero Kerbal movement state when triggering EVA science from a hull state.
        private void ZeroHullMovementForScience()
        {
            tgtFwd = Vector3.zero;
            tgtRpos = Vector3.zero;
            tgtSpeed = 0f;
            lastTgtSpeed = 0f;
            currentSpd = 0f;
            if (Part.rb != null)
                Part.rb.angularVelocity = Vector3.zero;
        }

        private void On_Playing_Golf_Hull_Hook()
        {
            if (FSM.CurrentState != st_idle_hull && FSM.CurrentState != st_walk_hull) return;
            _golfStartedFromHull = true;
            ZeroHullMovementForScience();
        }

        private void playing_golf_hull_OnFixedUpdate()
        {
            if (!_golfStartedFromHull) return;
            RefreshHullTarget();
            UpdateSmoothedHullSurface();
            OrientToSurfaceNormal();
            UpdateMovementOnVessel();
            ValidateHullAnchor();
        }

        private void On_Golf_Complete_Hull_Redirect()
        {
            if (_golfStartedFromHull)
            {
                Kerbal.On_Golf_Complete.GoToStateOnEvent = st_idle_hull;
                _golfStartedFromHull = false;
            }
            else
            {
                Kerbal.On_Golf_Complete.GoToStateOnEvent = Kerbal.st_idle_gr;
            }
        }

        private void UpdateConstructionFromHullFlag()
        {
            bool inHullState = FSM.CurrentState == st_idle_hull || FSM.CurrentState == st_walk_hull;

            bool inConstructionTransition =
                FSM.CurrentState == Kerbal.st_enteringConstruction ||
                FSM.CurrentState == Kerbal.st_exitingConstruction ||
                FSM.CurrentState == Kerbal.st_weldAcquireHeading ||
                FSM.CurrentState == Kerbal.st_weld;

            if (Kerbal.InConstructionMode && inHullState)
            {
                _constructionFromHull = true;
            }
            else if (!inHullState && !inConstructionTransition)
            {
                _constructionFromHull = false;
            }
        }

        private void construction_hull_OnFixedUpdate()
        {
            UpdateConstructionFromHullFlag();
            if (!_constructionFromHull) return;
            RefreshHullTarget();
            UpdateSmoothedHullSurface();
            OrientToSurfaceNormal();
            UpdateMovementOnVessel();
            ValidateHullAnchor();
            KerbalEVAAccess.CmdRot(Kerbal) = Vector3.zero;
            updateRagdollVelocities();
        }

        private void On_ConstructionEnter_Hull_Hook()
        {
            if (FSM.CurrentState == st_idle_hull || FSM.CurrentState == st_walk_hull)
            {
                _constructionFromHull = true;
                ZeroHullMovementForScience();
            }
        }

        private void On_ConstructionExit_Hull_Hook()
        {
            ZeroHullMovementForScience();
            if (_constructionFromHull && FSM.CurrentState == Kerbal.st_exitingConstruction)
            {
                Kerbal.On_constructionModeTrigger_fl_Complete.GoToStateOnEvent = st_idle_hull;
                Kerbal.On_constructionModeTrigger_gr_Complete.GoToStateOnEvent = st_idle_hull;
                _constructionFromHull = false;
            }

        }

        private void On_ConstructionComplete_Hull_Redirect()
        {
            if (_constructionFromHull && FSM.CurrentState == Kerbal.st_exitingConstruction)
            {
                Kerbal.On_constructionModeTrigger_fl_Complete.GoToStateOnEvent = st_idle_hull;
                Kerbal.On_constructionModeTrigger_gr_Complete.GoToStateOnEvent = st_idle_hull;
                _constructionFromHull = false;
            }
            else
            {
                Kerbal.On_constructionModeTrigger_fl_Complete.GoToStateOnEvent = Kerbal.st_idle_fl;
                Kerbal.On_constructionModeTrigger_gr_Complete.GoToStateOnEvent = Kerbal.st_idle_gr;
            }
        }

        private void On_weldStart_Hull_Hook()
        {
            bool fromHull =
                FSM.CurrentState == st_idle_hull ||
                FSM.CurrentState == st_walk_hull ||
                _constructionFromHull ||
                _hullTarget.IsValid();

            if (!fromHull)
            {
                _weldStartedFromHull = false;
                return;
            }

            _weldStartedFromHull = true;
            _constructionFromHull = true;
            ZeroHullMovementForScience();
        }

        private void weld_hull_OnFixedUpdate()
        {
            if (!_weldStartedFromHull) return;
            RefreshHullTarget();
            UpdateSmoothedHullSurface();
            OrientToSurfaceNormal();
            UpdateMovementOnVessel();
            ValidateHullAnchor();
            KerbalEVAAccess.CmdRot(Kerbal) = Vector3.zero;
            updateRagdollVelocities();
        }

        private void weldAcquireHeading_hull_OnLateUpdate()
        {
            if (!_weldStartedFromHull) return;
            UpdateHeading();
        }

        private void On_weldComplete_Hull_Redirect()
        {
            bool shouldReturnToHull =
                _weldStartedFromHull ||
                _constructionFromHull ||
                _hullTarget.IsValid();

            Logger.Debug($"[Weld] Complete redirect currentState={CurrentFSMStateName} shouldReturnToHull={shouldReturnToHull} weldStartedFromHull={_weldStartedFromHull} constructionFromHull={_constructionFromHull} hullValid={_hullTarget.IsValid()}");

            Kerbal.On_weldComplete.GoToStateOnEvent =
                shouldReturnToHull ? st_idle_hull : Kerbal.st_idle_gr;

            _weldStartedFromHull = false;

            if (!shouldReturnToHull)
                _constructionFromHull = false;
        }

        private void On_flagPlantStart_Hull_Hook()
        {
            if (FSM.CurrentState != st_idle_hull && FSM.CurrentState != st_walk_hull) return;
            _flagStartedFromHull = true;
            ZeroHullMovementForScience();
        }

        private void flag_hull_OnFixedUpdate()
        {
            if (!_flagStartedFromHull) return;
            RefreshHullTarget();
            UpdateSmoothedHullSurface();
            OrientToSurfaceNormal();
            UpdateMovementOnVessel();
        }

        private void On_flagPlantComplete_Hull_Redirect()
        {
            if (_flagStartedFromHull)
            {
                Kerbal.On_flagPlantComplete.GoToStateOnEvent = st_idle_hull;
                _flagStartedFromHull = false;
            }
            else
            {
                Kerbal.On_flagPlantComplete.GoToStateOnEvent = Kerbal.st_idle_gr;
            }
        }

        private void ApplyLetGoImpulse()
        {
            Part.rb.velocity += Kerbal.transform.up * LET_GO_IMPULSE_STRENGTH;
        }

        private void On_letGoFromHull()
        {
            _inLetGoCooldown = true;
            SetEnabled(false);
            ClearHullTarget();
            RemoveHullAnchor();
            ApplyLetGoImpulse();
            Kerbal.Events["PlantFlag"].active = false;
            Kerbal.StartCoroutine(On_letGo_Coroutine(LET_GO_COOLDOWN_TIME));
            Kerbal.StartCoroutine(AutoDeployJetpack_Coroutine(JETPACK_DEPLOY_DELAY_LETGO));
        }

        private IEnumerator On_letGo_Coroutine(float delay = 2.0f)
        {
            yield return new WaitForSeconds(delay);
            SetEnabled(true);
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
            if (Settings.jetpackAutoToggleEnabled && Kerbal.HasJetpack && FSM.CurrentState == Kerbal.st_idle_fl)
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

        private bool _doProbeRay = false;
        protected virtual void RefreshHullTarget_DoProbe()
        {
            _doProbeRay = true;
            try { RefreshHullTarget(); }
            finally { _doProbeRay = false; }
        }

        protected virtual void RefreshHullTarget()
        {
            if (!this.enabled || FSM.CurrentState == st_jump_hull)
            {
                ClearHullTarget();
                return;
            }

            if (!HullTargeting.TryAcquireHullSpherecast(
                Kerbal,
                (!_hullTarget.IsValid() && _doProbeRay), // doProbeRay
                GroundSpherecastUpOffset,
                GroundSpherecastRadius,
                GroundSpherecastLength,
                EngageRadius,
                out HullTarget target))
            {
                ClearHullTarget();
                return;
            }
            _hullTarget = target;
            _hullTransform = _hullTarget.part?.transform;
            _localHullForward = Vector3.zero; // re-init next orient
        }

        protected virtual void UpdateHeading()
        {
            if (base.vessel.packed || !_hullTarget.IsValid()) return;
            if (_hullAnchorJoint != null) return;

            var rb = Part.rb;
            if (rb == null) return;

            Vector3 curFwd = Vector3.ProjectOnPlane(base.transform.forward, fUp);
            Vector3 desFwd = Vector3.ProjectOnPlane(tgtFwd, fUp);

            if (curFwd.sqrMagnitude < VECTOR_ZERO_THRESHOLD || desFwd.sqrMagnitude < VECTOR_ZERO_THRESHOLD) return;

            curFwd.Normalize();
            desFwd.Normalize();

            deltaHdg = Vector3.SignedAngle(curFwd, desFwd, fUp);
            float sign = Mathf.Sign(deltaHdg);

            if (Mathf.Abs(deltaHdg) < turnRate * 2f)
                rb.angularVelocity = deltaHdg * 0.5f * fUp;
            else
                rb.angularVelocity = turnRate * sign * fUp;
        }

        protected virtual void UpdatePackLinear()
        {
            // Same as stock but blocked if not on hull
            if (!Kerbal.JetpackDeployed) return;
            if (base.vessel.packed || Kerbal.isRagdoll || EVAConstructionModeController.MovementRestricted) return;
            if (!_hullTarget.IsValid()) return;

            // Only allow up/down thrust when on hull
            Vector3 vertical = Vector3.Project(packTgtRPos, Kerbal.transform.up);
            packLinear = vertical * (Kerbal.thrustPercentage * 0.01f);
            if (packLinear != Vector3.zero && Kerbal.Fuel > 0.0)
            {
                base.part.AddForce(packLinear * Kerbal.linPower);
                fuelFlowRate += packLinear.magnitude * Time.fixedDeltaTime;
            }

        }

        protected virtual void updateRagdollVelocities()
        {
        }

        // Stock-alike custom implementation replacing correctGroundedRotation() which uses the scene-fixed coordinate frame
        private void OrientToSurfaceNormal()
        {
            if (base.vessel.packed || !_hullTarget.IsValid()) return;
            if (_hullAnchorJoint != null) return;

            var rb = Part.rb;
            if (rb == null) return;

            Vector3 upN = _smoothedHullNormal;
            fUp = upN;

            Vector3 fwdWorld;
            if (_localHullForward.sqrMagnitude > VECTOR_ZERO_THRESHOLD)
                fwdWorld = _hullTransform.TransformDirection(_localHullForward); // forward follows hull rotation
            else
                fwdWorld = rb.rotation * Vector3.forward; // fallback to current forward

            Vector3 fwd = Vector3.ProjectOnPlane(fwdWorld, fUp);
            if (fwd.sqrMagnitude < VECTOR_ZERO_THRESHOLD) return;
            fwd.Normalize();

            Quaternion targetRot = Quaternion.LookRotation(fwd, fUp);
            Quaternion newRot = Quaternion.RotateTowards(
                rb.rotation,
                targetRot,
                ROTATION_RATE_MULTIPLIER * Time.fixedDeltaTime
            );

            Vector3 pivot = rb.worldCenterOfMass;
            Quaternion delta = newRot * Quaternion.Inverse(rb.rotation);

            Vector3 posBefore = rb.position;
            rb.MoveRotation(newRot);
            rb.MovePosition(pivot + (delta * (rb.position - pivot)));
            Vector3 posAfter = rb.position; // MovePosition queues — read back next frame via this trick:

            // Actually MovePosition is queued, so track manually:
            Vector3 expectedPosDelta = (pivot + (delta * (posBefore - pivot))) - posBefore;
            //rb.velocity -= expectedPosDelta / Time.fixedDeltaTime;

            _localHullForward = _hullTransform.InverseTransformDirection(newRot * Vector3.forward); // update local hull forward after turning
        }

        private Vector3 _surfaceVelSMA;
        private int _surfaceVelCount;
        protected virtual void UpdateMovementOnVessel() // Stock-alike custom implementation replacing updateMovementOnVessel() which uses the scene-fixed coordinate frame
        {
            if (base.vessel.packed || !_hullTarget.IsValid()) return;

            if (_hullAnchorJoint != null)
            {
                if (_hullTarget.IsValid())
                {
                    Vector3 anchorContactPoint = Part.rb.position - fUp.normalized * FootHullPad;
                    _surfaceVelSMA = _hullTarget.rigidbody != null
                        ? _hullTarget.rigidbody.GetPointVelocity(anchorContactPoint)
                        : Vector3.zero;
                    _surfaceVelCount = 1;
                }

                return;
            }

            float num = (float)FSM.TimeAtCurrentState;
            num = ((num >= 0.3f) ? 1f : ((!(num > 0f)) ? 0f : (num * 3.3333333f)));
            currentSpd = Mathf.Lerp(lastTgtSpeed, tgtSpeed, num);

            Vector3 desiredDir = (tgtRpos != Vector3.zero) ? tgtRpos : Vector3.zero;
            desiredDir = Vector3.ProjectOnPlane(desiredDir, fUp);
            if (desiredDir.sqrMagnitude > VECTOR_ZERO_THRESHOLD) desiredDir.Normalize();
            Vector3 desiredTangentVel = desiredDir * currentSpd; // desired tangential relative velocity (walking); when idle this is zero.

            var rb = Part.rb;
            if (rb == null) return;

            Vector3 contactPoint = rb.position - fUp.normalized * FootHullPad;

            Vector3 surfaceVelRaw = _hullTarget.rigidbody != null
                ? _hullTarget.rigidbody.GetPointVelocity(contactPoint)
                : Vector3.zero;            // simple EMA works well and is cheap
            float dt = Time.fixedDeltaTime;
            float tau = SURFACE_VELOCITY_SMOOTHING_TAU;
            float alpha = 1f - Mathf.Exp(-dt / tau);
            _surfaceVelSMA = (_surfaceVelCount++ == 0) ? surfaceVelRaw : Vector3.Lerp(_surfaceVelSMA, surfaceVelRaw, alpha);

            Vector3 surfaceVel = _surfaceVelSMA;
            Vector3 v = rb.velocity;
            Vector3 vRel = v - surfaceVel;

            float vn = Vector3.Dot(vRel, fUp);
            Vector3 vRelN = fUp * vn;
            Vector3 vRelT = vRel - vRelN;

            float padError = FootHullPad - _smoothedHitDistance;

            // Optional deadzone to avoid micro-chatter.
            if (Mathf.Abs(padError) < 0.003f)
                padError = 0f;

            // Positive = push outward along fUp.
            // Negative = pull inward toward hull.
            float vnTarget = padError / dt;

            float maxDv = Constants.magbootsClampForce * dt;
            float dv = Mathf.Clamp(vnTarget - vn, -maxDv, maxDv);

            float vnNew = vn + dv;
            Vector3 vRelNNew = fUp * vnNew;


            // control tangential relative velocity toward desiredTangentVel. desiredTangentVel is already in world space tangent to the surface
            Vector3 slip = vRelT - desiredTangentVel;
            float slipMag = slip.magnitude;

            if (slipMag > VECTOR_ZERO_THRESHOLD)
            {
                float reduce = Mathf.Min(slipMag, 16 * dt);
                Vector3 slipNew = slip - slip.normalized * reduce;
                Vector3 vRelTNew = desiredTangentVel + slipNew;

                vRelT = vRelTNew;
            }

            // set new world velocity
            Vector3 vRelNew = vRelT + vRelNNew;
            rb.velocity = surfaceVel + vRelNew;
        }

        private void UpdateHullInputTargets()
        {
            if (!_hullTarget.IsValid() || !VesselUnderControl) return;

            GetCameraTangentBasis(fUp, out var camFwdT, out var camRightT);
            if (camFwdT == Vector3.zero || camRightT == Vector3.zero) return;

            Vector3 basisFwd = Vector3.ProjectOnPlane(Kerbal.transform.forward, fUp);
            if (basisFwd.sqrMagnitude < VECTOR_ZERO_THRESHOLD)
                basisFwd = Vector3.ProjectOnPlane(Kerbal.transform.right, fUp);
            basisFwd.Normalize();

            Vector3 basisRight = Vector3.ProjectOnPlane(Kerbal.transform.right, fUp);
            if (basisRight.sqrMagnitude < VECTOR_ZERO_THRESHOLD)
                basisRight = Vector3.Cross(fUp, basisFwd);
            basisRight.Normalize();


            float v = 0f, h = 0f;
            if (GameSettings.EVA_forward.GetKey()) v += 1f;
            if (GameSettings.EVA_back.GetKey()) v -= 1f;
            if (GameSettings.EVA_right.GetKey()) h += 1f;
            if (GameSettings.EVA_left.GetKey()) h -= 1f;

            Vector3 move = basisFwd * v + basisRight * h;
            if (move.sqrMagnitude > 1f) move.Normalize();
            tgtRpos = move;

            if (Mathf.Abs(v) > 0f || Mathf.Abs(h) > 0f)
            {
                Vector3 heading = basisFwd + basisRight * h;  // ignores v sign
                if (heading.sqrMagnitude < VECTOR_ZERO_THRESHOLD)
                    heading = basisFwd;

                tgtFwd = heading.normalized;
            }
        }
        private void GetCameraTangentBasis(Vector3 surfaceUp, out Vector3 tFwd, out Vector3 tRight)
        {
            // Init outputs
            tFwd = Vector3.zero;
            tRight = Vector3.zero;

            var cam = (FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null)
                ? FlightCamera.fetch.mainCamera.transform
                : (Camera.main != null ? Camera.main.transform : null);

            if (cam == null) return;

            // Use camera forward projected onto the plane
            Vector3 fwd = Vector3.ProjectOnPlane(cam.forward, surfaceUp);
            if (fwd.sqrMagnitude < VECTOR_ZERO_THRESHOLD)
                fwd = Vector3.ProjectOnPlane(cam.up, surfaceUp); // fallback, but only if needed

            if (fwd.sqrMagnitude < VECTOR_ZERO_THRESHOLD) return;
            fwd.Normalize();

            Vector3 right = Vector3.Cross(surfaceUp, fwd);
            if (right.sqrMagnitude < VECTOR_ZERO_THRESHOLD) return;
            right.Normalize();

            tFwd = fwd;
            tRight = right;
        }

        private void OnKerbalBlackedOut(ProtoCrewMember kerbal)
        {
            if (kerbal == null) return;
            if (Crew != null && kerbal != Crew) return;

            Logger.Debug($"[G] {kerbal.name} passed out from G-force, detaching from hull");

            RemoveHullAnchor();
            ClearHullTarget();

            Kerbal.fsm.RunEvent(On_detachFromHull);
        }

        private void OnKerbalInactiveChanged(ProtoCrewMember kerbal, bool wasInactive, bool nowInactive)
        {
            if (kerbal == null) return;
            if (Crew != null && kerbal != Crew) return;

            if (nowInactive && kerbal.outDueToG)
            {
                RemoveHullAnchor();
                ClearHullTarget();
            }
            else if (!nowInactive && !kerbal.outDueToG)
            {
            }
        }


        private void OnVesselUnpacked(Vessel v)
        {
            if (v != vessel && (_hullTarget.part == null || v != _hullTarget.part.vessel)) return;

            ClearHullTarget();
            _surfaceVelCount = 0;
            _surfaceVelSMA = Vector3.zero;
        }

        // Add these fields alongside _hullTarget
        private Vector3 _smoothedHullNormal = Vector3.up;
        private float _smoothedHitDistance = 0f;
        private bool _hullSmoothingValid = false;

        // Tune these:
        private const float NORMAL_SMOOTH_TAU_BASE = 0.06f;  // heavy damping for tiny jitter
        private const float NORMAL_SMOOTH_TAU_FAST = 0.012f; // fast response for real surface changes
        private const float NORMAL_SNAP_ANGLE = 60f;    // hard snap above this (e.g. going over an edge)
        private const float DIST_SMOOTH_TAU = 0.04f;  // smoother than normal — distance is noisier
        protected virtual void UpdateSmoothedHullSurface()
        {
            if (!_hullTarget.IsValid())
            {
                _hullSmoothingValid = false;
                return;
            }

            float dt = Time.fixedDeltaTime;
            Vector3 rawNormal = _hullTarget.hitNormal.normalized;
            float rawDist = _hullTarget.hitDistance;

            if (!_hullSmoothingValid)
            {
                _smoothedHullNormal = rawNormal;
                _smoothedHitDistance = rawDist;
                _hullSmoothingValid = true;
                return;
            }

            // Angle-adaptive EMA on normal — same logic as the camera fix
            float angleDiff = Vector3.Angle(_smoothedHullNormal, rawNormal);
            if (angleDiff >= NORMAL_SNAP_ANGLE)
            {
                _smoothedHullNormal = rawNormal;
            }
            else
            {
                float t = Mathf.InverseLerp(1f, 20f, angleDiff);
                float tau = Mathf.Lerp(NORMAL_SMOOTH_TAU_BASE, NORMAL_SMOOTH_TAU_FAST, t);
                float alpha = 1f - Mathf.Exp(-dt / tau);
                _smoothedHullNormal = Vector3.Slerp(_smoothedHullNormal, rawNormal, alpha).normalized;
            }

            // Simple EMA on distance — kills the vnTarget amplification
            float alphaDist = 1f - Mathf.Exp(-dt / DIST_SMOOTH_TAU);
            _smoothedHitDistance = Mathf.Lerp(_smoothedHitDistance, rawDist, alphaDist);

            // Future-cast anticipation: only while actively walking
            bool isWalking = tgtRpos.sqrMagnitude > VECTOR_ZERO_THRESHOLD && FSM.CurrentState == st_walk_hull;

            if (isWalking)
            {
                float lookahead = Mathf.Lerp(
                    FUTURE_LOOKAHEAD_MIN, FUTURE_LOOKAHEAD_MAX,
                    Mathf.InverseLerp(0f, Kerbal.walkSpeed, currentSpd));

                if (TryGetFutureNormal(lookahead,
                        out Vector3 futureNormal,
                        out float upComp,
                        out float distAhead)
                    && upComp <= FUTURE_CONCAVE_CUTOFF)          // concave only
                {
                    // proximity: 0 when far, 1 when the future hit is right at the foot
                    float proximity = Mathf.Clamp01(1f - distAhead / lookahead);

                    // Gentle progressive nudge — not a snap, not a full override
                    float alpha = proximity * FUTURE_BLEND_MAX
                                * (1f - Mathf.Exp(-dt / FUTURE_SMOOTH_TAU));

                    _smoothedHullNormal = Vector3.Slerp(
                        _smoothedHullNormal, futureNormal, alpha).normalized;
                }
            }
            if (_hullTransform != null && _hullSmoothingValid)
            {
                _smoothedHullNormalLocal =
                    _hullTransform.InverseTransformDirection(_smoothedHullNormal).normalized;

                _smoothedHullNormalLocalValid = true;
            }
            else
            {
                _smoothedHullNormalLocalValid = false;
            }
        }


        private const float FUTURE_LOOKAHEAD_MIN = 0.45f;
        private const float FUTURE_LOOKAHEAD_MAX = 0.60f;
        private const float FUTURE_CAST_UP_BIAS = 0.30f;
        private const float FUTURE_CAST_LENGTH = 0.60f;
        private const float FUTURE_FOOT_SPHERE_RADIUS = 0.13f;
        private const float FUTURE_CONCAVE_CUTOFF = 0.04f;
        private const float FUTURE_BLEND_MAX = 0.50f;
        private const float GAP_REJECT_DEPTH = 0.40f;
        private const float FUTURE_SMOOTH_TAU = 0.07f;

        private const float HELMET_HEIGHT = 0.41f;
        private const float HELMET_SPHERE_RADIUS = 0.6f;
        private const float HELMET_FORWARD_MARGIN = 0.30f;

        private bool TryGetFutureNormal(
            float lookaheadDist,
            out Vector3 futureNormal,
            out float upComponent,
            out float distAhead)
        {
            futureNormal = Vector3.zero;
            upComponent = float.MaxValue;
            distAhead = lookaheadDist;

            if (!_hullTarget.IsValid() || Kerbal?.footPivot == null) return false;

            Vector3 moveDir = Vector3.ProjectOnPlane(tgtRpos, _smoothedHullNormal);
            if (moveDir.sqrMagnitude < VECTOR_ZERO_THRESHOLD) return false;
            moveDir.Normalize();

            Vector3 footPos = Kerbal.footPivot.position;

            // --- Foot downcast (finds floor transitions ahead) ---
            bool footHit = false;
            Vector3 footNormal = Vector3.zero;
            float footUpComp = float.MaxValue;
            float footDistAhead = lookaheadDist;

            Vector3 footCastFrom = footPos
                                 + moveDir * lookaheadDist
                                 + _smoothedHullNormal * FUTURE_CAST_UP_BIAS;

            if (Physics.SphereCast(footCastFrom, FUTURE_FOOT_SPHERE_RADIUS,
                    -_smoothedHullNormal, out RaycastHit footRayHit,
                    FUTURE_CAST_LENGTH, HullTargeting.HullMask, QueryTriggerInteraction.Ignore)
                && IsValidHullPart(footRayHit.collider))
            {
                Vector3 toHit = footRayHit.point - footPos;
                float upComp = Vector3.Dot(toHit, _smoothedHullNormal);

                if (upComp >= -GAP_REJECT_DEPTH)  // not a gap
                {
                    footNormal = footRayHit.normal.normalized;
                    footUpComp = upComp;
                    footDistAhead = Vector3.ProjectOnPlane(toHit, _smoothedHullNormal).magnitude;
                    footHit = true;
                }
            }

            // --- Helmet forward cast (finds walls at head height) ---
            bool helmetHit = false;
            Vector3 helmetNormal = Vector3.zero;
            float helmetUpComp = float.MaxValue;
            float helmetDistAhead = lookaheadDist;

            Vector3 helmetPos = footPos + _smoothedHullNormal * HELMET_HEIGHT;

            if (Physics.SphereCast(helmetPos, HELMET_SPHERE_RADIUS,
                    moveDir, out RaycastHit helmetRayHit,
                    lookaheadDist + HELMET_FORWARD_MARGIN,
                    HullTargeting.HullMask, QueryTriggerInteraction.Ignore)
                && IsValidHullPart(helmetRayHit.collider))
            {
                // Wall must be vaugely facing the player to be relevant — avoid picking up surfaces alongside the walkable floor
                float facingDot = Vector3.Dot(helmetRayHit.normal, -moveDir);
                if (facingDot >= -.25f) 
                {
                    Vector3 toHelmetHit = helmetRayHit.point - footPos;
                    float hUpComp = Vector3.Dot(toHelmetHit, _smoothedHullNormal);
                    float hDistAhead = Vector3.ProjectOnPlane(toHelmetHit, _smoothedHullNormal).magnitude;

                    // Only if below current plane
                    //if (hUpComp <= FUTURE_CONCAVE_CUTOFF)
                    //{
                        helmetNormal = helmetRayHit.normal.normalized;
                        helmetUpComp = hUpComp;
                        helmetDistAhead = hDistAhead;
                        helmetHit = true;
                    //}
                }
            }

            // Helmet cast wins if foot cast missed entirely, or helmet is closer (head hits first)
            // Foot cast wins for floor-to-floor transitions where the helmet misses the new surface
            if (helmetHit) //&& (!footHit || helmetDistAhead <= footDistAhead))
            {
                futureNormal = helmetNormal;
                upComponent = helmetUpComp;
                distAhead = helmetDistAhead;
                return upComponent <= FUTURE_CONCAVE_CUTOFF;
            }

            if (footHit)
            {
                futureNormal = footNormal;
                upComponent = footUpComp;
                distAhead = footDistAhead;
                return upComponent <= FUTURE_CONCAVE_CUTOFF;
            }

            return false;
        }

        // Extracted so both cast paths share identical part validation
        private bool IsValidHullPart(Collider col)
        {
            if (col == null) return false;
            Part p = col.GetComponentInParent<Part>();
            if (p == null || p == Kerbal.part) return false;
            if (p.FindModuleImplementing<ModuleG3NoAttach>() != null) return false;
            if (!Settings.magbootsAsteroidsEnabled &&
                p.FindModuleImplementing<ModuleAsteroid>() != null) return false;
            return true;
        }

        //VELOCITYMATCH
        private const float VM_DEAD_ZONE = 0.05f; // m/s — don't chatter at rest
        internal Vector3 GetVelocityMatchContribution(Vector3 playerPackTgtRPos)
        {
            if (Kerbal == null) return Vector3.zero;
            if (vessel == null || vessel.mainBody == null) return Vector3.zero;
            if (vessel != FlightGlobals.ActiveVessel) return Vector3.zero;

            if (!Kerbal.HasJetpack) return Vector3.zero;
            if (!Kerbal.JetpackDeployed) return Vector3.zero;
            if (Kerbal.Fuel <= 0.0) return Vector3.zero;

            // Dumb/manual priority: any player jetpack input cancels matching this frame.
            if (playerPackTgtRPos.sqrMagnitude > 0.001f)
                return Vector3.zero;

            if (!GameSettings.BRAKES.GetKey())
                return Vector3.zero;

            Vessel target = GetTargetVessel();
            if (target == null) return Vector3.zero;

            var rb = part?.rb;
            if (rb == null) return Vector3.zero;

            Vector3 relVel = (Vector3)(vessel.GetObtVelocity() - target.GetObtVelocity());
            float relSpeed = relVel.magnitude;

            if (relSpeed < VM_DEAD_ZONE)
                return Vector3.zero;

            float thrustPct = Kerbal.thrustPercentage * 0.01f;
            if (thrustPct <= 0f) return Vector3.zero;
            if (Kerbal.linPower <= 0f) return Vector3.zero;

            Vector3 thrustDir = -relVel.normalized;

            float exactScale =
                (relSpeed * Mathf.Max(rb.mass, 0.001f)) /
                (Kerbal.linPower * Time.fixedDeltaTime);

            float thrustScale = Mathf.Min(thrustPct, exactScale) * 0.8f;

            return thrustDir * (thrustScale / thrustPct);
        }

        private Vessel GetTargetVessel()
        {
            var fetch = FlightGlobals.fetch;
            if (fetch == null) return null;

            var target = fetch.VesselTarget;
            if (target == null) return null;

            Vessel targetVessel = target as Vessel;

            if (targetVessel == null)
            {
                try { targetVessel = target.GetVessel(); }
                catch { return null; }
            }

            if (targetVessel == null) return null;
            if (targetVessel == vessel) return null;
            if (targetVessel.packed) return null;
            if (targetVessel.mainBody == null) return null;

            return targetVessel;
        }

    }

}