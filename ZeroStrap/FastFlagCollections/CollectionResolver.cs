namespace ZeroStrap.FastFlagCollections
{
    public sealed class CollectionResolveException : Exception
    {
        public CollectionResolveException(string message) : base(message) { }
    }

    /// <summary>Turns a collection template into the values that will actually be applied. Never mutates its input.</summary>
    public static class CollectionResolver
    {
        public static IReadOnlyList<(string Key, string Value)> Resolve(
            IReadOnlyList<(string Key, string Value)> entries,
            IReadOnlyList<CollectionParameter> parameters,
            int? logicalProcessors)
        {
            string? concurrency = null;

            if (parameters.Any(x => x.Kind == CollectionParameterKind.LogicalProcessorsMinusOne))
            {
                if (logicalProcessors is null or < 2)
                    throw new CollectionResolveException("The number of logical processors must be 2 or more.");

                concurrency = (logicalProcessors.Value - 1).ToString(CultureInfo.InvariantCulture);
            }

            var resolved = new List<(string Key, string Value)>(entries.Count);

            foreach (var (key, value) in entries)
            {
                var parameter = parameters.FirstOrDefault(p => p.Key == key && String.Equals(p.Placeholder, value, StringComparison.Ordinal));

                resolved.Add(parameter is { Kind: CollectionParameterKind.LogicalProcessorsMinusOne } ? (key, concurrency!) : (key, value));
            }

            foreach (var (key, value) in resolved)
            {
                if (parameters.Any(p => String.Equals(p.Placeholder, value, StringComparison.Ordinal)))
                    throw new CollectionResolveException($"'{key}' still has a placeholder value.");
            }

            return resolved;
        }
    }
}
