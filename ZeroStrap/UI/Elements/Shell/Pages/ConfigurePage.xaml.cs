using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using ZeroStrap.UI.Elements.Settings.Pages;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    public partial class ConfigurePage : Page
    {
        public ConfigurePage()
        {
            InitializeComponent();
            Loaded += (_, _) => RefreshStats();
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
                    StatModsCount.Text = Directory.EnumerateFiles(modsDir, "*", SearchOption.AllDirectories).Count().ToString();
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

        private void OpenFlagEditor_Click(object sender, RoutedEventArgs e)
        {
            // Switch to the Fast Flags tab (the editor opens from inside it via its own flow).
            ConfigureTabs.SelectedIndex = 0;
        }
    }
}
