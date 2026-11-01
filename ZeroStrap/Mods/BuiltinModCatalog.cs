using System.Security.Cryptography;

namespace ZeroStrap.Mods
{
    /// <summary>What the preset's existing setting must be set to in order to switch the preset off.</summary>
    public enum PresetOffValue
    {
        /// <summary>BoolBaseTask.NewState = false.</summary>
        False,

        /// <summary>EnumBaseTask.NewState = default(T).</summary>
        EnumDefault,

        /// <summary>StringBaseTask.NewState = "".</summary>
        EmptyString
    }

    /// <summary>One file (or file pattern) a built-in preset writes into Modifications.</summary>
    public sealed class PresetPathRule
    {
        /// <summary>Exact relative path, or the folder prefix when <see cref="Suffix"/> is set.</summary>
        public string Path { get; init; } = "";

        /// <summary>When set, the rule matches any file directly under <see cref="Path"/> that ends with this suffix.</summary>
        public string? Suffix { get; init; }

        /// <summary>MD5 hashes (lowercase hex) the file may have; null means any content.</summary>
        public IReadOnlySet<string>? AcceptedMd5 { get; init; }

        /// <summary>Whether a match proves the preset is switched on.</summary>
        public bool Activates { get; init; } = true;

        /// <summary>Whether switching the preset off deletes a matching file.</summary>
        public bool DeleteOnDisable { get; init; } = true;

