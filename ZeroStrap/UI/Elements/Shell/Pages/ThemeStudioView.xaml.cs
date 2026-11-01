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

using ZeroStrap.Enums;

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
        private bool _loading;
        private bool _subscribed;
        private string? _noteMessage;
        private readonly HashSet<string> _pendingRelease = new(StringComparer.OrdinalIgnoreCase);
        private MediaElement? _thumbVideo;
        private bool _backgroundBusy;

        /// <summary>One entry in the font family list: the stored family name and the text shown for it.</summary>
        private sealed record FontChoice(string Value, string Display);

        private static List<FontChoice>? _installedFonts;

        public ThemeStudioView()
        {
            InitializeComponent();

            _saveTimer.Tick += (_, _) => FlushSave();
            _noteTimer.Tick += (_, _) =>
            {
                _noteTimer.Stop();
                _noteMessage = null;
                UpdateStatusLine();
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
                BackgroundNoticeBoard.Default.Posted += BackgroundNotice_Posted;
                Dispatcher.ShutdownStarted += Dispatcher_ShutdownStarted;

                // Sweep media a failed delete left behind (nothing is pending or being imported at this point).
                if (!_dirty && _pendingRelease.Count == 0)
                    Store.PruneOrphanedMedia();
            }

            LoadMotionControls();
            Reload(_rememberedId ?? DefaultSelectionId());
        }

        private void OnHidden()
        {
            FlushSave();

            if (_subscribed)
            {
                _subscribed = false;
                ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
                BackgroundNoticeBoard.Default.Posted -= BackgroundNotice_Posted;
                Dispatcher.ShutdownStarted -= Dispatcher_ShutdownStarted;
            }

            StopThumbnailVideo();
        }

        private void BackgroundNotice_Posted(string themeId)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => BackgroundNotice_Posted(themeId)));
                return;
            }

            ShowBackgroundNoticeIfAny();
        }

        /// <summary>The backdrop could not show this theme's media: say so once.</summary>
        private void ShowBackgroundNoticeIfAny()
        {
            if (_selected.Kind == ThemeKind.Custom && BackgroundNoticeBoard.Default.Take(_selected.Id) is { } message)
            {
                ShowNote(message);

                if (BackgroundMessage is not null)
                    ShowBackgroundMessage(message, MessageKind.Error);
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
            LoadMotionControls();
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
            ShowBackgroundNoticeIfAny();
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

            Preview.Apply(_selected);
            BuildRows();
            LoadEditorControls();
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

        private bool PrimaryTextBlocked => ThemeGuard.IsUnreadable(_selected);

        /// <summary>The active theme is this one, but its primary text is too faint, so the app shows Obsidian instead.</summary>
        private bool NotApplied => PrimaryTextBlocked && ThemeManager.ActiveId == SelectedManagerId;

        private void UpdateUseButton()
        {
            bool isActive = ThemeManager.ActiveId == SelectedManagerId;
            bool blocked = PrimaryTextBlocked;

            UseButton.Content = isActive ? "Active" : "Use this theme";
            UseButton.Icon = isActive ? Wpf.Ui.Common.SymbolRegular.Checkmark16 : Wpf.Ui.Common.SymbolRegular.Empty;
            UseButton.IsEnabled = !isActive && !blocked;
            UseButton.ToolTip = blocked ? "Primary text is too hard to read on this background. Edits are saved, but the app won't show them until it is readable." : null;

            UpdateStatusLine();
        }

        /// <summary>Shows the transient note, else the "not applied" warning, else nothing. The row is always reserved.</summary>
        private void UpdateStatusLine()
        {
            if (_noteMessage is not null)
            {
                NoteText.Text = _noteMessage;
                NoteText.ToolTip = _noteMessage;
                NoteText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                NoteText.Visibility = Visibility.Visible;
            }
            else if (NotApplied)
            {
                NoteText.Text = ThemeGuard.WarningText;
                NoteText.ToolTip = null;
                NoteText.SetResourceReference(TextBlock.ForegroundProperty, "ZeroStatusWarningBrush");
                NoteText.Visibility = Visibility.Visible;
            }
            else
            {
                NoteText.Text = " ";
                NoteText.ToolTip = null;
                NoteText.Visibility = Visibility.Hidden;
            }
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

            Preview.Apply(_selected);
            ItemFor(_selected.Id)?.Update(_selected);
            UpdateContrastNotes();
            UpdateUseButton();

            QueueSave();
        }

        private void QueueSave()
        {
            _dirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>A non-colour edit: refresh the preview and queue the debounced save (and live apply when active).</summary>
        private void Edited()
        {
            if (_loading || _selected.Kind != ThemeKind.Custom)
                return;

            Preview.Apply(_selected);
            QueueSave();
        }

        // ---------------------------------------------------------------- editor sections

        private void SectionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TypographySection is null || LayoutSection is null || ComponentsSection is null || BackgroundSection is null)
                return; // raised while the XAML is still being built

            int index = SectionTabs.SelectedIndex;
            ColorsList.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            TypographySection.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            LayoutSection.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
            ComponentsSection.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
            BackgroundSection.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

            // The thumbnail video only runs while its section is on screen.
            UpdateThumbnail();
        }

        /// <summary>Installed font families, sorted, with the default stack first. Built once.</summary>
        private static List<FontChoice> InstalledFonts()
        {
            if (_installedFonts is not null)
                return _installedFonts;

            var list = new List<FontChoice> { new(ThemeTokens.DefaultFamily, "Inter (default)") };

            try
            {
                foreach (var name in Fonts.SystemFontFamilies.Select(f => f.Source).Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (!name.Equals("Inter", StringComparison.OrdinalIgnoreCase))
                        list.Add(new FontChoice(name, name));
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ThemeStudioView::InstalledFonts", ex);
            }

            return _installedFonts = list;
        }

        private static void SelectByTag(ListBox list, string tag)
        {
            foreach (var item in list.Items.OfType<ListBoxItem>())
                if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
                {
                    list.SelectedItem = item;
                    return;
                }

            list.SelectedItem = null;
        }

        /// <summary>The option whose numeric tag is closest to <paramref name="weight"/>.</summary>
        private static void SelectNearestWeight(ListBox list, int weight)
        {
            var best = list.Items.OfType<ListBoxItem>().OrderBy(i => Math.Abs(int.Parse((string)i.Tag) - weight)).FirstOrDefault();
            list.SelectedItem = best;
        }

        private static string? TagOf(ListBox list) => (list.SelectedItem as ListBoxItem)?.Tag as string;

        /// <summary>Fills the Typography, Layout and Components controls from the selected theme.</summary>
        private void LoadEditorControls()
        {
            if (_selected.Kind != ThemeKind.Custom)
                return;

            _loading = true;
            try
            {
                var t = _selected.Typography;

                var fonts = new List<FontChoice>(InstalledFonts());
                string stored = ThemeTokens.BuildFamily(t.Family);
                FontChoice? current = stored == ThemeTokens.DefaultFamily ? fonts[0] : fonts.FirstOrDefault(f => f.Value.Equals(t.Family.Trim(), StringComparison.OrdinalIgnoreCase));
                if (current is null)
                {
                    current = new FontChoice(t.Family.Trim(), t.Family.Trim() + " (not installed)");
                    fonts.Insert(1, current);
                }

                FamilyBox.ItemsSource = fonts;
                FamilyBox.SelectedItem = current;

                int percent = (int)Math.Round(Math.Clamp(t.Scale, ThemeValidator.MinScale, ThemeValidator.MaxScale) * 100 / 5) * 5;
                ScaleSlider.Value = percent;
                ScaleLabel.Text = percent + " %";

                SelectNearestWeight(BodyWeightList, t.BodyWeight);
                SelectNearestWeight(HeadingWeightList, t.HeadingWeight);

                SelectByTag(DensityList, _selected.Layout.Density.ToString());
                DensityHint.Text = DensityDescription(_selected.Layout.Density);

                int radius = Math.Clamp(_selected.Components.Radius, ThemeValidator.MinRadius, ThemeValidator.MaxRadius);
                RadiusSlider.Value = radius;
                RadiusLabel.Text = radius + " px";

                SelectByTag(ButtonStyleList, _selected.Components.ButtonStyle.ToString());
                SelectByTag(SurfaceStyleList, _selected.Components.SurfaceStyle.ToString());

                LoadBackgroundControls();
            }
            finally
            {
                _loading = false;
            }
        }

        private static string DensityDescription(ThemeDensity density) => density switch
        {
            ThemeDensity.Compact => "Tighter page margins and card padding, so more fits on screen.",
            ThemeDensity.Spacious => "Roomier page margins and card padding.",
            _ => "The default page margins and card padding."
        };

        private void FamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || FamilyBox.SelectedItem is not FontChoice choice)
                return;

            _selected.Typography.Family = choice.Value;
            Edited();
        }

        private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ScaleLabel is null)
                return;

            int percent = (int)Math.Round(ScaleSlider.Value);
            ScaleLabel.Text = percent + " %";

            if (_loading)
                return;

            _selected.Typography.Scale = percent / 100.0;
            Edited();
        }

        private void BodyWeight_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || TagOf(BodyWeightList) is not { } tag)
                return;

            _selected.Typography.BodyWeight = int.Parse(tag);
            Edited();
        }

        private void HeadingWeight_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || TagOf(HeadingWeightList) is not { } tag)
                return;

            _selected.Typography.HeadingWeight = int.Parse(tag);
            Edited();
        }

        private void Density_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || TagOf(DensityList) is not { } tag || !Enum.TryParse<ThemeDensity>(tag, out var density))
                return;

            _selected.Layout.Density = density;
            DensityHint.Text = DensityDescription(density);
            Edited();
        }

        private void RadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (RadiusLabel is null)
                return;

            int radius = (int)Math.Round(RadiusSlider.Value);
            RadiusLabel.Text = radius + " px";

            if (_loading)
                return;

            _selected.Components.Radius = radius;
            Edited();
        }

        private void ButtonStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || TagOf(ButtonStyleList) is not { } tag || !Enum.TryParse<ThemeButtonStyle>(tag, out var style))
                return;

            _selected.Components.ButtonStyle = style;
            Edited();
        }

        private void SurfaceStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || TagOf(SurfaceStyleList) is not { } tag || !Enum.TryParse<ThemeSurfaceStyle>(tag, out var style))
                return;

            _selected.Components.SurfaceStyle = style;
            Edited();
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

            // Media this theme no longer uses is deleted once no saved theme references it. Deferred a little so the
            // backdrop has released a file it was still playing; anything left over is swept up the next time this opens.
            if (_pendingRelease.Count > 0)
            {
                var release = _pendingRelease.ToArray();
                _pendingRelease.Clear();

                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    foreach (var media in release)
                        Store.ReleaseMedia(media);
                }));
            }
        }

        private void ShowNote(string text)
        {
            _noteMessage = text;
            UpdateStatusLine();
            _noteTimer.Stop();
            _noteTimer.Start();
        }

        // ---------------------------------------------------------------- background

        private enum MessageKind { Info, Warning, Error }

        private ThemeBackground EnsureBackground() => _selected.Background ??= new ThemeBackground();

        private static void Log(string message) => App.Logger.WriteLine("ThemeStudioView", message);

        /// <summary>Fills the Background section from the selected theme. Called with <c>_loading</c> set.</summary>
        private void LoadBackgroundControls()
        {
            var b = EnsureBackground();
            bool has = ThemeMedia.HasBackground(b);

            SelectByTag(BackgroundTypeList, (has ? b.Type : ThemeBackgroundType.None).ToString());

            int dim = (int)Math.Round(Math.Clamp(b.Opacity, ThemeValidator.MinScrim, ThemeValidator.MaxScrim) * 100 / 5) * 5;
            DimSlider.Value = dim;
            DimLabel.Text = dim + " %";

            int blur = Math.Clamp(b.Blur, ThemeValidator.MinBlur, ThemeValidator.MaxBlur);
            BlurSlider.Value = blur;
            BlurLabel.Text = blur + " px";

            int speed = (int)Math.Round(Math.Clamp(b.Speed, ThemeValidator.MinSpeed, ThemeValidator.MaxSpeed) * 100 / 25) * 25;
            SpeedSlider.Value = speed;
            SpeedLabel.Text = FormatSpeed(speed / 100.0);

            ShowBackgroundMessage(null, MessageKind.Info);
            RefreshBackgroundUi();
        }

        private static string FormatSpeed(double speed) => speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";

        private void RefreshBackgroundUi()
        {
            var b = EnsureBackground();
            bool has = ThemeMedia.HasBackground(b);
            bool video = has && b.Type == ThemeBackgroundType.Video;

            BackgroundDetails.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            SpeedRow.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            BackgroundCaption.Visibility = video ? Visibility.Visible : Visibility.Collapsed;

            if (has)
            {
                string? path = Store.GetMediaPath(_selected);
                BackgroundFileText.Text = path is null ? "File missing" : (b.Type == ThemeBackgroundType.Video ? "Video" : "Image") + " · " + ThemeMedia.FormatSize(new FileInfo(path).Length);
                BackgroundFileCaption.Text = path is null ? "The file is no longer in ZeroStrap's media folder. Choose another." : "Stored in ZeroStrap's theme media folder.";
            }

            UpdateThumbnail();
        }

        private void ShowBackgroundMessage(string? text, MessageKind kind)
        {
            if (string.IsNullOrEmpty(text))
            {
                BackgroundMessage.Text = "";
                BackgroundMessage.Visibility = Visibility.Collapsed;
                return;
            }

            BackgroundMessage.Text = text;
            BackgroundMessage.SetResourceReference(TextBlock.ForegroundProperty, kind switch
            {
                MessageKind.Error => "ZeroStatusErrorBrush",
                MessageKind.Warning => "ZeroStatusWarningBrush",
                _ => "TextFillColorSecondaryBrush"
            });
            BackgroundMessage.Visibility = Visibility.Visible;
        }

        private void SetBackgroundBusy(bool busy, string? message = null)
        {
            _backgroundBusy = busy;
            BackgroundTypeList.IsEnabled = !busy;
            ChooseFileButton.IsEnabled = !busy;
            RemoveBackgroundButton.IsEnabled = !busy;
            DimSlider.IsEnabled = !busy;
            BlurSlider.IsEnabled = !busy;
            SpeedSlider.IsEnabled = !busy;

            if (busy)
                ShowBackgroundMessage(message, MessageKind.Info);
        }

        // thumbnail ----------------------------------------------------

        private void StopThumbnailVideo()
        {
            var video = _thumbVideo;
            if (video is null)
                return;

            _thumbVideo = null;
            video.MediaEnded -= ThumbVideo_MediaEnded;
            video.MediaFailed -= ThumbVideo_MediaFailed;

            try
            {
                video.Stop();
                video.Close();
            }
            catch (InvalidOperationException)
            {
            }

            video.Source = null;
            BackgroundThumbHost.Children.Remove(video);
        }

        private void ThumbVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (sender is MediaElement video && ReferenceEquals(video, _thumbVideo))
            {
                video.Position = TimeSpan.Zero;
                video.Play();
            }
        }

        private void ThumbVideo_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            Log("Thumbnail video failed: " + e.ErrorException?.Message);
            StopThumbnailVideo();
        }

        /// <summary>16:9 preview of the chosen media: the picture, or a muted looping video while this section is visible.</summary>
        private void UpdateThumbnail()
        {
            if (BackgroundThumb is null)
                return;

            StopThumbnailVideo();

            var b = _selected.Background;
            string? path = ThemeMedia.HasBackground(b) && _selected.Kind == ThemeKind.Custom ? Store.GetMediaPath(_selected) : null;

            if (path is null)
            {
                BackgroundThumb.Visibility = Visibility.Collapsed;
                BackgroundThumbImage.Source = null;
                return;
            }

            BackgroundThumb.Visibility = Visibility.Visible;
            bool video = b!.Type == ThemeBackgroundType.Video;

            string imagePath = video ? Path.Combine(Path.GetDirectoryName(path)!, ThemeMedia.PosterName(Path.GetFileName(path))) : path;
            BackgroundThumbImage.Source = File.Exists(imagePath) ? LoadThumbnail(imagePath) : null;

            if (video && BackgroundSection.Visibility == Visibility.Visible && IsLoaded)
            {
                var player = new MediaElement
                {
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Manual,
                    IsMuted = true,
                    Volume = 0,
                    Stretch = Stretch.UniformToFill
                };

                player.MediaEnded += ThumbVideo_MediaEnded;
                player.MediaFailed += ThumbVideo_MediaFailed;

                _thumbVideo = player;
                BackgroundThumbHost.Children.Add(player);
                player.Source = new Uri(path);
                player.Play();
            }
        }

        private static ImageSource? LoadThumbnail(string path)
        {
            try
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                image.DecodePixelWidth = 640;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or FileFormatException or ArgumentException)
            {
                return null;
            }
        }

        // handlers ------------------------------------------------------

        private async void BackgroundType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _backgroundBusy || _selected.Kind != ThemeKind.Custom || TagOf(BackgroundTypeList) is not { } tag
                || !Enum.TryParse<ThemeBackgroundType>(tag, out var kind))
                return;

            var b = EnsureBackground();
            var current = ThemeMedia.HasBackground(b) ? b.Type : ThemeBackgroundType.None;
            if (kind == current)
                return;

            if (kind == ThemeBackgroundType.None)
            {
                RemoveBackground();
                return;
            }

            if (!await ChooseBackgroundAsync(kind))
                RevertBackgroundType();
        }

        private void RevertBackgroundType()
        {
            _loading = true;
            try
            {
                var b = EnsureBackground();
                SelectByTag(BackgroundTypeList, (ThemeMedia.HasBackground(b) ? b.Type : ThemeBackgroundType.None).ToString());
            }
            finally
            {
                _loading = false;
            }
        }

        private async void ChooseFile_Click(object sender, RoutedEventArgs e)
        {
            var b = EnsureBackground();
            if (!ThemeMedia.HasBackground(b) || _backgroundBusy)
                return;

            await ChooseBackgroundAsync(b.Type);
        }

        /// <summary>
        /// Asks for a file, validates it, proves it opens, copies it into the media folder (content-addressed), captures
        /// a poster for videos and assigns it to the selected theme. Returns false when nothing was changed.
        /// </summary>
        private async Task<bool> ChooseBackgroundAsync(ThemeBackgroundType kind)
        {
            var dialog = new OpenFileDialog
            {
                Title = kind == ThemeBackgroundType.Video ? "Choose a background video" : "Choose a background image",
                Filter = kind == ThemeBackgroundType.Video ? "MP4 video (*.mp4)|*.mp4" : "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(Owner) != true)
                return false;

            return await SetBackgroundFromFileAsync(kind, dialog.FileName);
        }

        /// <summary>The validation and import pipeline behind Choose file (public to the page so it can be driven without a dialog).</summary>
        internal async Task<bool> SetBackgroundFromFileAsync(ThemeBackgroundType kind, string source)
        {
            if (_selected.Kind != ThemeKind.Custom)
                return false;

            var target = _selected;

            var check = ThemeMedia.CheckFile(source, kind);
            if (!check.Ok)
            {
                ShowBackgroundMessage(check.Error, MessageKind.Error);
                return false;
            }

            SetBackgroundBusy(true, "Checking the file…");

            try
            {
                var probe = await MediaProbe.ProbeAsync(source, kind == ThemeBackgroundType.Video);
                if (!probe.Ok)
                {
                    Log($"Rejected background {Path.GetFileName(source)}: {probe.Error}");
                    ShowBackgroundMessage(probe.Error, MessageKind.Error);
                    return false;
                }

                ShowBackgroundMessage("Adding the file…", MessageKind.Info);
                string mediaDir = Store.MediaDirectory;
                var imported = await Task.Run(() => ThemeMedia.Import(mediaDir, source));

                if (kind == ThemeBackgroundType.Video)
                {
                    string stored = Path.Combine(mediaDir, imported.Name);
                    string poster = Path.Combine(mediaDir, ThemeMedia.PosterName(imported.Name));

                    if (!File.Exists(poster) && !await MediaProbe.CapturePosterAsync(stored, poster, Log))
                        Log("No poster could be captured; reduced motion will show the plain background.");
                }

                if (!ReferenceEquals(target, _selected))
                {
                    // The user moved to another theme while the file was being checked.
                    Store.ReleaseMedia(imported.Name);
                    return false;
                }

                var background = EnsureBackground();
                string? previous = background.Media;

                background.Type = kind;
                background.Media = imported.Name;

                if (!string.IsNullOrEmpty(previous) && !string.Equals(previous, imported.Name, StringComparison.OrdinalIgnoreCase))
                    _pendingRelease.Add(previous);

                RevertBackgroundType();
                RefreshBackgroundUi();
                ShowBackgroundMessage(check.Warning, MessageKind.Warning);
                Edited();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log("Could not add background: " + ex.Message);
                ShowBackgroundMessage("Couldn't copy that file into ZeroStrap: " + ex.Message, MessageKind.Error);
                return false;
            }
            finally
            {
                SetBackgroundBusy(false);
            }
        }

        private void RemoveBackground_Click(object sender, RoutedEventArgs e)
        {
            if (!_backgroundBusy)
                RemoveBackground();
        }

        private void RemoveBackground()
        {
            var b = EnsureBackground();

            if (!string.IsNullOrEmpty(b.Media))
                _pendingRelease.Add(b.Media);

            b.Type = ThemeBackgroundType.None;
            b.Media = null;

            RevertBackgroundType();
            RefreshBackgroundUi();
            ShowBackgroundMessage(null, MessageKind.Info);
            Edited();
        }

        private void DimSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DimLabel is null)
                return;

            int percent = (int)Math.Round(DimSlider.Value);
            DimLabel.Text = percent + " %";

            if (_loading)
                return;

            EnsureBackground().Opacity = percent / 100.0;
            Edited();
        }

        private void BlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BlurLabel is null)
                return;

            int blur = (int)Math.Round(BlurSlider.Value);
            BlurLabel.Text = blur + " px";

            if (_loading)
                return;

            EnsureBackground().Blur = blur;
            Edited();
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SpeedLabel is null)
                return;

            double speed = Math.Round(SpeedSlider.Value) / 100.0;
            SpeedLabel.Text = FormatSpeed(speed);

            if (_loading)
                return;

            EnsureBackground().Speed = speed;
            Edited();
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

        /// <summary>Shows the app-level motion preferences. Intensity is disabled while animations are effectively off.</summary>
        private void LoadMotionControls()
        {
            var settings = App.Settings.Prop;

            _loading = true;
            try
            {
                AnimationsToggle.IsChecked = !settings.ReduceMotion;
                FollowWindowsToggle.IsChecked = settings.FollowWindowsAnimations;
                SelectByTag(IntensityList, settings.MotionIntensity.ToString());
            }
            finally
            {
                _loading = false;
            }

            UpdateMotionEnabled();
        }

        private void UpdateMotionEnabled() => IntensityList.IsEnabled = Motion.Enabled;

        private void AnimationsToggle_Click(object sender, RoutedEventArgs e)
        {
            // Saved with the rest of the settings (Ctrl + S); takes effect immediately in this session.
            App.Settings.Prop.ReduceMotion = AnimationsToggle.IsChecked != true;
            UpdateMotionEnabled();
            Motion.NotifyChanged();
        }

        private void FollowWindowsToggle_Click(object sender, RoutedEventArgs e)
        {
            App.Settings.Prop.FollowWindowsAnimations = FollowWindowsToggle.IsChecked == true;
            UpdateMotionEnabled();
            Motion.NotifyChanged();
        }

        private void Intensity_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || TagOf(IntensityList) is not { } tag || !Enum.TryParse<MotionIntensity>(tag, out var intensity))
                return;

            App.Settings.Prop.MotionIntensity = intensity;
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
