using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace G3MagnetBoots
{
    internal static class KerbalEVAAccess
    {
        // private methods
        internal static readonly Action<KerbalEVA> _updatePackLinear = AccessTools.MethodDelegate<Action<KerbalEVA>>(AccessTools.Method(typeof(KerbalEVA), "UpdatePackLinear", Type.EmptyTypes));
        internal static void UpdatePackLinear(KerbalEVA eva){ _updatePackLinear(eva); }

        internal static readonly Action<KerbalEVA, string, float> _postInteractionScreenMessage =
            AccessTools.MethodDelegate<Action<KerbalEVA, string, float>>(AccessTools.Method(typeof(KerbalEVA), "PostInteractionScreenMessage", new Type[] { typeof(string), typeof(float) }));
        internal static void PostInteractionScreenMessage(KerbalEVA eva, string msg, float duration) { _postInteractionScreenMessage(eva, msg, duration); }

        // private fields
        internal static readonly AccessTools.FieldRef<KerbalEVA, float> _currentSpd =
            AccessTools.FieldRefAccess<KerbalEVA, float>("currentSpd");

        internal static readonly AccessTools.FieldRef<KerbalEVA, float> _lastTgtSpeed =
            AccessTools.FieldRefAccess<KerbalEVA, float>("lastTgtSpeed");

        internal static readonly AccessTools.FieldRef<KerbalEVA, float> _tgtSpeed =
            AccessTools.FieldRefAccess<KerbalEVA, float>("tgtSpeed");

        internal static readonly AccessTools.FieldRef<KerbalEVA, Vector3> _cmdDir =
            AccessTools.FieldRefAccess<KerbalEVA, Vector3>("cmdDir");

        internal static readonly AccessTools.FieldRef<KerbalEVA, Vector3> _fUp =
            AccessTools.FieldRefAccess<KerbalEVA, Vector3>("fUp");

        internal static readonly AccessTools.FieldRef<KerbalEVA, Vector3> _tgtRpos =
            AccessTools.FieldRefAccess<KerbalEVA, Vector3>("tgtRpos");

        internal static readonly AccessTools.FieldRef<KerbalEVA, Vector3> _tgtFwd =
            AccessTools.FieldRefAccess<KerbalEVA, Vector3>("tgtFwd");

        internal static readonly AccessTools.FieldRef<KerbalEVA, float> _deltaHdg =
            AccessTools.FieldRefAccess<KerbalEVA, float>("deltaHdg");

        internal static readonly AccessTools.FieldRef<KerbalEVA, float> _turnRate =
            AccessTools.FieldRefAccess<KerbalEVA, float>("turnRate");

        internal static readonly AccessTools.FieldRef<KerbalEVA, Animation> _animation =
            AccessTools.FieldRefAccess<KerbalEVA, Animation>("_animation");

        internal static readonly AccessTools.FieldRef<KerbalEVA, Vector3> _packLinear =
            AccessTools.FieldRefAccess<KerbalEVA, Vector3>("packLinear");

        internal static readonly AccessTools.FieldRef<KerbalEVA, Vector3> _packTgtRPos =
            AccessTools.FieldRefAccess<KerbalEVA, Vector3>("packTgtRPos");

        internal static readonly AccessTools.FieldRef<KerbalEVA, float> _fuelFlowRate =
            AccessTools.FieldRefAccess<KerbalEVA, float>("fuelFlowRate");

        internal static readonly AccessTools.FieldRef<KerbalEVA, List<Collider>> _currentLadderTriggers =
            AccessTools.FieldRefAccess<KerbalEVA, List<Collider>>("currentLadderTriggers");

        internal static ref float CurrentSpd(KerbalEVA eva) => ref _currentSpd(eva);
        internal static ref float LastTgtSpeed(KerbalEVA eva) => ref _lastTgtSpeed(eva);
        internal static ref float TgtSpeed(KerbalEVA eva) => ref _tgtSpeed(eva);

        internal static ref Vector3 CmdDir(KerbalEVA eva) => ref _cmdDir(eva);
        internal static ref Vector3 FUp(KerbalEVA eva) => ref _fUp(eva);
        internal static ref Vector3 TgtRpos(KerbalEVA eva) => ref _tgtRpos(eva);
        internal static ref Vector3 TgtFwd(KerbalEVA eva) => ref _tgtFwd(eva);
        internal static ref float DeltaHdg(KerbalEVA eva) => ref _deltaHdg(eva);
        internal static ref float TurnRate(KerbalEVA eva) => ref _turnRate(eva);
        internal static ref Animation Animation(KerbalEVA eva) => ref _animation(eva);
        internal static ref List<Collider> CurrentLadderTriggers(KerbalEVA eva) => ref _currentLadderTriggers(eva);
        internal static ref Vector3 PackLinear(KerbalEVA eva) => ref _packLinear(eva);
        internal static ref Vector3 PackTgtRPos(KerbalEVA eva) => ref _packTgtRPos(eva);
        internal static ref float FuelFlowRate(KerbalEVA eva) => ref _fuelFlowRate(eva);
    }
}
