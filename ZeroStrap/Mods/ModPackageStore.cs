namespace ZeroStrap.Mods
{
    /// <summary>
    /// Packages live under Packages\&lt;id&gt;\ (manifest.json, optional thumbnail, files\...).
    /// Listing never throws for a broken package: it is skipped and logged.
    /// </summary>
    public sealed class ModPackageStore
    {
        public const string ManifestFileName = "manifest.json";
        public const string FilesDirectoryName = "files";

        private static readonly Regex SlugPattern = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

        private readonly string _packagesDir;
        private readonly Action<string>? _log;
        private readonly object _lock = new();
        private readonly Dictionary<string, (long Size, long MTimeTicks, string Sha)> _hashCache = new(StringComparer.OrdinalIgnoreCase);

        public string PackagesDirectory => _packagesDir;

        public ModPackageStore(string packagesDirectory, Action<string>? log = null)
        {
            _packagesDir = packagesDirectory;
            _log = log;
        }

        private void Log(string message)
        {
            try { _log?.Invoke(message); } catch { }
        }

        public static bool IsValidId(string? id) => id is not null && SlugPattern.IsMatch(id);

        public static string Slugify(string text)
        {
            var sb = new StringBuilder();
            bool lastDash = true;

            foreach (char c in text.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                    lastDash = false;
                }
                else if (!lastDash)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }

            string slug = sb.ToString().Trim('-');

            if (slug.Length > 48)
                slug = slug[..48].Trim('-');

            return slug.Length == 0 ? "mod" : slug;
        }

        // ---------------------------------------------------------------- read

        public IReadOnlyList<ModPackage> List()
        {
            var result = new List<ModPackage>();

            if (!Directory.Exists(_packagesDir))
                return result;

            lock (_lock)
            {
                foreach (string dir in Directory.EnumerateDirectories(_packagesDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    if (Path.GetFileName(dir).StartsWith('.'))
                        continue; // staging leftovers

                    var package = LoadDirectory(dir);

                    if (package is not null)
                        result.Add(package);
                }
            }

            return result;
        }

        public ModPackage? Get(string id)
        {
            if (!IsValidId(id))
                return null;

            string dir = Path.Combine(_packagesDir, id);

            if (!Directory.Exists(dir))
                return null;

            lock (_lock)
            {
                return LoadDirectory(dir);
            }
        }

        private ModPackage? LoadDirectory(string dir)
        {
            string name = Path.GetFileName(dir);
            string manifestPath = Path.Combine(dir, ManifestFileName);

            try
            {
                if (!File.Exists(manifestPath))
                {
                    Log($"Skipping package folder '{name}': no manifest");
                    return null;
                }

                var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath), ModFileUtil.JsonOptions);

                if (manifest is null || !IsValidId(manifest.Id) || !manifest.Id.Equals(name, StringComparison.Ordinal))
                {
                    Log($"Skipping package folder '{name}': manifest id does not match");
                    return null;
                }

                manifest.Category ??= ModCategory.Other;

                var files = new List<ModPackageFile>();
                string filesDir = Path.Combine(dir, FilesDirectoryName);

                if (Directory.Exists(filesDir))
                {
                    var seen = ModPathRules.NewPathSet();

                    foreach (string file in Directory.EnumerateFiles(filesDir, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(filesDir, file);

                        if (!ModPathRules.TryNormalize(rel, out string normalized, out string? error))
                        {
                            Log($"Package '{name}': ignoring file '{rel}' ({error})");
                            continue;
                        }

                        if (!seen.Add(normalized))
                        {
                            Log($"Package '{name}': ignoring duplicate '{rel}'");
                            continue;
                        }

                        files.Add(new ModPackageFile(normalized, new FileInfo(file).Length));
                    }
                }

                files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));

                return new ModPackage { Manifest = manifest, Directory = dir, Files = files };
            }
            catch (Exception ex)
            {
                Log($"Skipping corrupt package '{name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>SHA-256 of a package file, cached by size and modification time.</summary>
        public string GetSha256(ModPackage package, string relativePath)
        {
            string full = package.FullPathOf(relativePath);
            var info = new FileInfo(full);

            if (!info.Exists)
                throw new FileNotFoundException("Package file missing", full);

            lock (_lock)
            {
                if (_hashCache.TryGetValue(full, out var cached)
                    && cached.Size == info.Length
                    && cached.MTimeTicks == info.LastWriteTimeUtc.Ticks)
                    return cached.Sha;
            }

            string sha = ModFileUtil.Sha256OfFile(full);

            lock (_lock)
            {
                _hashCache[full] = (info.Length, info.LastWriteTimeUtc.Ticks, sha);
            }

            return sha;
        }

        public Dictionary<string, string> GetAllSha256(ModPackage package)
        {
            var map = ModPathRules.NewPathMap<string>();

            foreach (var file in package.Files)
                map[file.RelativePath] = GetSha256(package, file.RelativePath);

            return map;
        }

        // ---------------------------------------------------------------- write

        /// <summary>
        /// Builds a package from the files under <paramref name="stagingDirectory"/> (version-root-relative layout).
        /// The package is assembled in a temp folder and renamed into place, so a failure leaves nothing half-made.
        /// With <paramref name="moveFiles"/> the staging files are deleted only after the package is complete;
        /// otherwise they are left untouched (copy).
        /// </summary>
        public ModPackage CreateFromStaging(ModManifest draft, string stagingDirectory, bool moveFiles = true, string? thumbnailSource = null)
        {
            if (!Directory.Exists(stagingDirectory))
                throw new ModStudioException("Staging folder does not exist.");

            var files = Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
                .Select(f => (Source: f, Relative: Path.GetRelativePath(stagingDirectory, f)))
                .ToList();

            return CreateFromFiles(draft, files, moveFiles, thumbnailSource);
        }

        /// <summary>
        /// Same as <see cref="CreateFromStaging"/> for an explicit list of (source file, version-root-relative path).
        /// </summary>
        public ModPackage CreateFromFiles(ModManifest draft, IReadOnlyList<(string Source, string Relative)> sourceFiles, bool moveFiles = false, string? thumbnailSource = null)
        {
            var plan = new List<(string Source, string Relative)>();
            var rejected = new List<string>();
            var seen = ModPathRules.NewPathSet();

            foreach (var (file, rel) in sourceFiles)
            {
                if (!ModPathRules.TryNormalize(rel, out string normalized, out string? error))
                {
                    rejected.Add($"{rel} ({error})");
                    continue;
                }

                if (!seen.Add(normalized))
                {
                    rejected.Add($"{rel} (duplicate path)");
                    continue;
                }

                plan.Add((file, normalized));
            }

            if (rejected.Count > 0)
                throw new ModStudioException("The mod contains files that are not allowed: " + string.Join("; ", rejected.Take(5)));

            if (plan.Count == 0)
                throw new ModStudioException("The mod contains no files.");

            Directory.CreateDirectory(_packagesDir);

            string baseId = IsValidId(draft.Id) ? draft.Id : Slugify(string.IsNullOrWhiteSpace(draft.Name) ? draft.Id : draft.Name);
            string id = baseId;

            for (int n = 2; Directory.Exists(Path.Combine(_packagesDir, id)); n++)
            {
                string suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
                id = (baseId.Length + suffix.Length > 64 ? baseId[..(64 - suffix.Length)] : baseId) + suffix;
            }

            var manifest = new ModManifest
            {
                SchemaVersion = 1,
                Id = id,
                Name = string.IsNullOrWhiteSpace(draft.Name) ? id : draft.Name.Trim(),
                Author = draft.Author,
                Description = draft.Description,
                Category = draft.Category ?? GuessCategory(plan.Select(p => p.Relative)),
                Version = draft.Version,
                Thumbnail = null,
                Source = draft.Source,
                InstalledAt = draft.InstalledAt,
                RobloxVersionAtInstall = draft.RobloxVersionAtInstall
            };

            string temp = Path.Combine(_packagesDir, ".tmp-" + Guid.NewGuid().ToString("N"));

            try
            {
                string tempFiles = Path.Combine(temp, FilesDirectoryName);
                Directory.CreateDirectory(tempFiles);

                foreach (var (source, relative) in plan)
                {
                    string destination = Path.Combine(tempFiles, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, false);
                }

                if (thumbnailSource is not null && File.Exists(thumbnailSource))
                {
                    File.Copy(thumbnailSource, Path.Combine(temp, "thumbnail.png"), true);
                    manifest.Thumbnail = "thumbnail.png";
                }

                File.WriteAllText(Path.Combine(temp, ManifestFileName), JsonSerializer.Serialize(manifest, ModFileUtil.JsonOptions), new UTF8Encoding(false));

                // verify the copy before it becomes visible (and before any staging file is deleted)
                foreach (var (source, relative) in plan)
                {
                    var a = new FileInfo(source);
                    var b = new FileInfo(Path.Combine(tempFiles, relative));

                    if (!b.Exists || a.Length != b.Length)
                        throw new ModStudioException($"Copy verification failed for {relative}");
                }

                Directory.Move(temp, Path.Combine(_packagesDir, id));
            }
            catch
            {
                TryDeleteDirectory(temp);
                throw;
            }

            if (moveFiles)
            {
                foreach (var (source, _) in plan)
                {
                    try
                    {
                        ModFileUtil.ClearReadOnly(source);
                        File.Delete(source);
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not remove staged file '{source}': {ex.Message}");
                    }
                }
            }

            return Get(id) ?? throw new ModStudioException("Package was created but could not be read back.");
        }

        public void Delete(string id)
        {
            if (!IsValidId(id))
                throw new ModStudioException("Invalid package id.");

            string dir = ModPathRules.CombineConfined(_packagesDir, id);

            if (!Directory.Exists(dir))
                return;

            lock (_lock)
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    ModFileUtil.ClearReadOnly(f);

                Directory.Delete(dir, true);

                foreach (string key in _hashCache.Keys.Where(k => k.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
                    _hashCache.Remove(key);
            }
        }

        private static void TryDeleteDirectory(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        // ---------------------------------------------------------------- category

        public static ModCategory GuessCategory(IEnumerable<string> relativePaths)
        {
            var counts = new Dictionary<ModCategory, int>();

            foreach (string rel in relativePaths)
            {
                string p = rel.Replace('/', '\\');
                ModCategory c;

                if (p.StartsWith(@"content\textures\Cursors\", StringComparison.OrdinalIgnoreCase))
                    c = ModCategory.Cursors;
                else if (p.StartsWith(@"content\sounds\", StringComparison.OrdinalIgnoreCase))
                    c = ModCategory.Sounds;
                else if (p.StartsWith(@"content\sky\", StringComparison.OrdinalIgnoreCase))
                    c = ModCategory.Sky;
                else if (p.StartsWith(@"content\fonts\", StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith(@"PlatformContent\pc\fonts\", StringComparison.OrdinalIgnoreCase))
                    c = ModCategory.Fonts;
                else if (p.StartsWith(@"ExtraContent\places\", StringComparison.OrdinalIgnoreCase))
                    c = ModCategory.Places;
                else if (p.StartsWith(@"content\textures\", StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith(@"PlatformContent\pc\textures\", StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith(@"ExtraContent\textures\", StringComparison.OrdinalIgnoreCase))
                    c = ModCategory.Textures;
                else
                    c = ModCategory.Other;

                counts[c] = counts.GetValueOrDefault(c) + 1;
            }

            if (counts.Count == 0)
                return ModCategory.Other;

            return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => (int)kv.Key).First().Key;
        }
    }
}
