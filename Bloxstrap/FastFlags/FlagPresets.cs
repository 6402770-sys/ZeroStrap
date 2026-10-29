namespace Bloxstrap.FastFlags
{
    public sealed class FlagPreset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "";

        /// <summary>Selected for the next merge/apply. Selecting a preset does not change any flag by itself.</summary>
        public bool Enabled { get; set; }

        public Dictionary<string, string> Flags { get; set; } = new(StringComparer.Ordinal);

        public FlagPreset Clone(string? newName = null) => new()
        {
            Name = newName ?? Name,
            Enabled = false,
            Flags = new Dictionary<string, string>(Flags, StringComparer.Ordinal)
        };
    }

    public sealed record PresetCandidate(string PresetName, string Value);

    public sealed record PresetConflict(string Flag, IReadOnlyList<PresetCandidate> Candidates);

    public sealed record PresetMergeResult(IReadOnlyDictionary<string, string> Combined, IReadOnlyList<PresetConflict> Conflicts);

    public enum FlagChangeKind
    {
        Add,
        Change,
        Unchanged
    }

    public sealed record FlagChange(string Flag, string? OldValue, string NewValue, FlagChangeKind Kind);

    public sealed record PresetPlan(IReadOnlyList<FlagChange> Changes, IReadOnlyList<string> Errors)
    {
        public bool IsValid => Errors.Count == 0;

        public int EffectiveChanges => Changes.Count(x => x.Kind != FlagChangeKind.Unchanged);
    }

    /// <summary>Original values of every flag an apply touched (null = the flag did not exist).</summary>
    public sealed class PresetApplySnapshot
    {
        public DateTime AppliedAt { get; set; }

        public List<string> PresetNames { get; set; } = new();

        public Dictionary<string, string?> Original { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Applied { get; set; } = new(StringComparer.Ordinal);
    }

    public static class PresetEngine
    {
        /// <summary>
        /// Combines presets in list order. A flag defined with different values by two presets is a
        /// conflict and is left out of <see cref="PresetMergeResult.Combined"/> until resolved.
        /// </summary>
        public static PresetMergeResult Merge(IEnumerable<FlagPreset> presets)
        {
            var candidates = new Dictionary<string, List<PresetCandidate>>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (var preset in presets)
            {
                foreach (var pair in preset.Flags)
                {
                    if (!candidates.TryGetValue(pair.Key, out var list))
                    {
                        candidates[pair.Key] = list = new();
                        order.Add(pair.Key);
                    }

                    list.Add(new PresetCandidate(preset.Name, pair.Value));
                }
            }

            var combined = new Dictionary<string, string>(StringComparer.Ordinal);
            var conflicts = new List<PresetConflict>();

            foreach (string flag in order)
            {
                var list = candidates[flag];

                // "True" and "true" mean the same thing to Roblox, so they are not a conflict
                if (list.Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                    combined[flag] = list[0].Value;
                else
                    conflicts.Add(new PresetConflict(flag, list));
            }

            return new PresetMergeResult(combined, conflicts);
        }

        /// <summary>
        /// Builds the change list without touching anything. Every unresolved conflict and every
        /// invalid flag is reported; a plan with errors must not be applied.
        /// </summary>
        public static PresetPlan Plan(IFlagStore store, PresetMergeResult merge, IReadOnlyDictionary<string, string>? resolutions)
        {
            var errors = new List<string>();
            var target = new Dictionary<string, string>(merge.Combined, StringComparer.Ordinal);

            foreach (var conflict in merge.Conflicts)
            {
                if (resolutions is not null && resolutions.TryGetValue(conflict.Flag, out string? chosen))
                    target[conflict.Flag] = chosen;
                else
                    errors.Add($"Unresolved conflict: {conflict.Flag}");
            }

            var changes = new List<FlagChange>();

            foreach (var pair in target.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var validation = FastFlagRules.Validate(pair.Key, pair.Value);

                if (!validation.IsValid)
                {
                    errors.Add(validation.Error ?? pair.Key);
                    continue;
                }

                string? current = store.Get(pair.Key);

                var kind = current is null
                    ? FlagChangeKind.Add
                    : String.Equals(current, pair.Value, StringComparison.Ordinal) ? FlagChangeKind.Unchanged : FlagChangeKind.Change;

                changes.Add(new FlagChange(pair.Key, current, pair.Value, kind));
            }

            return new PresetPlan(changes, errors);
        }

        /// <summary>
        /// Applies a valid plan. Throws before writing anything if the plan has errors, so a failed
        /// validation never leaves the configuration half-modified.
        /// </summary>
        public static PresetApplySnapshot Apply(IFlagStore store, PresetPlan plan, IEnumerable<string> presetNames, DateTime now)
        {
            if (!plan.IsValid)
                throw new InvalidOperationException("Cannot apply a preset plan that has errors.");

            var snapshot = new PresetApplySnapshot { AppliedAt = now, PresetNames = presetNames.ToList() };

            foreach (var change in plan.Changes.Where(x => x.Kind != FlagChangeKind.Unchanged))
            {
                snapshot.Original[change.Flag] = store.Get(change.Flag);
                snapshot.Applied[change.Flag] = change.NewValue;
            }

            // all checks are done; the writes below cannot fail validation
            foreach (var pair in snapshot.Applied)
                store.Set(pair.Key, pair.Value);

            return snapshot;
        }

        /// <summary>
        /// Restores the values from before the apply. Flags the user changed again afterwards are
        /// left alone and returned, so a revert never silently discards newer edits.
        /// </summary>
        public static IReadOnlyList<string> Revert(IFlagStore store, PresetApplySnapshot snapshot)
        {
            var skipped = new List<string>();

            foreach (var pair in snapshot.Original)
            {
                string? current = store.Get(pair.Key);

                if (snapshot.Applied.TryGetValue(pair.Key, out string? applied) && !String.Equals(current, applied, StringComparison.Ordinal))
                {
                    skipped.Add(pair.Key);
                    continue;
                }

                store.Set(pair.Key, pair.Value);
            }

            return skipped;
        }

        /// <summary>A preset made from the flags currently configured.</summary>
        public static FlagPreset FromStore(IFlagStore store, string name) => new()
        {
            Name = name,
            Flags = new Dictionary<string, string>(store.Snapshot(), StringComparer.Ordinal)
        };

        public static string UniqueName(IEnumerable<FlagPreset> existing, string wanted)
        {
            wanted = String.IsNullOrWhiteSpace(wanted) ? "Preset" : wanted.Trim();

            var names = existing.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!names.Contains(wanted))
                return wanted;

            for (int i = 2; ; i++)
            {
                string candidate = $"{wanted} ({i})";

                if (!names.Contains(candidate))
                    return candidate;
            }
        }
    }
}
