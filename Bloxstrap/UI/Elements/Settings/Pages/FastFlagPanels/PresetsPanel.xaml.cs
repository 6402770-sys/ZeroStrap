using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using Microsoft.Win32;

using Bloxstrap.FastFlags;
using Bloxstrap.UI.Elements.Dialogs;

namespace Bloxstrap.UI.Elements.Settings.Pages.FastFlagPanels
{
    /// <summary>
    /// Smart presets: named groups of flags stored in FastFlagWorkspace.json. Checking a preset only
    /// selects it; nothing changes until "Review &amp; apply" is confirmed, and every apply can be reverted.
    /// </summary>
    public partial class PresetsPanel : UserControl
    {
        private sealed class PresetItem
        {
            public FlagPreset Preset { get; }

            public string Name => Preset.Name;

            public bool Enabled
            {
                get => Preset.Enabled;
                set => Preset.Enabled = value;
            }

            public string Summary => Preset.Flags.Count == 1 ? Strings.Menu_FastFlagEditor_Status_OneFlag : String.Format(Strings.Menu_FastFlagEditor_Status_Flags, Preset.Flags.Count);

            public PresetItem(FlagPreset preset) => Preset = preset;
        }

        private static FastFlagWorkspace Workspace => FastFlagWorkspace.Instance;

        private static FastFlagWorkspaceState State => Workspace.State.Prop;

        public PresetsPanel()
        {
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e) => Refresh();

        private FlagPreset? Selected => (PresetList.SelectedItem as PresetItem)?.Preset;

        private void Save() => Workspace.State.Save();

        private void Refresh(FlagPreset? select = null)
        {
            select ??= Selected;

            var items = State.Presets.Select(x => new PresetItem(x)).ToList();
            PresetList.ItemsSource = items;
            PresetList.SelectedItem = items.FirstOrDefault(x => x.Preset == select) ?? items.FirstOrDefault();

            ListEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            RefreshSelected();
            RefreshFooter();
        }

        private void RefreshSelected()
        {
            var preset = Selected;
            bool has = preset is not null;

            DuplicateButton.IsEnabled = RenameButton.IsEnabled = DeleteButton.IsEnabled = EditorRow.IsEnabled = has;

            SelectedName.Text = preset?.Name ?? Strings.Menu_FastFlagEditor_Presets_SelectPreset;
            FlagsGrid.ItemsSource = preset?.Flags.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
            FlagsEmptyText.Visibility = has && preset!.Flags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshFooter()
        {
            var enabled = State.Presets.Where(x => x.Enabled).ToList();

            SelectionSummary.Text = enabled.Count switch
            {
                0 => Strings.Menu_FastFlagEditor_Presets_SelectionNone,
                1 => String.Format(Strings.Menu_FastFlagEditor_Presets_SelectionOne, enabled[0].Name),
                _ => String.Format(Strings.Menu_FastFlagEditor_Presets_SelectionMany, enabled.Count)
            };

            ApplyButton.IsEnabled = enabled.Count > 0;

            var last = State.LastApply;
            RevertButton.IsEnabled = last is not null;
            LastApplyText.Text = last is null
                ? ""
                : String.Format(Strings.Menu_FastFlagEditor_Presets_LastApply, String.Join(", ", last.PresetNames), last.Applied.Count, last.AppliedAt.ToLocalTime());
        }

        private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSelected();

        private void EnabledCheckBox_Click(object sender, RoutedEventArgs e)
        {
            Save();
            RefreshFooter();
        }

        #region Preset management

        private Window? Owner => Window.GetWindow(this);

        private void Add(FlagPreset preset)
        {
            State.Presets.Add(preset);
            Save();
            Refresh(preset);
        }

        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            string? name = TextPromptDialog.Ask(Owner, Strings.Menu_FastFlagEditor_Presets_NewTitle, Strings.Menu_FastFlagEditor_Presets_NewPrompt, PresetEngine.UniqueName(State.Presets, Strings.Menu_FastFlagEditor_Presets_NewTitle));

            if (name is not null)
                Add(new FlagPreset { Name = PresetEngine.UniqueName(State.Presets, name) });
        }

        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            var snapshot = Workspace.Store.Snapshot();

