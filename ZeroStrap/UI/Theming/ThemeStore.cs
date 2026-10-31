using System.IO.Compression;

using ZeroStrap.Models.Theming;
using ZeroStrap.Utility;

namespace ZeroStrap.UI.Theming
{
    public class ThemeStoreException : Exception
    {
        public ThemeStoreException(string message) : base(message) { }
    }

    public sealed class ThemeImportResult
    {
        public ThemeDefinition Theme { get; init; } = null!;
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// Built-in themes (in code) plus custom themes stored as one JSON file each in the themes directory.
    /// Corrupt or invalid files are skipped and logged, never thrown from listing.
    /// Mutating operations throw <see cref="ThemeStoreException"/> with a user-presentable message.
    /// </summary>
    public sealed class ThemeStore
    {
        public const long MaxMediaBytes = 500L * 1024 * 1024;
        private const long MaxThemeFileBytes = 256 * 1024;
        private const string ThemeEntryName = "theme.json";
        private const string MediaEntryPrefix = "media/";

        private static ThemeStore? _default;

        /// <summary>The store backed by Paths.Themes / Paths.ThemeMedia. Only valid after Paths.Initialize.</summary>
        public static ThemeStore Default => _default ??= new ThemeStore(Paths.Themes, Paths.ThemeMedia, msg => App.Logger.WriteLine("ThemeStore", msg));

        private readonly string _themesDir;
        private readonly string _mediaDir;
        private readonly Action<string>? _log;
        private readonly object _lock = new();

        /// <summary>Raised after a custom theme file was deleted (argument: its id).</summary>
        public event Action<string>? Deleted;

        public string ThemesDirectory => _themesDir;
        public string MediaDirectory => _mediaDir;

        public ThemeStore(string themesDirectory, string mediaDirectory, Action<string>? log = null)
        {
            _themesDir = themesDirectory;
            _mediaDir = mediaDirectory;
            _log = log;
        }

        private void Log(string message)
        {
            try { _log?.Invoke(message); } catch { /* logging must never break theming */ }
        }

        // ---------------------------------------------------------------- read

        /// <summary>Built-ins first, then custom themes ordered by name.</summary>
        public IReadOnlyList<ThemeDefinition> List()
        {
            var result = new List<ThemeDefinition>(BuiltinThemes.All());

            lock (_lock)
            {
                result.AddRange(ReadCustoms().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase));
            }

            return result;
        }

        public IReadOnlyList<ThemeDefinition> ListCustom() => List().Where(t => t.Kind == ThemeKind.Custom).ToList();

        /// <summary>Returns a copy of the theme with this id, or null when missing or invalid.</summary>
        public ThemeDefinition? Get(string? id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            var builtin = BuiltinThemes.Get(id);
            if (builtin is not null)
                return builtin;

            if (!ThemeValidator.IsValidCustomId(id))
                return null;

            lock (_lock)
            {
                return ReadCustomFile(PathFor(id), id);
            }
        }

        private IEnumerable<ThemeDefinition> ReadCustoms()
        {
            if (!Directory.Exists(_themesDir))
                yield break;

            string[] files;
            try
            {
                files = Directory.GetFiles(_themesDir, "*.json");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"Could not list {_themesDir}: {ex.Message}");
                yield break;
            }

            foreach (var file in files)
            {
                string id = Path.GetFileNameWithoutExtension(file);
                var theme = ReadCustomFile(file, id);
                if (theme is not null)
                    yield return theme;
            }
        }

        private ThemeDefinition? ReadCustomFile(string path, string expectedId)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    return null;

                if (info.Length > MaxThemeFileBytes)
                {
                    Log($"Skipping {info.Name}: file is too large.");
                    return null;
                }

                var theme = ThemeDefinition.FromJson(File.ReadAllText(path));
                if (theme is null)
                {
                    Log($"Skipping {info.Name}: not valid theme JSON.");
                    return null;
                }

                // Files are always custom themes, whatever they claim; the id must match the file name.
                theme.Kind = ThemeKind.Custom;

                if (!string.Equals(theme.Id, expectedId, StringComparison.Ordinal))
                {
                    Log($"Skipping {info.Name}: id \"{theme.Id}\" does not match the file name.");
                    return null;
                }

                var errors = ThemeValidator.Validate(theme);
                if (errors.Count > 0)
                {
                    Log($"Skipping {info.Name}: {errors[0]}");
                    return null;
                }

