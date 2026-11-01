namespace ZeroStrap.Mods
{
    /// <summary>Reads and atomically writes Mods\studio.json. An unreadable file is moved aside and defaults are used.</summary>
    public sealed class ModStudioStateStore
    {
        private readonly string _path;
        private readonly Action<string> _log;

        public string FilePath => _path;

        public ModStudioStateStore(string path, Action<string>? log = null)
        {
            _path = path;
            _log = log ?? (_ => { });
        }

        public bool Exists => File.Exists(_path);

        public ModStudioState Load()
        {
            if (!File.Exists(_path))
                return new ModStudioState();

            try
            {
                var state = JsonSerializer.Deserialize<ModStudioState>(File.ReadAllText(_path), ModFileUtil.JsonOptions)
                    ?? throw new JsonException("null document");

                state.Enabled ??= new();
                state.Priority ??= new();

                var choices = ModPathRules.NewPathMap<string>();

                foreach (var (path, owner) in state.FileChoices ?? new())
                {
                    if (ModPathRules.TryNormalize(path, out string normalized, out _) && !string.IsNullOrEmpty(owner))
                        choices[normalized] = owner;
                }

                state.FileChoices = choices;
                return state;
            }
            catch (Exception ex)
            {
                _log($"studio.json is unreadable ({ex.Message})");
                ModFileUtil.QuarantineCorrupt(_path, _log);
                return new ModStudioState();
            }
        }

        public void Save(ModStudioState state, Action? faultBeforeCommit = null)
        {
            ModFileUtil.WriteAllTextAtomic(_path, JsonSerializer.Serialize(state, ModFileUtil.JsonOptions), faultBeforeCommit);
        }
    }

    /// <summary>
    /// Reads and atomically writes Mods\overlay-index.json. If it is unreadable it is moved aside and an empty index
    /// is used, which is the safe direction: the studio then owns nothing and deletes nothing.
    /// </summary>
    public sealed class OverlayIndexStore
    {
        private readonly string _path;
        private readonly Action<string> _log;

        /// <summary>Test seam: throwing from here simulates a failure just before the index file is swapped.</summary>
        internal Action? FaultBeforeCommit { get; set; }

        public string FilePath => _path;

        public OverlayIndexStore(string path, Action<string>? log = null)
        {
            _path = path;
            _log = log ?? (_ => { });
        }

        public OverlayIndex Load()
        {
            var index = new OverlayIndex();

            if (!File.Exists(_path))
                return index;

            try
            {
                var loaded = JsonSerializer.Deserialize<OverlayIndex>(File.ReadAllText(_path), ModFileUtil.JsonOptions)
                    ?? throw new JsonException("null document");

                index.PriorityRevision = loaded.PriorityRevision;

                foreach (var (path, entry) in loaded.Files ?? new())
                {
                    if (entry is null || string.IsNullOrEmpty(entry.Owner) || string.IsNullOrEmpty(entry.Sha256))
                        continue;

                    if (!ModPathRules.TryNormalize(path, out string normalized, out string? error))
                    {
                        _log($"Ignoring invalid index entry '{path}' ({error})");
                        continue;
                    }

                    index.Files[normalized] = new OverlayIndexEntry { Owner = entry.Owner, Sha256 = entry.Sha256.ToLowerInvariant() };
                }

                return index;
            }
            catch (Exception ex)
            {
                _log($"overlay-index.json is unreadable ({ex.Message})");
                ModFileUtil.QuarantineCorrupt(_path, _log);
                return new OverlayIndex();
            }
        }

        public void Save(OverlayIndex index)
        {
            ModFileUtil.WriteAllTextAtomic(_path, JsonSerializer.Serialize(index, ModFileUtil.JsonOptions), FaultBeforeCommit);
        }
    }
}
