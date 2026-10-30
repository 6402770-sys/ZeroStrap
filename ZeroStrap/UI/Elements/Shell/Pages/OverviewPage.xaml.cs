using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Wpf.Ui.Common;

using ZeroStrap.UI.Elements.Base;
using ZeroStrap.UI.Utility;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    public partial class OverviewPage : Page
    {
        private const long LowDiskBytes = 5L * 1024 * 1024 * 1024;

        public event EventHandler<ShellTarget>? NavigationRequested;

        private bool _populating;

        private record ActivityRow(SymbolRegular Icon, string Title, string Detail, string When);

        public OverviewPage()
        {
            InitializeComponent();
            Loaded += (_, _) => Populate();
        }

        private void Populate()
        {
            _populating = true;

            var now = DateTime.Now;
            GreetingText.Text = now.Hour switch
            {
                < 5 => "Still up.",
                < 12 => "Good morning.",
                < 18 => "Good afternoon.",
                _ => "Good evening."
            };
            GreetingDay.Text = Track(now.ToString("dddd, MMMM d", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant());

            PopulateClient();
            PopulateSummaries();
            PopulateStatus();
            PopulateTheme();
            PopulateActivity();

            _populating = false;
        }

        /// <summary>Letter-spaces an eyebrow label (WPF has no tracking property).</summary>
        private static string Track(string text) => string.Join(" ", text.ToCharArray());

        private void PopulateClient()
        {
            bool installed = App.IsPlayerInstalled;
            string? fileVersion = installed ? GetRobloxFileVersion() : null;
            string guid = App.PlayerState.Prop.VersionGuid;

            if (!installed)
            {
                VersionRun.Text = "Not installed";
                VersionNoteRun.Text = " · installs on first launch";
            }
            else
            {
                VersionRun.Text = $"Version {fileVersion ?? guid}";
                VersionNoteRun.Text = "  (Installed)";
                VersionRun.Parent.SetValue(FrameworkElement.ToolTipProperty, guid);
            }

            ClientPillText.Text = installed ? "READY" : "NOT INSTALLED";
            SetStatusColour(ClientPill, ClientPillText, installed);

            string clientDir = installed ? Path.Combine(Paths.Versions, guid) : Paths.Versions;
            InstallPathText.Text = clientDir;
            InstallPathText.ToolTip = clientDir;

            InfoRobloxVersion.Text = fileVersion ?? (installed ? guid : "Not installed");
            InfoRobloxVersion.ToolTip = guid;
            InfoLauncherVersion.Text = App.Version;
            InfoPlatform.Text = $"{(Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10")} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})";
            InfoInstallPath.Text = Paths.Base;
            InfoInstallPath.ToolTip = Paths.Base;

            InstallationStatusText.Text = installed ? "Up to date" : "Not installed";
            InstallationDot.Fill = Brush(installed ? "ZeroStatusHealthyBrush" : "ZeroStatusWarningBrush");
        }

        private static string? GetRobloxFileVersion()
        {
            try
            {
                var exe = Path.Combine(Paths.Versions, App.PlayerState.Prop.VersionGuid, "RobloxPlayerBeta.exe");
                if (!File.Exists(exe))
                    return null;

                var info = FileVersionInfo.GetVersionInfo(exe);
                return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
            }
            catch
            {
                return null;
            }
        }

        private void PopulateSummaries()
        {
            try
            {
                int flags = App.FastFlags.Prop.Count;
                FlagsSummaryText.Text = flags == 0 ? "None configured" : $"{flags} configured";
            }
            catch { FlagsSummaryText.Text = "Unavailable"; }

            try
            {
                int mods = Directory.Exists(Paths.Modifications)
                    ? Directory.EnumerateFiles(Paths.Modifications, "*", SearchOption.AllDirectories)
                        .Count(f => !f.Contains(@"\ClientSettings\", StringComparison.OrdinalIgnoreCase))
                    : 0;
                ModsSummaryText.Text = mods == 0 ? "None installed" : $"{mods} file{(mods == 1 ? "" : "s")}";
            }
            catch { ModsSummaryText.Text = "Unavailable"; }
        }

        private void PopulateStatus()
        {
            bool client = App.IsPlayerInstalled;
            SetRow(StatusClientText, StatusClientDot, client ? "Ready" : "Not installed", client);

            bool configOk = App.Settings.Loaded;
            bool unsaved = App.FastFlags.Changed || App.PendingSettingTasks.Any();
            SetRow(StatusConfigText, StatusConfigDot, !configOk ? "Defaults" : unsaved ? "Unsaved changes" : "Loaded", configOk && !unsaved);

            bool online;
            try { online = NetworkInterface.GetIsNetworkAvailable(); } catch { online = true; }
            SetRow(StatusNetworkText, StatusNetworkDot, online ? "Online" : "Offline", online);

            bool diskOk = true;
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Paths.Base)!);
                diskOk = drive.AvailableFreeSpace >= LowDiskBytes;
                StatusDiskText.ToolTip = $"{drive.AvailableFreeSpace / 1024d / 1024 / 1024:0.0} GB free on {drive.Name}";
                SetRow(StatusDiskText, StatusDiskDot, diskOk ? "Healthy" : "Low", diskOk);
            }
            catch
            {
                SetRow(StatusDiskText, StatusDiskDot, "Unknown", true);
            }

            bool all = client && configOk && !unsaved && online && diskOk;
            OverallText.Text = all ? "All systems operational" : "Needs attention";
            OverallText.Foreground = Brush(all ? "ZeroStatusHealthyBrush" : "ZeroStatusWarningBrush");
            OverallDot.Fill = OverallText.Foreground;
        }

        private void SetRow(TextBlock text, System.Windows.Shapes.Ellipse dot, string value, bool healthy)
        {
            text.Text = value;
            dot.Fill = Brush(healthy ? "ZeroStatusHealthyBrush" : "ZeroStatusWarningBrush");
        }

        private void SetStatusColour(Border pill, TextBlock text, bool healthy)
        {
            var colour = ((SolidColorBrush)Brush(healthy ? "ZeroStatusHealthyBrush" : "ZeroStatusWarningBrush")).Color;
            text.Foreground = new SolidColorBrush(colour);
            pill.Background = new SolidColorBrush(Color.FromArgb(0x1A, colour.R, colour.G, colour.B));
        }

        private Brush Brush(string key) => (Brush)FindResource(key);

        private void PopulateTheme()
        {
            var theme = App.Settings.Prop.Theme;

            ThemeNameText.Text = theme switch
            {
                Enums.Theme.Light => "Light",
                Enums.Theme.Dark => "Obsidian",
                _ => "System"
            };

            ThemeDotDark.IsChecked = theme == Enums.Theme.Dark;
            ThemeDotLight.IsChecked = theme == Enums.Theme.Light;
            ThemeDotDefault.IsChecked = theme == Enums.Theme.Default;
        }

        private void PopulateActivity()
        {
            var rows = ActivityLog.Entries
                .Take(5)
                .Select(e => new ActivityRow(IconFor(e.Kind), e.Title, e.Detail, ActivityLog.Relative(e.Time)))
                .ToList();

            RecentActivityList.ItemsSource = rows;
            RecentActivityEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static SymbolRegular IconFor(ActivityType kind) => kind switch
        {
            ActivityType.Launch => SymbolRegular.Play24,
            ActivityType.Configuration => SymbolRegular.Options24,
            ActivityType.Theme => SymbolRegular.Color24,
            ActivityType.Backup => SymbolRegular.Archive24,
            _ => SymbolRegular.ArrowSync24
        };

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.Settings.Prop.ConfirmLaunches)
                {
                    var ok = Frontend.ShowMessageBox("Launch Roblox now?", MessageBoxImage.Question, MessageBoxButton.YesNo);
                    if (ok != MessageBoxResult.Yes)
                        return;
                }

                LaunchHandler.LaunchRoblox(LaunchMode.Player);
                ActivityLog.Record(ActivityType.Launch, "Roblox launched", "Started from Overview");
                PopulateActivity();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverviewPage::LaunchButton_Click", ex);
            }
        }

        private void ThemeDot_Checked(object sender, RoutedEventArgs e)
        {
            if (_populating || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<Enums.Theme>(tag, out var theme))
                return;

            if (App.Settings.Prop.Theme == theme)
                return;

            App.Settings.Prop.Theme = theme;

            try
            {
                if (Window.GetWindow(this) is WpfUiWindow window)
                    window.ApplyTheme();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverviewPage::ThemeDot_Checked", ex);
            }

            ActivityLog.Record(ActivityType.Theme, "Theme changed", $"Switched to {(theme == Enums.Theme.Dark ? "Obsidian" : theme == Enums.Theme.Default ? "System" : "Light")} theme");
            Populate();
        }

        private void Tile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag })
                return;

            var parts = tag.Split(':');
            if (!Enum.TryParse<ShellSection>(parts[0], out var section))
                return;

            int tab = parts.Length > 1 && int.TryParse(parts[1], out var t) ? t : 0;
            NavigationRequested?.Invoke(this, new ShellTarget(section, tab));
        }
    }
}
