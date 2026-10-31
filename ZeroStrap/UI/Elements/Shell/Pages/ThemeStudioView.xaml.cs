using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

using Microsoft.Win32;

using ZeroStrap.Models.Theming;
using ZeroStrap.UI.Elements.Controls;
using ZeroStrap.UI.Elements.Dialogs;
using ZeroStrap.UI.Theming;
using ZeroStrap.UI.Utility;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    /// <summary>A theme in the Theme Studio rail: four swatch colours plus its Active / Following Windows status.</summary>
    public sealed class ThemeRailItem : INotifyPropertyChanged
    {
        private string _status = "";

        public ThemeRailItem(ThemeDefinition definition)
        {
            Id = definition.Id;
            Kind = definition.Kind;
            Update(definition);
        }

        public string Id { get; }
        public ThemeKind Kind { get; }

        /// <summary>Id as understood by <see cref="ThemeManager"/>.</summary>
        public string ManagerId => Kind == ThemeKind.Builtin ? Id : ThemeManager.CustomPrefix + Id;

        public string Name { get; private set; } = "";
        public Brush Background { get; private set; } = Brushes.Transparent;
        public Brush Surface { get; private set; } = Brushes.Transparent;
        public Brush Text { get; private set; } = Brushes.Transparent;
        public Brush Accent { get; private set; } = Brushes.Transparent;

        public string Status
        {
            get => _status;
            set
            {
                if (_status == value)
                    return;

                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusVisibility)));
            }
        }

        public Visibility StatusVisibility => _status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(ThemeDefinition definition)
        {
            static Brush Paint(string hex)
            {
                var brush = ColorMath.TryParseHex(hex, out var rgb) ? new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B)) : new SolidColorBrush(Colors.Transparent);
                brush.Freeze();
                return brush;
            }

            Name = definition.Name;
            Background = Paint(definition.Colors.Background);
            Surface = Paint(definition.Colors.Surface);
            Text = Paint(definition.Colors.TextPrimary);
            Accent = Paint(definition.Colors.Accent);

            foreach (var name in new[] { nameof(Name), nameof(Background), nameof(Surface), nameof(Text), nameof(Accent) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>The Themes tab of the Appearance page: theme rail, live preview and colour editor.</summary>
    public partial class ThemeStudioView : UserControl
    {
        private sealed record ColorField(string Key, string Label, string Group, Func<ThemeColors, string> Get, Action<ThemeColors, string> Set);

        private static readonly ColorField[] Fields =
        {
            new(nameof(ThemeColors.Background), "Background", "Surfaces", c => c.Background, (c, v) => c.Background = v),
            new(nameof(ThemeColors.BackgroundSecondary), "Secondary background", "Surfaces", c => c.BackgroundSecondary, (c, v) => c.BackgroundSecondary = v),
            new(nameof(ThemeColors.Surface), "Surface", "Surfaces", c => c.Surface, (c, v) => c.Surface = v),
            new(nameof(ThemeColors.SurfaceElevated), "Elevated surface", "Surfaces", c => c.SurfaceElevated, (c, v) => c.SurfaceElevated = v),
            new(nameof(ThemeColors.TextPrimary), "Primary text", "Text", c => c.TextPrimary, (c, v) => c.TextPrimary = v),
            new(nameof(ThemeColors.TextSecondary), "Secondary text", "Text", c => c.TextSecondary, (c, v) => c.TextSecondary = v),
            new(nameof(ThemeColors.TextMuted), "Muted text", "Text", c => c.TextMuted, (c, v) => c.TextMuted = v),
            new(nameof(ThemeColors.Border), "Border", "Lines", c => c.Border, (c, v) => c.Border = v),
            new(nameof(ThemeColors.Accent), "Accent", "Accent", c => c.Accent, (c, v) => c.Accent = v),
            new(nameof(ThemeColors.OnAccent), "Text on accent", "Accent", c => c.OnAccent, (c, v) => c.OnAccent = v),
            new(nameof(ThemeColors.Success), "Success", "Status", c => c.Success, (c, v) => c.Success = v),
            new(nameof(ThemeColors.Warning), "Warning", "Status", c => c.Warning, (c, v) => c.Warning = v),
            new(nameof(ThemeColors.Danger), "Danger", "Status", c => c.Danger, (c, v) => c.Danger = v),
            new(nameof(ThemeColors.Info), "Info", "Status", c => c.Info, (c, v) => c.Info = v)
        };

        private readonly List<ThemeRailItem> _builtinItems = new();
        private readonly List<ThemeRailItem> _customItems = new();
        private readonly List<ColorRowVm> _rows = new();
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
        private readonly DispatcherTimer _noteTimer = new() { Interval = TimeSpan.FromSeconds(5) };

        private ThemeDefinition _selected = BuiltinThemes.Obsidian();
        private string? _rememberedId;
        private bool _dirty;
        private bool _suppressRail;
        private bool _suppressToggles;
        private bool _subscribed;

        public ThemeStudioView()
        {
            InitializeComponent();

            _saveTimer.Tick += (_, _) => FlushSave();
            _noteTimer.Tick += (_, _) =>
            {
                _noteTimer.Stop();
                NoteText.Visibility = Visibility.Collapsed;
            };

            Loaded += (_, _) => OnShown();
            Unloaded += (_, _) => OnHidden();
        }

        private static ThemeStore Store => ThemeManager.Store;

        private Window? Owner => Window.GetWindow(this);

        // ---------------------------------------------------------------- lifecycle

        private void OnShown()
        {
            if (!_subscribed)
            {
                _subscribed = true;
                ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
                Dispatcher.ShutdownStarted += Dispatcher_ShutdownStarted;
            }

            AnimationsToggle.IsChecked = !App.Settings.Prop.ReduceMotion;
            Reload(_rememberedId ?? DefaultSelectionId());
        }

        private void OnHidden()
        {
            FlushSave();

            if (_subscribed)
            {
                _subscribed = false;
                ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
                Dispatcher.ShutdownStarted -= Dispatcher_ShutdownStarted;
            }
        }

        private void Dispatcher_ShutdownStarted(object? sender, EventArgs e) => FlushSave();

        private void ThemeManager_ThemeChanged(ThemeDefinition _)
        {
            if (Dispatcher.CheckAccess())
                UpdateActiveState();
            else
                Dispatcher.BeginInvoke(new Action(UpdateActiveState));
        }

        /// <summary>Called by the shell after a theme was changed from outside this page (command palette).</summary>
        public void Refresh()
        {
            AnimationsToggle.IsChecked = !App.Settings.Prop.ReduceMotion;
            UpdateActiveState();
        }

        /// <summary>The theme to show first: the active one, or the one Windows currently selects.</summary>
        private static string DefaultSelectionId() => ThemeManager.ActiveDefinition.Id;

        // ---------------------------------------------------------------- rail

        /// <summary>Rebuilds both lists from the store and selects <paramref name="selectId"/> (falls back to the active theme).</summary>
        private void Reload(string? selectId)
        {
            _builtinItems.Clear();
            _customItems.Clear();

            foreach (var def in Store.List())
            {
                var item = new ThemeRailItem(def);
                (def.Kind == ThemeKind.Builtin ? _builtinItems : _customItems).Add(item);
            }

            _suppressRail = true;
            BuiltinList.ItemsSource = null;
            BuiltinList.ItemsSource = _builtinItems;
            CustomList.ItemsSource = null;
            CustomList.ItemsSource = _customItems;
            _suppressRail = false;

            CustomEmptyHint.Visibility = _customItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            bool known = selectId is not null && (_builtinItems.Concat(_customItems).Any(i => i.Id == selectId));
            Select(known ? selectId! : DefaultSelectionId(), flush: false);
        }

        private void BuiltinList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRail || BuiltinList.SelectedItem is not ThemeRailItem item)
                return;

            Select(item.Id);
        }

        private void CustomList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRail || CustomList.SelectedItem is not ThemeRailItem item)
                return;

            Select(item.Id);
        }

        private ThemeRailItem? ItemFor(string id) => _builtinItems.Concat(_customItems).FirstOrDefault(i => i.Id == id);

        /// <summary>Selects a theme for viewing and editing. This never activates it.</summary>
        private void Select(string id, bool flush = true)
        {
            if (flush)
                FlushSave();

            var definition = Store.Get(id) ?? BuiltinThemes.Obsidian();
            _selected = definition;
            _rememberedId = definition.Id;

            _suppressRail = true;
            var item = ItemFor(definition.Id);
            BuiltinList.SelectedItem = item is { Kind: ThemeKind.Builtin } ? item : null;
            CustomList.SelectedItem = item is { Kind: ThemeKind.Custom } ? item : null;
            _suppressRail = false;

            LoadSelected();
            UpdateActiveState();
        }

        /// <summary>Shows the selected theme in the header, preview and editor.</summary>
        private void LoadSelected()
        {
            bool custom = _selected.Kind == ThemeKind.Custom;

            NameText.Text = _selected.Name;
            NameText.ToolTip = _selected.Name;
            KindTagText.Text = custom ? "Custom" : "Built-in";
            KindTag.Style = (Style)FindResource(custom ? "ZeroTagStyle" : "ZeroTagOutlineStyle");

            BuiltinPanel.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
            EditorPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

            // Rename, Restore defaults and Delete only make sense for custom themes.
            var customOnly = custom ? Visibility.Visible : Visibility.Collapsed;
            MenuRename.Visibility = customOnly;
            MenuRestore.Visibility = customOnly;
            MenuDelete.Visibility = customOnly;
            MenuSeparator.Visibility = customOnly;

            Preview.Apply(_selected.Colors);
            BuildRows();
            UpdateContrastNotes();
            UpdateUseButton();

            DetailScroll.ScrollToVerticalOffset(0);
        }

        /// <summary>Refreshes Active / Following Windows markers, the Match Windows toggle and the Use button.</summary>
        private void UpdateActiveState()
        {
            string activeId = ThemeManager.ActiveId;
            bool following = activeId == ThemeManager.SystemId;
            string effectiveId = following ? ThemeManager.IdFor(ThemeManager.ActiveDefinition) : activeId;

            foreach (var item in _builtinItems.Concat(_customItems))
            {
                string key = item.ManagerId;
                item.Status = !following && key == activeId ? "Active"
                            : following && item.Kind == ThemeKind.Builtin && key == effectiveId ? "Following Windows"
                            : "";
            }

            _suppressToggles = true;
            MatchWindowsToggle.IsChecked = following;
            _suppressToggles = false;

            UpdateUseButton();
        }

        private string SelectedManagerId => ThemeManager.IdFor(_selected);

        private bool PrimaryTextBlocked =>
            _selected.Kind == ThemeKind.Custom &&
            ContrastNote.Ratio(_selected.Colors.TextPrimary, _selected.Colors.Background) < ContrastNote.MinimumRatio;

        private void UpdateUseButton()
        {
            bool isActive = ThemeManager.ActiveId == SelectedManagerId;
            bool blocked = PrimaryTextBlocked;

            UseButton.Content = isActive ? "Active" : "Use this theme";
            UseButton.Icon = isActive ? Wpf.Ui.Common.SymbolRegular.Checkmark16 : Wpf.Ui.Common.SymbolRegular.Empty;
            UseButton.IsEnabled = !isActive && !blocked;
            UseButton.ToolTip = blocked ? "Primary text is too hard to read on this background. Edits are saved, but the app won't show them until it is readable." : null;
        }

        // ---------------------------------------------------------------- editor

        private void BuildRows()
        {
            foreach (var old in _rows)
                old.UserChanged -= Row_UserChanged;

            _rows.Clear();

            if (_selected.Kind == ThemeKind.Custom)
            {
                foreach (var field in Fields)
                {
                    var row = new ColorRowVm(field.Key, field.Label, field.Group, field.Get(_selected.Colors), QuickColors);
                    row.UserChanged += Row_UserChanged;
                    _rows.Add(row);
                }
            }

            var view = new ListCollectionView(_rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ColorRowVm.Group)));
            ColorsList.ItemsSource = view;
        }

        /// <summary>The theme's own eight anchor colours, offered as quick swatches in the picker.</summary>
        private IReadOnlyList<string> QuickColors()
        {
            var c = _selected.Colors;
            return new[] { c.Background, c.Surface, c.SurfaceElevated, c.TextPrimary, c.TextSecondary, c.Border, c.Accent, c.OnAccent };
        }

        private void Row_UserChanged(ColorRowVm row)
        {
            var field = Fields.First(f => f.Key == row.Key);
            field.Set(_selected.Colors, row.Hex);

            Preview.Apply(_selected.Colors);
            ItemFor(_selected.Id)?.Update(_selected);
            UpdateContrastNotes();
            UpdateUseButton();

            _dirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void UpdateContrastNotes()
        {
            var c = _selected.Colors;

            foreach (var row in _rows)
            {
                double? ratio = row.Key switch
                {
                    nameof(ThemeColors.TextPrimary) => ContrastNote.Ratio(c.TextPrimary, c.Background),
                    nameof(ThemeColors.TextSecondary) => ContrastNote.Ratio(c.TextSecondary, c.Background),
                    nameof(ThemeColors.TextMuted) => ContrastNote.Ratio(c.TextMuted, c.Background),
                    nameof(ThemeColors.OnAccent) => ContrastNote.Ratio(c.OnAccent, c.Accent),
                    _ => null
                };

                row.SetContrast(ratio);
            }
        }

        /// <summary>Writes pending edits to disk and, if this is the active theme, applies them app-wide.</summary>
        private void FlushSave()
        {
            _saveTimer.Stop();

            if (!_dirty)
                return;

            _dirty = false;

            if (_selected.Kind != ThemeKind.Custom)
                return;

            try
            {
                Store.Update(_selected);
            }
            catch (ThemeStoreException ex)
            {
                App.Logger.WriteLine("ThemeStudioView::FlushSave", ex.Message);
                ShowNote("Couldn't save your changes: " + ex.Message);
                return;
            }

            // Unreadable primary text is saved but never pushed live to the whole app.
            if (ThemeManager.ActiveId == SelectedManagerId && !PrimaryTextBlocked)
                ThemeManager.Refresh();
        }

        private void ShowNote(string text)
        {
            NoteText.Text = text;
            NoteText.Visibility = Visibility.Visible;
            _noteTimer.Stop();
            _noteTimer.Start();
        }

        // ---------------------------------------------------------------- activation

        private void UseTheme_Click(object sender, RoutedEventArgs e)
        {
            FlushSave();

            if (PrimaryTextBlocked)
                return;

            try
            {
                ThemeManager.SetActive(SelectedManagerId);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ThemeStudioView::UseTheme_Click", ex);
            }

            ActivityLog.Record(ActivityType.Theme, "Theme changed", $"Switched to {ThemeManager.ActiveDisplayName} theme");
            UpdateActiveState();
        }

        private void MatchWindows_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressToggles)
                return;

            FlushSave();

            try
            {
                if (MatchWindowsToggle.IsChecked == true)
                {
                    ThemeManager.SetActive(ThemeManager.SystemId);
                }
                else
                {
                    // Stay on whichever theme Windows had selected.
                    ThemeManager.SetActive(ThemeManager.IdFor(ThemeManager.ActiveDefinition));
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ThemeStudioView::MatchWindows_Click", ex);
            }

            ActivityLog.Record(ActivityType.Theme, "Theme changed", $"Switched to {ThemeManager.ActiveDisplayName} theme");
            UpdateActiveState();
        }

        private void AnimationsToggle_Click(object sender, RoutedEventArgs e)
        {
            // Saved with the rest of the settings (Ctrl + S); takes effect immediately in this session.
            App.Settings.Prop.ReduceMotion = AnimationsToggle.IsChecked != true;
        }

        // ---------------------------------------------------------------- menu and commands

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            MoreMenu.PlacementTarget = MoreButton;
            MoreMenu.HorizontalOffset = MoreButton.ActualWidth - MoreMenu.MinWidth; // right-align under the button
            MoreMenu.IsOpen = true;
        }

        /// <summary>Asks for a theme name until it passes the name rules, or returns null when cancelled.</summary>
        private string? AskName(string title, string prompt, string initial)
        {
            string current = initial;

            while (true)
            {
                string? result = TextPromptDialog.Ask(Owner, title, prompt, current);
                if (result is null)
                    return null;

                string? error = ThemeValidator.ValidateName(result);
                if (error is null)
                    return result;

                Frontend.ShowMessageBox(error, MessageBoxImage.Warning);
                current = result;
            }
        }

        private void NewTheme_Click(object sender, RoutedEventArgs e)
        {
            FlushSave();

            string? name = AskName("New theme", "Name for the new theme (it starts as a copy of " + _selected.Name + ")", Store.UniqueName(_selected.Name + " copy"));
            if (name is null)
                return;

            try
            {
                var created = Store.Create(_selected.Id, name);
                Reload(created.Id);
                ShowNote($"Created {created.Name}.");
            }
            catch (ThemeStoreException ex)
            {
                Frontend.ShowMessageBox("Couldn't create the theme.\n" + ex.Message, MessageBoxImage.Error);
            }
        }

        private void Duplicate_Click(object sender, RoutedEventArgs e)
        {
            FlushSave();

            try
            {
                var copy = Store.Duplicate(_selected.Id);
                Reload(copy.Id);
                ShowNote($"Created {copy.Name}. Edit its colors below.");
            }
            catch (ThemeStoreException ex)
            {
                Frontend.ShowMessageBox("Couldn't duplicate the theme.\n" + ex.Message, MessageBoxImage.Error);
            }
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (_selected.Kind != ThemeKind.Custom)
                return;

            FlushSave();

            string current = _selected.Name;

            while (true)
            {
                string? name = AskName("Rename theme", "Theme name", current);
                if (name is null || name == _selected.Name)
                    return;

                try
                {
                    var renamed = Store.Rename(_selected.Id, name);
                    Reload(renamed.Id);
                    ShowNote($"Renamed to {renamed.Name}.");
                    return;
                }
                catch (ThemeStoreException ex)
                {
                    Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Warning);
                    current = name;
                }
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            FlushSave();

            var dialog = new SaveFileDialog
            {
                Title = "Export theme",
                Filter = "ZeroStrap theme (*.zerotheme)|*.zerotheme",
                DefaultExt = ".zerotheme",
                AddExtension = true,
                FileName = _selected.Name + ".zerotheme",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog(Owner) != true)
                return;

            try
            {
                Store.Export(_selected.Id, dialog.FileName);
                ShowNote("Exported to " + dialog.FileName);
            }
            catch (ThemeStoreException ex)
            {
                Frontend.ShowMessageBox("Couldn't export the theme.\n" + ex.Message, MessageBoxImage.Error);
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            FlushSave();

            var dialog = new OpenFileDialog
            {
                Title = "Import theme",
                Filter = "ZeroStrap theme (*.zerotheme)|*.zerotheme",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(Owner) != true)
                return;

            try
            {
                var result = Store.Import(dialog.FileName);
                Reload(result.Theme.Id);
                ShowNote($"Imported {result.Theme.Name}.");

                if (result.Warnings.Count > 0)
                    Frontend.ShowMessageBox("Imported with notes:\n" + string.Join("\n", result.Warnings), MessageBoxImage.Information);
            }
            catch (ThemeStoreException ex)
            {
                Frontend.ShowMessageBox("Couldn't import that theme.\n" + ex.Message, MessageBoxImage.Error);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
            {
                App.Logger.WriteException("ThemeStudioView::Import_Click", ex);
                Frontend.ShowMessageBox("Couldn't import that theme.\n" + ex.Message, MessageBoxImage.Error);
            }
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (_selected.Kind != ThemeKind.Custom)
                return;

            var origin = Store.Get(_selected.BasedOn) ?? BuiltinThemes.ForBase(_selected.Base);

            var answer = Frontend.ShowMessageBox($"Reset every color in {_selected.Name} to {origin.Name}?",
                MessageBoxImage.Question, MessageBoxButton.YesNo, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            _dirty = false;
            _saveTimer.Stop();

            try
            {
                Store.RestoreDefaults(_selected.Id);
                Select(_selected.Id, flush: false);

                if (ThemeManager.ActiveId == SelectedManagerId)
                    ThemeManager.Refresh();

                ShowNote($"Colors reset to {origin.Name}.");
            }
            catch (ThemeStoreException ex)
            {
                Frontend.ShowMessageBox("Couldn't restore the defaults.\n" + ex.Message, MessageBoxImage.Error);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_selected.Kind != ThemeKind.Custom)
                return;

            var answer = Frontend.ShowMessageBox($"Delete {_selected.Name}? This can't be undone.",
                MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            string name = _selected.Name;
            bool wasActive = ThemeManager.ActiveId == SelectedManagerId;

            _dirty = false;
            _saveTimer.Stop();

            try
            {
                // The store raises Deleted; ThemeManager falls back to Obsidian when the active theme goes.
                Store.Delete(_selected.Id);
            }
            catch (ThemeStoreException ex)
            {
                Frontend.ShowMessageBox("Couldn't delete the theme.\n" + ex.Message, MessageBoxImage.Error);
                return;
            }

            _rememberedId = null;
            Reload(null);
            ShowNote(wasActive ? $"Deleted {name}. Switched to Obsidian." : $"Deleted {name}.");
        }
    }
}
