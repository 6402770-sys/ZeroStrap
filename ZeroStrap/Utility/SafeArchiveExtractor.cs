using System.IO.Compression;

namespace ZeroStrap.Utility
{
    /// <summary>Limits applied to an archive before and during extraction.</summary>
    public sealed class ArchiveLimits
    {
        public long MaxTotalBytes { get; init; } = 1L * 1024 * 1024 * 1024;
        public int MaxEntries { get; init; } = 1000;
        public long MaxEntryBytes { get; init; } = 512L * 1024 * 1024;

        /// <summary>Maximum uncompressed:compressed ratio per entry (e.g. 100 = 100:1).</summary>
        public double MaxCompressionRatio { get; init; } = 100;

        /// <summary>Entries smaller than this are exempt from the ratio check (tiny files compress absurdly well).</summary>
        public long MinBytesForRatioCheck { get; init; } = 1024 * 1024;

        /// <summary>When set, only files with these extensions (with leading dot, case-insensitive) are accepted.</summary>
        public IReadOnlyCollection<string>? AllowedExtensions { get; init; }
    }

    /// <summary>What the archive claims about one entry. Declared sizes are untrusted.</summary>
    public readonly record struct ArchiveEntryInfo(string FullName, long Length, long CompressedLength, bool IsDirectory = false);

    public sealed record PlannedEntry(ArchiveEntryInfo Source, string RelativePath, string DestinationPath)
    {
        public bool IsDirectory => Source.IsDirectory;
    }

    public sealed record RejectedEntry(string FullName, string Reason);

    public sealed class ExtractionPlan
    {
        public List<PlannedEntry> Accepted { get; } = new();
        public List<RejectedEntry> Rejected { get; } = new();

        /// <summary>Set when the whole archive is refused (too many entries, total size too large).</summary>
        public string? FatalReason { get; internal set; }

        public bool IsRefused => FatalReason is not null;
    }

    public sealed class ExtractionResult
    {
        public ExtractionPlan Plan { get; init; } = new();
        public List<string> Extracted { get; } = new();
        public List<RejectedEntry> Failed { get; } = new();

        /// <summary>Plan rejections plus entries that failed while copying.</summary>
        public IEnumerable<RejectedEntry> AllRejected => Plan.Rejected.Concat(Failed);

        public string? FatalReason { get; internal set; }
        public bool Succeeded => FatalReason is null && Plan.FatalReason is null;
    }

    public static class SafeArchiveExtractor
    {
        /// <summary>
        /// Pure planning step: validates names and declared sizes against the limits and the destination root.
        /// Performs no IO beyond path normalisation.
        /// </summary>
        public static ExtractionPlan Plan(IEnumerable<ArchiveEntryInfo> entries, string destinationRoot, ArchiveLimits limits)
        {
            var plan = new ExtractionPlan();
            var list = entries as IList<ArchiveEntryInfo> ?? entries.ToList();

            if (list.Count > limits.MaxEntries)
            {
                plan.FatalReason = $"Archive has {list.Count} entries; the limit is {limits.MaxEntries}.";
                return plan;
            }

            string root = Path.GetFullPath(destinationRoot);
            string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

            var allowed = limits.AllowedExtensions is null
                ? null
                : new HashSet<string>(limits.AllowedExtensions, StringComparer.OrdinalIgnoreCase);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long declaredTotal = 0;

            foreach (var entry in list)
            {
                if (!TryNormalise(entry.FullName, out string relative, out string? error))
                {
                    plan.Rejected.Add(new RejectedEntry(entry.FullName, error!));
                    continue;
                }

                string destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    plan.Rejected.Add(new RejectedEntry(entry.FullName, "Path resolves outside the destination."));
                    continue;
                }

                if (!entry.IsDirectory)
                {
                    if (allowed is not null && !allowed.Contains(Path.GetExtension(relative)))
                    {
                        plan.Rejected.Add(new RejectedEntry(entry.FullName, "File type is not allowed."));
                        continue;
                    }

                    if (entry.Length < 0 || entry.CompressedLength < 0)
                    {
                        plan.Rejected.Add(new RejectedEntry(entry.FullName, "Entry has invalid sizes."));
                        continue;
                    }

                    if (entry.Length > limits.MaxEntryBytes)
                    {
                        plan.Rejected.Add(new RejectedEntry(entry.FullName, "Entry is larger than the allowed size."));
                        continue;
                    }

                    if (entry.Length >= limits.MinBytesForRatioCheck &&
                        entry.Length > limits.MaxCompressionRatio * Math.Max(entry.CompressedLength, 1))
                    {
                        plan.Rejected.Add(new RejectedEntry(entry.FullName, "Compression ratio is suspiciously high."));
                        continue;
                    }
                }

                if (!seen.Add(relative))
                {
                    plan.Rejected.Add(new RejectedEntry(entry.FullName, "Duplicate path."));
                    continue;
                }

                declaredTotal += entry.IsDirectory ? 0 : entry.Length;
                plan.Accepted.Add(new PlannedEntry(entry, relative, destination));
            }

