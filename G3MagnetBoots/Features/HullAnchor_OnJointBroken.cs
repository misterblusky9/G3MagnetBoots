using System;
using HarmonyLib;
using UnityEngine;
using System.Linq;

namespace G3MagnetBoots
{
    internal class HullAnchorBreakDetector : MonoBehaviour
    {
        public System.Action OnBroken;

        private void OnJointBreak(float breakForce)
        {
            OnBroken?.Invoke();
            Destroy(this);
        }
    }
}