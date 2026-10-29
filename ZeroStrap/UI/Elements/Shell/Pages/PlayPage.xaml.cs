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
                ConfirmLaunchToggle.Checked += ConfirmLaunchToggle_Changed;
                ConfirmLaunchToggle.Unchecked += ConfirmLaunchToggle_Changed;

                var guid = App.PlayerState?.Prop?.VersionGuid;
                VersionLine.Text = string.IsNullOrEmpty(guid)
                    ? "Roblox Player (not installed)."
                    : $"Roblox Player {guid}.";
            }
            catch { }
        }

        private void ConfirmLaunchToggle_Changed(object sender, RoutedEventArgs e)
        {
            App.Settings.Prop.ConfirmLaunches = ConfirmLaunchToggle.IsChecked == true;
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.Settings.Prop.ConfirmLaunches)
                {
                    var ok = Frontend.ShowMessageBox(
                        "Launch Roblox now?",
                        MessageBoxImage.Question,
                        MessageBoxButton.YesNo);

                    if (ok != MessageBoxResult.Yes)
                        return;
                }

                LaunchHandler.LaunchRoblox(LaunchMode.Player);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("PlayPage::LaunchButton_Click", ex);
            }
        }
    }
}
