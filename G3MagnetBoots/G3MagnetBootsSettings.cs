using KSP;
using System.Reflection;

namespace G3MagnetBoots
{
    public class G3MagnetBootsSettings : GameParameters.CustomParameterNode
    {
        public override string Title => "General Options";
        public override string DisplaySection => "G3MagnetBoots";
        public override string Section => "G3MagnetBoots";
        public override int SectionOrder => 1;
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        [GameParameters.CustomParameterUI("Enable EVA Magnet Boots", toolTip = "Enable/Disable Mod")]
        public bool modEnabled = true;

        public enum HullDetectionStyle
        {
            Simple, // current implementation, simple spherecast down, no SMA
            Smoothed, // SMA smoothed spherecast down
            KSP2Style // Similar to KSP2, SMA + an extra predictive spherecast to help walking up vertical surfaces
        }
        [GameParameters.CustomParameterUI("Hull detection style", toolTip = "Method used to detect hull surfaces")]
            public HullDetectionStyle hullDetectionStyle = HullDetectionStyle.Simple;

        [GameParameters.CustomParameterUI("Enable walking on Asteroids/Comets", toolTip = "Allow magnet boots to function on asteroids and comets")]
        public bool enableOnAsteroids = true;

        [GameParameters.CustomParameterUI("Allow RCS Jetpack on Hull", toolTip = "Allow use of RCS Jetpack while kerbal is attached to hull")]
        public bool canJetpackOnHull = true;

        [GameParameters.CustomParameterUI("Auto-enable Jetpack on Jump", toolTip = "Automatically enable jetpack when jumping on/off hull")]
        public bool autoToggleJetpack = true;

        [GameParameters.CustomParameterUI("Disable boots in atmosphere", toolTip = "Disable magnet boots when in atmosphere")]
        public bool disableInAtmosphere = true;

        [GameParameters.CustomFloatParameterUI("Max relative speed to stay attached (m/s)", minValue = 0.0f, maxValue = 20.0f, stepCount = 21, displayFormat = "F2", toolTip = "Maximum relative speed to surface to remain attached")]
        public float maxRelSpeedToStay = 3.0f;

        [GameParameters.CustomFloatParameterUI("Surface snap force", minValue = 0.0f, maxValue = 40.0f, stepCount = 41, displayFormat = "F2", toolTip = "Strength of surface snapping force")]
        public float strengthSurfaceSnap = 10.0f;

        [GameParameters.CustomFloatParameterUI("Surface slide dampener force", minValue = 0.0f, maxValue = 20.0f, stepCount = 21, displayFormat = "F2", toolTip = "Strength of surface sliding force")]
        public float strengthSurfaceSlide = 6.0f;

        [GameParameters.CustomParameterUI("Allow packing parachute on hull", toolTip = "Allow kerbals to pack their parachute while attached to hull")]
        public bool allowPackChute = true;

        [GameParameters.CustomParameterUI("Allow planting flags on hull (BROKEN)", toolTip = "Allow kerbals to plant flags while attached to hull")]
        public bool allowPlantFlag = false;

        [GameParameters.CustomParameterUI("Show Debug Info", toolTip = "Show debug information in the debug console")]
        public bool isDebugMode = false;

        public override bool Enabled(MemberInfo member, GameParameters parameters) => true;
        public override bool Interactible(MemberInfo member, GameParameters parameters) => true;

        public static G3MagnetBootsSettings Current => HighLogic.CurrentGame?.Parameters.CustomParams<G3MagnetBootsSettings>();
    }
}
