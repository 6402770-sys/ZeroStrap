using System;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

using Wpf.Ui.Common;

using ZeroStrap.UI.Elements.Shell.Pages;
using ZeroStrap.UI.Theming;
using ZeroStrap.UI.Utility;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.UI.Elements.Shell
{
    public partial class ShellWindow
    {
        private Models.Persistable.WindowState _state => App.State.Prop.SettingsWindow;

        private readonly ShellViewModel _viewModel;
        private readonly List<PaletteCommand> _commands;
        private bool _navigating;

        public ShellWindow(bool showAlreadyRunningWarning = false)
        {
            _viewModel = new ShellViewModel();
            _viewModel.RequestSaveNoticeEvent += (_, _) => SettingsSavedSnackbar.Show();
            _viewModel.RequestCloseWindowEvent += (_, _) => Close();

            DataContext = _viewModel;

            InitializeComponent();

            App.Logger.WriteLine("ShellWindow", "Initializing ZeroStrap shell");

            FastFlags.FlagBindingHost.Attach(this, message =>
            {
                FlagBindingSnackbar.Message = message;
                FlagBindingSnackbar.Show();
            });

            _commands = BuildCommands();

            PreviewKeyDown += ShellWindow_PreviewKeyDown;
            Deactivated += (_, _) => ClosePalette();
            LocationChanged += (_, _) => ClosePalette();

            NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged;
            UpdateNetworkStatus();

            LoadState();
            VersionLabel.Text = $"ZeroStrap v{App.Version}";
            ActivityLog.NoteVersion();
            NavigateTo(ShellSection.Overview);

            ModsStudioService.SummaryChanged += UpdateModsBadge;
            Loaded += (_, _) => _ = ModsStudioService.RefreshSummaryAsync();
        }

        public void LoadState()
        {
            if (_state.Left > SystemParameters.VirtualScreenWidth)
                _state.Left = 0;

            if (_state.Top > SystemParameters.VirtualScreenHeight)
                _state.Top = 0;

            if (_state.Width > 0)
                this.Width = _state.Width;

            if (_state.Height > 0)
                this.Height = _state.Height;

            if (_state.Left > 0 && _state.Top > 0)
            {
                this.WindowStartupLocation = WindowStartupLocation.Manual;
                this.Left = _state.Left;
                this.Top = _state.Top;
            }
        }

        #region Navigation

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (_navigating || sender is not RadioButton { Tag: string tag })
                return;

            if (Enum.TryParse<ShellSection>(tag, out var section))
                NavigateTo(section);
        }

        public void NavigateTo(ShellSection section, ConfigureTab configureTab = ConfigureTab.FastFlags)
        {
            ClosePalette();

            if (section == _viewModel.CurrentSection && ContentFrame.Content is ConfigurePage current && section == ShellSection.Configure)
            {
                current.SelectTab(configureTab);
                return;
            }

            _navigating = true;
            try
            {
                Page page = section switch
                {
                    ShellSection.Configure => new ConfigurePage(configureTab),
                    ShellSection.ModsStudio => BuildModsStudio(),
                    ShellSection.MacroStudio => new MacroStudioPage(),
                    ShellSection.Appearance => new AppearancePage(),
                    ShellSection.System => new SystemPage(),
                    ShellSection.About => new AboutPage(),
                    _ => BuildOverview()
                };

                ContentFrame.Navigate(page);
                _viewModel.CurrentSection = section;

                switch (section)
                {
                    case ShellSection.Overview: NavOverview.IsChecked = true; break;
                    case ShellSection.Configure: NavConfigure.IsChecked = true; break;
                    case ShellSection.ModsStudio: NavModsStudio.IsChecked = true; break;
                    case ShellSection.MacroStudio: NavMacroStudio.IsChecked = true; break;
                    case ShellSection.Appearance: NavAppearance.IsChecked = true; break;
                    case ShellSection.System: NavSystem.IsChecked = true; break;
                    case ShellSection.About:
                        foreach (var nav in new[] { NavOverview, NavConfigure, NavModsStudio, NavMacroStudio, NavAppearance, NavSystem })
                            nav.IsChecked = false;
                        break;
                }
            }
            finally
            {
                _navigating = false;
            }
        }

        private Page BuildOverview()
        {
            var overview = new OverviewPage();
            overview.NavigationRequested += (_, target) => NavigateTo(target.Section, target.Tab);
            return overview;
        }

        private void ContentFrame_Navigated(object sender, System.Windows.Navigation.NavigationEventArgs e)
        {
            // Pages are rebuilt on every visit; keep no journal (no memory growth, no Backspace "back").
            while (ContentFrame.CanGoBack)
                ContentFrame.RemoveBackEntry();

            if (ContentFrame.Content is UIElement page)
                Motion.PageEnter(page);
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e) => NavigateTo(ShellSection.About);

        private Page BuildModsStudio()
        {
            var page = new ModsStudioPage();
            page.ToastRequested += (_, toast) => ShowModsToast(toast.Message, toast.IsError);
            return page;
        }

        /// <summary>Short, self-dismissing notice for Mods Studio changes.</summary>
        private void ShowModsToast(string message, bool isError)
        {
            ModsToastSnackbar.Show(
                isError ? "Couldn't update mods" : "Mods updated",
                message,
                isError ? SymbolRegular.ErrorCircle24 : SymbolRegular.CheckmarkCircle24,
                isError ? ControlAppearance.Danger : ControlAppearance.Success);
        }

        private void UpdateModsBadge()
        {
            int conflicts = ModsStudioService.HasSummary ? ModsStudioService.UnresolvedCount : 0;

            NavModsBadge.Visibility = conflicts > 0 ? Visibility.Visible : Visibility.Collapsed;
            NavModsBadgeText.Text = conflicts.ToString();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e) => NavigateTo(ShellSection.Configure, ConfigureTab.Behaviour);

        private void SearchButton_Click(object sender, RoutedEventArgs e) => FocusPalette();

        #endregion

        #region Window chrome

        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindAncestor<ButtonBase>(source) is not null)
                return;

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            try { DragMove(); } catch (InvalidOperationException) { }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = System.Windows.WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void ToggleMaximize() =>
            WindowState = WindowState == System.Windows.WindowState.Maximized ? System.Windows.WindowState.Normal : System.Windows.WindowState.Maximized;

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            // A maximised chrome-less window overhangs the work area by the resize border.
            RootGrid.Margin = WindowState == System.Windows.WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaximizeGlyph.Symbol = WindowState == System.Windows.WindowState.Maximized ? SymbolRegular.SquareMultiple16 : SymbolRegular.Maximize16;
            ClosePalette();
        }

        private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
        {
            while (node is not null)
            {
                if (node is T match)
                    return match;

                node = node is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return null;
        }

        #endregion

        #region Network status

        private void NetworkChange_NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
            Dispatcher.BeginInvoke(UpdateNetworkStatus);

        private void UpdateNetworkStatus()
        {
            bool online;
            try { online = NetworkInterface.GetIsNetworkAvailable(); }
            catch { online = true; }

            NetworkText.Text = online ? "Connected" : "Offline";
            NetworkDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, online ? "ZeroStatusHealthyBrush" : "ZeroStatusErrorBrush");
        }

        #endregion

        #region Command palette

        public class PaletteCommand
        {
            public string Title { get; init; } = "";
            public string Hint { get; init; } = "";
            public string Group { get; init; } = "";
            public SymbolRegular Icon { get; init; }
            public string[] Keywords { get; init; } = Array.Empty<string>();
            public Action Run { get; init; } = () => { };
        }

        private List<PaletteCommand> BuildCommands() => new()
        {
            new() { Title = "Launch Roblox", Hint = "Start the Roblox Player", Group = "Action", Icon = SymbolRegular.Play24,
                    Keywords = new[] { "launch", "play", "start", "roblox", "run" }, Run = LaunchRoblox },
            new() { Title = "Save changes", Hint = "Write settings and Fast Flags to disk", Group = "Action", Icon = SymbolRegular.Save24,
                    Keywords = new[] { "save", "apply", "write" }, Run = () => _viewModel.SaveSettingsCommand.Execute(null) },

            new() { Title = "Overview", Hint = "Home dashboard", Group = "Go to", Icon = SymbolRegular.Home24,
                    Keywords = new[] { "overview", "home", "dashboard" }, Run = () => NavigateTo(ShellSection.Overview) },
            new() { Title = "Fast Flags", Hint = "Configure › Fast Flags", Group = "Go to", Icon = SymbolRegular.Flag24,
                    Keywords = new[] { "fast flags", "fflags", "flags", "editor", "configure" }, Run = () => NavigateTo(ShellSection.Configure, ConfigureTab.FastFlags) },
            new() { Title = "Fast Flag Editor", Hint = "Configure › Fast Flags › Editor", Group = "Go to", Icon = SymbolRegular.EditSettings24,
                    Keywords = new[] { "fast flag editor", "flag editor", "edit flags" }, Run = OpenFastFlagEditor },
            new() { Title = "Mods Studio", Hint = "Turn mods on or off, resolve conflicts, built-in mods", Group = "Go to", Icon = SymbolRegular.PuzzlePiece24,
                    Keywords = new[] { "mods", "mods studio", "modifications", "mod packages", "conflicts", "fonts", "cursor", "emoji", "sounds" }, Run = () => NavigateTo(ShellSection.ModsStudio) },
            new() { Title = "Macro Studio", Hint = "Record and edit macros", Group = "Go to", Icon = SymbolRegular.Record24,
                    Keywords = new[] { "macro", "macros", "record", "automation", "hotkey" }, Run = () => NavigateTo(ShellSection.MacroStudio) },
            new() { Title = "Integrations", Hint = "Configure › Integrations", Group = "Go to", Icon = SymbolRegular.PlugConnected24,
                    Keywords = new[] { "integrations", "discord", "rich presence", "activity" }, Run = () => NavigateTo(ShellSection.Configure, ConfigureTab.Integrations) },
            new() { Title = "Shortcuts", Hint = "Configure › Shortcuts", Group = "Go to", Icon = SymbolRegular.Keyboard24,
                    Keywords = new[] { "shortcuts", "desktop", "start menu" }, Run = () => NavigateTo(ShellSection.Configure, ConfigureTab.Shortcuts) },
            new() { Title = "Behaviour", Hint = "Configure › Behaviour", Group = "Go to", Icon = SymbolRegular.Settings24,
                    Keywords = new[] { "behaviour", "behavior", "settings", "updates", "channel" }, Run = () => NavigateTo(ShellSection.Configure, ConfigureTab.Behaviour) },
            new() { Title = "Appearance", Hint = "Themes and launcher window style", Group = "Go to", Icon = SymbolRegular.Color24,
                    Keywords = new[] { "appearance", "theme", "themes", "style", "look", "ui", "custom themes" }, Run = () => NavigateTo(ShellSection.Appearance) },
            new() { Title = "System", Hint = "Diagnostics, logs, backups, repair", Group = "Go to", Icon = SymbolRegular.Pulse24,
                    Keywords = new[] { "system", "diagnostics", "logs", "backup", "backups", "repair", "uninstall", "updates" }, Run = () => NavigateTo(ShellSection.System) },

            new() { Title = "Theme: Obsidian", Hint = "Dark graphite surfaces", Group = "Theme", Icon = SymbolRegular.WeatherMoon24,
                    Keywords = new[] { "theme dark", "dark", "obsidian" }, Run = () => SetTheme(ThemeManager.ObsidianId) },
            new() { Title = "Theme: Light", Hint = "Light surfaces", Group = "Theme", Icon = SymbolRegular.WeatherSunny24,
                    Keywords = new[] { "theme light", "light" }, Run = () => SetTheme(ThemeManager.LightId) },
            new() { Title = "Theme: System", Hint = "Follow Windows", Group = "Theme", Icon = SymbolRegular.Desktop24,
                    Keywords = new[] { "theme system", "theme default", "system theme", "default" }, Run = () => SetTheme(ThemeManager.SystemId) },

            new() { Title = "Open logs folder", Hint = "ZeroStrap log files", Group = "System", Icon = SymbolRegular.DocumentText24,
                    Keywords = new[] { "logs", "log folder" }, Run = () => Utilities.ShellExecute(Paths.Logs) },
            new() { Title = "Open install folder", Hint = Paths.Base, Group = "System", Icon = SymbolRegular.FolderOpen24,
                    Keywords = new[] { "folder", "install", "directory" }, Run = () => Utilities.ShellExecute(Paths.Base) },
            new() { Title = "About ZeroStrap", Hint = "Version, licenses and links", Group = "Help", Icon = SymbolRegular.Info24,
                    Keywords = new[] { "about", "version", "license", "licenses", "credits", "translators", "build" }, Run = () => NavigateTo(ShellSection.About) },
        };

        private void OpenFastFlagEditor()
        {
            NavigateTo(ShellSection.Configure, ConfigureTab.FastFlags);

            // a fresh ConfigurePage becomes the frame's content asynchronously; wait until it is there
            Dispatcher.BeginInvoke(() =>
            {
                if (ContentFrame.Content is ConfigurePage configure)
                    configure.OpenFastFlagEditor();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void LaunchRoblox()
        {
            try
            {
                LaunchHandler.LaunchRoblox(LaunchMode.Player);
                ActivityLog.Record(ActivityType.Launch, "Roblox launched", "Started from the command palette");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ShellWindow::LaunchRoblox", ex);
            }
        }

        private void SetTheme(string themeId)
        {
            ThemeManager.SetActive(themeId);
            ActivityLog.Record(ActivityType.Theme, "Theme changed", $"Switched to {ThemeManager.ActiveDisplayName} theme");

            if (ContentFrame.Content is AppearancePage appearance)
                appearance.Refresh();
        }

        private void FocusPalette()
        {
            CommandPaletteBox.Focus();
            CommandPaletteBox.SelectAll();
            OpenPalette();
        }

        private void OpenPalette()
        {
            FilterPalette();
            PalettePopup.IsOpen = true;
        }

        private void ClosePalette()
        {
            if (PalettePopup is not null)
                PalettePopup.IsOpen = false;
        }

        private void FilterPalette()
        {
            var query = CommandPaletteBox.Text.Trim().ToLowerInvariant();

            var results = string.IsNullOrEmpty(query)
                ? _commands
                : _commands
                    .Select(c => (c, score: Score(c, query)))
                    .Where(x => x.score > 0)
                    .OrderByDescending(x => x.score)
                    .Select(x => x.c)
                    .ToList();

            PaletteList.ItemsSource = results;
            PaletteList.SelectedIndex = results.Count > 0 ? 0 : -1;
            PaletteEmpty.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static int Score(PaletteCommand command, string query)
        {
            var title = command.Title.ToLowerInvariant();

            if (title == query) return 100;
            if (title.StartsWith(query)) return 80;
            if (command.Keywords.Any(k => k == query)) return 70;
            if (command.Keywords.Any(k => k.StartsWith(query))) return 60;
            if (title.Contains(query)) return 40;
            if (command.Keywords.Any(k => k.Contains(query)) || command.Hint.ToLowerInvariant().Contains(query)) return 20;

            return 0;
        }

        private void RunSelected()
        {
            if (PaletteList.SelectedItem is not PaletteCommand command)
                return;

            ClosePalette();
            CommandPaletteBox.Clear();
            Keyboard.ClearFocus();
            command.Run();
        }

        private void CommandPaletteBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PalettePlaceholder.Visibility = string.IsNullOrEmpty(CommandPaletteBox.Text) ? Visibility.Visible : Visibility.Collapsed;

            if (CommandPaletteBox.IsKeyboardFocused)
                OpenPalette();
        }

        private void CommandPaletteBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => OpenPalette();

        private void CommandPaletteBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Clicking a result moves focus into the popup; let that click run before closing.
            if (e.NewFocus is DependencyObject target && PalettePopup.Child is not null && PalettePopup.Child.IsAncestorOf(target))
                return;

            ClosePalette();
        }

        private void CommandPaletteBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    CommandPaletteBox.Clear();
                    ClosePalette();
                    Keyboard.ClearFocus();
                    e.Handled = true;
                    break;

                case Key.Down when PaletteList.Items.Count > 0:
                    PaletteList.SelectedIndex = (PaletteList.SelectedIndex + 1) % PaletteList.Items.Count;
                    PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                    e.Handled = true;
                    break;

                case Key.Up when PaletteList.Items.Count > 0:
                    PaletteList.SelectedIndex = (PaletteList.SelectedIndex - 1 + PaletteList.Items.Count) % PaletteList.Items.Count;
                    PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                    e.Handled = true;
                    break;

                case Key.Enter:
                    RunSelected();
                    e.Handled = true;
                    break;
            }
        }

        private void PaletteList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindAncestor<ListBoxItem>(source) is ListBoxItem { DataContext: PaletteCommand command })
            {
                PaletteList.SelectedItem = command;
                RunSelected();
            }
        }

        private void ShellWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;

            if (e.Key == Key.K)
            {
                FocusPalette();
                e.Handled = true;
            }
            else if (e.Key == Key.S)
            {
                _viewModel.SaveSettingsCommand.Execute(null);
                e.Handled = true;
            }
        }

        #endregion

        private void WpfUiWindow_Closing(object sender, CancelEventArgs e)
        {
            if (App.FastFlags.Changed || App.PendingSettingTasks.Any())
            {
                var result = Frontend.ShowMessageBox(Strings.Menu_UnsavedChanges, MessageBoxImage.Warning, MessageBoxButton.YesNo);
                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged;

            if (WindowState == System.Windows.WindowState.Normal)
            {
                _state.Width = this.Width;
                _state.Height = this.Height;
                _state.Top = this.Top;
                _state.Left = this.Left;
            }

            App.State.Save();
        }

        private void WpfUiWindow_Closed(object sender, EventArgs e)
        {
            if (App.LaunchSettings.TestModeFlag.Active)
                LaunchHandler.LaunchRoblox(LaunchMode.Player);
            else
                App.SoftTerminate();
        }
    }
}
