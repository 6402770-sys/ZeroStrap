namespace ZeroStrap.Mods
{
    public enum ConflictStatus
    {
        /// <summary>The owner follows an explicit choice or the priority order.</summary>
        Resolved,

        /// <summary>An existing owner keeps the path although another candidate wants it; the user has to choose.</summary>
        Unresolved
    }

    public enum ResolverActionKind
    {
        /// <summary>A package was chosen over an active built-in preset: the preset has to be switched off first.</summary>
        DisablePreset
    }

    public sealed class ResolverPackage
    {
        public string Id { get; init; } = "";
        public IReadOnlyCollection<string> Paths { get; init; } = Array.Empty<string>();
    }

    public sealed class ResolverInput
    {
        public IReadOnlyList<ResolverPackage> EnabledPackages { get; init; } = Array.Empty<ResolverPackage>();

        /// <summary>Package ids, highest priority first. Enabled packages missing from the list rank below the listed ones.</summary>
        public IReadOnlyList<string> Priority { get; init; } = Array.Empty<string>();

        /// <summary>Explicit choices: path to owner id.</summary>
        public IReadOnlyDictionary<string, string> FileChoices { get; init; } = new Dictionary<string, string>();

        /// <summary>Path to preset id for files owned by an active built-in preset.</summary>
        public IReadOnlyDictionary<string, string> ActiveBuiltinOwners { get; init; } = new Dictionary<string, string>();

        /// <summary>Path to package id from the overlay index (who wrote the file last time).</summary>
        public IReadOnlyDictionary<string, string> PreviousOwners { get; init; } = new Dictionary<string, string>();

        /// <summary>When true the user reordered priorities since the last build, so priority overrides existing ownership.</summary>
        public bool PriorityChanged { get; init; }
    }

    public sealed class ModConflict
    {
        public string Path { get; init; } = "";

        /// <summary>Every contender: the active built-in preset first (if any), then packages by priority.</summary>
        public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

        public string CurrentOwner { get; init; } = "";
        public ConflictStatus Status { get; init; }
    }

    public sealed class ResolverAction
    {
        public ResolverActionKind Kind { get; init; }
        public string PresetId { get; init; } = "";
        public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
    }

    public sealed class ModResolution
    {
        /// <summary>Owner of every path some enabled package wants (a package id, or a builtin id when the preset keeps it).</summary>
        public Dictionary<string, string> Owners { get; } = ModPathRules.NewPathMap<string>();

        public List<ModConflict> Conflicts { get; } = new();
        public List<ResolverAction> Actions { get; } = new();
    }

    /// <summary>
    /// Decides who owns each contested path. Pure: no I/O.
    /// Order of rules per path: explicit valid choice; an active built-in preset keeps its path; an existing
    /// package owner keeps it (unless priorities were just reordered); otherwise the highest-priority candidate.
    /// </summary>
    public static class ModConflictResolver
    {
        public static ModResolution Resolve(ResolverInput input)
        {
            var resolution = new ModResolution();

            var rank = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < input.Priority.Count; i++)
                rank.TryAdd(input.Priority[i], i);

            // stable order: listed ids by position, the rest after them sorted by id
            var ordered = input.EnabledPackages
                .Select(p => p.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => rank.TryGetValue(id, out int r) ? r : int.MaxValue)
                .ThenBy(id => id, StringComparer.Ordinal)
                .ToList();

            var byPath = ModPathRules.NewPathMap<List<string>>();

            foreach (string id in ordered)
            {
                var package = input.EnabledPackages.First(p => p.Id == id);

                foreach (string path in package.Paths)
                {
                    if (!byPath.TryGetValue(path, out var list))
                        byPath[path] = list = new List<string>();

                    list.Add(id);
                }
            }

            var disableByPreset = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var (path, candidates) in byPath.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                input.ActiveBuiltinOwners.TryGetValue(path, out string? builtin);
                input.FileChoices.TryGetValue(path, out string? choice);
                input.PreviousOwners.TryGetValue(path, out string? previous);

                string top = candidates[0];
                string owner;
                bool explicitChoice = false;

                bool choiceValid = choice is not null
                    && (candidates.Contains(choice, StringComparer.Ordinal) || (builtin is not null && choice == builtin));

                if (choiceValid)
                {
                    owner = choice!;
                    explicitChoice = true;
                }
                else if (builtin is not null)
                {
                    owner = builtin;
                }
                else if (!input.PriorityChanged && previous is not null && candidates.Contains(previous, StringComparer.Ordinal))
                {
                    owner = previous;
                }
                else
                {
                    owner = top;
                }

                resolution.Owners[path] = owner;

                if (builtin is not null && owner != builtin)
                {
                    if (!disableByPreset.TryGetValue(builtin, out var paths))
                        disableByPreset[builtin] = paths = new List<string>();

                    paths.Add(path);
                }

                int contenders = candidates.Count + (builtin is not null ? 1 : 0);

                if (contenders >= 2)
                {
                    var all = new List<string>();

                    if (builtin is not null)
                        all.Add(builtin);

                    all.AddRange(candidates);

                    bool resolved = explicitChoice || (builtin is null && owner == top);

                    resolution.Conflicts.Add(new ModConflict
                    {
                        Path = path,
                        Candidates = all,
                        CurrentOwner = owner,
                        Status = resolved ? ConflictStatus.Resolved : ConflictStatus.Unresolved
                    });
                }
            }

            foreach (var (preset, paths) in disableByPreset.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                resolution.Actions.Add(new ResolverAction
                {
                    Kind = ResolverActionKind.DisablePreset,
                    PresetId = preset,
                    Paths = paths
                });
            }

            return resolution;
        }
    }
}
