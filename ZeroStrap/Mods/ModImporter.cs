using System.IO.Compression;

namespace ZeroStrap.Mods
{
    public enum ImportSourceKind
    {
        Zip,
        Folder
    }

    public enum ImportLayout
    {
        /// <summary>No accepted shape was found; see <see cref="ImportPreview.FatalReason"/>.</summary>
        Unrecognised,

        /// <summary>content / ExtraContent / PlatformContent / shaders directly at the top.</summary>
        DirectRoots,

        /// <summary>The same, inside a single wrapper folder (stripped).</summary>
        WrappedRoots,

        /// <summary>The roots are inside a Modifications folder (optionally inside one wrapper).</summary>
        ModificationsFolder,

        /// <summary>manifest.json plus a files folder (optionally inside one wrapper).</summary>
        ZeroStrapPackage
    }

    /// <summary>An accepted file: version-root-relative path (backslashes, as in a package) and its size.</summary>
    public sealed record ImportFile(string Path, long Size);

    /// <summary>A file or folder that will not be imported. <paramref name="Path"/> is as written in the source.</summary>
    public sealed record ImportRejection(string Path, string Reason);

    /// <summary>A path the import provides that an enabled package already provides.</summary>
    public sealed record ImportConflict(string Path, IReadOnlyList<string> PackageNames);

    /// <summary>
    /// Result of analysing a zip or folder without importing anything. All strings meant for people are plain English.
    /// </summary>
    public sealed record ImportPreview
    {
        public string SourcePath { get; init; } = "";
        public ImportSourceKind SourceKind { get; init; }
        public ImportLayout Layout { get; init; }

        /// <summary>The single wrapper folder that was stripped, when there was one.</summary>
        public string? WrapperFolder { get; init; }

        /// <summary>When set, the whole source is refused and nothing can be installed.</summary>
        public string? FatalReason { get; init; }

        public IReadOnlyList<ImportFile> AcceptedFiles { get; init; } = Array.Empty<ImportFile>();
        public IReadOnlyList<ImportRejection> RejectedEntries { get; init; } = Array.Empty<ImportRejection>();

        /// <summary>Harmless junk (__MACOSX, .DS_Store, Thumbs.db, desktop.ini, README/LICENSE) that is skipped.</summary>
        public IReadOnlyList<string> IgnoredEntries { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        public string SuggestedName { get; init; } = "Imported mod";
        public ModCategory SuggestedCategory { get; init; } = ModCategory.Other;

        /// <summary>From a ZeroStrap package manifest; null otherwise.</summary>
        public string? Author { get; init; }
        public string? Description { get; init; }
        public string? Version { get; init; }

        /// <summary>Filled by <see cref="ModStudio.AnalyzeImport"/>: paths that enabled packages already provide.</summary>
        public IReadOnlyList<ImportConflict> Conflicts { get; init; } = Array.Empty<ImportConflict>();

        public long TotalBytes => AcceptedFiles.Sum(f => f.Size);

        public bool CanInstall => CannotInstallReason is null;

        /// <summary>Why <see cref="ModImporter.Install"/> would refuse this preview; null when it can proceed.</summary>
        public string? CannotInstallReason =>
            FatalReason ?? (AcceptedFiles.Count == 0 ? "None of the files in this mod can be imported." : null);

        internal ArchiveLimits? Limits { get; init; }

        /// <summary>Folder imports: (source file, staging-relative path with forward slashes).</summary>
        internal IReadOnlyList<(string Source, string Mapped)> FolderFiles { get; init; } = Array.Empty<(string, string)>();
    }

    public sealed record ModImportResult(ModPackage Package, ModRebuildResult Rebuild);

    /// <summary>
    /// Imports mods from a zip or a folder. Analysis is pure (central directory / directory listing only); installation
    /// stages the accepted files in a temporary folder, builds a package and enables it at the lowest priority.
    /// Nothing imported is ever executed.
    /// </summary>
    public sealed class ModImporter
    {
        public static readonly IReadOnlyList<string> AllowedExtensions = new[]
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds",
            ".ogg", ".mp3", ".wav", ".flac",
            ".ttf", ".otf", ".ttc",
            ".ktx", ".mesh"
        };

        private const int MaxManifestBytes = 64 * 1024;
        private const int MaxNameLength = 80;

        private static readonly string PlanRoot = Path.Combine(Path.GetTempPath(), "zerostrap-import-plan");

