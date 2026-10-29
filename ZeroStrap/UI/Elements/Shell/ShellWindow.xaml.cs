using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ZeroStrap.UI.Elements.Shell.Pages;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.UI.Elements.Shell
{
    public partial class ShellWindow
    {
        private Models.Persistable.WindowState _state => App.State.Prop.SettingsWindow;

        private readonly ShellViewModel _viewModel;
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

            CommandPaletteBox.KeyDown += CommandPaletteBox_KeyDown;
            PreviewKeyDown += ShellWindow_PreviewKeyDown;

            LoadState();
            VersionLabel.Text = $"ZeroStrap v{App.Version}";
            NavigateTo(ShellSection.Overview);
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

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (_navigating || sender is not RadioButton { Tag: string tag })
                return;

            if (Enum.TryParse<ShellSection>(tag, out var section))
                NavigateTo(section);
        }

        public void NavigateTo(ShellSection section)
        {
            _navigating = true;
            try
            {
                Page page = section switch
                {
                    ShellSection.Play => new PlayPage(),
                    ShellSection.Configure => new ConfigurePage(),
                    ShellSection.Appearance => new AppearancePage(),
                    ShellSection.System => new SystemPage(),
                    _ => BuildOverview()
                };

                ContentFrame.Navigate(page);
                _viewModel.CurrentSection = section;

                switch (section)
                {
                    case ShellSection.Overview: NavOverview.IsChecked = true; break;
                    case ShellSection.Play: NavPlay.IsChecked = true; break;
                    case ShellSection.Configure: NavConfigure.IsChecked = true; break;
                    case ShellSection.Appearance: NavAppearance.IsChecked = true; break;
                    case ShellSection.System: NavSystem.IsChecked = true; break;
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
            overview.NavigationRequested += (_, section) => NavigateTo(section);
            return overview;
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e) => _viewModel.OpenAboutCommand.Execute(null);

        private void CommandPaletteBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            var query = CommandPaletteBox.Text.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(query))
                return;

            var section = query switch
            {
                "overview" or "home" => ShellSection.Overview,
                "play" or "launch" => ShellSection.Play,
                "configure" or "fast flags" or "flags" or "mods" or "integrations" => ShellSection.Configure,
                "appearance" or "theme" or "themes" => ShellSection.Appearance,
                "system" or "logs" or "diagnostics" => ShellSection.System,
                _ => (ShellSection?)null
            };

            if (section is not null)
            {
                NavigateTo(section.Value);
                CommandPaletteBox.Clear();
            }
        }

        private void ShellWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                CommandPaletteBox.Focus();
                e.Handled = true;
            }
        }

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

            _state.Width = this.Width;
            _state.Height = this.Height;
            _state.Top = this.Top;
            _state.Left = this.Left;

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
