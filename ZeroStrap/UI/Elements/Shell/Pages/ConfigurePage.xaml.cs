using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using ZeroStrap.UI.Elements.Settings.Pages;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    public partial class ConfigurePage : Page
    {
        public ConfigurePage(ConfigureTab initialTab = ConfigureTab.FastFlags)
        {
            InitializeComponent();
            SelectTab(initialTab);
            Loaded += (_, _) => RefreshStats();
        }

        public void SelectTab(ConfigureTab tab)
        {
            ConfigureTabs.SelectedItem = tab switch
            {
                ConfigureTab.Mods => TabMods,
                ConfigureTab.Integrations => TabIntegrations,
                ConfigureTab.Shortcuts => TabShortcuts,
                ConfigureTab.Behaviour => TabBehaviour,
                _ => TabFastFlags
            };
        }

        private void ConfigureTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectionChanged bubbles up from combo boxes inside the hosted pages; only react to the tabs.
            if (!ReferenceEquals(e.OriginalSource, ConfigureTabs) || !IsLoaded)
                return;

            if (ConfigureTabs.SelectedContent is UIElement content)
                ZeroStrap.UI.Utility.Motion.FadeRise(content, 0, 8, 300);
        }

        private void RefreshStats()
        {
            try
            {
                var all = App.FastFlags.Prop;
                var presetValues = FastFlagManager.PresetFlags.Values.ToHashSet(System.StringComparer.Ordinal);

                StatFlagsCount.Text = all.Count.ToString();
                StatPresetsCount.Text = all.Keys.Count(k => presetValues.Contains(k)).ToString();
            }
            catch
            {
                StatFlagsCount.Text = "-";
                StatPresetsCount.Text = "-";
            }

            try
            {
                StatIntegrationsCount.Text = App.Settings.Prop.CustomIntegrations?.Count.ToString() ?? "0";
            }
            catch
            {
                StatIntegrationsCount.Text = "-";
            }

            try
            {
                var modsDir = Paths.Modifications;
                if (Directory.Exists(modsDir))
                    StatModsCount.Text = Directory.EnumerateFiles(modsDir, "*", SearchOption.AllDirectories)
                        .Count(f => !f.Contains(@"\ClientSettings\", System.StringComparison.OrdinalIgnoreCase)) // Fast Flags live here, not mods
                        .ToString();
                else
                    StatModsCount.Text = "0";
            }
            catch
            {
                StatModsCount.Text = "-";
            }
        }

        private static void NavigateOnce(object sender, Page page)
        {
            if (sender is Frame { Content: null } frame)
                frame.Navigate(page);
        }

        private void FastFlagsHost_Loaded(object sender, RoutedEventArgs e) => NavigateOnce(sender, new FastFlagsPage());
        private void ModsHost_Loaded(object sender, RoutedEventArgs e) => NavigateOnce(sender, new ModsPage());
        private void IntegrationsHost_Loaded(object sender, RoutedEventArgs e) => NavigateOnce(sender, new IntegrationsPage());
        private void ShortcutsHost_Loaded(object sender, RoutedEventArgs e) => NavigateOnce(sender, new ShortcutsPage());
        private void BehaviourHost_Loaded(object sender, RoutedEventArgs e) => NavigateOnce(sender, new BehaviourPage());
    }
}