        /// <summary>Limits for mod imports: 2 GB total, 20,000 entries, 256 MB per file, 100:1 compression ratio.</summary>
        public static ArchiveLimits DefaultLimits() => new()
        {
            MaxTotalBytes = 2L * 1024 * 1024 * 1024,
            MaxEntries = 20_000,
            MaxEntryBytes = 256L * 1024 * 1024,
            MaxCompressionRatio = 100,
            RejectLinks = true
        };

        private readonly ModStudio _studio;

        public ModImporter(ModStudio studio)
        {
            _studio = studio;
        }

        // ================================================================ analysis

        /// <summary>
        /// Analyses a .zip file or a folder. Never writes to disk and never throws for bad input: problems are
        /// reported in <see cref="ImportPreview.FatalReason"/> or <see cref="ImportPreview.RejectedEntries"/>.
        /// </summary>
        public static ImportPreview Analyze(string path, ArchiveLimits? limits = null)
        {
            limits ??= DefaultLimits();

            if (string.IsNullOrWhiteSpace(path))
                return Failed("", ImportSourceKind.Zip, "No file or folder was chosen.");

            try
            {
                if (Directory.Exists(path))
                    return AnalyzeFolder(path, limits);

                if (File.Exists(path))
                    return AnalyzeZip(path, limits);

                return Failed(path, ImportSourceKind.Zip, "That file or folder could not be found.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                return Failed(path, Directory.Exists(path) ? ImportSourceKind.Folder : ImportSourceKind.Zip,
                    "ZeroStrap could not read that mod: " + ex.Message);
            }
        }

        private static ImportPreview Failed(string path, ImportSourceKind kind, string reason) => new()
        {
            SourcePath = path,
            SourceKind = kind,
            Layout = ImportLayout.Unrecognised,
            FatalReason = reason
        };

        private static ImportPreview AnalyzeZip(string path, ArchiveLimits limits)
        {
            long length = new FileInfo(path).Length;

            if (length > limits.MaxTotalBytes)
                return Failed(path, ImportSourceKind.Zip, $"This file is {FormatSize(length)}, which is larger than the {FormatSize(limits.MaxTotalBytes)} limit for mods.");

            ZipArchive archive;
            FileStream stream = File.OpenRead(path);

            try
            {
                archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
            {
                stream.Dispose();
                return Failed(path, ImportSourceKind.Zip, "That file is not a valid zip archive.");
            }

            using (archive)
            {
                var infos = new List<ArchiveEntryInfo>(archive.Entries.Count);
                var byNormalised = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in archive.Entries)
                {
                    infos.Add(SafeArchiveExtractor.InfoOf(entry));

                    if (SafeArchiveExtractor.TryNormalise(entry.FullName, out string normalised, out _))
                        byNormalised.TryAdd(normalised, entry);
                }

                string? ReadManifest(string manifestPath)
                {
                    if (!byNormalised.TryGetValue(manifestPath, out var entry))
                        return null;

                    if (entry.Length > MaxManifestBytes)
                        throw new InvalidDataException("manifest.json is too large.");

                    using var input = entry.Open();
                    return ReadCapped(input);
                }

                return Build(path, ImportSourceKind.Zip, Path.GetFileNameWithoutExtension(path), infos, new List<ImportRejection>(), ReadManifest, null, limits);
            }
        }

        private static ImportPreview AnalyzeFolder(string path, ArchiveLimits limits)
        {
            var root = new DirectoryInfo(path);

            if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return Failed(path, ImportSourceKind.Folder, "That folder is a link or junction, which ZeroStrap does not follow. Choose the real folder instead.");

            var infos = new List<ArchiveEntryInfo>();
            var rejected = new List<ImportRejection>();
            var fullByRelative = new Dictionary<string, string>(StringComparer.Ordinal);

            string? fatal = WalkFolder(root.FullName, limits, infos, rejected, fullByRelative);

            if (fatal is not null)
                return Failed(path, ImportSourceKind.Folder, fatal);

            string? ReadManifest(string manifestPath)
            {
                if (!fullByRelative.TryGetValue(manifestPath, out string? full))
                {
                    // the walker keeps original names; find a case-insensitive match
                    full = fullByRelative.FirstOrDefault(kv => kv.Key.Equals(manifestPath, StringComparison.OrdinalIgnoreCase)).Value;

                    if (full is null)
                        return null;
                }

                var file = new FileInfo(full);

                if (file.Length > MaxManifestBytes)
                    throw new InvalidDataException("manifest.json is too large.");

                using var input = file.OpenRead();
                return ReadCapped(input);
            }

            string displayName = root.Name.Trim('\\', '/', ':');

            return Build(path, ImportSourceKind.Folder, displayName, infos, rejected, ReadManifest, fullByRelative, limits);
        }

