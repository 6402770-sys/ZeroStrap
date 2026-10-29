namespace Bloxstrap.FastFlags
{
    [Flags]
    public enum FlagOrigin
    {
        None = 0,

        /// <summary>Present in the downloaded catalog.</summary>
        Catalog = 1,

        /// <summary>Present in the user's ClientAppSettings.json (pending or saved).</summary>
        Configured = 2
    }

    public enum FlagScope
    {
        All,
        Configured,
        Catalog
    }

    public sealed class FlagIndexItem
    {
        public string Name { get; }

        internal string LowerName { get; }

        public FlagValueType Type { get; }

        public FlagOrigin Origin { get; }

        public FlagEffect Effect { get; }

        /// <summary>Configured value, or null when the flag is only known from the catalog.</summary>
        public string? Value { get; }

        /// <summary>Catalog layer tag ("C++", "Lua", ...), when known.</summary>
        public string? Layer { get; }

        public bool IsConfigured => Origin.HasFlag(FlagOrigin.Configured);

        public bool IsExperimental => Effect == FlagEffect.Experimental;

        public FlagIndexItem(string name, FlagValueType type, FlagOrigin origin, string? value, string? layer)
        {
            Name = name;
            LowerName = name.ToLowerInvariant();
            Type = type;
            Origin = origin;
            Value = value;
            Layer = layer;
            Effect = RobloxFlagAllowlist.GetEffect(name);
        }
    }

    public sealed class FlagQuery
    {
        public string Text { get; init; } = "";

        public FlagScope Scope { get; init; } = FlagScope.All;

        public FlagValueType? Type { get; init; }

        public FlagEffect? Effect { get; init; }

        /// <summary>Hide the flags managed by the built-in presets page.</summary>
        public IReadOnlySet<string>? Excluded { get; init; }

        /// <summary>Maximum number of items returned; <see cref="FlagQueryResult.TotalMatches"/> still counts everything.</summary>
        public int Limit { get; init; } = 500;
    }

    public sealed record FlagQueryResult(IReadOnlyList<FlagIndexItem> Items, int TotalMatches);

    /// <summary>
    /// Immutable in-memory index over catalog + configured flags. Rebuilt (cheaply) whenever the
    /// configured flags change; queries are read-only and safe to run off the UI thread.
    /// Matching is case-insensitive; every whitespace-separated token must appear in the name.
    /// Ranking: exact name, then names starting with the query (with or without the type prefix), then the rest; ties by name.
    /// </summary>
    public sealed class FlagSearchIndex
    {
        private readonly FlagIndexItem[] _items;

        public int Count => _items.Length;

        public IReadOnlyList<FlagIndexItem> Items => _items;

        public FlagSearchIndex(FlagCatalog catalog, IReadOnlyDictionary<string, string> configured)
        {
            var byName = new Dictionary<string, FlagIndexItem>(catalog.Entries.Count + configured.Count, StringComparer.Ordinal);

            foreach (var entry in catalog.Entries)
            {
                configured.TryGetValue(entry.Name, out string? value);
                var origin = value is null ? FlagOrigin.Catalog : FlagOrigin.Catalog | FlagOrigin.Configured;
                byName[entry.Name] = new FlagIndexItem(entry.Name, entry.Type, origin, value, entry.Origin);
            }

            foreach (var pair in configured)
            {
                if (byName.ContainsKey(pair.Key))
                    continue;

                // configured flags with an unknown prefix are still listed so they can be found and removed
                var type = FastFlagRules.GetValueType(pair.Key) ?? FlagValueType.String;
                byName[pair.Key] = new FlagIndexItem(pair.Key, type, FlagOrigin.Configured, pair.Value, null);
            }

            // allowlisted flags are always searchable, even before a catalog has been downloaded
            foreach (string name in RobloxFlagAllowlist.Names)
            {
                if (!byName.ContainsKey(name) && FastFlagRules.GetValueType(name) is FlagValueType type)
                    byName[name] = new FlagIndexItem(name, type, FlagOrigin.Catalog, null, null);
            }

            _items = byName.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        }

        public FlagQueryResult Query(FlagQuery query, CancellationToken token = default)
        {
            string[] tokens = query.Text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string full = tokens.Length == 1 ? tokens[0] : "";

            var matches = new List<(FlagIndexItem Item, int Rank)>();

            for (int i = 0; i < _items.Length; i++)
            {
                // cheap cancellation check; queries are restarted on every keystroke
                if ((i & 1023) == 0)
                    token.ThrowIfCancellationRequested();

                var item = _items[i];

                if (query.Scope == FlagScope.Configured && !item.IsConfigured)
                    continue;

                if (query.Scope == FlagScope.Catalog && !item.Origin.HasFlag(FlagOrigin.Catalog))
                    continue;

                if (query.Type is FlagValueType type && item.Type != type)
                    continue;

                if (query.Effect is FlagEffect effect && item.Effect != effect)
                    continue;

                if (query.Excluded is not null && query.Excluded.Contains(item.Name))
                    continue;

                bool all = true;

                foreach (string t in tokens)
                {
                    if (!item.LowerName.Contains(t, StringComparison.Ordinal))
                    {
                        all = false;
                        break;
                    }
                }

                if (!all)
                    continue;

                matches.Add((item, Rank(item, full)));
            }

            token.ThrowIfCancellationRequested();

            var ordered = matches
                .OrderBy(x => x.Rank)
                .ThenBy(x => x.Item.Name, StringComparer.Ordinal)
                .Take(Math.Max(0, query.Limit))
                .Select(x => x.Item)
                .ToList();

            return new FlagQueryResult(ordered, matches.Count);
        }

        private static int Rank(FlagIndexItem item, string full)
        {
            if (full.Length == 0)
                return 2;

            if (item.LowerName == full)
                return 0;

            if (item.LowerName.StartsWith(full, StringComparison.Ordinal))
                return 1;

            // "texturequality" should rank DFIntTextureQualityOverride above names that merely contain it
            string? prefix = FastFlagRules.GetPrefix(item.Name);

            if (prefix is not null && item.LowerName.AsSpan(prefix.Length).StartsWith(full, StringComparison.Ordinal))
                return 1;

            return 2;
        }
    }
}
