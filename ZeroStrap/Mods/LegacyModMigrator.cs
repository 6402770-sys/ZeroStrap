using System.IO.Compression;

namespace ZeroStrap.Mods
{
    public enum MigrationStatus
    {
        /// <summary>studio.json already says Migrated.</summary>
        AlreadyDone,

        /// <summary>Nothing worth packaging was found; Migrated was set.</summary>
        NoCandidates,

        /// <summary>A backup and a "My existing mods" package were created.</summary>
        Migrated,

        /// <summary>Something failed before any change was made; the next launch tries again.</summary>
        Aborted
    }

    public sealed class MigrationResult
    {
        public MigrationStatus Status { get; set; }
        public string? Reason { get; set; }
        public string? BackupPath { get; set; }
        public string? PackageId { get; set; }

        /// <summary>Files copied into the legacy package.</summary>
        public List<string> PackagedFiles { get; } = new();

        /// <summary>Files that belong to a built-in preset and stay with it.</summary>
        public List<string> PresetFiles { get; } = new();

        /// <summary>Files the studio cannot manage (outside the allowed folders, blocked type): left exactly where they are.</summary>
        public List<string> UnmanagedFiles { get; } = new();

        /// <summary>Files skipped on purpose (ClientSettings, README, locks, already managed).</summary>
        public List<string> SkippedFiles { get; } = new();
    }

    /// <summary>
    /// One-time import of whatever is already in Modifications. Files are COPIED, never moved, and nothing is changed
    /// unless a verified backup zip exists.
    /// </summary>
    public sealed class LegacyModMigrator
    {
        public const string LegacyPackageName = "My existing mods";

        private readonly string _modificationsRoot;
        private readonly string _backupsDirectory;
        private readonly ModPackageStore _packages;
        private readonly BuiltinModCatalog _catalog;
        private readonly ModStudioStateStore _stateStore;
        private readonly OverlayIndexStore _indexStore;
        private readonly Action<string> _log;

        /// <summary>Test seam: throwing from here simulates a backup failure.</summary>
        internal Action<string>? BackupFault { get; set; }

        public LegacyModMigrator(
            string modificationsRoot,
            string backupsDirectory,
            ModPackageStore packages,
            BuiltinModCatalog catalog,
            ModStudioStateStore stateStore,
            OverlayIndexStore indexStore,
            Action<string>? log = null)
        {
            _modificationsRoot = modificationsRoot;
            _backupsDirectory = backupsDirectory;
            _packages = packages;
            _catalog = catalog;
            _stateStore = stateStore;
            _indexStore = indexStore;
            _log = log ?? (_ => { });
        }

