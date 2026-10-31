using System.Windows;
using System.Windows.Media;

using ZeroStrap.Models.Theming;

namespace ZeroStrap.UI.Theming
{
    /// <summary>
    /// Maps the 14 theme colours onto the WPF-UI resource keys that UI/Style/Dark.xaml overrides, plus the
    /// accent ramp and status brushes. The derivations are tuned so that Obsidian's 14 colours reproduce
    /// Dark.xaml (within rounding); built-ins still load Dark.xaml / Light.xaml directly.
    /// </summary>
    public static class ThemePalette
    {
        /// <summary>Colour-level result, keyed by resource name without the "Brush" suffix.</summary>
        public static IReadOnlyDictionary<string, Rgb> Derive(ThemeDefinition theme)
        {
            var c = theme.Colors;
            bool dark = theme.Base == ThemeBase.Dark;

            Rgb bg = ColorMath.Parse(c.Background);
            Rgb bg2 = ColorMath.Parse(c.BackgroundSecondary);
            Rgb surface = ColorMath.Parse(c.Surface);
            Rgb elevated = ColorMath.Parse(c.SurfaceElevated);
            Rgb text = ColorMath.Parse(c.TextPrimary);
            Rgb text2 = ColorMath.Parse(c.TextSecondary);
            Rgb muted = ColorMath.Parse(c.TextMuted);
            Rgb border = ColorMath.Parse(c.Border);
            Rgb accent = ColorMath.Parse(c.Accent);

            static Rgb Mix(Rgb a, Rgb b, double t) => ColorMath.Mix(a, b, t);
            var black = new Rgb(0, 0, 0);

            return new Dictionary<string, Rgb>
            {
                ["ApplicationBackground"] = bg,

                // solid layer bases
                ["SolidBackgroundFillColorBase"] = bg2,
                ["SolidBackgroundFillColorSecondary"] = Mix(bg, black, dark ? 0.20 : 0.04),
                ["SolidBackgroundFillColorTertiary"] = elevated,

                // cards / layers
                ["CardBackgroundFillColorDefault"] = surface,
                ["CardBackgroundFillColorSecondary"] = Mix(surface, elevated, 0.40),
                ["LayerFillColorDefault"] = surface,
                ["LayerFillColorAlt"] = surface,

                // strokes
                ["CardStrokeColorDefault"] = border,
                ["CardStrokeColorDefaultSolid"] = border,
                ["DividerStrokeColorDefault"] = Mix(border, bg, 0.125),
                ["ControlStrokeColorDefault"] = Mix(border, text, 0.06),
                ["ControlStrokeColorSecondary"] = Mix(border, text, 0.12),
                ["SurfaceStrokeColorDefault"] = border,
                ["ControlElevationBorder"] = Mix(border, text, 0.03),

                // control fills
                ["ControlFillColorDefault"] = Mix(surface, elevated, 0.25),
                ["ControlFillColorSecondary"] = Mix(surface, elevated, 0.625),
                ["ControlFillColorTertiary"] = elevated,
                ["ControlFillColorInputActive"] = Mix(bg, surface, 0.55),

                // text
                ["TextFillColorPrimary"] = text,
                ["TextFillColorSecondary"] = text2,
                ["TextFillColorTertiary"] = Mix(text2, muted, 0.75),
                ["TextFillColorDisabled"] = Mix(muted, bg, 0.11),
                ["TextPlaceholderColor"] = muted,

                // text editor
                ["NewTextEditorBackground"] = Mix(surface, elevated, 0.30),
                ["NewTextEditorForeground"] = text,
                ["NewTextEditorLink"] = accent
            };
        }

        /// <summary>
        /// Four accent steps (system, primary, secondary, tertiary) derived from one colour. Dark bases step
        /// lighter, light bases step darker, mirroring the built-in monochrome ramps (D4/DC/E8/F4 on dark).
        /// </summary>
        public static (Rgb System, Rgb Primary, Rgb Secondary, Rgb Tertiary) AccentRamp(Rgb accent, ThemeBase themeBase)
        {
            return themeBase == ThemeBase.Dark
                ? (accent, ColorMath.ShiftLightness(accent, 0.03), ColorMath.ShiftLightness(accent, 0.08), ColorMath.ShiftLightness(accent, 0.13))
                : (accent, ColorMath.ShiftLightness(accent, -0.02), ColorMath.ShiftLightness(accent, -0.06), ColorMath.ShiftLightness(accent, -0.10));
        }

        /// <summary>Status colours keyed by the application resource name. Idle follows the muted text colour.</summary>
        public static IReadOnlyDictionary<string, Rgb> StatusColors(ThemeDefinition theme)
        {
            var c = theme.Colors;
            return new Dictionary<string, Rgb>
            {
                ["ZeroStatusHealthyBrush"] = ColorMath.Parse(c.Success),
                ["ZeroStatusWarningBrush"] = ColorMath.Parse(c.Warning),
                ["ZeroStatusErrorBrush"] = ColorMath.Parse(c.Danger),
                ["ZeroStatusInfoBrush"] = ColorMath.Parse(c.Info),
                ["ZeroStatusIdleBrush"] = ColorMath.Parse(c.TextMuted)
            };
        }

        public static Color ToColor(Rgb c, byte alpha = 0xFF) => Color.FromArgb(alpha, c.R, c.G, c.B);

        /// <summary>
        /// Builds the dictionary that replaces UI/Style/Dark.xaml at merged index 2 for custom themes: the same
        /// keys Dark.xaml defines, plus text-on-accent brushes.
        /// </summary>
        public static ResourceDictionary BuildResources(ThemeDefinition theme)
        {
            var map = Derive(theme);
            var dict = new ResourceDictionary();

            SolidColorBrush Brush(Color color) => new(color);

            // Colours Dark.xaml exposes as Color resources (read by ui:Card and inputs).
            foreach (var key in new[] { "ApplicationBackground", "ControlFillColorDefault", "ControlFillColorSecondary", "ControlFillColorTertiary", "ControlFillColorInputActive" })
            {
                string colourKey = key == "ApplicationBackground" ? "ApplicationBackgroundColor" : key;
                dict[colourKey] = ToColor(map[key]);
            }

            foreach (var (key, value) in map)
            {
                string brushKey = key == "ApplicationBackground" ? "ApplicationBackgroundBrush"
                    : key == "ControlElevationBorder" ? "ControlElevationBorderBrush"
                    : key.StartsWith("NewTextEditor", StringComparison.Ordinal) ? key
                    : key + "Brush";

                dict[brushKey] = Brush(ToColor(value));
            }

            // Text on accent-filled controls (WPF-UI derives these from accent brightness; the theme overrides it).
            Rgb onAccent = ColorMath.Parse(theme.Colors.OnAccent);
            dict["TextOnAccentFillColorPrimaryBrush"] = Brush(ToColor(onAccent));
            dict["TextOnAccentFillColorSecondaryBrush"] = Brush(ToColor(onAccent, 0x80));
            dict["TextOnAccentFillColorDisabledBrush"] = Brush(ToColor(onAccent, 0x77));
            dict["TextOnAccentFillColorSelectedTextBrush"] = Brush(ToColor(onAccent));

            return dict;
        }
    }
}
