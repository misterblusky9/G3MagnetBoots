using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace G3MagnetBoots
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal class G3MagnetBoots : MonoBehaviour
    {
        public static G3MagnetBoots Instance;

        internal const string MODID = "G3MagnetBoots";
        internal const string MODNAME = "G3 Magnet Boots";

        // variables (configurable in future)
        public const float EnterRelSpeed = 0.6f;
        public const float ExitRelSpeed = 1.0f; // hysteresis exit velocity
        public const float MaxStandClearance = 0.1f;

        private void Awake()
        {
            Instance = this;
            Logger.Trace("G3MagnetBoots Awake");
        }

        private void OnDestroy() { }

        private void Start() { Logger.Trace("G3MagnetBoots Start"); }

        private void Update() { }
   

    }

}