            if (snapshot.Count == 0)
            {
                Frontend.ShowMessageBox(Strings.Menu_FastFlagEditor_Presets_NothingToCapture, MessageBoxImage.Information);
                return;
            }

            string? name = TextPromptDialog.Ask(Owner, Strings.Menu_FastFlagEditor_Presets_NewTitle, String.Format(Strings.Menu_FastFlagEditor_Presets_CapturePrompt, snapshot.Count), PresetEngine.UniqueName(State.Presets, Strings.Menu_FastFlagEditor_Presets_CaptureDefaultName));

            if (name is not null)
                Add(PresetEngine.FromStore(Workspace.Store, PresetEngine.UniqueName(State.Presets, name)));
        }

        private void DuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is FlagPreset preset)
                Add(preset.Clone(PresetEngine.UniqueName(State.Presets, String.Format(Strings.Menu_FastFlagEditor_Presets_CopyName, preset.Name))));
        }

        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not FlagPreset preset)
                return;

            string? name = TextPromptDialog.Ask(Owner, Strings.Menu_FastFlagEditor_Presets_RenameTitle, Strings.Menu_FastFlagEditor_Presets_RenamePrompt, preset.Name);

            if (name is null || name == preset.Name)
                return;

            preset.Name = PresetEngine.UniqueName(State.Presets.Where(x => x != preset), name);
            Save();
            Refresh(preset);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not FlagPreset preset)
                return;

            var answer = Frontend.ShowMessageBox(
                String.Format(Strings.Menu_FastFlagEditor_Presets_DeleteConfirm, preset.Name),
                MessageBoxImage.Warning,
                MessageBoxButton.YesNo);

            if (answer != MessageBoxResult.Yes)
                return;

            State.Presets.Remove(preset);
            Save();
            Refresh();
        }

        #endregion

        #region Preset contents

        private void SetFlagInPreset()
        {
            if (Selected is not FlagPreset preset)
                return;

            string name = NewFlagName.Text.Trim();
            string value = NewFlagValue.Text;

            var validation = FastFlagRules.Validate(name, value);

            if (!validation.IsValid)
            {
                Frontend.ShowMessageBox(validation.Error ?? Strings.Menu_FastFlagEditor_InvalidFlag, MessageBoxImage.Error);
                return;
            }

            if (!Workspace.ExperimentalMode && RobloxFlagAllowlist.GetEffect(name) == FlagEffect.Experimental)
            {
                Frontend.ShowMessageBox(
                    String.Format(Strings.Menu_FastFlagEditor_Presets_ExperimentalBlocked, name),
                    MessageBoxImage.Information);
                return;
            }

            preset.Flags[name] = value;
            Save();

            NewFlagName.Text = NewFlagValue.Text = "";
            Refresh(preset);
        }

        private void SetFlagButton_Click(object sender, RoutedEventArgs e) => SetFlagInPreset();

        private void NewFlagValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                SetFlagInPreset();
        }

        private void RemoveFlagButton_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not FlagPreset preset)
                return;

            var names = FlagsGrid.SelectedItems.OfType<KeyValuePair<string, string>>().Select(x => x.Key).ToList();

            if (names.Count == 0)
                return;

            foreach (string name in names)
                preset.Flags.Remove(name);

            Save();
            Refresh(preset);
        }

        #endregion

        #region Apply / revert

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var presets = State.Presets.Where(x => x.Enabled).ToList();

            if (presets.Count == 0)
                return;

            var merge = PresetEngine.Merge(presets);
            var dialog = new PresetApplyDialog(Workspace.Store, merge, presets.Select(x => x.Name).ToList()) { Owner = Owner };
            dialog.ShowDialog();

            if (!dialog.Confirmed || dialog.Plan is null)
                return;

            try
            {
                State.LastApply = PresetEngine.Apply(Workspace.Store, dialog.Plan, presets.Select(x => x.Name), DateTime.UtcNow);
            }
            catch (InvalidOperationException ex)
            {
                Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
                return;
            }

            Save();
            Workspace.NotifyFlagsChanged();
            RefreshFooter();

            Frontend.ShowMessageBox(
                String.Format(Strings.Menu_FastFlagEditor_Presets_Applied, State.LastApply.Applied.Count),
                MessageBoxImage.Information);
        }

        private void RevertButton_Click(object sender, RoutedEventArgs e)
        {
            if (State.LastApply is not PresetApplySnapshot last)
                return;

            var answer = Frontend.ShowMessageBox(
                String.Format(Strings.Menu_FastFlagEditor_Presets_RevertConfirm, last.Original.Count, String.Join(", ", last.PresetNames)),
                MessageBoxImage.Question,
                MessageBoxButton.YesNo);

            if (answer != MessageBoxResult.Yes)
                return;

            var skipped = PresetEngine.Revert(Workspace.Store, last);

            State.LastApply = null;
            Save();
            Workspace.NotifyFlagsChanged();
            RefreshFooter();

            if (skipped.Count > 0)
            {
                Frontend.ShowMessageBox(
                    String.Format(Strings.Menu_FastFlagEditor_Presets_RevertSkipped, skipped.Count, String.Join(", ", skipped.Take(10))),
                    MessageBoxImage.Information);
            }
        }

        #endregion

        #region Import / export

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = Strings.Menu_FastFlagEditor_Presets_OpenFilter };

            if (dialog.ShowDialog() != true)
                return;

            string json;

            try
            {
                var info = new FileInfo(dialog.FileName);

                if (info.Length > 4 * 1024 * 1024)
                {
                    Frontend.ShowMessageBox(Strings.Menu_FastFlagEditor_Presets_FileTooLarge, MessageBoxImage.Error);
                    return;
                }

                json = File.ReadAllText(dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
                return;
            }

            var imported = PresetFile.Import(json, out var errors);

            if (errors.Count > 0)
            {
                Frontend.ShowMessageBox(
                    String.Format(Strings.Menu_FastFlagEditor_Presets_ImportFailed, $"{String.Join("\n", errors.Take(12))}{(errors.Count > 12 ? "\n..." : "")}"),
                    MessageBoxImage.Error);
                return;
            }

            var experimental = imported.SelectMany(x => x.Flags.Keys).Where(x => RobloxFlagAllowlist.GetEffect(x) == FlagEffect.Experimental).Distinct().Count();

            foreach (var preset in imported)
            {
                preset.Name = PresetEngine.UniqueName(State.Presets, preset.Name);
                State.Presets.Add(preset);
            }

            Save();
            Refresh(imported.First());

            Frontend.ShowMessageBox(
                String.Format(Strings.Menu_FastFlagEditor_Presets_Imported, imported.Count) +
                (experimental > 0 ? String.Format(Strings.Menu_FastFlagEditor_Presets_ImportedExperimental, experimental) : ""),
                MessageBoxImage.Information);
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var presets = State.Presets.Where(x => x.Enabled).ToList();

            if (presets.Count == 0)
                presets = State.Presets.ToList();

            if (presets.Count == 0)
            {
                Frontend.ShowMessageBox(Strings.Menu_FastFlagEditor_Presets_NothingToExport, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = Strings.Menu_FastFlagEditor_Presets_SaveFilter,
                FileName = presets.Count == 1 ? $"{presets[0].Name}.json" : "fastflag-presets.json"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                File.WriteAllText(dialog.FileName, PresetFile.Export(presets));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}