        /// <summary>Lists files without following links. Returns a fatal reason when the folder is over the limits.</summary>
        private static string? WalkFolder(string root, ArchiveLimits limits, List<ArchiveEntryInfo> infos, List<ImportRejection> rejected, Dictionary<string, string> fullByRelative)
        {
            var stack = new Stack<(string Full, string Relative)>();
            stack.Push((root, ""));

            long total = 0;
            int seen = 0;

            while (stack.Count > 0)
            {
                var (dirFull, dirRelative) = stack.Pop();

                foreach (FileSystemInfo item in new DirectoryInfo(dirFull).EnumerateFileSystemInfos())
                {
                    string relative = dirRelative.Length == 0 ? item.Name : dirRelative + "/" + item.Name;

                    if (++seen > limits.MaxEntries)
                        return $"This folder has more than {limits.MaxEntries:N0} files and folders, which is more than ZeroStrap imports.";

                    if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        rejected.Add(new ImportRejection(relative, "Links, shortcuts and junctions are not followed."));
                        continue;
                    }

                    if (item is DirectoryInfo)
                    {
                        stack.Push((item.FullName, relative));
                        continue;
                    }

                    long length = ((FileInfo)item).Length;
                    total += length;

                    if (total > limits.MaxTotalBytes)
                        return $"This folder is larger than the {FormatSize(limits.MaxTotalBytes)} limit for mods.";

                    infos.Add(new ArchiveEntryInfo(relative, length, length));
                    fullByRelative[relative] = item.FullName;
                }
            }

            return null;
        }

        private static ImportPreview Build(
            string sourcePath,
            ImportSourceKind kind,
            string displayName,
            List<ArchiveEntryInfo> infos,
            List<ImportRejection> rejected,
            Func<string, string?> readManifest,
            Dictionary<string, string>? folderFullByRelative,
            ArchiveLimits baseLimits)
        {
            if (infos.Count > baseLimits.MaxEntries)
                return Failed(sourcePath, kind, $"This mod has {infos.Count:N0} entries, which is more than the {baseLimits.MaxEntries:N0} ZeroStrap imports.");

            var files = new List<string>();

            foreach (var info in infos)
            {
                if (!info.IsDirectory && SafeArchiveExtractor.TryNormalise(info.FullName, out string normalised, out _))
                    files.Add(normalised);
            }

            var layout = DetectLayout(files);

            if (layout.Fatal is not null)
            {
                // still run the generic checks so unsafe names are reported alongside the explanation
                var failedPlan = SafeArchiveExtractor.Plan(infos, PlanRoot, WithMapper(baseLimits, (_, _) => ArchivePathDecision.Skip()));

                return new ImportPreview
                {
                    SourcePath = sourcePath,
                    SourceKind = kind,
                    Layout = ImportLayout.Unrecognised,
                    FatalReason = failedPlan.FatalReason ?? layout.Fatal,
                    RejectedEntries = rejected.Concat(failedPlan.Rejected.Select(r => new ImportRejection(r.FullName, r.Reason))).ToList()
                };
            }

            var warnings = new List<string>();
            ManifestInfo? manifest = null;

            if (layout.Layout == ImportLayout.ZeroStrapPackage)
            {
                try
                {
                    string? text = readManifest(layout.ManifestPath!);

                    if (text is null)
                        return Failed(sourcePath, kind, "manifest.json could not be found.");

                    manifest = ParseManifest(text, warnings, out string? manifestFatal);

                    if (manifestFatal is not null)
                        return Failed(sourcePath, kind, manifestFatal);
                }
                catch (InvalidDataException ex)
                {
                    return Failed(sourcePath, kind, ex.Message);
                }
            }

            var limits = WithMapper(baseLimits, BuildMapper(layout));
            var plan = SafeArchiveExtractor.Plan(infos, PlanRoot, limits);

            var allRejected = new List<ImportRejection>(rejected);
            allRejected.AddRange(plan.Rejected.Select(r => new ImportRejection(r.FullName, r.Reason)));

            var accepted = new List<PlannedEntry>();
            var directories = ModPathRules.NewPathSet();

            foreach (var item in plan.Accepted)
            {
                string[] parts = item.RelativePath.Split('/');

                for (int i = 1; i < parts.Length; i++)
                    directories.Add(string.Join('/', parts.Take(i)));
            }

            foreach (var item in plan.Accepted)
            {
                if (directories.Contains(item.RelativePath))
                    allRejected.Add(new ImportRejection(item.Source.FullName, "A file and a folder in this mod have the same name."));
                else
                    accepted.Add(item);
            }

            var acceptedFiles = accepted
                .Select(a => new ImportFile(a.RelativePath.Replace('/', '\\'), a.Source.Length))
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (plan.FatalReason is not null)
                acceptedFiles.Clear();

            ModCategory? manifestCategory = manifest?.Category;

            return new ImportPreview
            {
                SourcePath = sourcePath,
                SourceKind = kind,
                Layout = layout.Layout,
                WrapperFolder = layout.Wrapper,
                FatalReason = plan.FatalReason,
                AcceptedFiles = acceptedFiles,
                RejectedEntries = allRejected,
                IgnoredEntries = plan.Ignored.ToList(),
                Warnings = warnings,
                SuggestedName = manifest?.Name ?? TidyName(displayName),
                SuggestedCategory = manifestCategory ?? ModPackageStore.GuessCategory(acceptedFiles.Select(f => f.Path)),
                Author = manifest?.Author,
                Description = manifest?.Description,
                Version = manifest?.Version,
                Limits = limits,
                FolderFiles = folderFullByRelative is null
                    ? Array.Empty<(string, string)>()
                    : accepted.Select(a => (folderFullByRelative[a.Source.FullName], a.RelativePath)).ToList()
            };
        }

