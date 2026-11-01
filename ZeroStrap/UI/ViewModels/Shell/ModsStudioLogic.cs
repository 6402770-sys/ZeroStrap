using System.Globalization;

using ZeroStrap.Mods;

namespace ZeroStrap.UI.ViewModels.Shell
{
    /// <summary>Pill shown on an installed mod.</summary>
    public enum ModStatus
    {
        On,
        Off,
        Conflict
    }

    public enum ConflictRowStatus
    {
        Resolved,
        Unresolved,

        /// <summary>A file ZeroStrap did not create is in the way, so the mod's copy is not written.</summary>
        Blocked
    }

    /// <summary>One choice for a contested file. Informational candidates are shown but cannot be picked.</summary>
    public sealed record ConflictCandidate(string OwnerId, string Label, bool Selectable, bool IsCurrent, bool IsBuiltin);

    public sealed record ConflictRow(
        string Path,
        IReadOnlyList<ConflictCandidate> Candidates,
        string CurrentOwnerId,
        string CurrentOwnerLabel,
        ConflictRowStatus Status,
        IReadOnlyList<string> PackageIds);

    /// <summary>Pure presentation logic of Mods Studio (no WPF, no I/O) so it can be tested.</summary>
    public static class ModsStudioLogic
    {
        /// <summary>Owner id used for the informational "a file ZeroStrap did not write is in the way" candidate.</summary>
        public const string UnmanagedOwnerId = "unmanaged:file";

        public const string UnmanagedLabel = "File already in Modifications";

        // ---------------------------------------------------------------- labels

        /// <summary>The name a built-in mod has in the Library (the engine's own display names are older).</summary>
        public static string BuiltinLabel(string id, string? fallback = null) => id switch
        {
            "builtin:cursors" => "Classic cursors",
            "builtin:old-sounds" => "Classic character sounds",
            "builtin:old-avatar-background" => "Classic avatar background",
            "builtin:emoji-font" => "Emoji font",
            "builtin:custom-font" => "Custom font",
            _ => string.IsNullOrWhiteSpace(fallback) ? id : fallback
        };

        public static string ListMeta(ModCategory category, string? author) =>
            string.IsNullOrWhiteSpace(author) ? category.ToString() : $"{category} · {author.Trim()}";

        public static string SourceLabel(ModSource source) => source == ModSource.Legacy ? "Legacy" : "Imported";

        public static string DetailsMeta(ModManifest manifest, CultureInfo? culture = null)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(manifest.Author))
                parts.Add(manifest.Author.Trim());

            parts.Add((manifest.Category ?? ModCategory.Other).ToString());
            parts.Add(SourceLabel(manifest.Source));
            parts.Add("Installed " + manifest.InstalledAt.LocalDateTime.ToString("MMM d, yyyy", culture ?? CultureInfo.CurrentCulture));

