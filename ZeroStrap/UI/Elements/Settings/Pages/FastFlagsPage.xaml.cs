using System.Windows;
using System.Windows.Threading;

using ZeroStrap.Enums.FlagPresets;
using ZeroStrap.UI.ViewModels.Settings;
using Wpf.Ui.Mvvm.Contracts;

namespace ZeroStrap.UI.Elements.Settings.Pages
{
    /// <summary>
    /// Interaction logic for FastFlagsPage.xaml
    /// </summary>
    public partial class FastFlagsPage
    {
        private bool _initialLoad = false;

        private FastFlagsViewModel _viewModel = null!;

        /// <summary>Raised when the editor entry is used while hosted in the shell; the Configure page owns the workspace.</summary>
        public event EventHandler? OpenEditorRequested;

        public FastFlagsPage()
        {
            SetupViewModel();
            InitializeComponent();
            UpdateConfiguredCount();
        }

        private void SetupViewModel()
        {
            _viewModel = new FastFlagsViewModel();

            _viewModel.OpenFlagEditorEvent += OpenFlagEditor;
            _viewModel.RequestPageReloadEvent += (_, _) => SetupViewModel();

            DataContext = _viewModel;
            UpdateConfiguredCount();
        }

        /// <summary>Re-reads everything from the flag store (after the editor closed) and puts focus back on the editor entry.</summary>
        public void Reload()
        {
            SetupViewModel();
            Dispatcher.BeginInvoke(() => OpenEditorButton.Focus(), DispatcherPriority.Input);
        }

        private void UpdateConfiguredCount()
        {
            // the XAML-created element does not exist yet when SetupViewModel runs from the constructor
            if (ConfiguredCountText is null)
                return;

            try
            {
                ConfiguredCountText.Text = $"{App.FastFlags.Prop.Count} configured";
            }
            catch
            {
                ConfiguredCountText.Text = "";
            }
        }

        private void OpenFlagEditor(object? sender, EventArgs e)
        {
            if (Window.GetWindow(this) is INavigationWindow window)
                window.Navigate(typeof(FastFlagEditorPage));
            else if (OpenEditorRequested is not null)
                OpenEditorRequested(this, EventArgs.Empty); // hosted in the Obsidian shell
            else
                NavigationService?.Navigate(new FastFlagEditorPage());
        }

        private void MsaaReset_Click(object sender, RoutedEventArgs e) => MsaaList.SelectedItem = MSAAMode.Default;

        private void TextureReset_Click(object sender, RoutedEventArgs e) => TextureList.SelectedItem = TextureQuality.Default;

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // refresh datacontext on page load to synchronize with editor page
            
            if (!_initialLoad)
            {
                _initialLoad = true;
                return;
            }

            SetupViewModel();
        }
    }
}