        private static ArchiveLimits WithMapper(ArchiveLimits limits, Func<string, bool, ArchivePathDecision> mapper) => new()
        {
            MaxTotalBytes = limits.MaxTotalBytes,
            MaxEntries = limits.MaxEntries,
            MaxEntryBytes = limits.MaxEntryBytes,
            MaxCompressionRatio = limits.MaxCompressionRatio,
            MinBytesForRatioCheck = limits.MinBytesForRatioCheck,
            AllowedExtensions = limits.AllowedExtensions,
            RejectLinks = limits.RejectLinks,
            PathMapper = mapper
        };

        // ================================================================ layout

        internal sealed record LayoutInfo(ImportLayout Layout, string? Wrapper, string BasePrefix, string? ManifestPath, string? Fatal);

        private const string UnrecognisedMessage =
            "This does not look like a Roblox mod ZeroStrap can import. Expected folders named content, ExtraContent, PlatformContent or shaders at the top "
            + "(optionally inside a single folder or a Modifications folder), or a ZeroStrap package with manifest.json and a files folder.";

        private static bool IsJunkName(string path)
        {
            string[] parts = path.Split('/');

            if (parts.Any(p => p.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)))
                return true;

            string name = parts[^1];
            return name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
                || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDocumentName(string name) =>
            name.StartsWith("README", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase);

        private static string DirectoryOf(string path)
        {
            int i = path.LastIndexOf('/');
            return i < 0 ? "" : path[..i];
        }

        /// <summary>Decides which of the accepted shapes (if any) a set of normalised, forward-slash file paths has. Pure.</summary>
        internal static LayoutInfo DetectLayout(IReadOnlyList<string> normalisedFiles)
        {
            var files = normalisedFiles
                .Where(f => !IsJunkName(f) && !(IsDocumentName(f[(f.LastIndexOf('/') + 1)..]) && DirectoryOf(f).Length == 0))
                .ToList();

            if (files.Count == 0)
                return new LayoutInfo(ImportLayout.Unrecognised, null, "", null, "This mod does not contain any files.");

            var direct = TryShapesAt(files, "", null);

            if (direct is not null)
                return direct;

            if (files.All(f => f.Contains('/')))
            {
                var wrappers = files.Select(f => f[..f.IndexOf('/')]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                if (wrappers.Count == 1)
                {
                    var wrapped = TryShapesAt(files, wrappers[0] + "/", wrappers[0]);

                    if (wrapped is not null)
                        return wrapped;
                }
            }

            bool sawManifest = files.Any(f => f.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) && f.Count(c => c == '/') <= 1);

            string message = sawManifest
                ? "This looks like a ZeroStrap package, but it has no files folder containing content, ExtraContent, PlatformContent or shaders."
                : UnrecognisedMessage;

            return new LayoutInfo(ImportLayout.Unrecognised, null, "", null, message);
        }