        /// <summary>Classifies without changing anything (also used by the dry run).</summary>
        public MigrationResult Classify(OverlayIndex index, out List<(string Source, string Relative)> candidates)
        {
            var result = new MigrationResult { Status = MigrationStatus.NoCandidates };
            candidates = new List<(string, string)>();

            if (!Directory.Exists(_modificationsRoot))
                return result;

            foreach (string file in Directory.EnumerateFiles(_modificationsRoot, "*", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string rel = Path.GetRelativePath(_modificationsRoot, file);

                if (ModPathRules.IsReservedModificationsPath(rel))
                {
                    result.SkippedFiles.Add(rel);
                    continue;
                }

                if (_catalog.IsPresetOwned(rel))
                {
                    result.PresetFiles.Add(rel);
                    continue;
                }

                if (!ModPathRules.TryNormalize(rel, out string normalized, out _))
                {
                    result.UnmanagedFiles.Add(rel);
                    continue;
                }

                // already written by the studio (recovering from an interrupted migration)
                if (index.Files.TryGetValue(normalized, out var owned)
                    && string.Equals(owned.Sha256, SafeSha(file), StringComparison.OrdinalIgnoreCase))
                {
                    result.SkippedFiles.Add(rel);
                    continue;
                }

                candidates.Add((file, rel));
            }

            return result;
        }

        private static string? SafeSha(string file)
        {
            try { return ModFileUtil.Sha256OfFile(file); }
            catch { return null; }
        }

        public MigrationResult Run(ModStudioState state, OverlayIndex index)
        {
            if (state.Migrated)
                return new MigrationResult { Status = MigrationStatus.AlreadyDone };

            MigrationResult result;
            List<(string Source, string Relative)> candidates;

            try
            {
                result = Classify(index, out candidates);
            }
            catch (Exception ex)
            {
                _log($"Migration aborted while scanning Modifications: {ex.Message}");
                return new MigrationResult { Status = MigrationStatus.Aborted, Reason = ex.Message };
            }

            if (candidates.Count == 0)
            {
                // self-heal: a package from an interrupted earlier migration that never got enabled
                foreach (var legacy in _packages.List().Where(p => p.Manifest.Source == ModSource.Legacy))
                {
                    if (!state.Priority.Contains(legacy.Id))
                    {
                        state.Priority.Add(legacy.Id);

                        if (!state.Enabled.Contains(legacy.Id))
                            state.Enabled.Add(legacy.Id);
                    }
                }

                state.Migrated = true;
                _stateStore.Save(state);
                result.Status = MigrationStatus.NoCandidates;
                _log("Migration: nothing to import");
                return result;
            }

            // 1. backup, verified. Any failure here leaves everything untouched.
            string? backup = null;

            try
            {
                backup = CreateBackup();
                result.BackupPath = backup;
            }
            catch (Exception ex)
            {
                _log($"Migration aborted, backup failed: {ex.Message}");
                result.Status = MigrationStatus.Aborted;
                result.Reason = "backup failed: " + ex.Message;
                result.BackupPath = null;
                return result;
            }

            // 2. copy the loose mods into a package (the originals stay where they are)
            ModPackage package;
            ModPackage? created = null;
            var diskHashes = ModPathRules.NewPathMap<string>();

            try
            {
                foreach (var (source, rel) in candidates)
                    diskHashes[ModPathRules.Normalize(rel)] = ModFileUtil.Sha256OfFile(source);

                package = created = _packages.CreateFromFiles(new ModManifest
                {
                    Id = ModPackageStore.Slugify(LegacyPackageName),
                    Name = LegacyPackageName,
                    Description = "Files that were already in your Modifications folder before Mods Studio.",
                    Source = ModSource.Legacy,
                    InstalledAt = DateTimeOffset.UtcNow
                }, candidates, moveFiles: false);

                // verify content, not just names: every package file must hash like the file it was copied from
                foreach (var file in package.Files)
                {
                    if (!diskHashes.TryGetValue(file.RelativePath, out string? expected)
                        || !string.Equals(_packages.GetSha256(package, file.RelativePath), expected, StringComparison.OrdinalIgnoreCase))
                        throw new ModStudioException($"verification failed for {file.RelativePath}");
                }

                if (package.Files.Count != candidates.Count)
                    throw new ModStudioException("package file count mismatch");
            }
            catch (Exception ex)
            {
                _log($"Migration aborted while building the package: {ex.Message}");
                TryDeletePackage(created);
                result.Status = MigrationStatus.Aborted;
                result.Reason = ex.Message;
                return result;
            }

            // 3. record ownership (current hashes) so the first overlay build changes nothing, then flip the flag
            try
            {
                foreach (var file in package.Files)
                    index.Files[file.RelativePath] = new OverlayIndexEntry { Owner = package.Id, Sha256 = diskHashes[file.RelativePath] };

                _indexStore.Save(index);

                if (!state.Priority.Contains(package.Id))
                    state.Priority.Add(package.Id);

                if (!state.Enabled.Contains(package.Id))
                    state.Enabled.Add(package.Id);

                state.Migrated = true;
                state.MigrationBackup = backup;
                _stateStore.Save(state);
            }
            catch (Exception ex)
            {
                // package and backup are intact and Modifications was never touched; a retry reconciles
                _log($"Migration could not save its state: {ex.Message}");
                result.Status = MigrationStatus.Aborted;
                result.Reason = ex.Message;
                return result;
            }

            result.Status = MigrationStatus.Migrated;
            result.PackageId = package.Id;
            result.PackagedFiles.AddRange(package.Files.Select(f => f.RelativePath));
            _log($"Migration: copied {package.Files.Count} file(s) into '{package.Id}', backup at {backup}");
            return result;
        }

        private void TryDeletePackage(ModPackage? created)
        {
            if (created is null)
                return;

            try
            {
                // only the package this very run created; the originals are still in Modifications
                _packages.Delete(created.Id);
            }
            catch (Exception ex)
            {
                _log($"Could not clean up the partial package: {ex.Message}");
            }
        }

        /// <summary>
        /// Zips everything in Modifications except ClientSettings\ and *.lock files. Written under a temp name,
        /// verified (entry count and sizes), then renamed. Throws on any problem.
        /// </summary>
        private string CreateBackup()
        {
            Directory.CreateDirectory(_backupsDirectory);

            string finalPath = Path.Combine(_backupsDirectory, "pre-migration-" + ModFileUtil.NewStamp() + ".zip");
            string partial = finalPath + ".partial";

            var files = Directory.EnumerateFiles(_modificationsRoot, "*", SearchOption.AllDirectories)
                .Select(f => (Full: f, Rel: Path.GetRelativePath(_modificationsRoot, f)))
                .Where(f => !f.Rel.StartsWith("ClientSettings\\", StringComparison.OrdinalIgnoreCase)
                    && !f.Rel.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                .ToList();

            try
            {
                using (var zipStream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    foreach (var (full, rel) in files)
                    {
                        BackupFault?.Invoke(rel);

                        var entry = zip.CreateEntry(rel.Replace('\\', '/'), CompressionLevel.Optimal);

                        using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var output = entry.Open();
                        input.CopyTo(output);
                    }
                }

                using (var zip = ZipFile.OpenRead(partial))
                {
                    var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();

                    if (entries.Count != files.Count)
                        throw new ModStudioException($"backup verification failed: {entries.Count} entries for {files.Count} files");

                    foreach (var (full, rel) in files)
                    {
                        var entry = zip.GetEntry(rel.Replace('\\', '/'))
                            ?? throw new ModStudioException($"backup verification failed: missing {rel}");

                        if (entry.Length != new FileInfo(full).Length)
                            throw new ModStudioException($"backup verification failed: size mismatch for {rel}");
                    }
                }

                File.Move(partial, finalPath);
                return finalPath;
            }
            catch
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                throw;
            }
        }
    }
}
