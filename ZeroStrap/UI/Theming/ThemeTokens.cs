using System.Windows;
using System.Windows.Media;
using WpfFontFamily = System.Windows.Media.FontFamily;

using ZeroStrap.Models.Theming;

namespace ZeroStrap.UI.Theming
{
    /// <summary>
    /// Maps the Typography / Layout / Components part of a theme to design-token values (resource key to value).
    /// Pure and unit tested. The base values mirror the defaults in UI/Style/ZeroBrand.xaml, so a theme with default
    /// settings reproduces the shipped look exactly; a test keeps the two copies identical.
    /// </summary>
    public static class ThemeTokens
    {
        public const string DefaultFamily = "Inter, Segoe UI";

        /// <summary>WPF-UI's base content size; scaled with the body role so unstyled controls follow the text scale.</summary>
        public const string ControlContentFontSizeKey = "ControlContentThemeFontSize";

        public const string FamilyKey = "ZeroUIFont";
        public const string BodyWeightKey = "ZeroFontWeightBody";
        public const string HeadingWeightKey = "ZeroFontWeightHeading";
        public const string SurfaceBorderKey = "ZeroSurfaceBorderThickness";

        /// <summary>Radius in px that matches Components.Radius = 6 (the shipped look).</summary>
        public const double BaseRadius = 6;

        public static readonly IReadOnlyDictionary<string, double> BaseFontSizes = new Dictionary<string, double>
        {
            ["ZeroFontSizeDisplay"] = 28, ["ZeroFontSizeTitle"] = 22, ["ZeroFontSizeSection"] = 17,
            ["ZeroFontSizeBody"] = 14, ["ZeroFontSizeCaption"] = 12, ["ZeroFontSizeEyebrow"] = 11,
            ["ZeroFontSize10"] = 10, ["ZeroFontSize10_5"] = 10.5, ["ZeroFontSize11"] = 11, ["ZeroFontSize11_5"] = 11.5,
            ["ZeroFontSize12"] = 12, ["ZeroFontSize12_5"] = 12.5, ["ZeroFontSize13"] = 13, ["ZeroFontSize13_5"] = 13.5,
            ["ZeroFontSize14"] = 14, ["ZeroFontSize15"] = 15, ["ZeroFontSize16"] = 16, ["ZeroFontSize17"] = 17,
            ["ZeroFontSize18"] = 18, ["ZeroFontSize19"] = 19, ["ZeroFontSize20"] = 20, ["ZeroFontSize22"] = 22,
            ["ZeroFontSize24"] = 24, ["ZeroFontSize30"] = 30, ["ZeroFontSize38"] = 38
        };

        public static readonly IReadOnlyDictionary<string, double> BaseRadii = new Dictionary<string, double>
        {
            ["ControlCornerRadius"] = 6, ["ZeroRadiusSurface"] = 12, ["ZeroRadiusTile"] = 14,
            ["ZeroRadius4"] = 4, ["ZeroRadius5"] = 5, ["ZeroRadius7"] = 7, ["ZeroRadius8"] = 8,
            ["ZeroRadius10"] = 10, ["ZeroRadius11"] = 11, ["ZeroRadius17"] = 17
        };

        /// <summary>Left, top, right, bottom.</summary>
        public static readonly IReadOnlyDictionary<string, (double L, double T, double R, double B)> BaseSpacing =
            new Dictionary<string, (double L, double T, double R, double B)>
            {
                ["ZeroPageMargin"] = (40, 30, 40, 40),
                ["ZeroPageMarginTabbed"] = (40, 30, 40, 24),
                ["ZeroPageMarginFlush"] = (40, 30, 40, 20),
                ["ZeroPageMarginSplit"] = (40, 30, 32, 32),
                ["ZeroPageMarginSplitTall"] = (40, 30, 32, 40),
                ["ZeroSectionMargin"] = (0, 28, 0, 0),
                ["ZeroCardPadding"] = (16, 16, 16, 16),
                ["ZeroSettingCardPadding"] = (18, 16, 18, 16),
                ["ZeroSettingCardMargin"] = (0, 0, 0, 8),
                ["ZeroSettingRowPadding"] = (0, 14, 0, 14),
                ["ZeroListRowPadding"] = (0, 12, 0, 12)
            };

        // ---------------------------------------------------------------- pure functions

        /// <summary>Rounds to the nearest 0.5 (halves round up).</summary>
        public static double RoundToHalf(double value) => Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2;

        public static double ScaleFont(double baseSize, double scale) => RoundToHalf(baseSize * scale);