        public bool PathMatches(string relativePath)
        {
            string p = relativePath.Replace('/', '\\');

            if (Suffix is null)
                return p.Equals(Path, StringComparison.OrdinalIgnoreCase);

            string prefix = Path.TrimEnd('\\') + "\\";

            return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && p.IndexOf('\\', prefix.Length) < 0
                && p.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class BuiltinPreset
    {
        public const string IdPrefix = "builtin:";

        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";

        /// <summary>Name of the SettingTask in ModsViewModel (e.g. "CursorType") that the UI flips to switch it off.</summary>
        public string TaskName { get; init; } = "";

        public PresetOffValue OffValue { get; init; }
        public IReadOnlyList<PresetPathRule> Rules { get; init; } = Array.Empty<PresetPathRule>();

        public static bool IsBuiltinId(string? id) => id is not null && id.StartsWith(IdPrefix, StringComparison.Ordinal);
    }

    public sealed class ActiveBuiltinPreset
    {
        public BuiltinPreset Preset { get; init; } = null!;

        /// <summary>Files currently in Modifications that belong to the preset.</summary>
        public IReadOnlyList<string> OwnedPaths { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Describes the five built-in presets and detects which are on by comparing Modifications against the same
    /// embedded resources the existing preset tasks use. Only the "switch off" operation writes, and only
    /// to files whose content matches the preset.
    /// </summary>
    public sealed class BuiltinModCatalog
    {
        private readonly string _modificationsRoot;
        private readonly Func<string, string?> _resourceMd5;
        private readonly string[] _emojiHashes;
        private readonly Action<string>? _log;

        private readonly Lazy<IReadOnlyList<BuiltinPreset>> _presets;

        public IReadOnlyList<BuiltinPreset> Presets => _presets.Value;

        public BuiltinModCatalog(string modificationsRoot, Func<string, string?> resourceMd5, IEnumerable<string> emojiMd5Hashes, Action<string>? log = null)
        {
            _modificationsRoot = modificationsRoot;
            _resourceMd5 = resourceMd5;
            _emojiHashes = emojiMd5Hashes.Select(h => h.ToLowerInvariant()).ToArray();
            _log = log;
            _presets = new Lazy<IReadOnlyList<BuiltinPreset>>(BuildPresets);
        }

        /// <summary>Catalog over Paths.Modifications using the app's embedded resources. Only valid after Paths.Initialize.</summary>
        public static BuiltinModCatalog CreateDefault(Action<string>? log = null) => new(
            Paths.Modifications,
            ResourceMd5,
            EmojiTypeEx.Hashes.Values,
            log);

        /// <summary>Catalog over an arbitrary folder using the app's embedded resources (dry runs and tooling).</summary>
        public static BuiltinModCatalog ForDirectory(string modificationsRoot, Action<string>? log = null) => new(
            modificationsRoot,
            ResourceMd5,
            EmojiTypeEx.Hashes.Values,
            log);

        private static string? ResourceMd5(string resource)
        {
            try
            {
                using var stream = Resource.GetStream(resource);
                using var md5 = MD5.Create();
                return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }

        private HashSet<string> Accept(params string[] resources)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string resource in resources)
            {
                string? hash = _resourceMd5(resource);

                if (hash is not null)
                    set.Add(hash);
            }

            return set;
        }

        private IReadOnlyList<BuiltinPreset> BuildPresets()
        {
            const string cursors = @"content\textures\Cursors\KeyboardMouse\";

            return new List<BuiltinPreset>
            {
                new()
                {
                    Id = "builtin:cursors",
                    DisplayName = "Classic cursors",
                    TaskName = "CursorType",
                    OffValue = PresetOffValue.EnumDefault,
                    Rules = new[]
                    {
                        new PresetPathRule { Path = cursors + "ArrowCursor.png", AcceptedMd5 = Accept("Cursor.From2006.ArrowCursor.png", "Cursor.From2013.ArrowCursor.png") },
                        new PresetPathRule { Path = cursors + "ArrowFarCursor.png", AcceptedMd5 = Accept("Cursor.From2006.ArrowFarCursor.png", "Cursor.From2013.ArrowFarCursor.png") }
                    }
                },
                new()
                {
                    Id = "builtin:old-sounds",
                    DisplayName = "Old character sounds",
                    TaskName = "OldCharacterSounds",
                    OffValue = PresetOffValue.False,
                    Rules = new[]
                    {
                        new PresetPathRule { Path = @"content\sounds\action_footsteps_plastic.mp3", AcceptedMd5 = Accept("Sounds.OldWalk.mp3") },
                        new PresetPathRule { Path = @"content\sounds\action_jump.mp3", AcceptedMd5 = Accept("Sounds.OldJump.mp3") },
                        new PresetPathRule { Path = @"content\sounds\action_get_up.mp3", AcceptedMd5 = Accept("Sounds.OldGetUp.mp3") },
                        new PresetPathRule { Path = @"content\sounds\action_falling.mp3", AcceptedMd5 = Accept("Sounds.Empty.mp3") },
                        new PresetPathRule { Path = @"content\sounds\action_jump_land.mp3", AcceptedMd5 = Accept("Sounds.Empty.mp3") },
                        new PresetPathRule { Path = @"content\sounds\action_swim.mp3", AcceptedMd5 = Accept("Sounds.Empty.mp3") },
                        new PresetPathRule { Path = @"content\sounds\impact_water.mp3", AcceptedMd5 = Accept("Sounds.Empty.mp3") }
                    }
                },
                new()
                {
                    Id = "builtin:old-avatar-background",
                    DisplayName = "Old avatar background",
                    TaskName = "OldAvatarBackground",
                    OffValue = PresetOffValue.False,
                    Rules = new[]
                    {
                        new PresetPathRule { Path = @"ExtraContent\places\Mobile.rbxl", AcceptedMd5 = Accept("OldAvatarBackground.rbxl") }
                    }
                },
                new()
                {
                    Id = "builtin:emoji-font",
                    DisplayName = "Emoji font",
                    TaskName = "EmojiFont",
                    OffValue = PresetOffValue.EnumDefault,
                    Rules = new[]
                    {
                        new PresetPathRule
                        {
                            Path = @"content\fonts\TwemojiMozilla.ttf",
                            AcceptedMd5 = new HashSet<string>(_emojiHashes, StringComparer.OrdinalIgnoreCase)
                        }
                    }
                },
                new()
                {
                    Id = "builtin:custom-font",
                    DisplayName = "Custom font",
                    TaskName = "TextFont",
                    OffValue = PresetOffValue.EmptyString,
                    Rules = new[]
                    {
                        new PresetPathRule { Path = @"content\fonts\CustomFont.ttf", AcceptedMd5 = null },
                        // regenerated by the bootstrapper at launch (and deleted again when the custom font is off)
                        new PresetPathRule { Path = @"content\fonts\families", Suffix = ".json", AcceptedMd5 = null, Activates = false, DeleteOnDisable = false }
                    }
                }
            };
        }

        public BuiltinPreset? Find(string id) => Presets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.Ordinal));

        private bool RuleMatchesFile(PresetPathRule rule, string relativePath)
        {
            if (!rule.PathMatches(relativePath))
                return false;

            string full = Path.Combine(_modificationsRoot, relativePath);

            try
            {
                if (!File.Exists(full))
                    return false;

                if (rule.AcceptedMd5 is null)
                    return true;

                return rule.AcceptedMd5.Contains(ModFileUtil.Md5OfFile(full));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Could not check preset file {relativePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>True when the file at <paramref name="relativePath"/> in Modifications is one a preset writes (matching content).</summary>
        public bool IsPresetOwned(string relativePath) =>
            Presets.Any(p => p.Rules.Any(r => RuleMatchesFile(r, relativePath)));

        private IEnumerable<string> EnumeratePresetCandidatePaths(BuiltinPreset preset)
        {
            foreach (var rule in preset.Rules)
            {
                if (rule.Suffix is null)
                {
                    yield return rule.Path;
                    continue;
                }

                string dir = Path.Combine(_modificationsRoot, rule.Path);

                if (!Directory.Exists(dir))
                    continue;

                foreach (string file in Directory.EnumerateFiles(dir, "*" + rule.Suffix, SearchOption.TopDirectoryOnly))
                    yield return rule.Path.TrimEnd('\\') + "\\" + Path.GetFileName(file);
            }
        }

        /// <summary>Presets that are currently switched on, with the Modifications files they own.</summary>
        public IReadOnlyList<ActiveBuiltinPreset> GetActive()
        {
            var result = new List<ActiveBuiltinPreset>();

            foreach (var preset in Presets)
            {
                var owned = ModPathRules.NewPathSet();
                bool active = false;

                foreach (string path in EnumeratePresetCandidatePaths(preset))
                {
                    foreach (var rule in preset.Rules)
                    {
                        if (!RuleMatchesFile(rule, path))
                            continue;

                        owned.Add(path);

                        if (rule.Activates)
                            active = true;
                    }
                }

                if (active)
                    result.Add(new ActiveBuiltinPreset { Preset = preset, OwnedPaths = owned.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList() });
            }

            return result;
        }

        /// <summary>Path to preset id for every file owned by an active preset.</summary>
        public Dictionary<string, string> GetActiveOwnersByPath()
        {
            var map = ModPathRules.NewPathMap<string>();

            foreach (var active in GetActive())
            {
                foreach (string path in active.OwnedPaths)
                    map.TryAdd(path, active.Preset.Id);
            }

            return map;
        }

        /// <summary>
        /// Switches a preset off right now: deletes only the files whose content matches the preset's own resources,
        /// the same outcome as flipping its setting off and saving. The setting in the UI is the caller's to update
        /// (see <see cref="BuiltinPreset.TaskName"/> and <see cref="BuiltinPreset.OffValue"/>).
        /// Returns the number of files deleted.
        /// </summary>
        public int DisableNow(string presetId)
        {
            var preset = Find(presetId) ?? throw new ModStudioException($"Unknown preset '{presetId}'.");
            int deleted = 0;

            foreach (string path in EnumeratePresetCandidatePaths(preset).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                foreach (var rule in preset.Rules)
                {
                    if (!rule.DeleteOnDisable || !RuleMatchesFile(rule, path))
                        continue;

                    string full = ModPathRules.CombineConfined(_modificationsRoot, path);

                    ModFileUtil.ClearReadOnly(full);
                    File.Delete(full);
                    ModFileUtil.PruneEmptyDirectories(Path.GetDirectoryName(full)!, _modificationsRoot);
                    deleted++;
                    break;
                }
            }

            _log?.Invoke($"Switched off preset {presetId} ({deleted} file(s) removed)");
            return deleted;
        }
    }
}
