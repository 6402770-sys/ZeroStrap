using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    public partial class SystemPage : Page
    {
        public SystemPage()
        {
            InitializeComponent();
            Loaded += (_, _) => Refresh();
        }

        private void Refresh()
        {
            DiagRoblox.Text = App.IsPlayerInstalled
                ? $"Roblox Player: installed ({App.PlayerState.Prop.VersionGuid})"
                : "Roblox Player: not installed";

            DiagStudio.Text = App.IsStudioInstalled
                ? $"Roblox Studio: installed ({App.StudioState.Prop.VersionGuid})"
                : "Roblox Studio: not installed";

            DiagConfig.Text = $"Configuration path: {Paths.Base}";
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

        private void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = Paths.Logs;
                if (Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("SystemPage::OpenLogs_Click", ex);
            }
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = Paths.Logs;
                if (!Directory.Exists(dir))
                    return;

                var newest = new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (newest is null)
                    return;

                Clipboard.SetText(File.ReadAllText(newest.FullName));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("SystemPage::CopyLog_Click", ex);
            }
        }

        private void OpenInstall_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = Paths.Base, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("SystemPage::OpenInstall_Click", ex);
            }
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var release = await App.GetLatestRelease();
                var msg = release?.TagName is null
                    ? "Could not reach the update server."
                    : $"Latest release: {release.TagName}";
                Frontend.ShowMessageBox(msg, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("SystemPage::CheckUpdates_Click", ex);
            }
        }

        private static string BackupsRoot => Path.Combine(Paths.Base, "Backups");

        private void ExportConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(BackupsRoot);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var dir = Path.Combine(BackupsRoot, $"ZeroStrap-{stamp}");
                Directory.CreateDirectory(dir);

                // Settings and state JSONs
                CopyIfExists(Path.Combine(Paths.Base, "Settings.json"), Path.Combine(dir, "Settings.json"));
                CopyIfExists(Path.Combine(Paths.Base, "State.json"), Path.Combine(dir, "State.json"));

                // Fast Flags JSON (lives inside the Modifications tree)
                var flagsPath = App.FastFlags.FileLocation;
                if (File.Exists(flagsPath))
                {
                    var dest = Path.Combine(dir, "ClientAppSettings.json");
                    File.Copy(flagsPath, dest, overwrite: true);
                }

                Frontend.ShowMessageBox(
                    $"Backup saved to:\n{dir}",
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("SystemPage::ExportConfig_Click", ex);
                Frontend.ShowMessageBox($"Backup failed: {ex.Message}", MessageBoxImage.Error);
            }
        }

        private void OpenBackups_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(BackupsRoot);
                Process.Start(new ProcessStartInfo { FileName = BackupsRoot, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("SystemPage::OpenBackups_Click", ex);
            }
        }

        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            var confirm = Frontend.ShowMessageBox(
                "Uninstall ZeroStrap now?\n\nYour saved Fast Flags, settings and mods will be kept unless you clear them separately.",
                MessageBoxImage.Warning,
                MessageBoxButton.YesNo);

            if (confirm != MessageBoxResult.Yes)
                return;

            try
            {
                ZeroStrap.Installer.DoUninstall(keepData: true);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("SystemPage::Uninstall_Click", ex);
                Frontend.ShowMessageBox($"Uninstall failed: {ex.Message}", MessageBoxImage.Error);
            }
        }

        private static void CopyIfExists(string from, string to)
        {
            if (File.Exists(from))
                File.Copy(from, to, overwrite: true);
        }
    }
}
