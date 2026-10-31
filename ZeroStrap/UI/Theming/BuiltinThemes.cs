using ZeroStrap.Models.Theming;

namespace ZeroStrap.UI.Theming
{
    /// <summary>
    /// Read-only built-in themes, defined in code. Their colours mirror what the application renders today:
    /// Obsidian = the values in UI/Style/Dark.xaml, Light = the effective WPF-UI light theme values
    /// (translucent fills composited over white, text alpha composited over white).
    /// </summary>
    public static class BuiltinThemes
    {
        public const string ObsidianId = "obsidian";
        public const string LightId = "light";

        public static bool IsBuiltinId(string? id) => id is ObsidianId or LightId;

        public static IReadOnlyList<ThemeDefinition> All() => new[] { Obsidian(), Light() };

        public static ThemeDefinition? Get(string? id) => id switch
        {
            ObsidianId => Obsidian(),
            LightId => Light(),
            _ => null
        };

        /// <summary>The built-in a theme with this base falls back to.</summary>
        public static ThemeDefinition ForBase(ThemeBase themeBase) => themeBase == ThemeBase.Light ? Light() : Obsidian();

        public static ThemeDefinition Obsidian() => new()
        {
            Id = ObsidianId,
            Name = "Obsidian",
            Kind = ThemeKind.Builtin,
            Base = ThemeBase.Dark,
            BasedOn = null,
            Colors = new ThemeColors
            {
                Background = "#0E0E0E",          // ApplicationBackground
                BackgroundSecondary = "#101010", // SolidBackgroundFillColorBase (content area)
                Surface = "#151515",             // Card / Layer fill
                SurfaceElevated = "#252525",     // SolidBackgroundFillColorTertiary / ControlFillTertiary
                TextPrimary = "#F2F2F2",
                TextSecondary = "#A0A0A0",
                TextMuted = "#686868",           // placeholder
                Border = "#242424",              // CardStroke
                Accent = "#D4D4D4",              // dark monochrome ramp, system step
                OnAccent = "#000000",
                Success = "#3FBF7F",             // ZeroStatusHealthy
                Warning = "#D4A054",             // ZeroStatusWarning
                Danger = "#D15B5B",              // ZeroStatusError
                Info = "#5E9BD6"
            }
        };

        public static ThemeDefinition Light() => new()
        {
            Id = LightId,
            Name = "Light",
            Kind = ThemeKind.Builtin,
            Base = ThemeBase.Light,
            BasedOn = null,
            Colors = new ThemeColors
            {
                Background = "#FAFAFA",          // ApplicationBackground
                BackgroundSecondary = "#F3F3F3", // SolidBackgroundFillColorBase
                Surface = "#FFFFFF",             // LayerFillColorAlt / opaque card
                SurfaceElevated = "#F9F9F9",     // SolidBackgroundFillColorTertiary
                TextPrimary = "#1B1B1B",         // E4000000 over white
                TextSecondary = "#616161",       // 9E000000 over white
                TextMuted = "#8D8D8D",           // 72000000 over white
                Border = "#EBEBEB",              // CardStrokeColorDefaultSolid
                Accent = "#2A2A2A",              // light monochrome ramp, system step
                OnAccent = "#FFFFFF",
                Success = "#3FBF7F",
                Warning = "#D4A054",
                Danger = "#D15B5B",
                Info = "#5E9BD6"
            }
        };
    }
}
