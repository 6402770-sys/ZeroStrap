using System;
using System.Windows;
using System.Windows.Controls;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    public partial class PlayPage : Page
    {
        public PlayPage()
        {
            InitializeComponent();
            Loaded += PlayPage_Loaded;
        }

        private void PlayPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                ConfirmLaunchToggle.IsChecked = App.Settings.Prop.ConfirmLaunches;
            }
            catch { }
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LaunchHandler.LaunchRoblox(LaunchMode.Player);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("PlayPage::LaunchButton_Click", ex);
            }
        }
    }
}
