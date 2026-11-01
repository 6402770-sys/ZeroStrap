namespace ZeroStrap.Mods
{
    public enum OverlayActionKind
    {
        /// <summary>Write the package file (new file, restore of a deleted one, or replacement of a pristine managed one).</summary>
        Copy,

        /// <summary>Delete a pristine managed file that is no longer wanted.</summary>
        Delete,

        /// <summary>Move a hand-edited managed file to the displaced backup, then write the package file.</summary>
        DisplaceThenCopy,

        /// <summary>Move a hand-edited managed file to the displaced backup (nobody wants the path any more).</summary>
        DisplaceThenDelete,

        /// <summary>Nothing to do on disk; the index entry may still be updated (owner change or adoption).</summary>
        Keep,

        /// <summary>The managed file is already gone; just drop the index entry.</summary>
        ForgetIndex
    }

    public enum OverlayBlockReason
    {
        /// <summary>A file the studio did not write already exists at the path; it is never overwritten.</summary>
        UnmanagedFileExists,

        /// <summary>The owning package no longer has the file; nothing is changed on disk.</summary>
        SourceMissing
    }

    public sealed class OverlayAction
    {
        public OverlayActionKind Kind { get; init; }
        public string Path { get; init; } = "";

        /// <summary>The owner after the action (empty for deletions).</summary>
        public string Owner { get; init; } = "";

        /// <summary>The expected content hash after the action (empty for deletions).</summary>
        public string Sha256 { get; init; } = "";

        /// <summary>Package file to copy from (Copy / DisplaceThenCopy).</summary>
        public string SourcePath { get; init; } = "";

        /// <summary>Hash the file on disk had when planning; null means the file was absent.</summary>
        public string? ExpectedDiskSha256 { get; init; }

        public string Reason { get; init; } = "";
    }

    public sealed class OverlayBlock
    {
        public string Path { get; init; } = "";
        public string DesiredOwner { get; init; } = "";
        public OverlayBlockReason Reason { get; init; }
    }

    public sealed class OverlayPlan
    {
        public List<OverlayAction> Actions { get; } = new();
        public List<OverlayBlock> Blocked { get; } = new();

        /// <summary>The index before the plan (used as the rollback base).</summary>
        public OverlayIndex BaseIndex { get; init; } = new();

        /// <summary>The index after the plan, assuming every action succeeds.</summary>
        public OverlayIndex NewIndex { get; init; } = new();

        public bool HasDiskChanges => Actions.Any(a => a.Kind != OverlayActionKind.Keep && a.Kind != OverlayActionKind.ForgetIndex);

        public bool IndexChanged => !IndexEquals(BaseIndex, NewIndex);

        /// <summary>True when executing the plan would change nothing at all.</summary>
        public bool IsNoOp => !HasDiskChanges && !IndexChanged;

        internal static bool IndexEquals(OverlayIndex a, OverlayIndex b)
        {
            if (a.PriorityRevision != b.PriorityRevision || a.Files.Count != b.Files.Count)
                return false;

            foreach (var (path, entry) in a.Files)
            {
                if (!b.Files.TryGetValue(path, out var other) || other.Owner != entry.Owner || !string.Equals(other.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
    }

    public sealed class OverlayResult
    {
        public bool Skipped { get; set; }
        public string? SkipReason { get; set; }
        public int Copied { get; set; }
        public int Deleted { get; set; }
        public int Displaced { get; set; }
        public List<string> DisplacedPaths { get; } = new();
        public List<string> Errors { get; } = new();
        public List<OverlayBlock> Blocked { get; } = new();
    }

    /// <summary>
    /// Turns "who owns each path" into file operations on the Modifications folder. It only ever writes or removes
    /// files recorded in the ownership index, plus new files where nothing exists yet.
    /// </summary>
    public sealed class ModOverlayBuilder
    {
        private readonly string _modificationsRoot;
        private readonly string _displacedRoot;
        private readonly Action<string> _log;

        public ModOverlayBuilder(string modificationsRoot, string displacedRoot, Action<string>? log = null)
        {
            _modificationsRoot = modificationsRoot;
            _displacedRoot = displacedRoot;
            _log = log ?? (_ => { });
        }

        // ---------------------------------------------------------------- plan (pure)

        /// <param name="owners">Resolved owner per path (package ids; builtin ids are treated as "no package wants it").</param>
        /// <param name="packageSha">SHA-256 of the owner's file, or null when the package does not have it.</param>
        /// <param name="sourcePath">Full path of the owner's file inside the package.</param>
        /// <param name="isPackageId">True for ids that are packages (false for builtin presets).</param>
        /// <param name="diskSha">SHA-256 of the file currently in Modifications, null when absent.</param>
        /// <param name="diskExists">Whether any file exists at the path.</param>
        public static OverlayPlan BuildPlan(
            IReadOnlyDictionary<string, string> owners,
            Func<string, string, string?> packageSha,
            Func<string, string, string> sourcePath,
            Func<string, bool> isPackageId,
            OverlayIndex index,
            Func<string, string?> diskSha,
            Func<string, bool> diskExists,
            int priorityRevision,
            Action<string>? log = null)
        {
            var baseIndex = index.Clone();
            var newIndex = new OverlayIndex { PriorityRevision = priorityRevision };
            var plan = new OverlayPlan { BaseIndex = baseIndex, NewIndex = newIndex };

            // validated index entries only: an invalid or reserved path is never acted on
            var indexed = ModPathRules.NewPathMap<OverlayIndexEntry>();

            foreach (var (path, entry) in index.Files)
            {
                if (!ModPathRules.TryNormalize(path, out string normalized, out string? error))
                {
                    log?.Invoke($"Dropping invalid index entry '{path}' ({error})");
                    continue;
                }

                indexed[normalized] = entry;
            }

            var allPaths = ModPathRules.NewPathSet();

            foreach (string p in indexed.Keys)
                allPaths.Add(p);

            foreach (var (path, owner) in owners)
            {
                if (isPackageId(owner) && ModPathRules.IsValid(path))
                    allPaths.Add(ModPathRules.Normalize(path));
            }

            foreach (string path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                owners.TryGetValue(path, out string? owner);
                bool wanted = owner is not null && isPackageId(owner);

                indexed.TryGetValue(path, out var entry);

                string? wantedSha = null;

                if (wanted)
                {
                    wantedSha = packageSha(owner!, path);

                    if (wantedSha is null)
                    {
                        // never act on a path whose source vanished: keep whatever is on disk and in the index
                        plan.Blocked.Add(new OverlayBlock { Path = path, DesiredOwner = owner!, Reason = OverlayBlockReason.SourceMissing });

                        if (entry is not null)
                            newIndex.Files[path] = new OverlayIndexEntry { Owner = entry.Owner, Sha256 = entry.Sha256 };

                        continue;
                    }
                }

                if (wanted)
                {
                    string source = sourcePath(owner!, path);
                    var target = new OverlayIndexEntry { Owner = owner!, Sha256 = wantedSha! };

                    if (entry is null)
                    {
                        if (diskExists(path))
                        {
                            plan.Blocked.Add(new OverlayBlock { Path = path, DesiredOwner = owner!, Reason = OverlayBlockReason.UnmanagedFileExists });
                            continue;
                        }

                        plan.Actions.Add(new OverlayAction { Kind = OverlayActionKind.Copy, Path = path, Owner = owner!, Sha256 = wantedSha!, SourcePath = source, ExpectedDiskSha256 = null, Reason = "new file" });
                        newIndex.Files[path] = target;
                        continue;
                    }

                    string? disk = diskSha(path);

                    if (disk is null)
                    {
                        plan.Actions.Add(new OverlayAction { Kind = OverlayActionKind.Copy, Path = path, Owner = owner!, Sha256 = wantedSha!, SourcePath = source, ExpectedDiskSha256 = null, Reason = "restore missing file" });
                    }
                    else if (string.Equals(disk, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(entry.Sha256, wantedSha, StringComparison.OrdinalIgnoreCase))
                            plan.Actions.Add(new OverlayAction { Kind = OverlayActionKind.Keep, Path = path, Owner = owner!, Sha256 = wantedSha!, ExpectedDiskSha256 = disk, Reason = entry.Owner == owner ? "up to date" : "ownership moved" });
                        else
                            plan.Actions.Add(new OverlayAction { Kind = OverlayActionKind.Copy, Path = path, Owner = owner!, Sha256 = wantedSha!, SourcePath = source, ExpectedDiskSha256 = disk, Reason = "replace managed file" });
                    }
                    else if (string.Equals(disk, wantedSha, StringComparison.OrdinalIgnoreCase))
                    {
                        plan.Actions.Add(new OverlayAction { Kind = OverlayActionKind.Keep, Path = path, Owner = owner!, Sha256 = wantedSha!, ExpectedDiskSha256 = disk, Reason = "edited file already matches" });
                    }
                    else
                    {
                        plan.Actions.Add(new OverlayAction { Kind = OverlayActionKind.DisplaceThenCopy, Path = path, Owner = owner!, Sha256 = wantedSha!, SourcePath = source, ExpectedDiskSha256 = disk, Reason = "hand-edited file replaced" });
                    }

                    newIndex.Files[path] = target;
                    continue;
                }

                // nobody wants this path any more
                if (entry is null)
                    continue;

                string? current = diskSha(path);

                if (current is null)
                    plan.Actions.Add(new OverlayAction { Kind = OverlayActionKind.ForgetIndex, Path = path, Reason = "already gone" });
                else if (string.Equals(current, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    plan.Actions.Add(new OverlayAction { Kind = OverlayActionKind.Delete, Path = path, ExpectedDiskSha256 = current, Reason = "no longer owned" });
                else
                    plan.Actions.Add(new OverlayAction { Kind = OverlayActionKind.DisplaceThenDelete, Path = path, ExpectedDiskSha256 = current, Reason = "hand-edited file removed" });
            }

            return plan;
        }

        // ---------------------------------------------------------------- execute

        /// <summary>Applies the plan under the "ModStudio" inter-process lock, then writes the index atomically.</summary>
        public OverlayResult Execute(OverlayPlan plan, OverlayIndexStore indexStore)
        {
            var result = new OverlayResult();
            result.Blocked.AddRange(plan.Blocked);

            using var gate = new InterProcessLock("ModStudio", TimeSpan.FromSeconds(10));

            if (!gate.IsAcquired)
            {
                result.Skipped = true;
                result.SkipReason = "another process is changing the overlay";
                return result;
            }

            if (plan.IsNoOp)
                return result;

            string stamp = ModFileUtil.NewStamp();
            string displacedDir = Path.Combine(_displacedRoot, stamp);

            // Journal: record new files in the index BEFORE writing them, so an interruption can never leave
            // a file the studio wrote but does not know it owns.
            var journal = plan.BaseIndex.Clone();
            journal.PriorityRevision = plan.BaseIndex.PriorityRevision;

            foreach (var action in plan.Actions.Where(a => a.Kind == OverlayActionKind.Copy && a.ExpectedDiskSha256 is null))
                journal.Files[action.Path] = new OverlayIndexEntry { Owner = action.Owner, Sha256 = action.Sha256 };

            if (!OverlayPlan.IndexEquals(journal, plan.BaseIndex))
                indexStore.Save(journal);

            var final = plan.NewIndex.Clone();

            foreach (var action in plan.Actions)
            {
                try
                {
                    ApplyAction(action, displacedDir, result);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{action.Path}: {ex.Message}");
                    _log($"Overlay action {action.Kind} failed for {action.Path}: {ex.Message}");

                    // roll the index entry for this path back to what is true on disk
                    if (plan.BaseIndex.Files.TryGetValue(action.Path, out var previous))
                        final.Files[action.Path] = new OverlayIndexEntry { Owner = previous.Owner, Sha256 = previous.Sha256 };
                    else
                        final.Files.Remove(action.Path);
                }
            }

            // paths blocked because an unmanaged file is in the way (at plan time or discovered while executing) are not ours
            foreach (var blocked in result.Blocked.Where(b => b.Reason == OverlayBlockReason.UnmanagedFileExists))
                final.Files.Remove(blocked.Path);

            indexStore.Save(final);
            return result;
        }

        private void ApplyAction(OverlayAction action, string displacedDir, OverlayResult result)
        {
            if (action.Kind is OverlayActionKind.Keep or OverlayActionKind.ForgetIndex)
                return;

            // belt and braces: these can never be valid targets even if a caller hands us a bad plan
            if (!ModPathRules.IsValid(action.Path) || ModPathRules.IsReservedModificationsPath(action.Path))
                throw new ModStudioException("path is not allowed");

            string target = ModPathRules.CombineConfined(_modificationsRoot, action.Path);
            bool exists = File.Exists(target);

            switch (action.Kind)
            {
                case OverlayActionKind.Copy:
                case OverlayActionKind.DisplaceThenCopy:
                {
                    if (exists)
                    {
                        string disk = ModFileUtil.Sha256OfFile(target);

                        if (action.ExpectedDiskSha256 is null)
                        {
                            // nothing existed when planning; something appeared since: never overwrite it
                            result.Blocked.Add(new OverlayBlock { Path = action.Path, DesiredOwner = action.Owner, Reason = OverlayBlockReason.UnmanagedFileExists });
                            return;
                        }

                        if (action.Kind == OverlayActionKind.DisplaceThenCopy || !string.Equals(disk, action.ExpectedDiskSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            // hand-edited (or edited since planning): keep the user's version
                            Displace(target, action.Path, displacedDir, result);
                        }
                    }

                    var (temp, sha) = ModFileUtil.CopyToTemp(action.SourcePath, target);

                    if (!string.Equals(sha, action.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        ModFileUtil.DiscardTemp(temp);
                        throw new ModStudioException("package file changed while copying");
                    }

                    ModFileUtil.CommitTemp(temp, target);
                    result.Copied++;
                    break;
                }

                case OverlayActionKind.Delete:
                case OverlayActionKind.DisplaceThenDelete:
                {
                    if (!exists)
                        break;

                    string disk = ModFileUtil.Sha256OfFile(target);

                    if (action.Kind == OverlayActionKind.DisplaceThenDelete || !string.Equals(disk, action.ExpectedDiskSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Displace(target, action.Path, displacedDir, result);
                    }
                    else
                    {
                        ModFileUtil.ClearReadOnly(target);
                        File.Delete(target);
                        result.Deleted++;
                    }

                    ModFileUtil.PruneEmptyDirectories(Path.GetDirectoryName(target)!, _modificationsRoot);
                    break;
                }
            }
        }

        private void Displace(string target, string relativePath, string displacedDir, OverlayResult result)
        {
            string destination = Path.Combine(displacedDir, relativePath);

            for (int n = 2; File.Exists(destination); n++)
                destination = Path.Combine(displacedDir, relativePath) + "." + n.ToString(CultureInfo.InvariantCulture);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            ModFileUtil.ClearReadOnly(target);
            File.Move(target, destination);

            result.Displaced++;
            result.DisplacedPaths.Add(relativePath);
            _log($"Moved hand-edited file {relativePath} to {destination}");
        }
    }
}
