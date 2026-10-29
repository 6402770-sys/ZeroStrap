using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    public partial class OverviewPage : Page
    {
        public event EventHandler<ShellSection>? NavigationRequested;

        public OverviewPage()
        {
            InitializeComponent();
            Loaded += (_, _) => Populate();
        }

        private void Populate()
        {
            var hour = DateTime.Now.Hour;
            GreetingText.Text = hour switch
            {
                < 5 => "Still up.",
                < 12 => "Good morning.",
                < 18 => "Good afternoon.",
                _ => "Good evening."
            };
            GreetingDay.Text = DateTime.Now.ToString("dddd, MMMM d").ToUpperInvariant();

            try
            {
                var guid = App.PlayerState?.Prop?.VersionGuid;
                VersionText.Text = string.IsNullOrEmpty(guid) ? "Version unknown" : $"Version {guid}";
                InfoRobloxVersion.Text = string.IsNullOrEmpty(guid) ? "unknown" : guid;
            }
            catch
            {
                VersionText.Text = "Version unknown";
            }

            try
            {
                InstallPathText.Text = Paths.Base;
                InfoInstallPath.Text = Paths.Base;
            }
            catch { }

            try
            {
                InfoLauncherVersion.Text = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
            }
            catch { }

            InfoPlatform.Text = Environment.Is64BitOperatingSystem ? "Windows (x64)" : "Windows";

            ActiveProfileText.Text = "Default";
            LaunchOptionsText.Text = "No custom options";

            InstallationStatusText.Text = App.IsPlayerInstalled ? "Up to date" : "Not installed";
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LaunchHandler.LaunchRoblox(LaunchMode.Player);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverviewPage::LaunchButton_Click", ex);
            }
        }

        private void Tile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: string tag })
                return;

            if (Enum.TryParse<ShellSection>(tag, out var section))
                NavigationRequested?.Invoke(this, section);
        }
    }

}
