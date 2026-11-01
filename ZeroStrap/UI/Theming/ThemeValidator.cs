using ZeroStrap.Models.Theming;
using ZeroStrap.Utility;

namespace ZeroStrap.UI.Theming
{
    /// <summary>Format, range and name rules for theme definitions. Never throws; returns human-readable errors.</summary>
    public static class ThemeValidator
    {
        public const int MinNameLength = 1;
        public const int MaxNameLength = 40;

        public const double MinScale = 0.9, MaxScale = 1.15;
        public const int MinRadius = 0, MaxRadius = 16;
        public const double MinScrim = 0, MaxScrim = 0.9;
        public const int MinBlur = 0, MaxBlur = 30;
        public const double MinSpeed = 0.25, MaxSpeed = 2.0;
        public const int MaxFamilyLength = 100;

        /// <summary>Media allowed as a theme background (also the import allowlist).</summary>
        public static readonly string[] AllowedMediaExtensions = { ".mp4", ".png", ".jpg", ".jpeg" };

        private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled);

        public static bool IsValidHex(string? value) => ColorMath.TryParseHex(value, out _);

        /// <summary>Custom theme ids double as file names, so they are restricted to a safe slug.</summary>
        public static bool IsValidCustomId(string? id) =>
            id is not null && IdPattern.IsMatch(id) && !BuiltinThemes.IsBuiltinId(id) && id != "system";

        /// <summary>Returns null when the name is acceptable, otherwise the reason.</summary>
        public static string? ValidateName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Name is required.";

            if (name != name.Trim())
                return "Name cannot start or end with spaces.";

            if (name.Length < MinNameLength || name.Length > MaxNameLength)
                return $"Name must be {MinNameLength}-{MaxNameLength} characters.";

            return PathValidator.IsFileNameValid(name) switch
            {
                PathValidator.ValidationResult.Ok => null,
                PathValidator.ValidationResult.IllegalCharacter => "Name contains characters that are not allowed.",
                _ => "That name is reserved."
            };
        }

        /// <summary>Returns null when the media file name is acceptable for the background type.</summary>
        public static string? ValidateMediaName(string? media, ThemeBackgroundType type)
        {
            if (string.IsNullOrEmpty(media))
                return type == ThemeBackgroundType.None ? null : "A media file is required for this background type.";

            if (media.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || PathValidator.IsFileNameValid(media) != PathValidator.ValidationResult.Ok)
                return "Media must be a plain file name.";

            string ext = Path.GetExtension(media).ToLowerInvariant();
            if (!AllowedMediaExtensions.Contains(ext))
                return "Media must be .mp4, .png, .jpg or .jpeg.";

            if (type == ThemeBackgroundType.Video && ext != ".mp4")
                return "Video backgrounds must be .mp4.";

            if (type == ThemeBackgroundType.Image && ext == ".mp4")
                return "Image backgrounds must be .png, .jpg or .jpeg.";

            return null;
        }

        public static IReadOnlyList<string> Validate(ThemeDefinition? theme)
        {
            var errors = new List<string>();

            if (theme is null)
            {
                errors.Add("Theme is empty.");
                return errors;
            }

            if (theme.SchemaVersion != ThemeDefinition.CurrentSchemaVersion)
                errors.Add($"Unsupported schema version {theme.SchemaVersion}.");

            if (!Enum.IsDefined(theme.Kind)) errors.Add("Unknown theme kind.");
            if (!Enum.IsDefined(theme.Base)) errors.Add("Unknown theme base.");

            string? nameError = ValidateName(theme.Name);
            if (nameError is not null && theme.Kind != ThemeKind.Builtin)
                errors.Add(nameError);

            if (theme.Kind == ThemeKind.Custom && !IsValidCustomId(theme.Id))
                errors.Add("Theme id is invalid.");

            if (theme.BasedOn is not null && !BuiltinThemes.IsBuiltinId(theme.BasedOn) && !IdPattern.IsMatch(theme.BasedOn))
                errors.Add("BasedOn id is invalid.");

            if (theme.Colors is null)
            {
                errors.Add("Colors are missing.");
            }
            else
            {
                foreach (var (name, value) in theme.Colors.Enumerate())
                {
                    if (!IsValidHex(value))
                        errors.Add($"Colour {name} must be #RRGGBB.");
                }
            }

            var t = theme.Typography;
            if (t is null)
            {
                errors.Add("Typography is missing.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(t.Family) || t.Family.Length > MaxFamilyLength || t.Family.Any(char.IsControl))
                    errors.Add("Font family is invalid.");
                if (!(t.Scale >= MinScale && t.Scale <= MaxScale))
                    errors.Add($"Text scale must be {MinScale}-{MaxScale}.");
                if (t.BodyWeight < 100 || t.BodyWeight > 900)
                    errors.Add("Body weight must be 100-900.");
                if (t.HeadingWeight < 100 || t.HeadingWeight > 900)
                    errors.Add("Heading weight must be 100-900.");
            }

            if (theme.Layout is null)
                errors.Add("Layout is missing.");
            else if (!Enum.IsDefined(theme.Layout.Density))
                errors.Add("Unknown density.");

            var c = theme.Components;
            if (c is null)
            {
                errors.Add("Components are missing.");
            }
            else
            {
                if (c.Radius < MinRadius || c.Radius > MaxRadius)
                    errors.Add($"Radius must be {MinRadius}-{MaxRadius}.");
                if (!Enum.IsDefined(c.ButtonStyle)) errors.Add("Unknown button style.");
                if (!Enum.IsDefined(c.SurfaceStyle)) errors.Add("Unknown surface style.");
            }

            var b = theme.Background;
            if (b is null)
            {
                errors.Add("Background is missing.");
            }
            else
            {
                if (!Enum.IsDefined(b.Type)) errors.Add("Unknown background type.");
                if (!(b.Opacity >= MinScrim && b.Opacity <= MaxScrim))
                    errors.Add($"Background opacity must be {MinScrim}-{MaxScrim}.");
                if (b.Blur < MinBlur || b.Blur > MaxBlur)
                    errors.Add($"Background blur must be {MinBlur}-{MaxBlur}.");
                if (!(b.Speed >= MinSpeed && b.Speed <= MaxSpeed))
                    errors.Add($"Background speed must be {MinSpeed}-{MaxSpeed}.");

                if (Enum.IsDefined(b.Type))
                {
                    string? mediaError = ValidateMediaName(b.Media, b.Type);
                    if (mediaError is not null)
                        errors.Add(mediaError);
                }
            }

            return errors;
        }

        public static bool IsValid(ThemeDefinition? theme) => Validate(theme).Count == 0;

        public static double ContrastRatio(string a, string b) =>
            ColorMath.ContrastRatio(ColorMath.Parse(a), ColorMath.Parse(b));

        public static double ContrastRatio(Rgb a, Rgb b) => ColorMath.ContrastRatio(a, b);
    }
}