        private static LayoutInfo? TryShapesAt(List<string> files, string prefix, string? wrapper)
        {
            bool Has(string start) => files.Any(f => f.StartsWith(prefix + start, StringComparison.OrdinalIgnoreCase));

            string manifest = prefix + "manifest.json";
            bool hasManifest = files.Any(f => f.Equals(manifest, StringComparison.OrdinalIgnoreCase));

            if (hasManifest && ModPathRules.AllowedRoots.Any(r => Has("files/" + r + "/")))
                return new LayoutInfo(ImportLayout.ZeroStrapPackage, wrapper, prefix + "files/", manifest, null);

            if (ModPathRules.AllowedRoots.Any(r => Has(r + "/")))
                return new LayoutInfo(wrapper is null ? ImportLayout.DirectRoots : ImportLayout.WrappedRoots, wrapper, prefix, null, null);

            if (ModPathRules.AllowedRoots.Any(r => Has("Modifications/" + r + "/")))
                return new LayoutInfo(ImportLayout.ModificationsFolder, wrapper, prefix + "Modifications/", null, null);

            return null;
        }

        private static Func<string, bool, ArchivePathDecision> BuildMapper(LayoutInfo layout)
        {
            string wrapperPrefix = layout.Wrapper is null ? "" : layout.Wrapper + "/";
            string manifest = layout.Layout == ImportLayout.ZeroStrapPackage ? wrapperPrefix + "manifest.json" : "";
            string thumbnail = wrapperPrefix + "thumbnail.png";

            return (path, isDirectory) =>
            {
                if (isDirectory)
                    return ArchivePathDecision.Skip();

                if (IsJunkName(path))
                    return ArchivePathDecision.Ignore();

                string directory = DirectoryOf(path);

                if (IsDocumentName(path[(path.LastIndexOf('/') + 1)..])
                    && (directory.Length == 0 || (layout.Wrapper is not null && directory.Equals(layout.Wrapper, StringComparison.OrdinalIgnoreCase))))
                    return ArchivePathDecision.Ignore();

                if (manifest.Length > 0 && (path.Equals(manifest, StringComparison.OrdinalIgnoreCase) || path.Equals(thumbnail, StringComparison.OrdinalIgnoreCase)))
                    return ArchivePathDecision.Skip();

                if (!path.StartsWith(layout.BasePrefix, StringComparison.OrdinalIgnoreCase))
                    return ArchivePathDecision.Reject("This file is outside the folders ZeroStrap imports from.");

                string relative = path[layout.BasePrefix.Length..];

                if (!relative.Contains('/'))
                    return ArchivePathDecision.Reject("Loose files at the top level are not allowed. Mod files must be inside content, ExtraContent, PlatformContent or shaders.");

                if (!ModPathRules.TryNormalize(relative, out string normalised, out string? error))
                    return ArchivePathDecision.Reject(Sentence(error));

                string? extensionProblem = CheckExtension(normalised);

                if (extensionProblem is not null)
                    return ArchivePathDecision.Reject(extensionProblem);

                return ArchivePathDecision.Accept(normalised.Replace('\\', '/'));
            };
        }

