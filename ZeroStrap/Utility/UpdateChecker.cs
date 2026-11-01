using ZeroStrap.Enums;
using ZeroStrap.Models.APIs.GitHub;

namespace ZeroStrap.Utility
{
    internal record UpdateCheckResult(UpdateState State, string? LatestTag, GithubReleaseAsset? Asset);

    internal readonly record struct ZipEntryInfo(string FullName, long Length);

    internal record ZipEntrySelection(ZipEntryInfo? Entry, string? Reason);

    /// <summary>
    /// Pure update decision logic. Nothing in here touches App state, the logger, or the network.
    /// </summary>
    internal static class UpdateChecker
    {
        public const string ExecutableName = "ZeroStrap.exe";
        public const long MaxExtractedLength = 300L * 1024 * 1024;

        public static UpdateCheckResult Evaluate(string? localVersion, GithubRelease? release)
        {
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                return new UpdateCheckResult(UpdateState.CheckFailed, null, null);

            string tag = release.TagName;

            if (!TryParseVersion(localVersion, out Version? local))
                return new UpdateCheckResult(UpdateState.UnknownVersion, tag, null);

            if (!TryParseVersion(tag, out Version? remote))
                return new UpdateCheckResult(UpdateState.CheckFailed, tag, null);

            // never offer pre-releases or drafts
            if (release.Prerelease || release.Draft)
                return new UpdateCheckResult(UpdateState.UpToDate, tag, null);

            if (remote!.CompareTo(local!) > 0)
                return new UpdateCheckResult(UpdateState.UpdateAvailable, tag, SelectAsset(release.Assets));

            return new UpdateCheckResult(UpdateState.UpToDate, tag, null);
        }

        /// <summary>
        /// Parses a numeric version, normalising so that 3.0.0 == 3.0.0.0
        /// </summary>
        public static bool TryParseVersion(string? input, out Version? version)
        {
            version = null;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            try
            {
                var parsed = Utilities.GetVersionFromString(input.Trim());
                version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static GithubReleaseAsset? SelectAsset(IEnumerable<GithubReleaseAsset>? assets)
        {
            if (assets is null)
                return null;

            var candidates = assets.Where(x => !string.IsNullOrEmpty(x?.Name) && !string.IsNullOrEmpty(x.BrowserDownloadUrl)).ToList();

            return candidates.FirstOrDefault(x =>
                       x.Name.StartsWith("ZeroStrap", StringComparison.OrdinalIgnoreCase)
                       && x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                   ?? candidates.FirstOrDefault(x =>
                       x.Name.StartsWith("ZeroStrap", StringComparison.OrdinalIgnoreCase)
                       && x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Picks the single ZeroStrap.exe entry out of a release archive: at the archive root or
        /// inside exactly one top-level folder. Any unsafe candidate rejects the whole archive.
        /// </summary>
        public static ZipEntrySelection SelectZipEntry(IEnumerable<ZipEntryInfo> entries)
        {
            var matches = new List<ZipEntryInfo>();

            foreach (var entry in entries)
            {
                string name = (entry.FullName ?? "").Replace('\\', '/');

                if (name.Length == 0 || name.EndsWith('/'))
                    continue; // directory entry

                string[] segments = name.Split('/');

                if (!segments[^1].Equals(ExecutableName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (name.StartsWith('/') || Path.IsPathRooted(name))
                    return new ZipEntrySelection(null, $"Unsafe entry path (rooted): {entry.FullName}");

                if (name.Contains(':'))
                    return new ZipEntrySelection(null, $"Unsafe entry path (contains ':'): {entry.FullName}");

                if (segments.Any(x => x == ".."))
                    return new ZipEntrySelection(null, $"Unsafe entry path (traversal): {entry.FullName}");

                if (segments.Length > 2 || segments.Any(x => x.Length == 0))
                    continue; // deeper than one top-level folder

                if (entry.Length > MaxExtractedLength)
                    return new ZipEntrySelection(null, $"Entry too large ({entry.Length} bytes): {entry.FullName}");

                matches.Add(entry);
            }

            if (matches.Count == 0)
                return new ZipEntrySelection(null, $"No {ExecutableName} entry found at the archive root or in one top-level folder");

            if (matches.Count > 1)
                return new ZipEntrySelection(null, $"More than one {ExecutableName} entry found");

            return new ZipEntrySelection(matches[0], null);
        }

        /// <summary>
        /// Makes a remote-supplied tag safe to use as a single directory name
        /// </summary>
        public static string SanitizeForPath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "latest";

            var chars = value.Trim().Select(c => (c < 128 && char.IsLetterOrDigit(c)) || c == '.' || c == '-' || c == '_' ? c : '_').ToArray();
            string result = new string(chars).Trim('.');

            return result.Length == 0 ? "latest" : result;
        }
    }
}