        /// <summary>Radius factor: the theme radius relative to the shipped 6.</summary>
        public static double RadiusFactor(int radius) => Math.Max(0, radius) / BaseRadius;

        /// <summary>Scales a base corner radius; rounded to 0.5, never below 0.</summary>
        public static double ScaleRadius(double baseRadius, int radius) => Math.Max(0, RoundToHalf(baseRadius * RadiusFactor(radius)));

        public static double DensityFactor(ThemeDensity density) => density switch
        {
            ThemeDensity.Compact => 0.8,
            ThemeDensity.Spacious => 1.2,
            _ => 1.0
        };

        /// <summary>Scales one spacing value to whole pixels (keeps edges crisp).</summary>
        public static double ScaleSpacing(double value, double factor) => Math.Round(value * factor, MidpointRounding.AwayFromZero);

        /// <summary>The UI font stack: "{family}, Inter, Segoe UI", or the default stack for an empty family or Inter.</summary>
        public static string BuildFamily(string? family)
        {
            string name = (family ?? "").Trim();

            if (name.Length == 0 ||
                name.Equals("Inter", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(DefaultFamily, StringComparison.OrdinalIgnoreCase))
                return DefaultFamily;

            return name + ", " + DefaultFamily;
        }

        /// <summary>Clamps an OpenType weight to a sane 100-900 range.</summary>
        public static int NormaliseWeight(int weight) => Math.Clamp(weight, 100, 900);

        public static double SurfaceBorder(ThemeSurfaceStyle style) => style == ThemeSurfaceStyle.Flat ? 0 : 1;

        // ---------------------------------------------------------------- token set

        /// <summary>Builds every token for a theme. Always contains the same keys, so a switch overwrites all of them.</summary>
        public static Dictionary<string, object> Build(ThemeDefinition theme)
        {
            var t = theme.Typography ?? new ThemeTypography();
            var l = theme.Layout ?? new ThemeLayout();
            var c = theme.Components ?? new ThemeComponents();

            double scale = double.IsNaN(t.Scale) ? 1.0 : Math.Clamp(t.Scale, ThemeValidator.MinScale, ThemeValidator.MaxScale);
            int radius = Math.Clamp(c.Radius, ThemeValidator.MinRadius, ThemeValidator.MaxRadius);
            double density = DensityFactor(l.Density);

            var tokens = new Dictionary<string, object>();

            foreach (var (key, size) in BaseFontSizes)
                tokens[key] = ScaleFont(size, scale);

            tokens[ControlContentFontSizeKey] = ScaleFont(14, scale);

            foreach (var (key, value) in BaseRadii)
                tokens[key] = new CornerRadius(ScaleRadius(value, radius));

            foreach (var (key, v) in BaseSpacing)
                tokens[key] = new Thickness(ScaleSpacing(v.L, density), ScaleSpacing(v.T, density), ScaleSpacing(v.R, density), ScaleSpacing(v.B, density));

            tokens[SurfaceBorderKey] = new Thickness(SurfaceBorder(c.SurfaceStyle));
            tokens[FamilyKey] = new WpfFontFamily(BuildFamily(t.Family));
            tokens[BodyWeightKey] = FontWeight.FromOpenTypeWeight(NormaliseWeight(t.BodyWeight));
            tokens[HeadingWeightKey] = FontWeight.FromOpenTypeWeight(NormaliseWeight(t.HeadingWeight));

            return tokens;
        }

        /// <summary>True when both sets hold the same keys and equal values (skips redundant resource swaps).</summary>
        public static bool Same(IReadOnlyDictionary<string, object>? a, IReadOnlyDictionary<string, object>? b)
        {
            if (a is null || b is null || a.Count != b.Count)
                return false;

            foreach (var (key, value) in a)
            {
                if (!b.TryGetValue(key, out var other))
                    return false;

                object left = value is WpfFontFamily fa ? fa.Source : value;
                object right = other is WpfFontFamily fb ? fb.Source : other;

                if (!Equals(left, right))
                    return false;
            }

            return true;
        }
    }

    /// <summary>Decides whether the active theme may be applied.</summary>
    public static class ThemeGuard
    {
        public const string WarningText = "Not applied: primary text is too low-contrast to read.";

        /// <summary>
        /// True when a custom theme's primary text is too low-contrast against its background (below 3:1) to be applied.
        /// Built-in themes are never blocked.
        /// </summary>
        public static bool IsUnreadable(ThemeDefinition theme) =>
            theme.Kind == ThemeKind.Custom &&
            ContrastNote.Ratio(theme.Colors.TextPrimary, theme.Colors.Background) < ContrastNote.MinimumRatio;
    }
}