        /// <summary>The mod-only file type allowlist, applied to a normalised (backslash) version-root-relative path.</summary>
        internal static string? CheckExtension(string normalisedPath)
        {
            string extension = Path.GetExtension(normalisedPath).ToLowerInvariant();

            if (AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                return null;

            if (extension is ".rbxl" or ".rbxm")
            {
                bool inPlaces = normalisedPath.StartsWith(@"ExtraContent\places\", StringComparison.OrdinalIgnoreCase);
                bool inModels = normalisedPath.StartsWith(@"ExtraContent\models\", StringComparison.OrdinalIgnoreCase);

                return inPlaces || inModels
                    ? null
                    : $"{extension} files are only allowed inside ExtraContent\\places or ExtraContent\\models.";
            }

            if (extension == ".json")
            {
                return normalisedPath.StartsWith(@"content\fonts\families\", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : ".json files are only allowed inside content\\fonts\\families.";
            }

            return extension.Length == 0
                ? "Files without a file type are not allowed."
                : $"The file type '{extension}' is not allowed in mods.";
        }

        // ================================================================ manifest

        private sealed record ManifestInfo(string? Name, string? Author, string? Description, string? Version, ModCategory? Category);

        private static ManifestInfo? ParseManifest(string text, List<string> warnings, out string? fatal)
        {
            fatal = null;

            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });

                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    fatal = "manifest.json is not a valid ZeroStrap manifest.";
                    return null;
                }

                string? Read(string field, int max)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (!prop.Name.Equals(field, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (prop.Value.ValueKind == JsonValueKind.Null)
                            return null;

                        if (prop.Value.ValueKind != JsonValueKind.String)
                        {
                            warnings.Add($"The manifest's {field} was ignored because it is not text.");
                            return null;
                        }

                        string cleaned = StripControl(prop.Value.GetString() ?? "");

                        if (cleaned.Length > max)
                        {
                            warnings.Add($"The manifest's {field} was shortened to {max} characters.");
                            cleaned = cleaned[..max].TrimEnd();
                        }

                        return cleaned.Length == 0 ? null : cleaned;
                    }

                    return null;
                }

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.Number
                        && prop.Value.TryGetInt32(out int schema) && schema > 1)
                    {
                        fatal = "This package was made by a newer version of ZeroStrap. Update ZeroStrap to import it.";
                        return null;
                    }
                }

                string? name = Read("name", MaxNameLength);

                if (name is null)
                    warnings.Add("The manifest has no name, so the file name is used instead.");

                ModCategory? category = null;
                string? categoryText = Read("category", 32);

                if (categoryText is not null)
                {
                    if (Enum.TryParse(categoryText, ignoreCase: true, out ModCategory parsed) && Enum.IsDefined(parsed) && !int.TryParse(categoryText, out _))
                        category = parsed;
                    else
                        warnings.Add($"The manifest's category '{categoryText}' is not known, so it was guessed from the files.");
                }

