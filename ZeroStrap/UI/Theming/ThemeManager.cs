using System.Windows;
using System.Windows.Media;

using Wpf.Ui.Appearance;
using Wpf.Ui.Mvvm.Contracts;
using Wpf.Ui.Mvvm.Services;

using ZeroStrap.Models.Persistable;
using ZeroStrap.Models.Theming;

using LegacyTheme = ZeroStrap.Enums.Theme;

namespace ZeroStrap.UI.Theming
{
    /// <summary>
    /// Single authority for the interface theme: which one is active, persisting the choice, keeping the legacy
    /// <c>Settings.Theme</c> in sync for the bootstrapper dialogs, and applying it to the application resources.
    /// </summary>
    public static class ThemeManager
    {
        public const string ObsidianId = BuiltinThemes.ObsidianId;
        public const string LightId = BuiltinThemes.LightId;
        public const string SystemId = "system";
        public const string CustomPrefix = "custom:";

        private const int CustomThemeDictionaryIndex = 2; // App.xaml: CustomTheme dictionary

        private static readonly string[] StatusKeys =
        {
            "ZeroStatusHealthyBrush", "ZeroStatusWarningBrush", "ZeroStatusErrorBrush", "ZeroStatusInfoBrush", "ZeroStatusIdleBrush"
        };

        private static readonly IThemeService _themeService = new ThemeService();
        private static bool _storeWired;

        /// <summary>Raised after <see cref="SetActive"/> or <see cref="Refresh"/> has applied a theme.</summary>
        public static event Action<ThemeDefinition>? ThemeChanged;

        public static ThemeStore Store
        {
            get
            {
                var store = ThemeStore.Default;

                if (!_storeWired)
                {
                    _storeWired = true;

                    // Deleting the active custom theme falls back to Obsidian.
                    store.Deleted += id =>
                    {
                        if (ActiveId == CustomPrefix + id)
                            SetActive(ObsidianId);
                    };
                }

                return store;
            }
        }

        // ---------------------------------------------------------------- pure logic (unit tested)

        /// <summary>Maps the legacy Theme setting to an interface theme id.</summary>
        internal static string MigrateId(LegacyTheme legacy) => legacy switch
        {
            LegacyTheme.Dark => ObsidianId,
            LegacyTheme.Light => LightId,
            _ => SystemId
        };

        /// <summary>Fills <see cref="Settings.InterfaceThemeId"/> from <see cref="Settings.Theme"/> when it is unset.</summary>
        internal static bool EnsureMigrated(Settings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.InterfaceThemeId))
                return false;

