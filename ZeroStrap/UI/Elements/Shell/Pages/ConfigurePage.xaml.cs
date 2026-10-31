using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using ZeroStrap.UI.Elements.Settings.Pages;
using ZeroStrap.UI.Utility;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    public partial class ConfigurePage : Page
    {
        public ConfigurePage(ConfigureTab initialTab = ConfigureTab.FastFlags)
        {
            InitializeComponent();
            SelectTab(initialTab);
            Loaded += (_, _) => RefreshStats();
        }

        private FastFlagsPage? _fastFlagsPage;
        private FastFlagEditorPage? _editor;

        public bool IsWorkspaceOpen => _editor is not null;

        #region Workspace

        /// <summary>Opens the Fast Flag Editor as a full-section workspace. A fresh editor is created every time.</summary>
        public void OpenFastFlagEditor()
        {
            if (_editor is not null || Motion.IsSwapping)
                return;

            if (!ReferenceEquals(ConfigureTabs.SelectedItem, TabFastFlags))
                ConfigureTabs.SelectedItem = TabFastFlags;

            var editor = new FastFlagEditorPage();
            editor.CloseRequested += (_, _) => CloseWorkspace();
            editor.Loaded += (_, _) => Dispatcher.BeginInvoke(editor.FocusBackButton, DispatcherPriority.Input);

            _editor = editor;
            WorkspaceHost.Navigate(editor);

            if (IsLoaded)
            {
                Motion.WorkspaceSwap(MainContent, WorkspaceHost, forward: true);
            }
            else
            {
                MainContent.Visibility = Visibility.Collapsed;
                WorkspaceHost.Visibility = Visibility.Visible;
            }
        }

        /// <summary>Returns from the workspace to the Configure tabs.</summary>
        public void CloseWorkspace(bool animate = true)
        {
            if (_editor is null || Motion.IsSwapping)
                return;

            var editor = _editor;
            _editor = null;

            void Finish()
            {
                // drop the editor (and any journal entry) so its Unloaded handler releases the workspace events
                WorkspaceHost.Navigate(null);
                while (WorkspaceHost.CanGoBack)
                    WorkspaceHost.RemoveBackEntry();

                // the editor may have changed flags: refresh the page behind it and the stats strip
                _fastFlagsPage?.Reload();
                RefreshStats();
            }

            if (animate && IsLoaded)
            {
                Motion.WorkspaceSwap(WorkspaceHost, MainContent, forward: false, Finish);
            }
            else
            {
                MainContent.Visibility = Visibility.Visible;
                WorkspaceHost.Visibility = Visibility.Collapsed;
                Finish();
            }
        }

        private void WorkspaceHost_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_editor is null || Motion.IsSwapping)
                return;

            bool back = e.Key switch
            {
                Key.System => e.SystemKey == Key.Left && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt,
                Key.Escape => !IsTextInputFocused(),
                _ => false
            };

            if (!back)
                return;

            e.Handled = true;
            CloseWorkspace();
        }

        private void WorkspaceHost_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.XButton1 || _editor is null || Motion.IsSwapping)
                return;

            e.Handled = true;
            CloseWorkspace();
        }

        /// <summary>True while typing or choosing would be interrupted by Esc: a text box, a combo box (or its popup), or a grid cell being edited.</summary>
        private static bool IsTextInputFocused()
        {
            for (var node = Keyboard.FocusedElement as DependencyObject; node is not null; node = ParentOf(node))
            {
                switch (node)
                {
                    case TextBoxBase:
                    case ComboBox:
                    case DataGridCell { IsEditing: true }:
                        return true;
                    case ComboBoxItem item when ItemsControl.ItemsControlFromItemContainer(item) is ComboBox:
                        return true;
                }
            }

            return false;
        }

        private static DependencyObject? ParentOf(DependencyObject node) =>
            node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);

        #endregion

        public void SelectTab(ConfigureTab tab)
        {
            // another tab means the user is done with the workspace
            if (tab != ConfigureTab.FastFlags && _editor is not null)
                CloseWorkspace(animate: false);

            ConfigureTabs.SelectedItem = tab switch
            {
                ConfigureTab.Mods => TabMods,
                ConfigureTab.Integrations => TabIntegrations,
                ConfigureTab.Shortcuts => TabShortcuts,
                ConfigureTab.Behaviour => TabBehaviour,
                _ => TabFastFlags
            };
        }

        private void ConfigureTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectionChanged bubbles up from combo boxes inside the hosted pages; only react to the tabs.
            if (!ReferenceEquals(e.OriginalSource, ConfigureTabs) || !IsLoaded)
                return;

            if (ConfigureTabs.SelectedContent is UIElement content)
                Motion.FadeRise(content, 0, 8, 300);
        }

        private void RefreshStats()
        {
            try
            {
                var all = App.FastFlags.Prop;
                var presetValues = FastFlagManager.PresetFlags.Values.ToHashSet(System.StringComparer.Ordinal);

                StatFlagsCount.Text = all.Count.ToString();
                StatPresetsCount.Text = all.Keys.Count(k => presetValues.Contains(k)).ToString();
            }
            catch
            {
                StatFlagsCount.Text = "-";
                StatPresetsCount.Text = "-";
            }

            try
            {
                StatIntegrationsCount.Text = App.Settings.Prop.CustomIntegrations?.Count.ToString() ?? "0";
            }
            catch
            {
                StatIntegrationsCount.Text = "-";
            }

            try
            {
                var modsDir = Paths.Modifications;
                if (Directory.Exists(modsDir))
                    StatModsCount.Text = Directory.EnumerateFiles(modsDir, "*", SearchOption.AllDirectories)
                        .Count(f => !f.Contains(@"\ClientSettings\", System.StringComparison.OrdinalIgnoreCase)) // Fast Flags live here, not mods
                        .ToString();
                else
                    StatModsCount.Text = "0";
            }
            catch
            {
                StatModsCount.Text = "-";
            }
        }

        private static void NavigateOnce(object sender, Page page)
        {
            if (sender is Frame { Content: null } frame)
                frame.Navigate(page);
        }

        private void FastFlagsHost_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Frame { Content: null })
                return;

            var page = new FastFlagsPage();
            page.OpenEditorRequested += (_, _) => OpenFastFlagEditor();
            _fastFlagsPage = page;
            NavigateOnce(sender, page);
        }
        private void ModsHost_Loaded(object sender, RoutedEventArgs e) => NavigateOnce(sender, new ModsPage());
        private void IntegrationsHost_Loaded(object sender, RoutedEventArgs e) => NavigateOnce(sender, new IntegrationsPage());
        private void ShortcutsHost_Loaded(object sender, RoutedEventArgs e) => NavigateOnce(sender, new ShortcutsPage());
        private void BehaviourHost_Loaded(object sender, RoutedEventArgs e) => NavigateOnce(sender, new BehaviourPage());
    }
}
