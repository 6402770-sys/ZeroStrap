using System.Globalization;

namespace ZeroStrap.UI.Theming
{
    public enum ContrastLevel { Ok, Low, TooLow }

    /// <summary>Thresholds and wording for the contrast notes shown next to text colours in the theme editor.</summary>
    public static class ContrastNote
    {
        public const double GoodRatio = 4.5;
        public const double MinimumRatio = 3.0;

        public static ContrastLevel Classify(double ratio) =>
            ratio >= GoodRatio ? ContrastLevel.Ok : ratio >= MinimumRatio ? ContrastLevel.Low : ContrastLevel.TooLow;

        /// <summary>Ratio with one decimal, truncated so that 4.49 never reads as the passing "4.5".</summary>
        public static string FormatRatio(double ratio) =>
            (Math.Floor(ratio * 10 + 1e-9) / 10).ToString("0.0", CultureInfo.InvariantCulture);

        /// <summary>Null when the contrast is fine.</summary>
        public static string? Message(double ratio) => Classify(ratio) switch
        {
            ContrastLevel.Low => $"Low contrast ({FormatRatio(ratio)}:1)",
            ContrastLevel.TooLow => $"Too low to read ({FormatRatio(ratio)}:1)",
            _ => null
        };

        /// <summary>Contrast of two #RRGGBB colours; 21 when either is unparsable (no note is shown).</summary>
        public static double Ratio(string foreground, string background) =>
            ColorMath.TryParseHex(foreground, out var fg) && ColorMath.TryParseHex(background, out var bg)
                ? ColorMath.ContrastRatio(fg, bg)
                : 21;
    }
}
