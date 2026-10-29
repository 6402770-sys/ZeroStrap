using System.Windows;
using System.Windows.Controls;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    public partial class AppearancePage : Page
    {
        public AppearancePage()
        {
            InitializeComponent();
        }

        private void AppearanceHost_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Frame { Content: null } frame)
                frame.Navigate(new Settings.Pages.AppearancePage());
        }
    }
}