                return theme;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"Could not read {path}: {ex.Message}");
                return null;
            }
        }

        private string PathFor(string id) => Path.Combine(_themesDir, id + ".json");

        public string? GetMediaPath(ThemeDefinition theme)
        {
            string? media = theme.Background?.Media;
            if (string.IsNullOrEmpty(media))
                return null;

            var kind = media.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? ThemeBackgroundType.Video : ThemeBackgroundType.Image;
            if (ThemeValidator.ValidateMediaName(media, kind) is not null)
                return null;

            string path = Path.Combine(_mediaDir, media);
            return File.Exists(path) ? path : null;
        }

        // ---------------------------------------------------------------- names

        private HashSet<string> AllNames(string? exceptId = null)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in BuiltinThemes.All())
                names.Add(t.Name);

            foreach (var t in ReadCustoms())
            {
                if (exceptId is null || t.Id != exceptId)
                    names.Add(t.Name);
            }

            return names;
        }

        /// <summary>Returns the name, or "Name 2", "Name 3"... when it is already taken. Result is at most 40 characters.</summary>
        public string UniqueName(string desired)
        {
            lock (_lock)
            {
                return UniqueNameCore(desired, AllNames());
            }
        }

        internal static string UniqueNameCore(string desired, ISet<string> taken)
        {
            string baseName = (desired ?? "").Trim();
            if (baseName.Length == 0)
                baseName = "Custom theme";

            if (baseName.Length > ThemeValidator.MaxNameLength)
                baseName = baseName[..ThemeValidator.MaxNameLength].TrimEnd();

            if (!taken.Contains(baseName))
                return baseName;

            for (int n = 2; n < 10000; n++)
            {
                string suffix = " " + n;
                string stem = baseName.Length + suffix.Length > ThemeValidator.MaxNameLength
                    ? baseName[..(ThemeValidator.MaxNameLength - suffix.Length)].TrimEnd()
                    : baseName;
                string candidate = stem + suffix;

                if (!taken.Contains(candidate))
                    return candidate;
            }

            return baseName + " " + Guid.NewGuid().ToString("N")[..6];
        }

        private static string NewId() => Guid.NewGuid().ToString("N")[..12];

        // ---------------------------------------------------------------- write

        /// <summary>Creates a custom theme from an existing one (built-in or custom).</summary>
        public ThemeDefinition Create(string fromId, string name)
        {
            lock (_lock)
            {
                var source = Get(fromId) ?? throw new ThemeStoreException("The theme to start from was not found.");
                return CreateCopy(source, name);
            }
        }

        public ThemeDefinition Duplicate(string id)
        {
            lock (_lock)
            {
                var source = Get(id) ?? throw new ThemeStoreException("The theme was not found.");
                return CreateCopy(source, source.Name + " copy");
            }
        }

        private ThemeDefinition CreateCopy(ThemeDefinition source, string desiredName)
        {
            var copy = source.Clone();
            copy.Id = NewId();
            copy.Kind = ThemeKind.Custom;
            copy.BasedOn = source.Kind == ThemeKind.Builtin ? source.Id : (source.BasedOn ?? BuiltinThemes.ForBase(source.Base).Id);
            copy.Name = UniqueNameCore(desiredName, AllNames());

            Save(copy);
            return copy;
        }

        public ThemeDefinition Rename(string id, string newName)
        {
            lock (_lock)
            {
                var theme = GetCustomOrThrow(id);

                string? error = ThemeValidator.ValidateName(newName);
                if (error is not null)
                    throw new ThemeStoreException(error);

                if (AllNames(id).Contains(newName))
                    throw new ThemeStoreException("A theme with that name already exists.");

                theme.Name = newName;
                Save(theme);
                return theme;
            }
        }

        /// <summary>Validates and atomically replaces a custom theme file.</summary>
        public void Update(ThemeDefinition theme)
        {
            lock (_lock)
            {
                if (theme is null)
                    throw new ThemeStoreException("Theme is empty.");

                if (theme.Kind == ThemeKind.Builtin || BuiltinThemes.IsBuiltinId(theme.Id))
                    throw new ThemeStoreException("Built-in themes are read-only.");

                if (!ThemeValidator.IsValidCustomId(theme.Id) || !File.Exists(PathFor(theme.Id)))
                    throw new ThemeStoreException("The theme no longer exists.");

                if (AllNames(theme.Id).Contains(theme.Name ?? ""))
                    throw new ThemeStoreException("A theme with that name already exists.");

                Save(theme);
            }
        }

        /// <summary>Resets a custom theme's values to the theme it was based on, keeping its id and name.</summary>
        public ThemeDefinition RestoreDefaults(string id)
        {
            lock (_lock)
            {
                var theme = GetCustomOrThrow(id);

                var origin = Get(theme.BasedOn) ?? BuiltinThemes.ForBase(theme.Base);
                var reset = origin.Clone();

                reset.Id = theme.Id;
                reset.Name = theme.Name;
                reset.Kind = ThemeKind.Custom;
                reset.BasedOn = origin.Kind == ThemeKind.Builtin ? origin.Id : (origin.BasedOn ?? BuiltinThemes.ForBase(origin.Base).Id);

                Save(reset);
                return reset;
            }
        }

        /// <summary>Deletes a custom theme (and its media when no other theme uses it). Built-ins cannot be deleted.</summary>
        public void Delete(string id)
        {
            lock (_lock)
            {
                if (BuiltinThemes.IsBuiltinId(id))
                    throw new ThemeStoreException("Built-in themes cannot be deleted.");

                var theme = GetCustomOrThrow(id);

                try
                {
                    File.Delete(PathFor(id));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new ThemeStoreException("Could not delete the theme: " + ex.Message);
                }

                string? media = theme.Background?.Media;
                if (!string.IsNullOrEmpty(media) && !ReadCustoms().Any(t => string.Equals(t.Background?.Media, media, StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        string path = Path.Combine(_mediaDir, media);
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        Log($"Could not delete media for {id}: {ex.Message}");
                    }
                }
            }

            Deleted?.Invoke(id);
        }

        private ThemeDefinition GetCustomOrThrow(string id)
        {
            if (BuiltinThemes.IsBuiltinId(id))
                throw new ThemeStoreException("Built-in themes are read-only.");

            return Get(id) ?? throw new ThemeStoreException("The theme was not found.");
        }

        private void Save(ThemeDefinition theme)
        {
            NormaliseColours(theme);
            theme.SchemaVersion = ThemeDefinition.CurrentSchemaVersion;
            theme.Kind = ThemeKind.Custom;

            var errors = ThemeValidator.Validate(theme);
            if (errors.Count > 0)
                throw new ThemeStoreException(errors[0]);

            try
            {
                Directory.CreateDirectory(_themesDir);

                string target = PathFor(theme.Id);
                string temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";

                File.WriteAllText(temp, theme.ToJson());

                if (File.Exists(target))
                    File.Replace(temp, target, null);
                else
                    File.Move(temp, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ThemeStoreException("Could not save the theme: " + ex.Message);
            }
        }

        private static void NormaliseColours(ThemeDefinition theme)
        {
            var c = theme.Colors;
            if (c is null)
                return;

            string Up(string v) => ColorMath.TryParseHex(v, out var rgb) ? rgb.ToHex() : v;

            c.Background = Up(c.Background);
            c.BackgroundSecondary = Up(c.BackgroundSecondary);
            c.Surface = Up(c.Surface);
            c.SurfaceElevated = Up(c.SurfaceElevated);
            c.TextPrimary = Up(c.TextPrimary);
            c.TextSecondary = Up(c.TextSecondary);
            c.TextMuted = Up(c.TextMuted);
            c.Border = Up(c.Border);
            c.Accent = Up(c.Accent);
            c.OnAccent = Up(c.OnAccent);
            c.Success = Up(c.Success);
            c.Warning = Up(c.Warning);
            c.Danger = Up(c.Danger);
            c.Info = Up(c.Info);
        }

        // ---------------------------------------------------------------- .zerotheme

        /// <summary>Writes a .zerotheme zip: theme.json plus media/&lt;file&gt; when the theme has background media.</summary>
        public void Export(string id, string destinationFile)
        {
            ThemeDefinition theme;
            string? mediaPath;

            lock (_lock)
            {
                theme = Get(id) ?? throw new ThemeStoreException("The theme was not found.");
                mediaPath = GetMediaPath(theme);
            }

            string temp = destinationFile + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";

            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(destinationFile));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry(ThemeEntryName, CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                        writer.Write(theme.ToJson());

                    if (mediaPath is not null)
                    {
                        var mediaEntry = zip.CreateEntry(MediaEntryPrefix + Path.GetFileName(mediaPath), CompressionLevel.NoCompression);
                        using var src = File.OpenRead(mediaPath);
                        using var dst = mediaEntry.Open();
                        src.CopyTo(dst);
                    }
                }

                File.Move(temp, destinationFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch { /* best effort */ }
                throw new ThemeStoreException("Could not export the theme: " + ex.Message);
            }
        }

        /// <summary>
        /// Imports a .zerotheme file as a new custom theme: fresh id, de-duplicated name, validated JSON and values,
        /// media copied only when allowed (.mp4/.png/.jpg/.jpeg, at most 500 MB).
        /// </summary>
        public ThemeImportResult Import(string zipFile)
        {
            string staging = Path.Combine(Path.GetTempPath(), "ZeroStrap-theme-import-" + Guid.NewGuid().ToString("N"));

            try
            {
                var limits = new ArchiveLimits
                {
                    MaxEntries = 32,
                    MaxEntryBytes = MaxMediaBytes,
                    MaxTotalBytes = MaxMediaBytes + MaxThemeFileBytes,
                    MaxCompressionRatio = 100,
                    AllowedExtensions = new[] { ".json" }.Concat(ThemeValidator.AllowedMediaExtensions).ToArray()
                };

                ExtractionResult extracted;
                try
                {
                    extracted = SafeArchiveExtractor.Extract(zipFile, staging, limits);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new ThemeStoreException("Could not read the file: " + ex.Message);
                }

                if (!extracted.Succeeded)
                    throw new ThemeStoreException(extracted.FatalReason ?? extracted.Plan.FatalReason ?? "The archive was rejected.");

                string themeFile = Path.Combine(staging, ThemeEntryName);
                if (!File.Exists(themeFile))
                    throw new ThemeStoreException("The file does not contain theme.json.");

                if (new FileInfo(themeFile).Length > MaxThemeFileBytes)
                    throw new ThemeStoreException("theme.json is too large.");

                var theme = ThemeDefinition.FromJson(File.ReadAllText(themeFile))
                            ?? throw new ThemeStoreException("theme.json is not a valid theme.");

                if (theme.SchemaVersion != ThemeDefinition.CurrentSchemaVersion)
                    throw new ThemeStoreException(theme.SchemaVersion > ThemeDefinition.CurrentSchemaVersion
                        ? "This theme was made with a newer version of ZeroStrap."
                        : "Unsupported theme version.");

                var result = new ThemeImportResult { Theme = theme };

                string? nameError = ThemeValidator.ValidateName(theme.Name);
                if (nameError is not null)
                    throw new ThemeStoreException(nameError);

                lock (_lock)
                {
                    theme.Id = NewId();
                    theme.Kind = ThemeKind.Custom;
                    theme.Name = UniqueNameCore(theme.Name, AllNames());

                    // An unknown "based on" id means nothing on this machine; fall back to the built-in for its base.
                    if (!BuiltinThemes.IsBuiltinId(theme.BasedOn))
                        theme.BasedOn = BuiltinThemes.ForBase(theme.Base).Id;

                    var errors = ThemeValidator.Validate(theme);
                    if (errors.Count > 0)
                        throw new ThemeStoreException(errors[0]);

                    string? media = theme.Background.Media;
                    if (!string.IsNullOrEmpty(media))
                    {
                        string source = Path.Combine(staging, "media", media);

                        if (File.Exists(source) && new FileInfo(source).Length <= MaxMediaBytes)
                        {
                            string stored = theme.Id + "-" + media;
                            Directory.CreateDirectory(_mediaDir);
                            File.Copy(source, Path.Combine(_mediaDir, stored), overwrite: true);
                            theme.Background.Media = stored;
                        }
                        else
                        {
                            theme.Background.Type = ThemeBackgroundType.None;
                            theme.Background.Media = null;
                            result.Warnings.Add("The background media was missing or not allowed and was left out.");
                        }
                    }

                    Save(theme);
                }

                foreach (var rejected in extracted.AllRejected)
                    result.Warnings.Add($"Skipped {rejected.FullName}: {rejected.Reason}");

                return result;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(staging))
                        Directory.Delete(staging, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log($"Could not clean up {staging}: {ex.Message}");
                }
            }
        }
    }
}
