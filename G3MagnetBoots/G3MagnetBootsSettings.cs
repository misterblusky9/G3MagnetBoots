using System.Reflection;

namespace G3MagnetBoots
{
    public class G3MagnetBootsSettings : GameParameters.CustomParameterNode
    {
        public override string Title => "Features";
        public override string DisplaySection => "G3MagnetBoots";
        public override string Section => "G3MagnetBoots";
        public override int SectionOrder => 1;
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        [GameParameters.CustomParameterUI("Allow Walking on Asteroids and Comets", toolTip = "Allow magnet boots to attach to asteroids and comets.")]
        public bool magbootsAsteroidsEnabled = true;

        [GameParameters.CustomParameterUI("Allow LOCKED Camera Mode on Hull", toolTip = "A new dampened orbiting camera boom option for better control working atop a moving vessel.")]
        public bool magbootsLockedCameraModeEnabled = true;

        [GameParameters.CustomParameterUI("Require Microgravity", toolTip = "Prevent magnet boots from working below 3500m altitude to avoid interference with parachute deployment and atmospheric flight.")]
        public bool magbootsRequireMicrogravity = false;

        [GameParameters.CustomParameterUI("Auto-Enable Jetpack When Detaching from Hull in Microgravity", toolTip = "Automatically enable the jetpack when detaching from a hull in microgravity, improving quality of life when falling into space.")]
        public bool jetpackAutoToggleEnabled = true;

        [GameParameters.CustomParameterUI("Auto-Enable Magnet Boots When Leaving a Ladder", toolTip = "Automatically enable the magnet boots when leaving a ladder, improving quality of life when transitioning from ladders to a hull.")]
        public bool magbootsAutoToggleEnabled = true;

        [GameParameters.CustomParameterUI("Allow Repacking EVA Parachutes on Hull", toolTip = "Allow Kerbals to repack their parachutes while attached to a hull.")]
        public bool magbootsRepackChuteEnabled = true;

        [GameParameters.CustomParameterUI("Safely Allow Helmets Off in Vacuum", toolTip = "Allow Kerbals to remove their helmets in vacuum. Useful for roleplay or photography.")]
        public bool allowHelmetOffInSpace = false;

        [GameParameters.CustomParameterUI("WIP: Allow Planting Flags on Vessels (BACKUP SAVE FIRST!)", toolTip = "Serious injury or bodily harm may occur, ask your doctor if planting flags on vessels is right for you.")]
        public bool magbootsPlantFlagEnabled = false;

        [GameParameters.CustomParameterUI("Show Debug Info", toolTip = "Enable logging for developers")]
        public bool isDebugMode = false;

        public override bool Enabled(MemberInfo member, GameParameters parameters) => true;
        public override bool Interactible(MemberInfo member, GameParameters parameters) => true;

        public static G3MagnetBootsSettings Current => HighLogic.CurrentGame?.Parameters.CustomParams<G3MagnetBootsSettings>();
    }

    public class G3MagnetBootsConstants : GameParameters.CustomParameterNode
    {
        public override string Title => "Constants";
        public override string DisplaySection => "G3MagnetBoots";
        public override string Section => "G3MagnetBoots";
        public override int SectionOrder => 2;
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        [GameParameters.CustomIntParameterUI("Magnet Strength Factor", minValue = 0, maxValue = 15, stepSize = 1, toolTip = "Strength of collider face snapping.")] // increment of 1
        public int magbootsClampForce = 4;

        [GameParameters.CustomIntParameterUI("Parking Brake Maximum G-Load", minValue = 0, maxValue = 11, stepSize = 1, toolTip = "Set to 0 for unlimited g-load. Otherwise, magnet boots will detach if the g-load exceeds this value.")] // increment of 1g
        public int parkingBrakeMaxG = 0;

        public override bool Enabled(MemberInfo member, GameParameters parameters) => true;
        public override bool Interactible(MemberInfo member, GameParameters parameters) => true;

        public static G3MagnetBootsConstants Current => HighLogic.CurrentGame?.Parameters.CustomParams<G3MagnetBootsConstants>();
    }
}
