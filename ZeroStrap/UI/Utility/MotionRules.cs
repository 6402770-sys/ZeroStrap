using ZeroStrap.Enums;

namespace ZeroStrap.UI.Utility
{
    /// <summary>Pure rules behind <see cref="Motion"/> (unit tested).</summary>
    public static class MotionRules
    {
        public const double SubtleTravel = 0.6;
        public const double SubtleDuration = 0.85;

        /// <summary>
        /// Animations run unless "Smooth animations" is off, or the user chose to follow Windows and
        /// Windows has animation effects turned off.
        /// </summary>
        public static bool IsEnabled(bool reduceMotion, bool followWindowsAnimations, bool windowsAnimationsOn) =>
            !reduceMotion && !(followWindowsAnimations && !windowsAnimationsOn);

        /// <summary>Multiplier for how far an element travels.</summary>
        public static double TravelFactor(MotionIntensity intensity) => intensity == MotionIntensity.Subtle ? SubtleTravel : 1.0;

        /// <summary>Multiplier for durations and stagger delays.</summary>
        public static double DurationFactor(MotionIntensity intensity) => intensity == MotionIntensity.Subtle ? SubtleDuration : 1.0;

        public static double Travel(double pixels, MotionIntensity intensity) => pixels * TravelFactor(intensity);

        public static int Duration(int milliseconds, MotionIntensity intensity) =>
            (int)Math.Round(milliseconds * DurationFactor(intensity), MidpointRounding.AwayFromZero);
    }
}
