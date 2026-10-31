namespace ZeroStrap.FastFlagCollections
{
    public enum CollectionParameterKind
    {
        /// <summary>Replaced at apply time by (logical processors - 1).</summary>
        LogicalProcessorsMinusOne
    }

    /// <summary>A value in an official collection that is a placeholder until the collection is applied.</summary>
    public sealed record CollectionParameter(string Key, string Placeholder, CollectionParameterKind Kind);

    public sealed record OfficialCollection(string Id, string Name, string Author, string ResourceName, IReadOnlyList<CollectionParameter> Parameters)
    {
        public bool NeedsLogicalProcessors => Parameters.Any(x => x.Kind == CollectionParameterKind.LogicalProcessorsMinusOne);

        public string LoadText()
        {
            using var stream = typeof(ZeroCollectionCatalog).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"Embedded collection '{ResourceName}' is missing.");
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }

        /// <summary>The embedded template, placeholders unresolved.</summary>
        public IReadOnlyList<(string Key, string Value)> LoadTemplate()
        {
            var result = CollectionPayload.Parse(LoadText());

            if (!result.Success)
                throw new InvalidOperationException($"Embedded collection '{Name}' is invalid: {result.Errors[0]}");

            return result.Entries;
        }
    }

    /// <summary>The official ZeroStrap collections shipped inside the app.</summary>
    public static class ZeroCollectionCatalog
    {
        public const string PureFpsId = "pure-fps";

        public const string LowPingId = "low-ping";

        public static readonly CollectionParameter RuntimeConcurrency = new(
            "DFIntRuntimeConcurrency", "Your Logical Processors - 1", CollectionParameterKind.LogicalProcessorsMinusOne);

        public static IReadOnlyList<OfficialCollection> All { get; } = new[]
        {
            new OfficialCollection(PureFpsId, "Pure FPS", "ZeroStrap", "ZeroStrap.FastFlagCollections.pure-fps.json", new[] { RuntimeConcurrency }),
            new OfficialCollection(LowPingId, "Low Ping", "ZeroStrap", "ZeroStrap.FastFlagCollections.low-ping.json", Array.Empty<CollectionParameter>())
        };

        public static OfficialCollection? Find(string id) => All.FirstOrDefault(x => x.Id == id);

        public static bool IsPlaceholder(string value) =>
            All.Any(c => c.Parameters.Any(p => String.Equals(p.Placeholder, value, StringComparison.Ordinal)));
    }
}
