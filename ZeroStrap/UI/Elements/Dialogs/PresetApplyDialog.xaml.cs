using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

using ZeroStrap.FastFlags;

namespace ZeroStrap.UI.Elements.Dialogs
{
    /// <summary>
    /// Review step before presets touch the configuration: resolve conflicts, read the change list,
    /// then confirm. Only computes a plan; the caller applies it.
    /// </summary>
    public partial class PresetApplyDialog
    {
        public sealed class ConflictOption
        {
            public string Label { get; init; } = "";

            public string Value { get; init; } = "";
        }

        public sealed class ConflictRow : INotifyPropertyChanged
        {
            private ConflictOption? _selected;

            public string Flag { get; init; } = "";

            public List<ConflictOption> Options { get; init; } = new();

            public ConflictOption? Selected
            {
                get => _selected;
                set
                {
                    _selected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        public sealed class ChangeRow
        {
            public string Flag { get; init; } = "";

            public string KindLabel { get; init; } = "";

            public string Summary { get; init; } = "";
        }

        private readonly IFlagStore _store;
        private readonly PresetMergeResult _merge;
        private readonly List<ConflictRow> _conflicts;

        public PresetPlan? Plan { get; private set; }

        public bool Confirmed { get; private set; }

        public PresetApplyDialog(IFlagStore store, PresetMergeResult merge, IReadOnlyList<string> presetNames)
        {
            _store = store;
            _merge = merge;

            InitializeComponent();

            HeaderText.Text = presetNames.Count == 1
                ? String.Format(Strings.Dialog_PresetApply_HeaderOne, presetNames[0])
                : String.Format(Strings.Dialog_PresetApply_HeaderMany, presetNames.Count, String.Join(", ", presetNames));

            // no pre-selection: the user has to decide every conflict explicitly
            _conflicts = merge.Conflicts.Select(c => new ConflictRow
            {
                Flag = c.Flag,
                Options = c.Candidates.Select(x => new ConflictOption { Label = $"{x.Value}  ({x.PresetName})", Value = x.Value }).ToList()
            }).ToList();

            ConflictsList.ItemsSource = _conflicts;
            ConflictsSection.Visibility = _conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            Recompute();
        }

        private void Recompute()
        {
            var resolutions = _conflicts
                .Where(x => x.Selected is not null)
                .ToDictionary(x => x.Flag, x => x.Selected!.Value, StringComparer.Ordinal);

            Plan = PresetEngine.Plan(_store, _merge, resolutions);

            var rows = Plan.Changes
                .Where(x => x.Kind != FlagChangeKind.Unchanged)
                .Select(x => new ChangeRow
                {
                    Flag = x.Flag,
                    KindLabel = x.Kind == FlagChangeKind.Add ? Strings.Dialog_PresetApply_KindAdd : Strings.Dialog_PresetApply_KindChange,
                    Summary = x.Kind == FlagChangeKind.Add ? x.NewValue : $"{x.OldValue} → {x.NewValue}"
                })
                .ToList();

            ChangesList.ItemsSource = rows;

            int unchanged = Plan.Changes.Count(x => x.Kind == FlagChangeKind.Unchanged);
            ChangesEmptyText.Text = rows.Count == 0
                ? (unchanged > 0 ? String.Format(Strings.Dialog_PresetApply_AllUnchanged, unchanged) : Strings.Dialog_PresetApply_NoFlags)
                : "";
            ChangesEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            int unresolved = _conflicts.Count(x => x.Selected is null);
            var errors = Plan.Errors.Where(x => !x.StartsWith("Unresolved conflict", StringComparison.Ordinal)).ToList();

            ErrorsText.Text = String.Join("\n", errors);
            ErrorsText.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            ApplyButton.IsEnabled = Plan.IsValid && Plan.EffectiveChanges > 0;
            ApplyButton.Content = unresolved > 0
                ? String.Format(unresolved == 1 ? Strings.Dialog_PresetApply_ResolveOne : Strings.Dialog_PresetApply_ResolveMany, unresolved)
                : String.Format(Plan.EffectiveChanges == 1 ? Strings.Dialog_PresetApply_ApplyOne : Strings.Dialog_PresetApply_ApplyMany, Plan.EffectiveChanges);
        }

        private void Conflict_SelectionChanged(object sender, SelectionChangedEventArgs e) => Recompute();

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            Recompute();

            if (Plan is null || !Plan.IsValid)
                return;

            Confirmed = true;
            Close();
        }
    }
}
