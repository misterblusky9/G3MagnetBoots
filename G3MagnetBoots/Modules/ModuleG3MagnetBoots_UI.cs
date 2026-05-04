using KSP.UI;
using UnityEngine;
using UnityEngine.Events;
using System.Collections;
using static KerbalEVA;

namespace G3MagnetBoots
{
    public partial class ModuleG3MagnetBoots : PartModule, IModuleInfo
    {
        // IModuleInfo implementation, mainly for PartInfo tooltip
        public string GetModuleTitle() { return "G3 Magnetic Boots Module"; }
        public override string GetInfo() { return "Enables walking on spacecraft hulls, among other features."; }
        public Callback<Rect> GetDrawModulePanelCallback() { return null; }
        public string GetPrimaryField() { return "Magnet Boots"; }

        public void UpdateUI()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || Kerbal == null || Kerbal.part == null || Kerbal.fsm == null) return;

            // Only process key input for the active vessel and only when UI isn't consuming keyboard
            if (vessel == FlightGlobals.ActiveVessel && InputLockManager.IsUnlocked(ControlTypes.KEYBOARDINPUT))
            {
                if (GameSettings.LANDING_GEAR.GetKeyDown())
                {
                    if (!HasMagnetBootsInInventory())
                        PostTechNotResearchedMsg();
                    else
                        ToggleAG(KSPActionGroup.Gear);
                }
            }

            if (!HasMagnetBootsInInventory()) return;

            if (_lastGear != IsGearOn)
            {
                SetEnabled(IsGearOn);

                if (!IsGearOn && _hullTarget.IsValid() && (FSM.CurrentState == st_idle_hull || FSM.CurrentState == st_walk_hull))
                {
                    ClearHullTarget();
                    ApplyLetGoImpulse();
                    Kerbal.StartCoroutine(AutoDeployJetpack_Coroutine(0.5f));
                }

                _lastGear = IsGearOn;
            }

            if (_lastOnHull != IsOnHull)
            {
                PostMagMsg(IsOnHull);
                _lastOnHull = IsOnHull;
            }

            UpdatePlantFlagOnHullButton();
        }

        void PostMagMsg(bool on)
        {
            if (!on && _anchorBrokenMsgPosted)
            {
                _anchorBrokenMsgPosted = false;
                return;
            }
            string msg = on ? "Magnet Boots Engaged" : "Magnet Boots Disengaged";
            string prefix = (vessel != null && vessel == FlightGlobals.ActiveVessel) ? "" : $"{Crew.displayName}: ";
            _magMsg = ScreenMessages.PostScreenMessage(prefix + msg, 2f, ScreenMessageStyle.UPPER_CENTER, _magMsg);
        }

        private bool _anchorBrokenMsgPosted;
        void PostAnchorBrakeMsg()
        {
            if (_anchorBrokenMsgPosted) return;
            _anchorBrokenMsgPosted = true;

            string msg = "Magnet Boots Anchor broken due to G-Force!";
            string prefix = (vessel != null && vessel == FlightGlobals.ActiveVessel) ? "" : $"{Crew.displayName}: ";
            _magMsg = ScreenMessages.PostScreenMessage(prefix + msg, 2f, ScreenMessageStyle.UPPER_CENTER, _magMsg);
        }

        float _postTechMsgCooldown = 0f;
        void PostTechNotResearchedMsg()
        {
            if (_postTechMsgCooldown > 0f)
            {
                _postTechMsgCooldown -= Time.deltaTime;
                return;
            }
            _postTechMsgCooldown = 5f;
            _magMsg = ScreenMessages.PostScreenMessage($"{unlockTech} not researched!", 3f, ScreenMessageStyle.UPPER_CENTER, _magMsg);
        }

        // Keep the stock Gear AG button in sync with the magboots state visually
        private UIButtonToggle agGearButton;
        private bool _syncingAGButtons;
        private UnityAction _agGearOnAction;
        private UnityAction _agGearOffAction;

        protected void HookAGGearButton()
        {
            _lastGear = IsAGOn(KSPActionGroup.Gear);

            var buttonObj = GameObject.Find("ButtonActionGroupGears");
            agGearButton = buttonObj?.GetComponent<UIButtonToggle>();
            if (agGearButton == null)
            {
                StartCoroutine(RetryHookAGGearButton());
                return;
            }

            _agGearOnAction = SyncAGGearButton;
            _agGearOffAction = SyncAGGearButton;
            agGearButton.onToggleOn.AddListener(_agGearOnAction);
            agGearButton.onToggleOff.AddListener(_agGearOffAction);
        }

        private System.Collections.IEnumerator RetryHookAGGearButton()
        {
            while (agGearButton == null && this != null && enabled)
            {
                yield return new WaitForSeconds(0.5f);
                var buttonObj = GameObject.Find("ButtonActionGroupGears");
                agGearButton = buttonObj?.GetComponent<UIButtonToggle>();
            }
            if (agGearButton == null) yield break;

            _agGearOnAction = SyncAGGearButton;
            _agGearOffAction = SyncAGGearButton;
            agGearButton.onToggleOn.AddListener(_agGearOnAction);
            agGearButton.onToggleOff.AddListener(_agGearOffAction);
        }

        private void SyncAGGearButton()
        {
            if (agGearButton == null || _syncingAGButtons) return;

            _syncingAGButtons = true;
            try { agGearButton.SetState(IsAGOn(KSPActionGroup.Gear)); }
            finally { _syncingAGButtons = false; }
        }

        private void OnDisable()
        {
            _syncingAGButtons = false;
            try
            {
                if (agGearButton != null)
                {
                    if (_agGearOnAction != null) agGearButton.onToggleOn.RemoveListener(_agGearOnAction);
                    if (_agGearOffAction != null) agGearButton.onToggleOff.RemoveListener(_agGearOffAction);
                }
            }
            catch { }
        }
    }
}