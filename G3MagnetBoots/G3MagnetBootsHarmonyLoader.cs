using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace G3MagnetBoots
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class G3MagnetBootsHarmonyLoader : MonoBehaviour
    {
        private void Awake()
        {
            try
            {
                var h = new Harmony("EVABatteryPwr.EVAMagBoots");
                h.PatchAll();

                var m = AccessTools.Method(typeof(KerbalEVA), "SetupFSM");
                var info = Harmony.GetPatchInfo(m);
                Logger.Debug($"[MagBoots] PatchAll done. SetupFSM patched={info != null}; owners={string.Join(",", info != null ? info.Owners : Array.Empty<string>())}");
            }
            catch (Exception ex)
            {
                Logger.Exception(ex);
            }
        }
    }

    [HarmonyPatch(typeof(KerbalEVA), "SetupFSM")]
    internal static class Patch_KerbalEVA_SetupFSM
    {
        static void Postfix(KerbalEVA __instance)
        {
            if (__instance == null) return;

            Logger.Debug($"[MagBoots] KerbalEVA.SetupFSM Postfix eva={__instance.name} part={__instance.part?.partName} fsmNull={__instance.fsm == null}");

            var part = __instance.part;
            if (part == null) return;

            // First try: module is on the EVA part
            var mod = part.FindModuleImplementing<G3MagnetBootsModule>();

            // Fallback: module is on some other part in the EVA vessel (MM patch mismatch)
            if (mod == null)
                mod = __instance.vessel?
                    .FindPartModulesImplementing<G3MagnetBootsModule>()
                    .FirstOrDefault();

            if (mod == null)
            {
                // Tell yourself why it no-opped
                Logger.Debug("[MagBoots] ModuleEVAMagneticBoots not found on EVA part or EVA vessel. Modules on eva part: " +
                             string.Join(",", part.Modules.Cast<PartModule>().Select(pm => pm?.moduleName ?? "null")));
                return;
            }

            mod.HookIntoEva(__instance);
        }
    }

}
