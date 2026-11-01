using System.Windows;
using System.Windows.Controls;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    /// <summary>Appearance section: the Theme Studio (interface themes and motion) and the launcher window settings.</summary>
    public partial class AppearancePage : Page
    {
        private Settings.Pages.AppearancePage? _launcherPage;

        public AppearancePage()
        {
            InitializeComponent();
        }

        /// <summary>Re-reads the theme state after it was changed from outside this page (command palette).</summary>
        public void Refresh() => Studio.Refresh();

        private void AppearanceHost_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Frame { Content: null } frame)
            {
                _launcherPage = new Settings.Pages.AppearancePage();
                frame.Navigate(_launcherPage);
            }
        }
    }
}
