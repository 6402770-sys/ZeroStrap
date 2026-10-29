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