            settings.InterfaceThemeId = MigrateId(settings.Theme);
            return true;
        }

        /// <summary>
        /// Resolves an id to a definition. "system" follows <paramref name="systemIsDark"/>; unknown or missing custom
        /// themes resolve to Obsidian and are logged. Never returns null.
        /// </summary>
        internal static ThemeDefinition Resolve(string? id, ThemeStore store, bool systemIsDark, Action<string>? log = null)
        {
            switch (id)
            {
                case ObsidianId:
                    return BuiltinThemes.Obsidian();
                case LightId:
                    return BuiltinThemes.Light();
                case SystemId:
                    return systemIsDark ? BuiltinThemes.Obsidian() : BuiltinThemes.Light();
            }

            if (id is not null && id.StartsWith(CustomPrefix, StringComparison.Ordinal))
            {
                var custom = store.Get(id[CustomPrefix.Length..]);
                if (custom is not null && custom.Kind == ThemeKind.Custom)
                    return custom;

                log?.Invoke($"Custom theme \"{id}\" was not found; using Obsidian.");
                return BuiltinThemes.Obsidian();
            }

            log?.Invoke($"Unknown theme id \"{id}\"; using Obsidian.");
            return BuiltinThemes.Obsidian();
        }

        /// <summary>Returns an id that is safe to store: known built-in, "system", or an existing custom theme.</summary>
        internal static string NormaliseId(string? id, ThemeStore store, Action<string>? log = null)
        {
            if (id is ObsidianId or LightId or SystemId)
                return id;

            if (id is not null && id.StartsWith(CustomPrefix, StringComparison.Ordinal))
            {
                var custom = store.Get(id[CustomPrefix.Length..]);
                if (custom is not null && custom.Kind == ThemeKind.Custom)
                    return id;
            }

            log?.Invoke($"Theme \"{id}\" is not available; using Obsidian.");
            return ObsidianId;
        }

        /// <summary>
        /// Stores the id and keeps the legacy <see cref="Settings.Theme"/> aligned:
        /// obsidian = Dark, light = Light, system = Default, custom = its base. Returns the stored id.
        /// </summary>
        internal static string ApplyToSettings(Settings settings, string? id, ThemeStore store, Action<string>? log = null)
        {
            string normalised = NormaliseId(id, store, log);

            settings.InterfaceThemeId = normalised;
            settings.Theme = normalised switch
            {
                ObsidianId => LegacyTheme.Dark,
                LightId => LegacyTheme.Light,
                SystemId => LegacyTheme.Default,
                _ => Resolve(normalised, store, false).Base == ThemeBase.Light ? LegacyTheme.Light : LegacyTheme.Dark
            };

            return normalised;
        }

        // ---------------------------------------------------------------- state

        public static string ActiveId
        {
            get
            {
                var settings = App.Settings.Prop;
                EnsureMigrated(settings);
                return settings.InterfaceThemeId!;
            }
        }

        public static ThemeDefinition ActiveDefinition =>
            Resolve(ActiveId, Store, LegacyTheme.Default.GetFinal() == LegacyTheme.Dark, Log);

        public static bool IsDark => ActiveDefinition.Base == ThemeBase.Dark;

        /// <summary>Name for the UI: "System" for the follow-Windows choice, otherwise the theme name.</summary>
        public static string ActiveDisplayName => ActiveId == SystemId ? "System" : ActiveDefinition.Name;

        public static string IdFor(ThemeDefinition theme) => theme.Kind == ThemeKind.Builtin ? theme.Id : CustomPrefix + theme.Id;

        // ---------------------------------------------------------------- actions

        /// <summary>Persists and applies a theme. Accepts "obsidian", "light", "system" or "custom:&lt;id&gt;".</summary>
        public static void SetActive(string id)
        {
            string stored = ApplyToSettings(App.Settings.Prop, id, Store, Log);
            App.Settings.Save();

            Refresh();
            App.Logger.WriteLine("ThemeManager::SetActive", $"Active theme is now {stored}");
        }

        /// <summary>Re-applies the active theme (after it was edited) and notifies listeners.</summary>
        public static void Refresh()
        {
            Apply();
            ThemeChanged?.Invoke(ActiveDefinition);
        }

        /// <summary>
        /// Applies the active theme to the application resources. These are app-wide, so every open WpfUiWindow
        /// updates at once. Built-ins load the shipped dictionaries exactly as before.
        /// </summary>
        public static void Apply()
        {
            // keep the legacy Settings.Theme aligned even if InterfaceThemeId was changed outside SetActive (in memory; saved with the next settings save)
            ApplyToSettings(App.Settings.Prop, ActiveId, Store, Log);

            var definition = ActiveDefinition;

            try
            {
                ApplyDefinition(definition);
            }
            catch (Exception ex) when (definition.Kind == ThemeKind.Custom)
            {
                App.Logger.WriteException("ThemeManager::Apply", ex);
                ApplyDefinition(BuiltinThemes.Obsidian());
            }
        }

        private static void ApplyDefinition(ThemeDefinition definition)
        {
            bool dark = definition.Base == ThemeBase.Dark;
            var resources = Application.Current.Resources;

            _themeService.SetTheme(dark ? ThemeType.Dark : ThemeType.Light);

            ResourceDictionary dictionary;

            if (definition.Kind == ThemeKind.Builtin)
            {
                ApplyMonochromeAccent(dark);

                dictionary = new ResourceDictionary { Source = new Uri($"pack://application:,,,/UI/Style/{(dark ? "Dark" : "Light")}.xaml") };

                // Status brushes fall back to the defaults in ZeroControls.xaml.
                foreach (var key in StatusKeys)
                    resources.Remove(key);
            }
            else
            {
                var ramp = ThemePalette.AccentRamp(ColorMath.Parse(definition.Colors.Accent), definition.Base);
                Accent.Apply(ThemePalette.ToColor(ramp.System), ThemePalette.ToColor(ramp.Primary), ThemePalette.ToColor(ramp.Secondary), ThemePalette.ToColor(ramp.Tertiary));

                dictionary = ThemePalette.BuildResources(definition);

                foreach (var (key, colour) in ThemePalette.StatusColors(definition))
                    resources[key] = new SolidColorBrush(ThemePalette.ToColor(colour));
            }

            resources.MergedDictionaries[CustomThemeDictionaryIndex] = dictionary;
        }

        private static void ApplyMonochromeAccent(bool dark)
        {
            static Color Grey(byte v) => Color.FromRgb(v, v, v);

            // ZeroStrap: explicit accent ramp. IThemeService.SetAccent always derives the ramp with
            // light-theme maths (darker steps), which turns #D4D4D4 into a muddy #BBBBBB on dark.
            // Order: system, primary, secondary (most used: primary buttons, toggles, nav pill), tertiary (hover).
            if (dark)
                Accent.Apply(Grey(0xD4), Grey(0xDC), Grey(0xE8), Grey(0xF4));
            else
                Accent.Apply(Grey(0x2A), Grey(0x26), Grey(0x1E), Grey(0x3A));
        }

        private static void Log(string message) => App.Logger.WriteLine("ThemeManager", message);
    }
}
