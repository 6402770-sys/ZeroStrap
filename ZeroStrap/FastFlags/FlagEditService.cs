namespace ZeroStrap.FastFlags
{
    /// <summary>
    /// The pending Fast Flag configuration. In the app this is <see cref="FastFlagManager"/>:
    /// changes stay pending until the user presses Save, and reach Roblox on its next launch.
    /// </summary>
    public interface IFlagStore
    {
        string? Get(string name);

        /// <summary>null removes the flag.</summary>
        void Set(string name, string? value);

        IReadOnlyDictionary<string, string> Snapshot();
    }

    public sealed class FastFlagManagerStore : IFlagStore
    {
        private readonly FastFlagManager _manager;

        public FastFlagManagerStore(FastFlagManager manager) => _manager = manager;

        public string? Get(string name) => _manager.GetValue(name);

        public void Set(string name, string? value) => _manager.SetValue(name, value);

        public IReadOnlyDictionary<string, string> Snapshot() =>
            _manager.Prop.Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value.ToString()!, StringComparer.Ordinal);
    }

    public sealed class DictionaryFlagStore : IFlagStore
    {
        public Dictionary<string, string> Flags { get; } = new(StringComparer.Ordinal);

        public string? Get(string name) => Flags.TryGetValue(name, out var value) ? value : null;

        public void Set(string name, string? value)
        {
            if (value is null)
                Flags.Remove(name);
            else
                Flags[name] = value;
        }

        public IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(Flags, StringComparer.Ordinal);
    }

    public enum FlagEditStatus
    {
        Ok,
        Invalid,
        Duplicate,
        NotFound,
        BlockedExperimental
    }

    public readonly record struct FlagEditResult(FlagEditStatus Status, string? Message = null)
    {
        public bool Succeeded => Status == FlagEditStatus.Ok;
    }

    /// <summary>
    /// Add / edit / rename / remove with validation. When experimental mode is off, only flags on
    /// Roblox's Player allowlist may be added or renamed into; existing flags are never touched.
    /// </summary>
    public sealed class FlagEditService
    {
        private readonly IFlagStore _store;

        public bool ExperimentalMode { get; set; }

        public FlagEditService(IFlagStore store, bool experimentalMode)
        {
            _store = store;
            ExperimentalMode = experimentalMode;
        }

        public FlagEditResult Add(string name, string value)
        {
            name = name.Trim();

            var result = FastFlagRules.Validate(name, value);

            if (!result.IsValid)
                return new(FlagEditStatus.Invalid, result.Error);

            if (_store.Get(name) is not null)
                return new(FlagEditStatus.Duplicate, Strings.Menu_FastFlagEditor_AlreadyExists);

            if (!ExperimentalMode && RobloxFlagAllowlist.GetEffect(name) == FlagEffect.Experimental)
                return new(FlagEditStatus.BlockedExperimental);

            _store.Set(name, value);
            return new(FlagEditStatus.Ok);
        }

        /// <summary>Changes the value of an existing flag. Allowed for experimental flags already present.</summary>
        public FlagEditResult SetValue(string name, string value)
        {
            if (_store.Get(name) is null)
                return new(FlagEditStatus.NotFound);

            var result = FastFlagRules.Validate(name, value);

            if (!result.IsValid)
                return new(FlagEditStatus.Invalid, result.Error);

            _store.Set(name, value);
            return new(FlagEditStatus.Ok);
        }

        /// <summary>Renames a flag, keeping its value. Atomic: either both steps happen or neither.</summary>
        public FlagEditResult Rename(string oldName, string newName)
        {
            newName = newName.Trim();

            if (oldName == newName)
                return new(FlagEditStatus.Ok);

            string? value = _store.Get(oldName);

            if (value is null)
                return new(FlagEditStatus.NotFound);

            var result = FastFlagRules.Validate(newName, value);

            if (!result.IsValid)
                return new(FlagEditStatus.Invalid, result.Error);

            if (_store.Get(newName) is not null)
                return new(FlagEditStatus.Duplicate, Strings.Menu_FastFlagEditor_AlreadyExists);

            if (!ExperimentalMode && RobloxFlagAllowlist.GetEffect(newName) == FlagEffect.Experimental)
                return new(FlagEditStatus.BlockedExperimental);

            _store.Set(oldName, null);
            _store.Set(newName, value);
            return new(FlagEditStatus.Ok);
        }

        public FlagEditResult Remove(string name)
        {
            if (_store.Get(name) is null)
                return new(FlagEditStatus.NotFound);

            _store.Set(name, null);
            return new(FlagEditStatus.Ok);
        }
    }
}
