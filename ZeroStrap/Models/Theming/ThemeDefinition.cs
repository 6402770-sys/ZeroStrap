namespace ZeroStrap.Models.Theming
{
    public enum ThemeKind { Builtin, Custom }

    public enum ThemeBase { Dark, Light }

    public enum ThemeDensity { Compact, Comfortable, Spacious }

    public enum ThemeButtonStyle { Solid, Subtle }

    public enum ThemeSurfaceStyle { Flat, Outlined }

    public enum ThemeBackgroundType { None, Image, Video }

    /// <summary>
    /// Persisted description of an interface theme (Theme Studio). Serialised as JSON; colours are "#RRGGBB".
    /// Only Colors (and Base) are applied today; Typography/Layout/Components/Background are stored for Phase 6
    /// and default to values matching the current look.
    /// </summary>
    public class ThemeDefinition
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public ThemeKind Kind { get; set; } = ThemeKind.Custom;
        public ThemeBase Base { get; set; } = ThemeBase.Dark;

        /// <summary>Id of the built-in theme this one started from (used by "Restore defaults").</summary>
        public string? BasedOn { get; set; }

        public ThemeColors Colors { get; set; } = new();
        public ThemeTypography Typography { get; set; } = new();
        public ThemeLayout Layout { get; set; } = new();
        public ThemeComponents Components { get; set; } = new();
        public ThemeBackground Background { get; set; } = new();

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        /// <summary>Parses a definition; returns null when the JSON is malformed or not an object.</summary>
        public static ThemeDefinition? FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<ThemeDefinition>(json, JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }

        public ThemeDefinition Clone() => FromJson(ToJson())!;
    }

    public class ThemeColors
    {
        public string Background { get; set; } = "#0E0E0E";
        public string BackgroundSecondary { get; set; } = "#101010";
        public string Surface { get; set; } = "#151515";
        public string SurfaceElevated { get; set; } = "#252525";
        public string TextPrimary { get; set; } = "#F2F2F2";
        public string TextSecondary { get; set; } = "#A0A0A0";
        public string TextMuted { get; set; } = "#686868";
        public string Border { get; set; } = "#242424";
        public string Accent { get; set; } = "#D4D4D4";
        public string OnAccent { get; set; } = "#000000";
        public string Success { get; set; } = "#3FBF7F";
        public string Warning { get; set; } = "#D4A054";
        public string Danger { get; set; } = "#D15B5B";
        public string Info { get; set; } = "#5E9BD6";

        /// <summary>Colour names with their values, in a stable order (used by the validator and builders).</summary>
        public IEnumerable<KeyValuePair<string, string>> Enumerate()
        {
            yield return new(nameof(Background), Background);
            yield return new(nameof(BackgroundSecondary), BackgroundSecondary);
            yield return new(nameof(Surface), Surface);
            yield return new(nameof(SurfaceElevated), SurfaceElevated);
            yield return new(nameof(TextPrimary), TextPrimary);
            yield return new(nameof(TextSecondary), TextSecondary);
            yield return new(nameof(TextMuted), TextMuted);
            yield return new(nameof(Border), Border);
            yield return new(nameof(Accent), Accent);
            yield return new(nameof(OnAccent), OnAccent);
            yield return new(nameof(Success), Success);
            yield return new(nameof(Warning), Warning);
            yield return new(nameof(Danger), Danger);
            yield return new(nameof(Info), Info);
        }
    }

    public class ThemeTypography
    {
        public string Family { get; set; } = "Inter, Segoe UI";
        public double Scale { get; set; } = 1.0;
        public int BodyWeight { get; set; } = 400;
        public int HeadingWeight { get; set; } = 600;
    }

    public class ThemeLayout
    {
        public ThemeDensity Density { get; set; } = ThemeDensity.Comfortable;
    }

    public class ThemeComponents
    {
        public int Radius { get; set; } = 6;
        public ThemeButtonStyle ButtonStyle { get; set; } = ThemeButtonStyle.Solid;
        public ThemeSurfaceStyle SurfaceStyle { get; set; } = ThemeSurfaceStyle.Outlined;
    }

    public class ThemeBackground
    {
        public ThemeBackgroundType Type { get; set; } = ThemeBackgroundType.None;

        /// <summary>File name (no directories) inside the theme media folder.</summary>
        public string? Media { get; set; }

        /// <summary>Scrim opacity over the media, 0 to 0.9.</summary>
        public double Opacity { get; set; } = 0.7; // dim enough that text over a bright video stays readable

        public int Blur { get; set; } = 0;
        public double Speed { get; set; } = 1.0;
    }
}
