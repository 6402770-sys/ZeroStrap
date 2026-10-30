namespace ZeroStrap.FastFlags
{
    /// <summary>
    /// Persisted state of the Fast Flag workspace (FastFlagWorkspace.json in the ZeroBootstrap folder).
    /// Kept apart from Settings.json and ClientAppSettings.json so it can never corrupt either.
    /// </summary>
    public sealed class FastFlagWorkspaceState
    {
        /// <summary>null until the user chooses; then defaults follow <see cref="FastFlagWorkspace.DefaultExperimentalMode"/>.</summary>
        public bool? ExperimentalMode { get; set; }

        public List<FlagPreset> Presets { get; set; } = new();

        public List<FlagBinding> Bindings { get; set; } = new();

        public bool BindingsEnabled { get; set; } = true;

        public CatalogUpdateMode CatalogUpdateMode { get; set; } = CatalogUpdateMode.Manual;

        public int CatalogCheckIntervalHours { get; set; } = 24;

        /// <summary>Values from before the last preset apply, for revert.</summary>
        public PresetApplySnapshot? LastApply { get; set; }
    }

    public sealed class FastFlagWorkspaceManager : JsonManager<FastFlagWorkspaceState>
    {
        public override string ClassName => "FastFlagWorkspace";

        public override string LOG_IDENT_CLASS => nameof(FastFlagWorkspaceManager);
    }

    /// <summary>
    /// Process-wide entry point used by the UI: persisted state, the catalog and the search index.
    /// Loaded lazily the first time the Fast Flag workspace is opened.
    /// </summary>
    public sealed class FastFlagWorkspace
    {
        private static FastFlagWorkspace? _instance;

        public static FastFlagWorkspace Instance => _instance ??= new FastFlagWorkspace();

        /// <summary>Upstream only showed the raw editor when Studio was installed (Studio ignores the Player allowlist).</summary>
        public static bool DefaultExperimentalMode => App.IsStudioInstalled;

        public FastFlagWorkspaceManager State { get; } = new();

        public FlagCatalogUpdater Updater { get; }

        public FlagCatalog Catalog { get; private set; } = FlagCatalog.Empty;

        public string? CatalogProblem { get; private set; }

        public CatalogMetadata? CatalogMetadata { get; private set; }

        public IFlagStore Store { get; } = new FastFlagManagerStore(App.FastFlags);

        public event EventHandler? CatalogChanged;

        /// <summary>Raised after any change to the pending flag configuration made through the workspace.</summary>
        public event EventHandler? FlagsChanged;

        public bool ExperimentalMode
        {
            get => State.Prop.ExperimentalMode ?? DefaultExperimentalMode;
            set
            {
                State.Prop.ExperimentalMode = value;
                State.Save();
            }
        }

        private FastFlagWorkspace()
        {
            // alertFailure: true makes JsonManager keep a .bak of a corrupt file before resetting it
            State.Load(true);
            Updater = new FlagCatalogUpdater(App.HttpClient, Path.Combine(Paths.Base, "FastFlagCatalog"));
            ReloadCatalog();
        }

        public FlagEditService CreateEditor() => new(Store, ExperimentalMode);

        public FlagSearchIndex BuildIndex() => new(Catalog, Store.Snapshot());

        public void ReloadCatalog()
        {
            Catalog = Updater.LoadCatalog(out string? problem);
            CatalogProblem = problem;
            CatalogMetadata = Updater.LoadMetadata();
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }

        public void NotifyFlagsChanged() => FlagsChanged?.Invoke(this, EventArgs.Empty);

        public async Task<CatalogUpdateResult> UpdateCatalogAsync(CancellationToken token = default)
        {
            var result = await Updater.UpdateAsync(token);

            // metadata (last check) changes on every outcome; the catalog only on Updated
            ReloadCatalog();

            return result;
        }

        /// <summary>Runs an automatic check if the user enabled it and the interval elapsed. Never throws.</summary>
        public async Task<CatalogUpdateResult?> RunAutomaticCheckIfDueAsync()
        {
            var prop = State.Prop;

            if (prop.CatalogUpdateMode != CatalogUpdateMode.Automatic || Updater.IsRunning)
                return null;

            if (!FlagCatalogUpdater.IsDue(CatalogMetadata, TimeSpan.FromHours(prop.CatalogCheckIntervalHours), DateTime.UtcNow))
                return null;

            try
            {
                return await UpdateCatalogAsync();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("FastFlagWorkspace::RunAutomaticCheckIfDueAsync", ex);
                return null;
            }
        }
    }
}
