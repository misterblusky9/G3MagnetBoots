using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace G3MagnetBoots
{
    // Simple flight-only debug panel to view & set the Kerbal EVA FSM state.
    // Opens automatically when the active vessel is an EVA kerbal.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class EVAFSMDebugPanel : MonoBehaviour
    {
        private Rect windowRect = new(20, 80, 340, 420);
        private Vector2 scroll;
        private bool showWindow;
        private KerbalEVA activeEva;
        private object activeFsm;
        private string activeStateName;
        private List<FieldInfo> kfsmStateFields = new();
        private List<object> kfsmStateValues = new();

        private void Start()
        {
            showWindow = false;
        }

        private void Update()
        {
            var v = FlightGlobals.ActiveVessel;
            if (v != null && v.isEVA)
            {
                // automatically show when EVA becomes active
                if (!showWindow)
                    showWindow = true;

                // cache current EVA and FSM
                var eva = v.evaController ?? v.rootPart?.FindModuleImplementing<KerbalEVA>();
                if (eva != activeEva)
                {
                    activeEva = eva;
                    RefreshFSMCache();
                }
            }
            else
            {
                // hide when not EVA
                if (showWindow)
                {
                    showWindow = false;
                    activeEva = null;
                    activeFsm = null;
                    kfsmStateFields.Clear();
                    kfsmStateValues.Clear();
                }
            }
        }

        private void RefreshFSMCache()
        {
            kfsmStateFields.Clear();
            kfsmStateValues.Clear();
            activeFsm = null;
            activeStateName = null;

            if (activeEva == null) return;

            // get fsm field
            var fsmField = activeEva.GetType().GetField("fsm", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fsmField == null) return;

            activeFsm = fsmField.GetValue(activeEva);
            // current state name
            activeStateName = GetCurrentStateName();

            // find KFSMState typed fields declared on KerbalEVA and collect non-null ones
            var fields = activeEva.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var f in fields)
            {
                if (f.FieldType.Name == "KFSMState")
                {
                    var value = f.GetValue(activeEva);
                    if (value != null)
                    {
                        kfsmStateFields.Add(f);
                        kfsmStateValues.Add(value);
                    }
                }
            }

            // sort alphabetically by field name for stable order
            var zipped = kfsmStateFields.Zip(kfsmStateValues, (fi, val) => new { fi, val })
                                       .OrderBy(x => x.fi.Name, StringComparer.OrdinalIgnoreCase)
                                       .ToList();
            kfsmStateFields = zipped.Select(x => x.fi).ToList();
            kfsmStateValues = zipped.Select(x => x.val).ToList();
        }

        private string GetCurrentStateName()
        {
            if (activeFsm == null || activeEva == null) return null;
            var curProp = activeFsm.GetType().GetProperty("CurrentState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (curProp != null)
            {
                var curState = curProp.GetValue(activeFsm);
                if (curState != null)
                {
                    var nameField = curState.GetType().GetField("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                 ?? (MemberInfo)curState.GetType().GetProperty("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (nameField is FieldInfo nf)
                        return nf.GetValue(curState) as string;
                    if (nameField is PropertyInfo np)
                        return np.GetValue(curState) as string;
                }
            }
            return null;
        }

        private void OnGUI()
        {
            if (!showWindow || FlightGlobals.ActiveVessel == null || !FlightGlobals.ActiveVessel.isEVA) return;

            windowRect = GUILayout.Window(GetHashCode(), windowRect, DrawWindow, $"EVA FSM Debug ({FlightGlobals.ActiveVessel.vesselName})");
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            if (activeEva == null)
            {
                GUILayout.Label("No KerbalEVA instance found.");
            }
            else if (activeFsm == null)
            {
                GUILayout.Label("KerbalEVA.fsm not available yet.");
            }
            else
            {
                scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(300));

                string cur = GetCurrentStateName();
                GUILayout.Label($"Current FSM State: {(!string.IsNullOrEmpty(cur) ? cur : "<unknown>")}");
                GUILayout.Space(6);

                // list available KFSMState fields detected on KerbalEVA
                for (int i = 0; i < kfsmStateFields.Count; i++)
                {
                    var fi = kfsmStateFields[i];
                    var val = kfsmStateValues[i];
                    string stateName = GetStateNameFromKFSMState(val);
                    bool isCurrent = string.Equals(stateName, cur, StringComparison.Ordinal);
                    GUILayout.BeginHorizontal();
                    GUI.enabled = !isCurrent; // disable button for current state
                    if (GUILayout.Button(stateName ?? fi.Name, GUILayout.ExpandWidth(true)))
                    {
                        TrySetFSMState(stateName);
                        // refresh cache so current state reflects change
                        RefreshFSMCache();
                    }
                    GUI.enabled = true;
                    GUILayout.Label(isCurrent ? " (active)" : "", GUILayout.Width(64));
                    GUILayout.EndHorizontal();
                }

                GUILayout.EndScrollView();

                GUILayout.Space(6);

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Refresh", GUILayout.Width(90)))
                {
                    RefreshFSMCache();
                }
                if (GUILayout.Button("Close", GUILayout.Width(90)))
                {
                    showWindow = false;
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            GUILayout.EndVertical();

            // allow dragging
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private string GetStateNameFromKFSMState(object kfsmState)
        {
            if (kfsmState == null) return "<null>";
            var nameField = kfsmState.GetType().GetField("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (nameField != null)
                return nameField.GetValue(kfsmState) as string;
            var nameProp = kfsmState.GetType().GetProperty("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (nameProp != null)
                return nameProp.GetValue(kfsmState) as string;
            return kfsmState.ToString();
        }

        private void TrySetFSMState(string stateName)
        {
            if (activeFsm == null || string.IsNullOrEmpty(stateName)) return;

            // prefer StartFSM(string) if available
            var startMethod = activeFsm.GetType().GetMethod("StartFSM", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(string) }, null);
            try
            {
                if (startMethod != null)
                {
                    startMethod.Invoke(activeFsm, new object[] { stateName });
                    return;
                }

                // fallback: look for method that accepts a KFSMState object - try to find a matching field value
                var stateFieldIndex = kfsmStateValues.FindIndex(v => string.Equals(GetStateNameFromKFSMState(v), stateName, StringComparison.Ordinal));
                if (stateFieldIndex >= 0)
                {
                    var candidate = kfsmStateValues[stateFieldIndex];
                    var startStateMethod = activeFsm.GetType().GetMethod("StartFSM", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { candidate.GetType() }, null);
                    if (startStateMethod != null)
                    {
                        startStateMethod.Invoke(activeFsm, new object[] { candidate });
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Exception(ex);
            }
        }
    }
}