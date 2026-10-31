using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

using ZeroStrap.UI.ViewModels.About;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    /// <summary>Identity, credits, build information, licences and translations.</summary>
    public partial class AboutPage : Page
    {
        private const double StackBelowWidth = 760;

        public AboutPage()
        {
            DataContext = new AboutViewModel();
            InitializeComponent();
        }

        private void Link_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string url } && !string.IsNullOrEmpty(url))
                Utilities.ShellExecute(url);
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Utilities.ShellExecute(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        /// <summary>Two equal credit cards side by side; stacked once the content gets narrow.</summary>
        private void CreditsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool stacked = e.NewSize.Width < StackBelowWidth;

            Grid.SetColumn(AiCard, stacked ? 0 : 1);
            Grid.SetRow(AiCard, stacked ? 1 : 0);
            Grid.SetColumnSpan(HumanCard, stacked ? 2 : 1);
            Grid.SetColumnSpan(AiCard, stacked ? 2 : 1);
            HumanCard.Margin = stacked ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 6, 0);
            AiCard.Margin = stacked ? new Thickness(0) : new Thickness(6, 0, 0, 0);
            LicenseGrid.Columns = e.NewSize.Width < 560 ? 1 : 2;
        }
    }
}
