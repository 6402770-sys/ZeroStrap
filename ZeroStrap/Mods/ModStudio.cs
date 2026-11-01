namespace ZeroStrap.Mods
{
    public sealed class ModPackageStatus
    {
        public ModPackage Package { get; init; } = null!;
        public bool Enabled { get; init; }

        /// <summary>0 is the highest priority.</summary>
        public int PriorityIndex { get; init; }

        /// <summary>Number of the package's paths that exist in the current Roblox version folder; null when unknown.</summary>
        public int? Compatibility { get; init; }
    }

    public sealed class ModStudioSnapshot
    {
        public bool Migrated { get; init; }
        public IReadOnlyList<ModPackageStatus> Packages { get; init; } = Array.Empty<ModPackageStatus>();
        public IReadOnlyList<ModConflict> Conflicts { get; init; } = Array.Empty<ModConflict>();

        /// <summary>Presets that must be switched off before a chosen package can take their files.</summary>
        public IReadOnlyList<ResolverAction> Actions { get; init; } = Array.Empty<ResolverAction>();

        public IReadOnlyList<ActiveBuiltinPreset> ActivePresets { get; init; } = Array.Empty<ActiveBuiltinPreset>();

        /// <summary>Paths a package wants but cannot have yet (a file the studio did not write is in the way).</summary>
        public IReadOnlyList<OverlayBlock> Blocked { get; init; } = Array.Empty<OverlayBlock>();
    }

    public sealed class ModRebuildResult
    {
        public ModResolution Resolution { get; init; } = new();
        public OverlayPlan Plan { get; init; } = new();
        public OverlayResult Result { get; init; } = new();
    }

    /// <summary>
    /// Facade over the Mods Studio engine. The effective overlay is the Modifications folder itself, so the
    /// launcher's existing apply step is unchanged; packages live next to it under Base\Mods.
    /// </summary>
    public sealed class ModStudio
    {
        private static ModStudio? _default;

        /// <summary>The studio over Paths.Base\Mods and Paths.Modifications. Only valid after Paths.Initialize.</summary>
        public static ModStudio Default => _default ??= CreateDefault();

        private static ModStudio CreateDefault()
        {
            void Log(string message) => App.Logger.WriteLine("ModStudio", message);

            return new ModStudio(
                Path.Combine(Paths.Base, "Mods"),
                Paths.Modifications,
                BuiltinModCatalog.CreateDefault(Log),
                CurrentVersionDirectory,
                Log);
        }

        /// <summary>Folder of the installed Roblox version (player first, then Studio); null when none is installed.</summary>
        private static string? CurrentVersionDirectory()
        {
            if (App.IsPlayerInstalled)
                return new AppData.RobloxPlayerData().Directory;

            if (App.IsStudioInstalled)
                return new AppData.RobloxStudioData().Directory;

            return null;
        }

        private readonly string _studioDir;
        private readonly string _modificationsRoot;
        private readonly BuiltinModCatalog _catalog;
        private readonly Func<string?> _versionDirectory;
        private readonly Action<string> _log;

        private readonly ModPackageStore _packages;
        private readonly ModStudioStateStore _stateStore;
        private readonly OverlayIndexStore _indexStore;
        private readonly ModOverlayBuilder _builder;
        private readonly LegacyModMigrator _migrator;

        public ModPackageStore Packages => _packages;
        public BuiltinModCatalog Catalog => _catalog;
        public string StudioDirectory => _studioDir;

        public ModStudio(string studioDirectory, string modificationsRoot, BuiltinModCatalog catalog, Func<string?> versionDirectory, Action<string>? log = null)
        {
            _studioDir = studioDirectory;
            _modificationsRoot = modificationsRoot;
            _catalog = catalog;
            _versionDirectory = versionDirectory;
            _log = log ?? (_ => { });

            _packages = new ModPackageStore(Path.Combine(_studioDir, "Packages"), _log);
            _stateStore = new ModStudioStateStore(Path.Combine(_studioDir, "studio.json"), _log);
            _indexStore = new OverlayIndexStore(Path.Combine(_studioDir, "overlay-index.json"), _log);
            _builder = new ModOverlayBuilder(_modificationsRoot, Path.Combine(_studioDir, "Backups", "displaced"), _log);
            _migrator = new LegacyModMigrator(_modificationsRoot, Path.Combine(_studioDir, "Backups"), _packages, _catalog, _stateStore, _indexStore, _log);
        }

        internal OverlayIndexStore IndexStore => _indexStore;
        internal ModStudioStateStore StateStore => _stateStore;
        internal LegacyModMigrator Migrator => _migrator;

        private InterProcessLock AcquireGate()
        {
            var gate = new InterProcessLock("ModStudio", TimeSpan.FromSeconds(10));

            if (!gate.IsAcquired)
            {
                gate.Dispose();
                throw new ModStudioException("Another ZeroStrap process is changing mods right now.");
            }

            return gate;
        }

        // ---------------------------------------------------------------- migration

        public MigrationResult EnsureMigrated()
        {
            using var gate = AcquireGate();

            var state = _stateStore.Load();

            if (state.Migrated)
                return new MigrationResult { Status = MigrationStatus.AlreadyDone };

            return _migrator.Run(state, _indexStore.Load());
        }

        // ---------------------------------------------------------------- state

        public ModStudioState LoadState() => _stateStore.Load();

        public ModStudioSnapshot GetState()
        {
            var state = _stateStore.Load();
            var packages = _packages.List();
            var (resolution, plan, _) = Compute(state, packages, _indexStore.Load());

            string? versionDir = SafeVersionDirectory();
            var enabled = new HashSet<string>(state.Enabled, StringComparer.Ordinal);
            var order = OrderedIds(state, packages);

            return new ModStudioSnapshot
            {
                Migrated = state.Migrated,
                Packages = packages
                    .Select(p => new ModPackageStatus
                    {
                        Package = p,
                        Enabled = enabled.Contains(p.Id),
                        PriorityIndex = order.IndexOf(p.Id),
                        Compatibility = CompatibilityFor(p, versionDir)
                    })
                    .OrderBy(s => s.PriorityIndex)
                    .ToList(),
                Conflicts = resolution.Conflicts,
                Actions = resolution.Actions,
                ActivePresets = _catalog.GetActive(),
                Blocked = plan.Blocked
            };
        }

        public void SetEnabled(string packageId, bool enabled)
        {
            using var gate = AcquireGate();

            if (_packages.Get(packageId) is null)
                throw new ModStudioException("Unknown package.");

            var state = _stateStore.Load();

            if (enabled && !state.Enabled.Contains(packageId))
                state.Enabled.Add(packageId);
            else if (!enabled)
                state.Enabled.RemoveAll(id => id == packageId);

            if (!state.Priority.Contains(packageId))
                state.Priority.Add(packageId);

            _stateStore.Save(state);
        }

        /// <summary>Sets the priority order (first = highest). Unknown ids are ignored; unlisted known ids keep their relative order after the listed ones.</summary>
        public void SetPriority(IReadOnlyList<string> orderedIds)
        {
            using var gate = AcquireGate();

            var state = _stateStore.Load();
            var known = new HashSet<string>(_packages.List().Select(p => p.Id), StringComparer.Ordinal);

            var next = new List<string>();

            foreach (string id in orderedIds)
            {
                if (known.Contains(id) && !next.Contains(id))
                    next.Add(id);
            }

            foreach (string id in state.Priority)
            {
                if (known.Contains(id) && !next.Contains(id))
                    next.Add(id);
            }

            if (next.SequenceEqual(state.Priority))
                return;

            state.Priority = next;
            state.PriorityRevision++;
            _stateStore.Save(state);
        }

        /// <summary>
        /// Records an explicit choice for a contested path (null clears it) and returns what the choice implies,
        /// including presets that would have to be switched off first.
        /// </summary>
        public ModResolution ChooseOwner(string path, string? ownerId)
        {
            using var gate = AcquireGate();

            string normalized = ModPathRules.Normalize(path);
            var state = _stateStore.Load();
            var packages = _packages.List();

            if (ownerId is null)
            {
                state.FileChoices.Remove(normalized);
            }
            else
            {
                bool isPackage = packages.Any(p => p.Id == ownerId && p.PathSet().Contains(normalized));
                bool isBuiltin = BuiltinPreset.IsBuiltinId(ownerId) && _catalog.Find(ownerId) is not null;

                if (!isPackage && !isBuiltin)
                    throw new ModStudioException("That owner does not provide this file.");

                state.FileChoices[normalized] = ownerId;
            }

            _stateStore.Save(state);

            return Compute(state, packages, _indexStore.Load()).Resolution;
        }

        /// <summary>Disables the package, removes the files it owns from Modifications (originals return on the next launch), then deletes it.</summary>
        public ModRebuildResult RemovePackage(string packageId)
        {
            using var gate = AcquireGate();

            if (_packages.Get(packageId) is null)
                throw new ModStudioException("Unknown package.");

            var state = _stateStore.Load();
            state.Enabled.RemoveAll(id => id == packageId);
            state.Priority.RemoveAll(id => id == packageId);

            foreach (string path in state.FileChoices.Where(kv => kv.Value == packageId).Select(kv => kv.Key).ToList())
                state.FileChoices.Remove(path);

            _stateStore.Save(state);

            var rebuilt = RebuildCore(allowPresetDisable: false);

            if (rebuilt.Result.Skipped)
                throw new ModStudioException("Could not remove the package right now: " + rebuilt.Result.SkipReason);

            // owned files that could not be removed stay in the index with an owner that no longer exists; the next
            // rebuild deletes them (if untouched) or moves them to the displaced backup (if edited)
            _packages.Delete(packageId);
            return rebuilt;
        }

        // ---------------------------------------------------------------- rebuild

        public ModRebuildResult Rebuild(bool allowPresetDisable = false)
        {
            using var gate = AcquireGate();
            return RebuildCore(allowPresetDisable);
        }

        /// <summary>
        /// The one call made before the launcher applies modifications: migrates once, then brings Modifications
        /// in line with the enabled packages. Callers wrap this in try/catch; a failure must not stop the launch.
        /// </summary>
        public void EnsureOverlay()
        {
            var migration = EnsureMigrated();

            if (migration.Status == MigrationStatus.Aborted)
                _log($"Legacy mod migration was skipped this time: {migration.Reason}");

            var rebuilt = Rebuild();

            var r = rebuilt.Result;

            if (r.Skipped)
                _log($"Overlay build skipped: {r.SkipReason}");
            else if (r.Copied + r.Deleted + r.Displaced + r.Errors.Count + r.Blocked.Count > 0)
                _log($"Overlay: {r.Copied} copied, {r.Deleted} removed, {r.Displaced} moved to backup, {r.Blocked.Count} blocked, {r.Errors.Count} error(s)");
        }

        private ModRebuildResult RebuildCore(bool allowPresetDisable)
        {
            var state = _stateStore.Load();
            var packages = _packages.List();
            var index = _indexStore.Load();

            var (resolution, plan, _) = Compute(state, packages, index);

            if (allowPresetDisable && resolution.Actions.Count > 0)
            {
                foreach (var action in resolution.Actions.Where(a => a.Kind == ResolverActionKind.DisablePreset))
                    _catalog.DisableNow(action.PresetId);

                (resolution, plan, _) = Compute(state, packages, index);
            }

            var result = _builder.Execute(plan, _indexStore);

            return new ModRebuildResult { Resolution = resolution, Plan = plan, Result = result };
        }

        private (ModResolution Resolution, OverlayPlan Plan, ModStudioState State) Compute(ModStudioState state, IReadOnlyList<ModPackage> packages, OverlayIndex index)
        {
            var byId = packages.ToDictionary(p => p.Id, StringComparer.Ordinal);
            var enabled = state.Enabled.Where(byId.ContainsKey).Distinct(StringComparer.Ordinal).ToList();

            var previousOwners = ModPathRules.NewPathMap<string>();

            foreach (var (path, entry) in index.Files)
                previousOwners[path] = entry.Owner;

            var resolution = ModConflictResolver.Resolve(new ResolverInput
            {
                EnabledPackages = enabled.Select(id => new ResolverPackage { Id = id, Paths = byId[id].PathSet() }).ToList(),
                Priority = state.Priority,
                FileChoices = state.FileChoices,
                ActiveBuiltinOwners = _catalog.GetActiveOwnersByPath(),
                PreviousOwners = previousOwners,
                PriorityChanged = state.PriorityRevision != index.PriorityRevision
            });

            var plan = ModOverlayBuilder.BuildPlan(
                resolution.Owners,
                (owner, path) =>
                {
                    try { return byId.TryGetValue(owner, out var p) ? _packages.GetSha256(p, path) : null; }
                    catch (Exception ex) { _log($"Package file unavailable {owner}:{path}: {ex.Message}"); return null; }
                },
                (owner, path) => byId[owner].FullPathOf(path),
                id => byId.ContainsKey(id),
                index,
                DiskSha,
                DiskExists,
                state.PriorityRevision,
                _log);

            return (resolution, plan, state);
        }

        private string? DiskSha(string relativePath)
        {
            string full = ModPathRules.CombineConfined(_modificationsRoot, relativePath);

            if (!File.Exists(full))
                return null;

            try
            {
                return ModFileUtil.Sha256OfFile(full);
            }
            catch (Exception ex)
            {
                _log($"Could not read {relativePath}: {ex.Message}");
                return "unreadable"; // never equals a real hash, so the file is treated as changed and kept safe
            }
        }

        private bool DiskExists(string relativePath) => File.Exists(ModPathRules.CombineConfined(_modificationsRoot, relativePath));

        private static List<string> OrderedIds(ModStudioState state, IReadOnlyList<ModPackage> packages)
        {
            var ids = new List<string>();

            foreach (string id in state.Priority)
            {
                if (packages.Any(p => p.Id == id) && !ids.Contains(id))
                    ids.Add(id);
            }

            foreach (var p in packages.OrderBy(p => p.Manifest.InstalledAt).ThenBy(p => p.Id, StringComparer.Ordinal))
            {
                if (!ids.Contains(p.Id))
                    ids.Add(p.Id);
            }

            return ids;
        }

        // ---------------------------------------------------------------- compatibility

        private string? SafeVersionDirectory()
        {
            try { return _versionDirectory(); }
            catch (Exception ex)
            {
                _log($"Could not determine the Roblox version folder: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// How many of the package's files have a counterpart in the current Roblox version folder, or null when
        /// there is no version folder to compare with. (A file added by an earlier mod application counts too.)
        /// </summary>
        public int? CompatibilityFor(ModPackage package) => CompatibilityFor(package, SafeVersionDirectory());

        private static int? CompatibilityFor(ModPackage package, string? versionDirectory)
        {
            if (string.IsNullOrEmpty(versionDirectory) || !Directory.Exists(versionDirectory))
                return null;

            int count = 0;

            foreach (var file in package.Files)
            {
                if (File.Exists(Path.Combine(versionDirectory, file.RelativePath)))
                    count++;
            }

            return count;
        }
    }
}
