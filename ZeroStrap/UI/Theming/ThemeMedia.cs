using System.Security.Cryptography;

using ZeroStrap.Models.Theming;

namespace ZeroStrap.UI.Theming
{
    /// <summary>Result of checking a candidate background file before it is accepted.</summary>
    public sealed record MediaCheck(ThemeBackgroundType Kind, string? Error, string? Warning)
    {
        public bool Ok => Error is null;
    }

    /// <summary>A media file stored in the theme media folder.</summary>
    public sealed record ImportedMedia(string Name, bool Added);

    public enum BackgroundChange
    {
        /// <summary>Nothing visible changed.</summary>
        None,

        /// <summary>Only dim, blur, speed or the scrim colour changed: update the running visuals in place.</summary>
        Parameters,

        /// <summary>The media, its type or the render mode changed: tear down and rebuild.</summary>
        Rebuild
    }

    /// <summary>Pure rules for theme background media: validation, content-addressed naming, reference counting and change detection.</summary>
    public static class ThemeMedia
    {
        /// <summary>Files above this size are accepted but flagged.</summary>
        public const long WarnBytes = 200L * 1024 * 1024;

        public const long MaxBytes = ThemeStore.MaxMediaBytes;

        /// <summary>At this blur radius and above the media is rendered at half resolution.</summary>
        public const int HalfResolutionBlur = 8;

        public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };
        public const string VideoExtension = ".mp4";

        // ---------------------------------------------------------------- validation

        public static ThemeBackgroundType KindForExtension(string? extension)
        {
            string ext = (extension ?? "").ToLowerInvariant();
            if (ext == VideoExtension)
                return ThemeBackgroundType.Video;

            return ImageExtensions.Contains(ext) ? ThemeBackgroundType.Image : ThemeBackgroundType.None;
        }

        /// <summary>Checks extension, existence and size from a file name and (when known) its length.</summary>
        public static MediaCheck Check(string? fileName, long? length, ThemeBackgroundType expected = ThemeBackgroundType.None)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return new MediaCheck(ThemeBackgroundType.None, "Choose a file.", null);

            var kind = KindForExtension(Path.GetExtension(fileName));
            if (kind == ThemeBackgroundType.None)
                return new MediaCheck(kind, "Only .mp4 videos and .png, .jpg or .jpeg images can be used.", null);

            if (expected != ThemeBackgroundType.None && expected != kind)
            {
                return new MediaCheck(kind, expected == ThemeBackgroundType.Video
                    ? "Choose an .mp4 video."
                    : "Choose a .png, .jpg or .jpeg image.", null);
            }

            if (length is null)
                return new MediaCheck(kind, "That file could not be found.", null);

            if (length.Value <= 0)
                return new MediaCheck(kind, "That file is empty.", null);

            if (length.Value > MaxBytes)
                return new MediaCheck(kind, $"That file is {FormatSize(length.Value)}. Backgrounds can be at most {FormatSize(MaxBytes)}.", null);

            if (length.Value > WarnBytes)
                return new MediaCheck(kind, null, $"This file is {FormatSize(length.Value)}. Large backgrounds use more memory and can slow ZeroStrap down.");