            if (declaredTotal > limits.MaxTotalBytes)
            {
                plan.Accepted.Clear();
                plan.FatalReason = "Archive is larger than the allowed total size.";
            }

            return plan;
        }

        /// <summary>
        /// Normalises an archive entry name to a forward-slash relative path. Rejects rooted paths, drive letters,
        /// alternate data streams, "..", reserved device names and anything Windows would reinterpret.
        /// </summary>
        public static bool TryNormalise(string? entryName, out string relativePath, out string? error)
        {
            relativePath = "";
            error = null;

            if (string.IsNullOrEmpty(entryName))
            {
                error = "Empty entry name.";
                return false;
            }

            string name = entryName.Replace('\\', '/');

            if (name.StartsWith('/'))
            {
                error = "Rooted path.";
                return false;
            }

            if (name.Contains(':'))
            {
                error = "Drive letters and stream names are not allowed.";
                return false;
            }

            var parts = new List<string>();
            foreach (var segment in name.Split('/'))
            {
                if (segment.Length == 0 || segment == ".")
                    continue;

                if (segment == "..")
                {
                    error = "Path traversal.";
                    return false;
                }

                var check = PathValidator.IsFileNameValid(segment);
                if (check != PathValidator.ValidationResult.Ok)
                {
                    error = check == PathValidator.ValidationResult.ReservedFileName
                        ? "Reserved or invalid name."
                        : "Illegal character in name.";
                    return false;
                }

                parts.Add(segment);
            }

            if (parts.Count == 0)
            {
                error = "Empty entry name.";
                return false;
            }

            relativePath = string.Join('/', parts);
            return true;
        }

        public static ExtractionResult Extract(string zipPath, string destinationRoot, ArchiveLimits limits)
        {
            using var stream = File.OpenRead(zipPath);
            return Extract(stream, destinationRoot, limits);
        }

        public static ExtractionResult Extract(Stream zipStream, string destinationRoot, ArchiveLimits limits)
        {
            ZipArchive archive;

            try
            {
                archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
            {
                return new ExtractionResult { FatalReason = "Not a valid zip archive." };
            }

            using (archive)
            {
                var infos = new List<ArchiveEntryInfo>(archive.Entries.Count);
                var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);

                foreach (var e in archive.Entries)
                {
                    bool isDir = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
                    infos.Add(new ArchiveEntryInfo(e.FullName, e.Length, e.CompressedLength, isDir));
                    byName.TryAdd(e.FullName, e);
                }

                var plan = Plan(infos, destinationRoot, limits);
                var result = new ExtractionResult { Plan = plan };

                if (plan.IsRefused)
                    return result;

                Directory.CreateDirectory(destinationRoot);
                long written = 0;

                foreach (var item in plan.Accepted)
                {
                    if (item.IsDirectory)
                    {
                        Directory.CreateDirectory(item.DestinationPath);
                        continue;
                    }

                    if (!byName.TryGetValue(item.Source.FullName, out var zipEntry))
                        continue;

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);

                        // Declared lengths are not trusted: cap by what is actually read.
                        long budget = Math.Min(limits.MaxEntryBytes, limits.MaxTotalBytes - written);
                        long copied = CopyCapped(zipEntry, item.DestinationPath, budget, out bool overflow);

                        if (overflow)
                        {
                            TryDelete(item.DestinationPath);

                            if (limits.MaxTotalBytes - written <= limits.MaxEntryBytes)
                            {
                                result.FatalReason = "Archive expanded beyond the allowed total size.";
                                break;
                            }

                            result.Failed.Add(new RejectedEntry(item.Source.FullName, "Entry expanded beyond the allowed size."));
                            continue;
                        }

                        written += copied;
                        result.Extracted.Add(item.DestinationPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        TryDelete(item.DestinationPath);
                        result.Failed.Add(new RejectedEntry(item.Source.FullName, "Could not extract: " + ex.Message));
                    }
                }

                if (result.FatalReason is not null)
                {
                    foreach (var path in result.Extracted)
                        TryDelete(path);

                    result.Extracted.Clear();
                }

                return result;
            }
        }

        private static long CopyCapped(ZipArchiveEntry entry, string destination, long budget, out bool overflow)
        {
            overflow = false;
            long total = 0;
            var buffer = new byte[81920];

            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;

                if (total > budget)
                {
                    overflow = true;
                    return total;
                }

                output.Write(buffer, 0, read);
            }

            return total;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
