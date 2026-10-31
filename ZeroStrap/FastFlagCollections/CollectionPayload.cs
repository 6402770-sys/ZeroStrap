namespace ZeroStrap.FastFlagCollections
{
    /// <summary>The result of reading a collection: its ordered entries, or the problems that make it unusable.</summary>
    public sealed class CollectionParseResult
    {
        public IReadOnlyList<(string Key, string Value)> Entries { get; }

        public IReadOnlyList<string> Errors { get; }

        /// <summary>The text exactly as read. Only set by <see cref="CollectionPayload.ReadFile"/>; kept verbatim for storage and export.</summary>
        public string? OriginalText { get; }

        public bool Success => Errors.Count == 0;

        public CollectionParseResult(IReadOnlyList<(string Key, string Value)> entries, IReadOnlyList<string> errors, string? originalText = null)
        {
            Entries = entries;
            Errors = errors;
            OriginalText = originalText;
        }

        public CollectionParseResult WithText(string text) => new(Entries, Errors, text);
    }

    /// <summary>
    /// Structural validation of a flag collection: a JSON object of flag names to string, number or boolean values.
    /// Collections are opaque payloads; keys and values are never rewritten.
    /// </summary>
    public static class CollectionPayload
    {
        public const int MaxEntries = 5000;

        public const int MaxKeyLength = 200;

        public const long MaxFileBytes = 1024 * 1024;

        public static CollectionParseResult Parse(string text)
        {
            var errors = new List<string>();

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(text.TrimStart('﻿'), new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
            }
            catch (JsonException ex)
            {
                return Fail($"The file is not valid JSON: {ex.Message}");
            }

            using (document)
            {
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    return Fail("The JSON root must be an object of flag names and values.");

                var entries = new List<(string Key, string Value)>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int count = 0;

                foreach (var property in root.EnumerateObject())
                {
                    if (++count > MaxEntries)
                        return Fail($"The file has more than {MaxEntries:N0} flags.");

                    string key = property.Name;
                    string shown = key.Length > 60 ? key[..60] + "..." : key;

                    if (String.IsNullOrWhiteSpace(key))
                    {
                        errors.Add($"Entry {count} has an empty name.");
                        continue;
                    }

                    if (key.Length > MaxKeyLength)
                    {
                        errors.Add($"'{shown}' has a name longer than {MaxKeyLength} characters.");
                        continue;
                    }

                    if (!seen.Add(key))
                    {
                        errors.Add($"'{shown}' appears more than once.");
                        continue;
                    }

                    switch (property.Value.ValueKind)
                    {
                        case JsonValueKind.String:
                            entries.Add((key, property.Value.GetString() ?? ""));
                            break;
                        case JsonValueKind.Number:
                            entries.Add((key, property.Value.GetRawText()));
                            break;
                        case JsonValueKind.True:
                            entries.Add((key, "True"));
                            break;
                        case JsonValueKind.False:
                            entries.Add((key, "False"));
                            break;
                        default:
                            errors.Add($"'{shown}' must be a string, number or true/false, not {Describe(property.Value.ValueKind)}.");
                            break;
                    }
                }

                if (errors.Count == 0 && entries.Count == 0)
                    errors.Add("The file has no flags.");

                return errors.Count == 0
                    ? new CollectionParseResult(entries, Array.Empty<string>())
                    : new CollectionParseResult(Array.Empty<(string, string)>(), errors);
            }

            static CollectionParseResult Fail(string error) => new(Array.Empty<(string, string)>(), new[] { error });
        }

        /// <summary>Reads and validates a file for import: at most 1 MB, original text kept verbatim.</summary>
        public static CollectionParseResult ReadFile(string path)
        {
            string text;

            try
            {
                var info = new FileInfo(path);

                if (info.Length > MaxFileBytes)
                    return new CollectionParseResult(Array.Empty<(string, string)>(), new[] { "The file is larger than 1 MB." });

                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new CollectionParseResult(Array.Empty<(string, string)>(), new[] { $"The file couldn't be read: {ex.Message}" });
            }

            return Parse(text).WithText(text);
        }

        private static string Describe(JsonValueKind kind) => kind switch
        {
            JsonValueKind.Null => "null",
            JsonValueKind.Object => "an object",
            JsonValueKind.Array => "an array",
            _ => kind.ToString()
        };
    }
}