            return new MediaCheck(kind, null, null);
        }

        /// <summary>Checks a file on disk.</summary>
        public static MediaCheck CheckFile(string? path, ThemeBackgroundType expected = ThemeBackgroundType.None)
        {
            long? length = null;

            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    length = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new MediaCheck(KindForExtension(Path.GetExtension(path)), "That file could not be read.", null);
            }

            return Check(path, length, expected);
        }

        public static string FormatSize(long bytes)
        {
            const double mb = 1024.0 * 1024.0;
            return bytes >= 1024L * 1024 * 1024 ? $"{bytes / (mb * 1024):0.0} GB" : $"{bytes / mb:0.#} MB";
        }

        // ---------------------------------------------------------------- naming

        /// <summary>Content-addressed stored name: lower-case sha256 hex plus the lower-case extension.</summary>
        public static string StoredName(string sha256Hex, string extension) =>
            sha256Hex.ToLowerInvariant() + extension.ToLowerInvariant();

        /// <summary>Poster still of a video: the media name without its extension plus ".poster.png".</summary>
        public static string PosterName(string media) => Path.GetFileNameWithoutExtension(media) + ".poster.png";

        public static string HashFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        /// <summary>
        /// Copies <paramref name="source"/> into <paramref name="mediaDir"/> as &lt;sha256&gt;.&lt;ext&gt;. The source is never
        /// moved. When the same content is already stored nothing is copied.
        /// </summary>
        public static ImportedMedia Import(string mediaDir, string source)
        {
            string name = StoredName(HashFile(source), Path.GetExtension(source));
            string dest = Path.Combine(mediaDir, name);

            Directory.CreateDirectory(mediaDir);

            if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(source).Length)
                return new ImportedMedia(name, false);

            string temp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temp, overwrite: true);
                File.Move(temp, dest, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            return new ImportedMedia(name, true);
        }

        // ---------------------------------------------------------------- reference counting

        /// <summary>True when any theme in <paramref name="themes"/> uses the media file.</summary>
        public static bool IsReferenced(string? media, IEnumerable<ThemeDefinition> themes) =>
            !string.IsNullOrEmpty(media) &&
            themes.Any(t => string.Equals(t.Background?.Media, media, StringComparison.OrdinalIgnoreCase));

        /// <summary>The files to delete for a media name that is no longer referenced: the media and, for videos, its poster.</summary>
        public static IReadOnlyList<string> FilesFor(string media)
        {
            var files = new List<string> { media };
            if (KindForExtension(Path.GetExtension(media)) == ThemeBackgroundType.Video)
                files.Add(PosterName(media));

            return files;
        }

        // ---------------------------------------------------------------- change detection

        /// <summary>A type of None, or a missing media name, means there is no background.</summary>
        public static bool HasBackground(ThemeBackground? b) =>
            b is { Type: not ThemeBackgroundType.None } && !string.IsNullOrEmpty(b.Media);

        public static bool IsHalfResolution(ThemeBackground? b) => b is not null && b.Blur >= HalfResolutionBlur;

        /// <summary>Decides how the running background must react to a changed definition.</summary>
        public static BackgroundChange Compare(ThemeBackground? before, string? scrimBefore, ThemeBackground? after, string? scrimAfter)
        {
            bool hadBg = HasBackground(before);
            bool hasBg = HasBackground(after);

            if (!hadBg && !hasBg)
                return BackgroundChange.None;

            if (hadBg != hasBg
                || before!.Type != after!.Type
                || !string.Equals(before.Media, after.Media, StringComparison.OrdinalIgnoreCase)
                || IsHalfResolution(before) != IsHalfResolution(after))
                return BackgroundChange.Rebuild;

            bool same = before.Opacity.Equals(after.Opacity)
                        && before.Blur == after.Blur
                        && before.Speed.Equals(after.Speed)
                        && string.Equals(scrimBefore, scrimAfter, StringComparison.OrdinalIgnoreCase);

            return same ? BackgroundChange.None : BackgroundChange.Parameters;
        }
    }

    /// <summary>
    /// One-time notices about a theme's background that could not be shown (raised by the shell backdrop, consumed by
    /// Theme Studio when that theme is opened).
    /// </summary>
    public sealed class BackgroundNoticeBoard
    {
        private readonly Dictionary<string, string> _pending = new();

        public static BackgroundNoticeBoard Default { get; } = new();

        public event Action<string>? Posted;

        public void Post(string themeId, string message)
        {
            lock (_pending)
                _pending[themeId] = message;

            Posted?.Invoke(themeId);
        }

        /// <summary>Returns the pending notice for the theme and clears it, or null.</summary>
        public string? Take(string themeId)
        {
            lock (_pending)
                return _pending.Remove(themeId, out var message) ? message : null;
        }

        public bool Has(string themeId)
        {
            lock (_pending)
                return _pending.ContainsKey(themeId);
        }

        public static string FailureMessage(ThemeBackgroundType type) => type == ThemeBackgroundType.Image
            ? "The background image couldn't be loaded. Choose another file."
            : "The background video couldn't be played. Choose another file.";
    }
}
