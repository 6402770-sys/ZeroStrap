using System.Windows;
using System.Windows.Controls;

using ZeroStrap.FastFlags;

namespace ZeroStrap.UI.Elements.Settings.Pages.FastFlagPanels
{
    public partial class CatalogPanel : UserControl
    {
        private static FastFlagWorkspace Workspace => FastFlagWorkspace.Instance;

        private CancellationTokenSource? _cts;

        private bool _loading;

        public CatalogPanel()
        {
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            Workspace.CatalogChanged += Workspace_CatalogChanged;
            Refresh();
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            Workspace.CatalogChanged -= Workspace_CatalogChanged;
        }

        private void Workspace_CatalogChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh);

        private void Refresh()
        {
            _loading = true;

            var meta = Workspace.CatalogMetadata;
            var state = Workspace.State.Prop;

            SourceText.Text = FlagCatalogUpdater.SourceDescription;
            SourceLink.ToolTip = Workspace.Updater.SourceUrl;

            CountText.Text = Workspace.Catalog.Entries.Count > 0
                ? $"{Workspace.Catalog.Entries.Count:N0}"
                : Workspace.CatalogProblem is string problem
                    ? String.Format(Strings.Menu_FastFlagEditor_Catalog_Unusable, problem)
                    : Strings.Menu_FastFlagEditor_Catalog_NotDownloaded;

            VersionText.Text = meta?.Sha256 is null ? "-" : meta.Version;
            UpdatedText.Text = meta?.FetchedAtUtc is DateTime fetched ? fetched.ToLocalTime().ToString("g") : Strings.Menu_FastFlagEditor_Catalog_Never;
            CheckedText.Text = meta?.LastCheckedUtc is DateTime checkedAt
                ? $"{checkedAt.ToLocalTime():g} · {meta.LastCheckResult}"
                : Strings.Menu_FastFlagEditor_Catalog_Never;

            AutoToggle.IsChecked = state.CatalogUpdateMode == CatalogUpdateMode.Automatic;
            IntervalBox.IsEnabled = state.CatalogUpdateMode == CatalogUpdateMode.Automatic;
            IntervalBox.SelectedItem = IntervalBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == state.CatalogCheckIntervalHours.ToString())
                                       ?? IntervalBox.Items[2];

            // an automatic check may be running without this panel having started it
            SetRunning(Workspace.Updater.IsRunning || _cts is not null);

            _loading = false;
        }

        private void SetRunning(bool running)
        {
            CheckButton.IsEnabled = !running;
            Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.Visibility = _cts is not null ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts is not null)
                return;

            _cts = new CancellationTokenSource();
            SetRunning(true);
            ResultText.Text = Strings.Menu_FastFlagEditor_Catalog_Checking;

            try
            {
                var result = await Workspace.UpdateCatalogAsync(_cts.Token);
                ResultText.Text = result.Message;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("CatalogPanel::CheckButton_Click", ex);
                ResultText.Text = String.Format(Strings.Menu_FastFlagEditor_Catalog_UnexpectedError, ex.Message);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetRunning(false);
                Refresh();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void AutoToggle_Click(object sender, RoutedEventArgs e)
        {
            var state = Workspace.State.Prop;
            state.CatalogUpdateMode = AutoToggle.IsChecked == true ? CatalogUpdateMode.Automatic : CatalogUpdateMode.Manual;
            Workspace.State.Save();
            IntervalBox.IsEnabled = state.CatalogUpdateMode == CatalogUpdateMode.Automatic;
        }

        private void IntervalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || IntervalBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out int hours))
                return;

            Workspace.State.Prop.CatalogCheckIntervalHours = hours;
            Workspace.State.Save();
        }

        private void SourceLink_Click(object sender, RoutedEventArgs e) =>
            Utilities.ShellExecute("https://github.com/MaximumADHD/Roblox-Client-Tracker");
    }
}
