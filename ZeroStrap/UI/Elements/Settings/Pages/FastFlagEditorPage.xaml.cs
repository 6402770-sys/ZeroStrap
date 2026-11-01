using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

using Wpf.Ui.Mvvm.Contracts;

using ZeroStrap.FastFlags;
using ZeroStrap.UI.Elements.Dialogs;
using ZeroStrap.UI.ViewModels.Settings;

namespace ZeroStrap.UI.Elements.Settings.Pages
{
    /// <summary>
    /// Interaction logic for FastFlagEditorPage.xaml
    /// </summary>
    public partial class FastFlagEditorPage
    {
        // believe me when i say there is absolutely zero point to using mvvm for this
        // using a datagrid is a codebehind thing only and thats it theres literally no way around it

        private readonly FastFlagWorkspace _workspace = FastFlagWorkspace.Instance;

        private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };

        private CancellationTokenSource? _searchCts;

        private FlagSearchIndex? _index;

        private bool _indexDirty = true;

        private bool _showPresets = false;

        private string _searchFilter = "";

        private bool _subscribed = false;

        public FastFlagEditorPage()
        {
            InitializeComponent();

            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                RunSearch();
            };
        }

        #region Search

        private void MarkIndexDirty() => _indexDirty = true;

        private void ReloadList()
        {
            MarkIndexDirty();
            RunSearch();
        }

        private FlagScope SelectedScope => ScopeFilter.SelectedIndex switch
        {
            1 => FlagScope.Catalog,
            2 => FlagScope.All,
            _ => FlagScope.Configured
        };

        private FlagQuery BuildQuery() => new()
        {
            Text = _searchFilter,
            Scope = SelectedScope,
            Type = TypeFilter.SelectedIndex switch
            {
                1 => FlagValueType.Bool,
                2 => FlagValueType.Int,
                3 => FlagValueType.String,
                4 => FlagValueType.Log,
                _ => null
            },
            // "show preset flags" only declutters the configured list; catalog searches always find them
            Excluded = _showPresets || SelectedScope != FlagScope.Configured ? null : FastFlagManager.PresetFlags.Values.ToHashSet(StringComparer.Ordinal),
            Limit = 1000
        };

        /// <summary>
        /// Queries off the UI thread. Each call cancels the previous one, so typing never queues work.
        /// The flag snapshot is taken here, on the UI thread, because FastFlagManager is not thread-safe.
        /// </summary>
        private async void RunSearch()
        {
            if (!IsLoaded)
                return;

            _searchCts?.Cancel();
            var cts = _searchCts = new CancellationTokenSource();

            var query = BuildQuery();
            var catalog = _workspace.Catalog;
            var index = _index;
            IReadOnlyDictionary<string, string>? snapshot = _indexDirty || index is null ? _workspace.Store.Snapshot() : null;

            string? selectedName = (DataGrid.SelectedItem as FastFlagRow)?.Name;

            // only show the spinner when a search is actually slow, and never after it has finished
            bool done = false;

            _ = Task.Delay(150, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled)
                    return;

                Dispatcher.BeginInvoke(() =>
                {
                    if (!done && _searchCts == cts)
                        SearchProgress.Visibility = Visibility.Visible;
                });
            }, TaskScheduler.Default);

            try
            {
                var (newIndex, result, rows) = await Task.Run(() =>
                {
                    var idx = snapshot is null ? index! : new FlagSearchIndex(catalog, snapshot);
                    var res = idx.Query(query, cts.Token);
                    return (idx, res, res.Items.Select(x => new FastFlagRow(x)).ToList());
                }, cts.Token);

                if (cts.IsCancellationRequested)
                    return;

                _index = newIndex;

                if (snapshot is not null)
                    _indexDirty = false;

                DataGrid.ItemsSource = rows;

                if (selectedName is not null)
                {
                    var row = rows.FirstOrDefault(x => x.Name == selectedName);

                    if (row is not null)
                    {
                        DataGrid.SelectedItem = row;
                        DataGrid.ScrollIntoView(row);
                    }
                }

                UpdateStatus(query, result);
                UpdateDetails();
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer search
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("FastFlagEditorPage::RunSearch", ex);
                StatusText.Text = String.Format(Strings.Menu_FastFlagEditor_Status_SearchFailed, ex.Message);
                StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ZeroStatusErrorBrush");
            }
            finally
            {
                done = true;

                if (_searchCts == cts)
                    SearchProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateStatus(FlagQuery query, FlagQueryResult result)
        {
            string text = result.TotalMatches == 1 ? Strings.Menu_FastFlagEditor_Status_OneFlag : String.Format(Strings.Menu_FastFlagEditor_Status_Flags, result.TotalMatches);

            if (result.TotalMatches > result.Items.Count)
                text += String.Format(Strings.Menu_FastFlagEditor_Status_Truncated, result.Items.Count);

            if (_workspace.Catalog.Entries.Count == 0)
                text += Strings.Menu_FastFlagEditor_Status_NoCatalog;
            else if (_workspace.CatalogMetadata?.FetchedAtUtc is DateTime fetched)
                text += String.Format(Strings.Menu_FastFlagEditor_Status_Catalog, _workspace.Catalog.Entries.Count, fetched.ToLocalTime());

            StatusText.Text = text;
            StatusText.ClearValue(TextBlock.ForegroundProperty); // back to the caption colour after an error

            if (result.TotalMatches > 0)
            {
                EmptyState.Visibility = Visibility.Collapsed;
                return;
            }

            bool noFlagsAtAll = query.Scope == FlagScope.Configured && String.IsNullOrWhiteSpace(query.Text) && query.Type is null;

            EmptyTitle.Text = noFlagsAtAll ? "No flags configured yet" : "No flags match your search";
            EmptyBody.Text = noFlagsAtAll
                ? "Add a flag, or apply a collection from the Fast Flags page."
                : "Try a different name or clear the filters.";
            EmptyAddButton.Visibility = noFlagsAtAll ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
        }

        private void ClearSearch(bool refresh = true)
        {
            SearchTextBox.Text = "";
            _searchFilter = "";

            if (refresh)
                ReloadList();
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox textbox)
                return;

            _searchFilter = textbox.Text;

            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // fired once per ComboBox during InitializeComponent; RunSearch ignores calls before Loaded
            RunSearch();
        }

        #endregion

        #region Editing

        private FlagEditService Editor => _workspace.CreateEditor();

        private void FlagsChangedHere()
        {
            _workspace.NotifyFlagsChanged();
        }

        /// <summary>Explains a failed edit. ZeroStrap: experimental mode is always on, so BlockedExperimental never fires.</summary>
        private bool ExplainFailure(FlagEditResult result, string name)
        {
            switch (result.Status)
            {
                case FlagEditStatus.BlockedExperimental:
                    // ZeroStrap: dead branch kept for enum completeness; experimental toggle removed.
                    _workspace.ExperimentalMode = true;
                    return true;

                case FlagEditStatus.Duplicate:
                    Frontend.ShowMessageBox(Strings.Menu_FastFlagEditor_AlreadyExists, MessageBoxImage.Information);
                    return false;

                case FlagEditStatus.Invalid:
                    Frontend.ShowMessageBox(result.Message ?? Strings.Menu_FastFlagEditor_InvalidFlag, MessageBoxImage.Error);
                    return false;

                default:
                    return false;
            }
        }

        private void ShowAddDialog()
        {
            var dialog = new AddFastFlagDialog();
            dialog.ShowDialog();

            if (dialog.Result != MessageBoxResult.OK)
                return;

            if (dialog.Tabs.SelectedIndex == 0)
                AddSingle(dialog.FlagNameTextBox.Text.Trim(), dialog.FlagValueTextBox.Text);
            else if (dialog.Tabs.SelectedIndex == 1)
                ImportJSON(dialog.JsonTextBox.Text);
        }

        private void AddSingle(string name, string value)
        {
            var result = Editor.Add(name, value);

            if (result.Status == FlagEditStatus.BlockedExperimental && ExplainFailure(result, name))
                result = Editor.Add(name, value);

            if (result.Status == FlagEditStatus.Duplicate)
            {
                ExplainFailure(result, name);
                FocusFlag(name, preferConfigured: true);
                return;
            }

            if (!result.Succeeded)
            {
                if (result.Status == FlagEditStatus.Invalid)
                {
                    ExplainFailure(result, name);
                    ShowAddDialog();
                }

                return;
            }

            FlagsChangedHere();
            FocusFlag(name, preferConfigured: true);
        }

        /// <summary>Makes sure a flag is visible in the list and selects it.</summary>
        private void FocusFlag(string name, bool preferConfigured)
        {
            if (!_showPresets && FastFlagManager.PresetFlags.Values.Contains(name))
            {
                TogglePresetsButton.IsChecked = true;
                _showPresets = true;
            }

            if (preferConfigured && ScopeFilter.SelectedIndex == 1)
                ScopeFilter.SelectedIndex = 0;

            TypeFilter.SelectedIndex = 0;

            SearchTextBox.Text = name;
            _searchFilter = name;
            _searchDebounce.Stop();
            ReloadList();
        }

        private void ImportJSON(string json)
        {
            Dictionary<string, object>? list = null;

            json = json.Trim();

            // autocorrect where possible
            if (!json.StartsWith('{'))
                json = '{' + json;

            if (!json.EndsWith('}'))
            {
                int lastIndex = json.LastIndexOf('}');

                if (lastIndex == -1)
                    json += '}';
                else
                    json = json.Substring(0, lastIndex+1);
            }

            try
            {
                var options = new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                list = JsonSerializer.Deserialize<Dictionary<string, object>>(json, options);

                if (list is null)
                    throw new Exception("JSON deserialization returned null");
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox(
                    String.Format(Strings.Menu_FastFlagEditor_InvalidJSON, ex.Message),
                    MessageBoxImage.Error
                );

                ShowAddDialog();

                return;
            }

            if (list.Count > 16)
            {
                var result = Frontend.ShowMessageBox(
                    Strings.Menu_FastFlagEditor_LargeConfig,
                    MessageBoxImage.Warning,
                    MessageBoxButton.YesNo
                );

                if (result != MessageBoxResult.Yes)
                    return;
            }

            var store = _workspace.Store;
            var conflictingFlags = list.Keys.Where(x => store.Get(x) is not null).ToList();
            bool overwriteConflicting = false;

            if (conflictingFlags.Any())
            {
                int count = conflictingFlags.Count;

                string message = String.Format(
                    Strings.Menu_FastFlagEditor_ConflictingImport,
                    count,
                    String.Join(", ", conflictingFlags.Take(25))
                );

                if (count > 25)
                    message += "...";

                var result = Frontend.ShowMessageBox(message, MessageBoxImage.Question, MessageBoxButton.YesNo);

                overwriteConflicting = result == MessageBoxResult.Yes;
            }

            // validate everything first, then write: a bad entry is reported instead of half-applied silently
            var invalid = new List<string>();
            var accepted = new List<KeyValuePair<string, string>>();

            foreach (var pair in list)
            {
                if (store.Get(pair.Key) is not null && !overwriteConflicting)
                    continue;

                string? val = pair.Value?.ToString();

                if (val is null)
                    continue;

                var validation = FastFlagRules.Validate(pair.Key, val);

                if (!validation.IsValid)
                {
                    invalid.Add(validation.Error ?? pair.Key);
                    continue;
                }

                // ZeroStrap: experimental toggle removed; every valid flag imports.
                accepted.Add(new(pair.Key, val));
            }

            foreach (var pair in accepted)
                store.Set(pair.Key, pair.Value);

            if (invalid.Count > 0)
            {
                Frontend.ShowMessageBox(
                    String.Format(Strings.Menu_FastFlagEditor_ImportSkippedInvalid, accepted.Count, invalid.Count, $"{String.Join("\n", invalid.Take(10))}{(invalid.Count > 10 ? "\n..." : "")}"),
                    MessageBoxImage.Warning);
            }

            ClearSearch(false);
            FlagsChangedHere();
        }

        private void DataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit)
                return;

            if (e.Row.DataContext is not FastFlagRow entry)
                return;

            if (e.EditingElement is not TextBox textbox)
                return;

            FlagEditResult result;

            if (e.Column == NameColumn)
            {
                string oldName = entry.Name;
                string newName = textbox.Text.Trim();

                if (newName == oldName)
                    return;

                if (!entry.IsConfigured)
                {
                    // catalog rows are reference data; renaming one makes no sense
                    e.Cancel = true;
                    textbox.Text = oldName;
                    return;
                }

                result = Editor.Rename(oldName, newName);

                if (result.Status == FlagEditStatus.BlockedExperimental && ExplainFailure(result, newName))
                    result = Editor.Rename(oldName, newName);
                else if (!result.Succeeded)
                    ExplainFailure(result, newName);

                if (!result.Succeeded)
                {
                    e.Cancel = true;
                    textbox.Text = oldName;
                    return;
                }

                entry.Name = newName;
            }
            else if (e.Column == ValueColumn)
            {
                string oldValue = entry.Value;
                string newValue = textbox.Text;

                if (newValue == oldValue)
                    return;

                if (entry.IsConfigured)
                {
                    result = Editor.SetValue(entry.Name, newValue);
                }
                else
                {
                    // typing a value on a catalog row adds the flag
                    result = Editor.Add(entry.Name, newValue);

                    if (result.Status == FlagEditStatus.BlockedExperimental && ExplainFailure(result, entry.Name))
                        result = Editor.Add(entry.Name, newValue);
                    else if (!result.Succeeded && result.Status != FlagEditStatus.BlockedExperimental)
                        ExplainFailure(result, entry.Name);
                }

                if (result.Status == FlagEditStatus.Invalid && entry.IsConfigured)
                    ExplainFailure(result, entry.Name);

                if (!result.Succeeded)
                {
                    e.Cancel = true;
                    textbox.Text = oldValue;
                    return;
                }

                entry.Value = newValue;
            }
            else
            {
                return;
            }

            // the grid is still inside its edit transaction; refresh once it has finished
            Dispatcher.BeginInvoke(FlagsChangedHere, DispatcherPriority.Background);
        }

        #endregion

        #region Details panel

        private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDetails();
            UpdateDetailsVisibility();
        }

        private bool _detailsDocked;

        /// <summary>Docked below the grid, the details panel only shows while a flag is selected so the grid keeps its rows.</summary>
        private void UpdateDetailsVisibility() =>
            DetailsPanel.Visibility = _detailsDocked && DataGrid.SelectedItem is null ? Visibility.Collapsed : Visibility.Visible;

        private void UpdateDetails()
        {
            DetailError.Visibility = Visibility.Collapsed;
            DetailTags.Children.Clear();

            if (DataGrid.SelectedItems.Count != 1 || DataGrid.SelectedItem is not FastFlagRow row)
            {
                DetailName.Text = DataGrid.SelectedItems.Count > 1 ? String.Format(Strings.Menu_FastFlagEditor_Details_MultipleSelected, DataGrid.SelectedItems.Count) : Strings.Menu_FastFlagEditor_Details_NothingSelected;
                DetailValue.Text = "";
                DetailValue.IsEnabled = DetailSetButton.IsEnabled = DetailRemoveButton.IsEnabled = false;
                return;
            }

            DetailName.Text = row.Name;

            AddTag(row.TypeLabel.ToUpperInvariant(), outline: true);
            AddTag(row.IsConfigured ? Strings.Menu_FastFlagEditor_Details_TagSet : Strings.Menu_FastFlagEditor_Details_TagNotSet, outline: !row.IsConfigured);

            if (row.InCatalog && row.Layer is not null)
                AddTag(row.Layer.ToUpperInvariant(), outline: true);


            DetailValue.Text = row.Value;
            DetailValue.IsEnabled = DetailSetButton.IsEnabled = true;
            DetailRemoveButton.IsEnabled = row.IsConfigured;
            DetailSetButton.Content = row.IsConfigured ? Strings.Menu_FastFlagEditor_Details_Set : Strings.Menu_FastFlagEditor_Details_Add;
        }

        private void AddTag(string text, bool outline)
        {
            var tag = new Border
            {
                Style = (Style)FindResource(outline ? "ZeroTagOutlineStyle" : "ZeroTagStyle"),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock { Text = text, Style = (Style)FindResource("ZeroTagTextStyle") }
            };

            if (outline)
                ((TextBlock)tag.Child).SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

            DetailTags.Children.Add(tag);
        }

        private void ApplyDetailValue()
        {
            if (DataGrid.SelectedItem is not FastFlagRow row)
                return;

            string value = DetailValue.Text;
            var result = row.IsConfigured ? Editor.SetValue(row.Name, value) : Editor.Add(row.Name, value);

            if (result.Status == FlagEditStatus.BlockedExperimental && ExplainFailure(result, row.Name))
                result = Editor.Add(row.Name, value);

            if (!result.Succeeded)
            {
                if (result.Message is not null)
                {
                    DetailError.Text = result.Message;
                    DetailError.Visibility = Visibility.Visible;
                }

                return;
            }

            FlagsChangedHere();
        }

        private void DetailSetButton_Click(object sender, RoutedEventArgs e) => ApplyDetailValue();

        private void DetailValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                ApplyDetailValue();
        }

        private void DetailRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataGrid.SelectedItem is not FastFlagRow row || !row.IsConfigured)
                return;

            Editor.Remove(row.Name);
            FlagsChangedHere();
        }

        #endregion

        // refresh list on page load to synchronize with preset page
        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // ZeroStrap: experimental toggle removed; workspace always permits any valid flag.
            _workspace.ExperimentalMode = true;

            if (!_subscribed)
            {
                _workspace.FlagsChanged += Workspace_DataChanged;
                _workspace.CatalogChanged += Workspace_DataChanged;
                _subscribed = true;
            }

            ReloadList();

            // automatic catalog check, only if the user enabled it and the interval elapsed
            _ = _workspace.RunAutomaticCheckIfDueAsync();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            // pages are cached by the navigation service; unhook so a closed window is not kept alive
            if (_subscribed)
            {
                _workspace.FlagsChanged -= Workspace_DataChanged;
                _workspace.CatalogChanged -= Workspace_DataChanged;
                _subscribed = false;
            }

            _searchDebounce.Stop();
            _searchCts?.Cancel();
        }

        private void Workspace_DataChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => Workspace_DataChanged(sender, e));
                return;
            }

            ReloadList();
        }

        /// <summary>Raised when the editor is hosted in the shell and wants to go back; the Configure page closes the workspace.</summary>
        public event EventHandler? CloseRequested;

        /// <summary>Puts keyboard focus on Back (used when the workspace opens).</summary>
        public void FocusBackButton() => BackButton.Focus();

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is INavigationWindow window)
                window.Navigate(typeof(FastFlagsPage));
            else
                CloseRequested?.Invoke(this, EventArgs.Empty); // hosted in the Obsidian shell
        }

        /// <summary>Details sit beside the grid when there is room, and dock below it (scrolling) when there is not.</summary>
        private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool wide = e.NewSize.Width >= 1000;

            if (wide)
            {
                Grid.SetRow(DetailsPanel, 0);
                Grid.SetColumn(DetailsPanel, 1);
                Grid.SetColumnSpan(DetailsPanel, 1);
                DetailsPanel.Width = 280;
                DetailsPanel.MaxHeight = Double.PositiveInfinity;
                DetailsPanel.Margin = new Thickness(12, 0, 0, 0);

                // stacked: identity above the value editor
                DetailsSecondColumn.Width = new GridLength(0);
                Grid.SetRow(DetailsEditor, 1);
                Grid.SetColumn(DetailsEditor, 0);
                DetailsEditor.Margin = new Thickness(0, 10, 0, 0);
            }
            else
            {
                Grid.SetRow(DetailsPanel, 1);
                Grid.SetColumn(DetailsPanel, 0);
                Grid.SetColumnSpan(DetailsPanel, 2);
                DetailsPanel.Width = Double.NaN;
                DetailsPanel.MaxHeight = 200;
                DetailsPanel.Margin = new Thickness(0, 12, 0, 0);

                // side by side: keeps the docked panel short so the grid keeps its rows
                DetailsSecondColumn.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(DetailsEditor, 0);
                Grid.SetColumn(DetailsEditor, 1);
                DetailsEditor.Margin = new Thickness(24, 0, 0, 0);
            }

            _detailsDocked = !wide;
            UpdateDetailsVisibility();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e) => ShowAddDialog();

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var names = DataGrid.SelectedItems.OfType<FastFlagRow>().Where(x => x.IsConfigured).Select(x => x.Name).ToList();

            if (names.Count == 0)
                return;

            var editor = Editor;

            foreach (string name in names)
                editor.Remove(name);

            FlagsChangedHere();
        }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton button)
                return;

            _showPresets = button.IsChecked ?? false;
            ReloadList();
        }

        private void ExportJSONButton_Click(object sender, RoutedEventArgs e)
        {
            string json = JsonSerializer.Serialize(App.FastFlags.Prop, new JsonSerializerOptions { WriteIndented = true });
            Clipboard.SetDataObject(json);
            Frontend.ShowMessageBox(Strings.Menu_FastFlagEditor_JsonCopiedToClipboard, MessageBoxImage.Information);
        }
    }
}
