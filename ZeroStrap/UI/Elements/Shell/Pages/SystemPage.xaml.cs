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
            SetDiag(DiagRoblox, DiagRobloxDot, App.IsPlayerInstalled,
                App.IsPlayerInstalled ? App.PlayerState.Prop.VersionGuid : "Not installed — installs on first launch");

            SetDiag(DiagStudio, DiagStudioDot, App.IsStudioInstalled,
                App.IsStudioInstalled ? App.StudioState.Prop.VersionGuid : "Not installed", idleWhenMissing: true);

            SetDiag(DiagConfig, DiagConfigDot, App.Settings.Loaded, Paths.Base);
            DiagConfig.ToolTip = Paths.Base;

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Paths.Base)!);
                double freeGb = drive.AvailableFreeSpace / 1024d / 1024 / 1024;
                SetDiag(DiagDisk, DiagDiskDot, freeGb >= 5, $"{freeGb:0.0} GB free on {drive.Name.TrimEnd('\\')}");
            }
            catch
            {
                SetDiag(DiagDisk, DiagDiskDot, true, "Unknown");
            }

            try
            {
                int logs = Directory.Exists(Paths.Logs) ? Directory.GetFiles(Paths.Logs, "*.log").Length : 0;
                LogsDescription.Text = $"{logs} log file{(logs == 1 ? "" : "s")} · one per session.";

                int backups = Directory.Exists(BackupsRoot) ? Directory.GetDirectories(BackupsRoot).Length : 0;
                BackupsDescription.Text = backups == 0
                    ? "Snapshot of Fast Flags, settings and state. No backups yet."
                    : $"Snapshot of Fast Flags, settings and state. {backups} saved.";
            }
            catch { }

            UpdatesDescription.Text = $"This build: v{App.Version}. Compares against the latest GitHub release.";
        }

        private void SetDiag(TextBlock text, System.Windows.Shapes.Ellipse dot, bool ok, string detail, bool idleWhenMissing = false)
        {
            text.Text = detail;
            dot.Fill = (System.Windows.Media.Brush)FindResource(ok ? "ZeroStatusHealthyBrush" : idleWhenMissing ? "ZeroStatusIdleBrush" : "ZeroStatusWarningBrush");
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Refresh();

            if (ZeroStrap.UI.Utility.Motion.Enabled && RefreshGlyph.RenderTransform is System.Windows.Media.RotateTransform spin)
            {
                spin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(560))
                    { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut } });
            }
        }

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
                    : $"Latest release: {release.TagName}\nThis build: v{App.Version}";
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

                ZeroStrap.UI.Utility.ActivityLog.Record(ZeroStrap.UI.Utility.ActivityType.Backup, "Configuration backed up", Path.GetFileName(dir));
                Refresh();

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
