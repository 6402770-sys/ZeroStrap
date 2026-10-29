using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using Bloxstrap.FastFlags;

namespace Bloxstrap.UI.Elements.Settings.Pages.FastFlagPanels
{
    public partial class BindingsPanel : UserControl
    {
        private sealed class BindingRow
        {
            public FlagBinding Binding { get; }

            public bool Enabled => Binding.Enabled;

            public string Gesture => FlagBindingEngine.NormalizeGesture(Binding.Gesture) ?? Binding.Gesture;

            public string FlagName => Binding.FlagName;

            public string ValuesText
            {
                get
                {
                    var values = FlagBindingEngine.EffectiveValues(Binding).ToList();

                    if (Binding.IncludeUnset)
                        values.Add(Strings.Menu_FastFlagEditor_Shortcuts_NotSetValue);

                    return String.Join(" → ", values);
                }
            }

            public string Warning { get; }

            public BindingRow(FlagBinding binding, bool conflicted)
            {
                Binding = binding;

                string? invalid = FlagBindingEngine.Validate(binding);
                Warning = invalid is not null ? Strings.Menu_FastFlagEditor_Shortcuts_Invalid : conflicted ? Strings.Menu_FastFlagEditor_Shortcuts_UsedTwice : "";
            }
        }

        private static FastFlagWorkspace Workspace => FastFlagWorkspace.Instance;

        private static FastFlagWorkspaceState State => Workspace.State.Prop;

        private FlagBinding? _editing;

        private string? _capturedGesture;

        public BindingsPanel()
        {
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e) => Refresh();

        private void Refresh()
        {
            var conflicted = FlagBindingEngine.FindConflicts(State.Bindings).SelectMany(x => x.Bindings).ToHashSet();

            var rows = State.Bindings.Select(x => new BindingRow(x, conflicted.Contains(x))).ToList();
            var selected = (BindingsGrid.SelectedItem as BindingRow)?.Binding;

            BindingsGrid.ItemsSource = rows;
            BindingsGrid.SelectedItem = rows.FirstOrDefault(x => x.Binding == selected);

            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MasterToggle.IsChecked = State.BindingsEnabled;

            UpdateButtons();
        }

        private void UpdateButtons() => EditButton.IsEnabled = DeleteButton.IsEnabled = BindingsGrid.SelectedItem is not null;

        private void Save() => Workspace.State.Save();

        private void BindingsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

        private void BindingsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => BeginEdit();

        private void MasterToggle_Click(object sender, RoutedEventArgs e)
        {
            State.BindingsEnabled = MasterToggle.IsChecked == true;
            Save();
        }

        private void EnabledCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox { Tag: BindingRow row } box)
            {
                row.Binding.Enabled = box.IsChecked == true;
                Save();
                Refresh();
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (BindingsGrid.SelectedItem is not BindingRow row)
                return;

            State.Bindings.Remove(row.Binding);
            Save();

            if (_editing == row.Binding)
                ResetEditor();

            Refresh();
        }

        private void EditButton_Click(object sender, RoutedEventArgs e) => BeginEdit();

        private void BeginEdit()
        {
            if (BindingsGrid.SelectedItem is not BindingRow row)
                return;

            _editing = row.Binding;
            _capturedGesture = FlagBindingEngine.NormalizeGesture(row.Binding.Gesture);

            GestureBox.Text = _capturedGesture ?? Strings.Menu_FastFlagEditor_Shortcuts_PressKeys;
            FlagBox.Text = row.Binding.FlagName;
            ValuesBox.Text = String.Join(", ", row.Binding.Values);
            UnsetBox.IsChecked = row.Binding.IncludeUnset;

            EditorTitle.Text = Strings.Menu_FastFlagEditor_Shortcuts_EditTitle;
            SaveBindingButton.Content = Strings.Menu_FastFlagEditor_Shortcuts_Save;
            CancelEditButton.Visibility = Visibility.Visible;
            EditorError.Visibility = Visibility.Collapsed;
        }

        private void ResetEditor()
        {
            _editing = null;
            _capturedGesture = null;

            GestureBox.Text = Strings.Menu_FastFlagEditor_Shortcuts_PressKeys;
            FlagBox.Text = ValuesBox.Text = "";
            UnsetBox.IsChecked = false;

            EditorTitle.Text = Strings.Menu_FastFlagEditor_Shortcuts_NewTitle;
            SaveBindingButton.Content = Strings.Menu_FastFlagEditor_Shortcuts_Add;
            CancelEditButton.Visibility = Visibility.Collapsed;
            EditorError.Visibility = Visibility.Collapsed;
        }

        private void CancelEditButton_Click(object sender, RoutedEventArgs e) => ResetEditor();

        /// <summary>Records the pressed combination instead of typing it.</summary>
        private void GestureBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;

            string? gesture = FlagBindingHost.DescribeGesture(e);

            // modifier-only presses just wait for the real key
            if (gesture is null)
                return;

            string? normalized = FlagBindingEngine.NormalizeGesture(gesture);

            _capturedGesture = normalized;
            GestureBox.Text = normalized ?? String.Format(Strings.Menu_FastFlagEditor_Shortcuts_NotAllowed, gesture);
        }

        private void SaveBindingButton_Click(object sender, RoutedEventArgs e)
        {
            var binding = new FlagBinding
            {
                Gesture = _capturedGesture ?? "",
                FlagName = FlagBox.Text.Trim(),
                Values = ValuesBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                IncludeUnset = UnsetBox.IsChecked == true
            };

            string? error = FlagBindingEngine.Validate(binding);

            if (error is null && !Workspace.ExperimentalMode && RobloxFlagAllowlist.GetEffect(binding.FlagName) == FlagEffect.Experimental)
                error = String.Format(Strings.Menu_FastFlagEditor_Shortcuts_ExperimentalBlocked, binding.FlagName);

            if (error is not null)
            {
                EditorError.Text = error;
                EditorError.Visibility = Visibility.Visible;
                return;
            }

            var others = State.Bindings.Where(x => x != _editing && x.Enabled);
            var clash = FlagBindingEngine.Find(others, FlagBindingEngine.NormalizeGesture(binding.Gesture)!);

            if (clash is not null)
            {
                var answer = Frontend.ShowMessageBox(
                    String.Format(Strings.Menu_FastFlagEditor_Shortcuts_Clash, binding.Gesture, clash.FlagName),
                    MessageBoxImage.Warning,
                    MessageBoxButton.YesNo);

                if (answer != MessageBoxResult.Yes)
                    return;
            }

            if (_editing is not null)
            {
                _editing.Gesture = binding.Gesture;
                _editing.FlagName = binding.FlagName;
                _editing.Values = binding.Values;
                _editing.IncludeUnset = binding.IncludeUnset;
            }
            else
            {
                State.Bindings.Add(binding);
            }

            Save();
            ResetEditor();
            Refresh();
        }
    }
}