                // the manifest's id (and anything else) is deliberately never read: ZeroStrap picks the id itself
                return new ManifestInfo(name, Read("author", MaxNameLength), Read("description", 500), Read("version", 32), category);
            }
            catch (JsonException)
            {
                fatal = "manifest.json could not be read because it is not valid JSON.";
                return null;
            }
        }

        // ================================================================ install

        /// <summary>
        /// Imports the accepted files of <paramref name="preview"/> as a new package, enables it at the lowest priority and
        /// rebuilds the overlay. A new conflict never takes a file away from its current owner. Throws
        /// <see cref="ModStudioException"/> with a readable message on any problem; nothing is left behind on failure.
        /// </summary>
        public ModImportResult Install(ImportPreview preview, string name, ModCategory category, string? author = null, string? description = null, string? version = null)
        {
            if (preview.CannotInstallReason is not null)
                throw new ModStudioException(preview.CannotInstallReason);

            string cleanName = StripControl(name ?? "").Trim();

            if (cleanName.Length == 0)
                throw new ModStudioException("Please enter a name for the mod.");

            if (cleanName.Length > MaxNameLength)
                throw new ModStudioException($"The name is too long. Use {MaxNameLength} characters or fewer.");

            if (!Enum.IsDefined(category))
                throw new ModStudioException("Please choose a category for the mod.");

            string stagingRoot = Path.Combine(_studio.StudioDirectory, "Staging");
            string staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            ModPackage? package = null;

            try
            {
                Directory.CreateDirectory(staging);

                if (preview.SourceKind == ImportSourceKind.Zip)
                    StageZip(preview, staging);
                else
                    StageFolder(preview, staging);

                VerifyStaging(preview, staging);

                var draft = new ModManifest
                {
                    // the id is always generated by the store from the name
                    Id = "",
                    Name = cleanName,
                    Author = Trim(author ?? preview.Author, MaxNameLength),
                    Description = Trim(description ?? preview.Description, 500),
                    Version = Trim(version ?? preview.Version, 32),
                    Category = category,
                    Source = ModSource.Imported,
                    InstalledAt = DateTimeOffset.UtcNow,
                    RobloxVersionAtInstall = _studio.CurrentVersionName()
                };

                package = _studio.Packages.CreateFromStaging(draft, staging, moveFiles: false);

                var rebuild = _studio.EnableImported(package.Id);
                return new ModImportResult(_studio.Packages.Get(package.Id) ?? package, rebuild);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                RemoveQuietly(package);
                throw new ModStudioException("The mod could not be imported: " + ex.Message, ex);
            }
            catch
            {
                RemoveQuietly(package);
                throw;
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                try { if (Directory.Exists(stagingRoot) && !Directory.EnumerateFileSystemEntries(stagingRoot).Any()) Directory.Delete(stagingRoot); } catch { }
            }
        }

        private void RemoveQuietly(ModPackage? package)
        {
            if (package is null)
                return;

            try { _studio.Packages.Delete(package.Id); } catch { }
        }

        private static void StageZip(ImportPreview preview, string staging)
        {
            if (!File.Exists(preview.SourcePath) || preview.Limits is null)
                throw new ModStudioException("The mod file is no longer available. Please choose it again.");

            var result = SafeArchiveExtractor.Extract(preview.SourcePath, staging, preview.Limits);

            if (result.Plan.FatalReason is not null)
                throw new ModStudioException(result.Plan.FatalReason);

            if (result.FatalReason is not null)
                throw new ModStudioException("The archive is larger than ZeroStrap allows once unpacked.");

            var failed = result.Failed.FirstOrDefault();

            if (failed is not null)
                throw new ModStudioException($"'{failed.FullName}' could not be unpacked ({failed.Reason}). Nothing was imported.");
        }

        private static void StageFolder(ImportPreview preview, string staging)
        {
            var limits = preview.Limits ?? DefaultLimits();
            long remaining = limits.MaxTotalBytes;

            foreach (var (source, mapped) in preview.FolderFiles)
            {
                var info = new FileInfo(source);

                if (!info.Exists)
                    throw new ModStudioException("The folder changed while it was being imported. Please try again.");

                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new ModStudioException($"'{mapped}' is a link, which ZeroStrap does not follow. Nothing was imported.");

                string destination = ModPathRules.CombineConfined(staging, mapped.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                long budget = Math.Min(limits.MaxEntryBytes, remaining);
                long copied = 0;
                var buffer = new byte[81920];

                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    int read;

                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        copied += read;

                        if (copied > budget)
                            throw new ModStudioException($"'{mapped}' grew beyond the allowed size while it was being imported. Nothing was imported.");

                        output.Write(buffer, 0, read);
                    }
                }

                remaining -= copied;
            }
        }

        private static void VerifyStaging(ImportPreview preview, string staging)
        {
            var expected = ModPathRules.NewPathSet();

            foreach (var file in preview.AcceptedFiles)
                expected.Add(file.Path);

            var actual = ModPathRules.NewPathSet();

            foreach (string file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                actual.Add(Path.GetRelativePath(staging, file));

            if (!actual.SetEquals(expected))
                throw new ModStudioException("The mod changed after it was checked. Please choose it again.");
        }

        // ================================================================ helpers

        private static string? ReadCapped(Stream input)
        {
            using var reader = new StreamReader(input, Encoding.UTF8, true);
            var buffer = new char[MaxManifestBytes + 1];
            int read = reader.ReadBlock(buffer, 0, buffer.Length);

            if (read > MaxManifestBytes)
                throw new InvalidDataException("manifest.json is too large.");

            return new string(buffer, 0, read);
        }

        private static string StripControl(string text) => new(text.Where(c => !char.IsControl(c)).ToArray());

        private static string? Trim(string? text, int max)
        {
            if (text is null)
                return null;

            string cleaned = StripControl(text).Trim();

            if (cleaned.Length > max)
                cleaned = cleaned[..max].TrimEnd();

            return cleaned.Length == 0 ? null : cleaned;
        }

        private static string Sentence(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return "Not allowed.";

            return char.ToUpperInvariant(text[0]) + text[1..] + ".";
        }

        internal static string TidyName(string raw)
        {
            string name = StripControl(raw ?? "").Replace('_', ' ');
            name = Regex.Replace(name, @"\s+", " ").Trim(' ', '.', '-');

            if (name.Length > MaxNameLength)
                name = name[..MaxNameLength].TrimEnd();

            return name.Length == 0 ? "Imported mod" : name;
        }

        internal static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30)
                return (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GB";

            if (bytes >= 1L << 20)
                return (bytes / (double)(1L << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";

            return Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
        }
    }
}
