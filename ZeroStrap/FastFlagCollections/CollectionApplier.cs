using ZeroStrap.FastFlags;

namespace ZeroStrap.FastFlagCollections
{
    /// <summary>
    /// Replaces the whole pending flag configuration with a collection. Never merges.
    /// The caller notifies the workspace and saves.
    /// </summary>
    public static class CollectionApplier
    {
        /// <returns>The configuration as it was before the replace.</returns>
        public static IReadOnlyDictionary<string, string> Replace(IFlagStore store, IReadOnlyList<(string Key, string Value)> entries)
        {
            // refuse before touching the store: a placeholder must never be written
            foreach (var (key, value) in entries)
            {
                if (ZeroCollectionCatalog.IsPlaceholder(value))
                    throw new CollectionResolveException($"'{key}' still has a placeholder value.");
            }

            var snapshot = store.Snapshot();
            var keep = new HashSet<string>(entries.Select(x => x.Key), StringComparer.Ordinal);

            foreach (string key in snapshot.Keys)
            {
                if (!keep.Contains(key))
                    store.Set(key, null);
            }

            foreach (var (key, value) in entries)
                store.Set(key, value);

            return snapshot;
        }

        /// <summary>Puts a configuration captured by <see cref="Replace"/> back.</summary>
        public static IReadOnlyDictionary<string, string> Restore(IFlagStore store, IReadOnlyDictionary<string, string> snapshot) =>
            Replace(store, snapshot.Select(x => (x.Key, x.Value)).ToList());
    }
}
