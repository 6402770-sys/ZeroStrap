namespace ZeroStrap.FastFlags
{
    /// <summary>
    /// Preset exchange format (version 1). UTF-8 JSON:
    /// <code>
    /// {
    ///   "format": "zerobootstrap.fastflag-presets",
    ///   "version": 1,
    ///   "presets": [
    ///     { "name": "Low graphics", "flags": { "DFIntTextureQualityOverride": "0", "DFFlagTextureQualityOverrideEnabled": "True" } }
    ///   ]
    /// }
    /// </code>
    /// Rules: "format" and "version" must match; every preset needs a non-empty name; every flag must
    /// pass <see cref="FastFlagRules.Validate"/>; flag values may be JSON strings, numbers or booleans
    /// (converted to their string form). Imports are all-or-nothing: one invalid entry rejects the file.
    /// </summary>
    public static class PresetFile
    {
        public const string Format = "zerobootstrap.fastflag-presets";

        public const int Version = 1;

        public const int MaxPresets = 500;

        private sealed class FileModel
        {
            [JsonPropertyName("format")]
            public string? Format { get; set; }

            [JsonPropertyName("version")]
            public int Version { get; set; }

            [JsonPropertyName("presets")]
            public List<PresetModel>? Presets { get; set; }
        }

        private sealed class PresetModel
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("flags")]
            public Dictionary<string, JsonElement>? Flags { get; set; }
        }

        public static string Export(IEnumerable<FlagPreset> presets)
        {
            var model = new
            {
                format = Format,
                version = Version,
                presets = presets.Select(x => new { name = x.Name, flags = x.Flags.OrderBy(f => f.Key, StringComparer.Ordinal).ToDictionary(f => f.Key, f => f.Value) })
            };

            return JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
        }

        public static IReadOnlyList<FlagPreset> Import(string json, out IReadOnlyList<string> errors)
        {
            var problems = new List<string>();
            errors = problems;

            FileModel? model;

            try
            {
                model = JsonSerializer.Deserialize<FileModel>(json, new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
            }
            catch (JsonException ex)
            {
                problems.Add(String.Format(Strings.FastFlags_PresetFile_InvalidJson, ex.Message));
                return Array.Empty<FlagPreset>();
            }

            if (model is null || model.Format != Format)
            {
                problems.Add(String.Format(Strings.FastFlags_PresetFile_WrongFormat, Format));
                return Array.Empty<FlagPreset>();
            }

            if (model.Version != Version)
            {
                problems.Add(String.Format(Strings.FastFlags_PresetFile_UnsupportedVersion, model.Version, Version));
                return Array.Empty<FlagPreset>();
            }

            if (model.Presets is null || model.Presets.Count == 0)
            {
                problems.Add(Strings.FastFlags_PresetFile_NoPresets);
                return Array.Empty<FlagPreset>();
            }

            if (model.Presets.Count > MaxPresets)
            {
                problems.Add(String.Format(Strings.FastFlags_PresetFile_TooMany, model.Presets.Count, MaxPresets));
                return Array.Empty<FlagPreset>();
            }

            var result = new List<FlagPreset>();

            for (int i = 0; i < model.Presets.Count; i++)
            {
                var entry = model.Presets[i];
                string label = String.IsNullOrWhiteSpace(entry.Name) ? $"#{i + 1}" : entry.Name.Trim();

                if (String.IsNullOrWhiteSpace(entry.Name))
                    problems.Add(String.Format(Strings.FastFlags_PresetFile_NoName, label));

                var preset = new FlagPreset { Name = label };

                foreach (var pair in entry.Flags ?? new())
                {
                    string? value = pair.Value.ValueKind switch
                    {
                        JsonValueKind.String => pair.Value.GetString(),
                        JsonValueKind.Number => pair.Value.GetRawText(),
                        JsonValueKind.True => "True",
                        JsonValueKind.False => "False",
                        _ => null
                    };

                    if (value is null)
                    {
                        problems.Add(String.Format(Strings.FastFlags_PresetFile_UnsupportedValue, label, pair.Key));
                        continue;
                    }

                    var validation = FastFlagRules.Validate(pair.Key, value);

                    if (!validation.IsValid)
                    {
                        problems.Add($"{label}: {validation.Error}");
                        continue;
                    }

                    preset.Flags[pair.Key] = value;
                }

                result.Add(preset);
            }

            return problems.Count == 0 ? result : Array.Empty<FlagPreset>();
        }
    }
}
