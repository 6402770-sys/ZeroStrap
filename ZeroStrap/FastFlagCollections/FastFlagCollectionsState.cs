using System.Security.Cryptography;
using System.Text.Encodings.Web;

using ZeroStrap.FastFlags;

namespace ZeroStrap.FastFlagCollections
{
    public sealed class ImportedCollection
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "";

        public DateTime ImportedAt { get; set; }

        public string SourceFileName { get; set; } = "";

        /// <summary>The imported text, byte-for-byte as read. Export writes this unchanged.</summary>
        public string PayloadText { get; set; } = "";
    }

    public sealed class LastAppliedCollection
    {
        public string CollectionId { get; set; } = "";

        public string Name { get; set; } = "";

        public DateTime AppliedAt { get; set; }

        /// <summary>See <see cref="CollectionStatusEvaluator.ComputeHash"/>.</summary>
        public string PayloadHash { get; set; } = "";
    }

    /// <summary>Persisted in FastFlagCollections.json. Kept apart from every other settings file.</summary>
    public sealed class FastFlagCollectionsState
    {
        public List<ImportedCollection> Collections { get; set; } = new();

        public LastAppliedCollection? LastApplied { get; set; }

        /// <summary>The configuration from before the last apply. Cleared after Undo.</summary>
        public Dictionary<string, string>? UndoSnapshot { get; set; }

        /// <summary>True once the old preset list has been offered to (or brought into) My collections.</summary>
        public bool PresetsMigrated { get; set; }
    }

    public enum CollectionStatus
    {
        NotApplied,
        Applied,
        ChangedSinceApplied
    }

    public static class CollectionStatusEvaluator
    {
        /// <summary>SHA-256 (lowercase hex) of the entries sorted by key as "key=value\n". Order-independent.</summary>
        public static string ComputeHash(IEnumerable<KeyValuePair<string, string>> entries)
        {
            var builder = new StringBuilder();

            foreach (var pair in entries.OrderBy(x => x.Key, StringComparer.Ordinal))
                builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
        }

        public static string ComputeHash(IEnumerable<(string Key, string Value)> entries) =>
            ComputeHash(entries.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)));

        /// <summary>Only the last applied collection can be Applied or Changed; every other collection is NotApplied.</summary>
        public static CollectionStatus Evaluate(LastAppliedCollection? lastApplied, string collectionId, IReadOnlyDictionary<string, string> currentFlags)
        {
            if (lastApplied is null || lastApplied.CollectionId != collectionId)
                return CollectionStatus.NotApplied;

            return ComputeHash(currentFlags) == lastApplied.PayloadHash
                ? CollectionStatus.Applied
                : CollectionStatus.ChangedSinceApplied;
        }
    }

    public static class CollectionNaming
    {
        public const int MaxNameLength = 60;

        /// <summary>null when the name is acceptable.</summary>
        public static string? Validate(string name, IEnumerable<string> existingNames)
        {
            name = name.Trim();

            if (name.Length == 0)
                return "Enter a name.";

            if (name.Length > MaxNameLength)
                return $"Names can be at most {MaxNameLength} characters.";

            if (existingNames.Any(x => String.Equals(x.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                return "A collection with that name already exists.";

            return null;
        }

        /// <summary>A valid name based on <paramref name="preferred"/>, numbered if it is taken.</summary>
        public static string Unique(string preferred, IEnumerable<string> existingNames)
        {
            var taken = existingNames.ToList();
            string baseName = preferred.Trim();

            if (baseName.Length == 0)
                baseName = "Imported collection";

            if (baseName.Length > MaxNameLength)
                baseName = baseName[..MaxNameLength].TrimEnd();

            string name = baseName;

            for (int i = 2; Validate(name, taken) is not null; i++)
            {
                string suffix = $" ({i})";
                name = (baseName.Length + suffix.Length > MaxNameLength ? baseName[..(MaxNameLength - suffix.Length)].TrimEnd() : baseName) + suffix;
            }

            return name;
        }
    }

    public static class PresetMigration
    {
        /// <summary>
        /// Turns old presets into collections (name = preset name, payload = indented JSON object of its flags).
        /// Presets with no flags, or flags that don't form a valid collection, are skipped.
        /// </summary>
        public static (List<ImportedCollection> Collections, int Skipped) FromPresets(IEnumerable<FlagPreset> presets, IEnumerable<string> existingNames, DateTime now)
        {
            var names = existingNames.ToList();
            var result = new List<ImportedCollection>();
            int skipped = 0;

            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

            foreach (var preset in presets)
            {
                string text = JsonSerializer.Serialize(preset.Flags, options);

                if (!CollectionPayload.Parse(text).Success)
                {
                    skipped++;
                    continue;
                }

                string name = CollectionNaming.Unique(preset.Name, names);
                names.Add(name);

                result.Add(new ImportedCollection
                {
                    Name = name,
                    ImportedAt = now,
                    SourceFileName = "Preset list",
                    PayloadText = text
                });
            }

            return (result, skipped);
        }
    }
}