            return string.Join(" · ", parts);
        }

        public static string CompatibilityText(int? found, int total) =>
            found is null
                ? "Unknown until Roblox is installed"
                : $"{found} of {total} files found in the current Roblox build";

        public static string FilesHeader(int count) => $"Files ({count})";

        public static string ConflictsLinkText(int count) => $"Conflicts ({count})";

        // ---------------------------------------------------------------- status

        public static ModStatus StatusFor(bool enabled, bool hasUnresolvedConflict) =>
            !enabled ? ModStatus.Off : hasUnresolvedConflict ? ModStatus.Conflict : ModStatus.On;

        public static string StatusText(ModStatus status) => status switch
        {
            ModStatus.On => "On",
            ModStatus.Off => "Off",
            _ => "Conflict"
        };

        /// <summary>Ids of packages that take part in a conflict nobody has settled yet.</summary>
        public static HashSet<string> PackagesWithUnresolvedConflicts(IEnumerable<ModConflict> conflicts)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);

            foreach (var conflict in conflicts.Where(c => c.Status == ConflictStatus.Unresolved))
            {
                foreach (string candidate in conflict.Candidates.Where(id => !BuiltinPreset.IsBuiltinId(id)))
                    ids.Add(candidate);
            }

            return ids;
        }

        /// <summary>Built-in preset ids whose files are contested by a package.</summary>
        public static HashSet<string> ContestedBuiltins(IEnumerable<ModConflict> conflicts)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);

            foreach (var conflict in conflicts)
            {
                foreach (string candidate in conflict.Candidates.Where(BuiltinPreset.IsBuiltinId))
                    ids.Add(candidate);
            }

            return ids;
        }

        public static int UnresolvedCount(IEnumerable<ModConflict> conflicts) =>
            conflicts.Count(c => c.Status == ConflictStatus.Unresolved);

        // ---------------------------------------------------------------- order

        /// <summary>
        /// Moves a package one step up (negative delta) or down (positive) among the enabled packages. Disabled packages keep
        /// their place. Returns the new full order, or null when the package cannot move that way.
        /// </summary>
        public static List<string>? MoveAmongEnabled(IReadOnlyList<string> fullOrder, ISet<string> enabled, string id, int delta)
        {
            if (delta == 0)
                return null;

            int from = -1;

            for (int i = 0; i < fullOrder.Count; i++)
            {
                if (fullOrder[i] == id)
                {
                    from = i;
                    break;
                }
            }

            if (from < 0 || !enabled.Contains(id))
                return null;

            int step = delta < 0 ? -1 : 1;
            int to = -1;

            for (int i = from + step; i >= 0 && i < fullOrder.Count; i += step)
            {
                if (enabled.Contains(fullOrder[i]))
                {
                    to = i;
                    break;
                }
            }

            if (to < 0)
                return null;

            var next = fullOrder.ToList();
            (next[from], next[to]) = (next[to], next[from]);
            return next;
        }

        // ---------------------------------------------------------------- conflict rows

        /// <summary>One row per contested path, in path order. A path a file outside the studio is blocking gets an extra informational candidate.</summary>
        public static List<ConflictRow> BuildConflictRows(
            IEnumerable<ModConflict> conflicts,
            IEnumerable<OverlayBlock> blocked,
            Func<string, string> labelOf)
        {
            var blockedPaths = new HashSet<string>(
                blocked.Where(b => b.Reason == OverlayBlockReason.UnmanagedFileExists).Select(b => b.Path),
                StringComparer.OrdinalIgnoreCase);

            var blockedDesired = blocked
                .Where(b => b.Reason == OverlayBlockReason.UnmanagedFileExists)
                .GroupBy(b => b.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().DesiredOwner, StringComparer.OrdinalIgnoreCase);

            var rows = new List<ConflictRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var conflict in conflicts)
            {
                seen.Add(conflict.Path);
                bool isBlocked = blockedPaths.Contains(conflict.Path);

                var candidates = conflict.Candidates
                    .Select(id => new ConflictCandidate(
                        id,
                        labelOf(id),
                        Selectable: !isBlocked,
                        IsCurrent: !isBlocked && id == conflict.CurrentOwner,
                        IsBuiltin: BuiltinPreset.IsBuiltinId(id)))
                    .ToList();

                string ownerId = conflict.CurrentOwner;
                string ownerLabel = labelOf(conflict.CurrentOwner);

                if (isBlocked)
                {
                    candidates.Add(new ConflictCandidate(UnmanagedOwnerId, UnmanagedLabel, Selectable: false, IsCurrent: true, IsBuiltin: false));
                    ownerId = UnmanagedOwnerId;
                    ownerLabel = UnmanagedLabel;
                }

                rows.Add(new ConflictRow(
                    conflict.Path,
                    candidates,
                    ownerId,
                    ownerLabel,
                    isBlocked ? ConflictRowStatus.Blocked : conflict.Status == ConflictStatus.Resolved ? ConflictRowStatus.Resolved : ConflictRowStatus.Unresolved,
                    conflict.Candidates.Where(id => !BuiltinPreset.IsBuiltinId(id)).ToList()));
            }

            // a package file that only a foreign file stands in the way of is still worth showing
            foreach (var (path, desired) in blockedDesired)
            {
                if (seen.Contains(path))
                    continue;

                rows.Add(new ConflictRow(
                    path,
                    new[]
                    {
                        new ConflictCandidate(desired, labelOf(desired), Selectable: false, IsCurrent: false, IsBuiltin: BuiltinPreset.IsBuiltinId(desired)),
                        new ConflictCandidate(UnmanagedOwnerId, UnmanagedLabel, Selectable: false, IsCurrent: true, IsBuiltin: false)
                    },
                    UnmanagedOwnerId,
                    UnmanagedLabel,
                    ConflictRowStatus.Blocked,
                    BuiltinPreset.IsBuiltinId(desired) ? Array.Empty<string>() : new[] { desired }));
            }

            return rows.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<ConflictRow> ForPackage(IEnumerable<ConflictRow> rows, string? packageId) =>
            packageId is null
                ? rows.ToList()
                : rows.Where(r => r.PackageIds.Contains(packageId, StringComparer.Ordinal)).ToList();

        public static int ConflictCountFor(IEnumerable<ConflictRow> rows, string packageId) => ForPackage(rows, packageId).Count;

        // ---------------------------------------------------------------- messages

        public const string NextLaunchNote = "Roblox picks this up on its next launch.";

        public static string UpdatedMessage(int copied, int deleted, int displaced)
        {
            int changed = copied + deleted + displaced;

            return changed == 0
                ? "Saved. " + NextLaunchNote
                : $"Updated {changed} file{(changed == 1 ? "" : "s")}. " + NextLaunchNote;
        }

        public static string MigrationNotice => "Your existing mods are now in “My existing mods”. A backup was saved to Mods\\Backups.";

        public static string RemoveConfirmation(string name) =>
            $"Remove {name}? Its files are removed from Modifications, and Roblox's original files come back the next time you launch.";

        public static string PresetOffConfirmation(string preset, string package) =>
            $"{preset} will be turned off so {package} can use this file.";
    }
}
