using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Wpf.Ui.Common;

using ZeroStrap.Mods;
using ZeroStrap.UI.Utility;
using ZeroStrap.UI.ViewModels.Settings;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    public sealed class ModsToastEventArgs : EventArgs
    {
        public ModsToastEventArgs(string message, bool isError)
        {
            Message = message;
            IsError = isError;
        }

        public string Message { get; }
        public bool IsError { get; }
    }

    /// <summary>Row of the Installed list.</summary>
    public sealed class ModRowVm
    {
        public ModRowVm(ModPackageStatus source, bool hasUnresolvedConflict)
        {
            Source = source;

            var manifest = source.Package.Manifest;
            var status = ModsStudioLogic.StatusFor(source.Enabled, hasUnresolvedConflict);

            Id = source.Package.Id;
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? source.Package.Id : manifest.Name;
            Meta = ModsStudioLogic.ListMeta(manifest.Category ?? ModCategory.Other, manifest.Author);
            Enabled = source.Enabled;
            Status = status.ToString();
            StatusText = ModsStudioLogic.StatusText(status);
            Glyph = GlyphFor(manifest.Category ?? ModCategory.Other);
            Thumb = LoadThumbnail(source.Package, 96);
        }

        public ModPackageStatus Source { get; }
        public string Id { get; }
        public string Name { get; }
        public string Meta { get; }
        public bool Enabled { get; }
        public string Status { get; }
        public string StatusText { get; }
        public SymbolRegular Glyph { get; }
        public ImageSource? Thumb { get; }

        public override string ToString() => Name;

        public Visibility ThumbVisibility => Thumb is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility GlyphVisibility => Thumb is null ? Visibility.Visible : Visibility.Collapsed;

        public static SymbolRegular GlyphFor(ModCategory category) => category switch
        {
            ModCategory.Sounds => SymbolRegular.Speaker224,
            ModCategory.Textures => SymbolRegular.Image24,
            ModCategory.Fonts => SymbolRegular.TextFont24,
            ModCategory.Sky => SymbolRegular.WeatherSunny24,
            ModCategory.Cursors => SymbolRegular.Cursor24,
            ModCategory.Places => SymbolRegular.Globe24,
            _ => SymbolRegular.PuzzlePiece24
        };

        /// <summary>The package's thumbnail, or null (none, unreadable, or not an image). Never keeps the file open.</summary>
        public static BitmapImage? LoadThumbnail(ModPackage package, int decodeWidth)
        {
            string? name = package.Manifest.Thumbnail;

            if (string.IsNullOrWhiteSpace(name))
                return null;

            try
            {
                // the manifest is untrusted input: only a plain file name inside the package folder
                string path = Path.Combine(package.Directory, Path.GetFileName(name));

                if (!File.Exists(path))
                    return null;

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.DecodePixelWidth = decodeWidth;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }
    }

    public sealed class OrderRowVm
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public int Position { get; init; }
        public bool CanUp { get; init; }
        public bool CanDown { get; init; }

        public override string ToString() => Name;
    }

    public sealed class CandidateVm
    {
        public string Path { get; init; } = "";
        public string OwnerId { get; init; } = "";
        public string Label { get; init; } = "";
        public bool Selectable { get; init; }
        public bool IsCurrent { get; init; }
        public bool IsBuiltin { get; init; }
        public ConflictRowStatus RowStatus { get; init; }

        public override string ToString() => Label;

        public string? ToolTip => Selectable ? null : "A file that ZeroStrap did not add is already in Modifications, so it is left alone. Remove it to use the mod's file.";
    }

    public sealed class ConflictRowVm
    {
        public string Path { get; init; } = "";
        public string Status { get; init; } = "";
        public string StatusText { get; init; } = "";
        public string CurrentText { get; init; } = "";
        public IReadOnlyList<CandidateVm> Candidates { get; init; } = Array.Empty<CandidateVm>();

        public override string ToString() => Path;
    }

    public partial class ModsStudioPage : Page
    {
        private const string LOG_IDENT = "ModsStudioPage";

        public event EventHandler<ModsToastEventArgs>? ToastRequested;

        private readonly ModsViewModel _presets = ModsViewModel.Shared;

        private ModStudioSnapshot? _snapshot;
        private List<ConflictRow> _rows = new();
        private Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private string? _selectedId;
        private string? _conflictFilter;
        private bool _busy;
        private bool _choosing;
        private bool _started;
        private bool _loaded;

        private ImageSource? _cursor2006Arrow, _cursor2006Far, _cursor2013Arrow, _cursor2013Far;

        public ModsStudioPage()
        {
            InitializeComponent();

            LibraryRoot.DataContext = _presets;
            Loaded += Page_Loaded;
        }

        // ---------------------------------------------------------------- start

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;

            if (_started)
                return;

            _started = true;

            SetBusy(true);

            try
            {
                MigrationResult migration = await ModsStudioService.RunAsync(() => ModStudio.Default.EnsureMigrated());

                if (migration.Status == MigrationStatus.Migrated)
                {
                    MigrationNoticeText.Text = ModsStudioLogic.MigrationNotice;
                    MigrationNotice.Visibility = Visibility.Visible;
                }
                else if (migration.Status == MigrationStatus.Aborted)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Legacy migration did not run: {migration.Reason}");
                    Toast("Your existing mods could not be packaged right now. ZeroStrap will try again next time.", isError: true);
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Toast(Plain(ex), isError: true);
            }

            UpdateCursorPreview();
            await RefreshAsync();
            SetBusy(false);
        }

        private void DismissNotice_Click(object sender, RoutedEventArgs e) => MigrationNotice.Visibility = Visibility.Collapsed;

        // ---------------------------------------------------------------- state

        private async Task RefreshAsync()
        {
            try
            {
                var snapshot = await ModsStudioService.GetStateAsync();
                Apply(snapshot);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Toast("Could not read your mods: " + Plain(ex), isError: true);
            }
        }

        private string LabelOf(string id)
        {
            if (BuiltinPreset.IsBuiltinId(id))
                return ModsStudioLogic.BuiltinLabel(id, ModStudio.Default.Catalog.Find(id)?.DisplayName);

            return _names.TryGetValue(id, out string? name) ? name : id;
        }

        private void Apply(ModStudioSnapshot snapshot)
        {
            _snapshot = snapshot;

            _names = snapshot.Packages.ToDictionary(
                p => p.Package.Id,
                p => string.IsNullOrWhiteSpace(p.Package.Manifest.Name) ? p.Package.Id : p.Package.Manifest.Name,
                StringComparer.Ordinal);

            _rows = ModsStudioLogic.BuildConflictRows(snapshot.Conflicts, snapshot.Blocked, LabelOf);

            ApplyInstalled(snapshot);
            ApplyLibrary(snapshot);
            ApplyConflicts(snapshot);
        }

        private void ApplyInstalled(ModStudioSnapshot snapshot)
        {
            var unresolved = ModsStudioLogic.PackagesWithUnresolvedConflicts(snapshot.Conflicts);
            var rows = snapshot.Packages.Select(p => new ModRowVm(p, unresolved.Contains(p.Package.Id))).ToList();

            bool any = rows.Count > 0;
            InstalledPanes.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            InstalledEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

            PackageList.ItemsSource = rows;

            var selected = rows.FirstOrDefault(r => r.Id == _selectedId) ?? rows.FirstOrDefault();
            _selectedId = selected?.Id;
            PackageList.SelectedItem = selected;

            ShowDetails(selected);
        }

        private void ApplyLibrary(ModStudioSnapshot snapshot)
        {
            var contested = ModsStudioLogic.ContestedBuiltins(snapshot.Conflicts);

            CursorsPill.Visibility = Pill(contested, "builtin:cursors");
            SoundsPill.Visibility = Pill(contested, "builtin:old-sounds");
            AvatarPill.Visibility = Pill(contested, "builtin:old-avatar-background");
            EmojiPill.Visibility = Pill(contested, "builtin:emoji-font");
            FontPill.Visibility = Pill(contested, "builtin:custom-font");
        }

        private static Visibility Pill(HashSet<string> contested, string id) => contested.Contains(id) ? Visibility.Visible : Visibility.Collapsed;

        private void ApplyConflicts(ModStudioSnapshot snapshot)
        {
            int unresolved = ModsStudioLogic.UnresolvedCount(snapshot.Conflicts);

            ConflictsBadge.Visibility = unresolved > 0 ? Visibility.Visible : Visibility.Collapsed;
            ConflictsBadgeText.Text = unresolved.ToString();

            if (_conflictFilter is not null && !_names.ContainsKey(_conflictFilter))
                _conflictFilter = null;

            // order card: enabled packages by priority
            var enabled = snapshot.Packages.Where(p => p.Enabled).ToList();
            OrderList.ItemsSource = enabled
                .Select((p, i) => new OrderRowVm
                {
                    Id = p.Package.Id,
                    Name = LabelOf(p.Package.Id),
                    Position = i + 1,
                    CanUp = i > 0,
                    CanDown = i < enabled.Count - 1
                })
                .ToList();
            OrderCard.Visibility = enabled.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;

            var shown = ModsStudioLogic.ForPackage(_rows, _conflictFilter);

            ConflictList.ItemsSource = shown.Select(ToViewModel).ToList();
            ConflictsEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            ConflictFilterBar.Visibility = _conflictFilter is null ? Visibility.Collapsed : Visibility.Visible;

            if (_conflictFilter is not null)
                ConflictFilterText.Text = $"Showing conflicts for {LabelOf(_conflictFilter)}";
        }

        private static ConflictRowVm ToViewModel(ConflictRow row) => new()
        {
            Path = row.Path,
            Status = row.Status.ToString(),
            StatusText = row.Status.ToString(),
            CurrentText = "In use now: " + row.CurrentOwnerLabel,
            Candidates = row.Candidates
                .Select(c => new CandidateVm
                {
                    Path = row.Path,
                    OwnerId = c.OwnerId,
                    Label = c.Label,
                    Selectable = c.Selectable,
                    IsCurrent = c.IsCurrent,
                    IsBuiltin = c.IsBuiltin,
                    RowStatus = row.Status
                })
                .ToList()
        };

        // ---------------------------------------------------------------- details

        private ModRowVm? SelectedRow => PackageList.SelectedItem as ModRowVm;

        private void PackageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, PackageList))
                return;

            var row = SelectedRow;

            if (row is null)
                return;

            _selectedId = row.Id;
            ShowDetails(row);
        }

        private void ShowDetails(ModRowVm? row)
        {
            DetailsPanel.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;

            if (row is null)
                return;

            var package = row.Source.Package;
            var manifest = package.Manifest;

            DetailsName.Text = row.Name;
            DetailsName.ToolTip = row.Name;
            DetailsMeta.Text = ModsStudioLogic.DetailsMeta(manifest);
            LegacyNote.Visibility = manifest.Source == ModSource.Legacy ? Visibility.Visible : Visibility.Collapsed;

            var big = ModRowVm.LoadThumbnail(package, 640);
            DetailsThumbBrush.ImageSource = big;
            DetailsThumb.Visibility = big is null ? Visibility.Collapsed : Visibility.Visible;

            DetailsDescription.Text = manifest.Description ?? "";
            DetailsDescription.Visibility = string.IsNullOrWhiteSpace(manifest.Description) ? Visibility.Collapsed : Visibility.Visible;

            DetailsCompat.Text = ModsStudioLogic.CompatibilityText(row.Source.Compatibility, package.Files.Count);

            FilesHeader.Text = ModsStudioLogic.FilesHeader(package.Files.Count);
            FilesExpander.IsExpanded = false;
            FilesList.ItemsSource = null;

            int conflicts = ModsStudioLogic.ConflictCountFor(_rows, row.Id);
            ConflictsLink.Visibility = conflicts > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoConflictsText.Visibility = conflicts > 0 ? Visibility.Collapsed : Visibility.Visible;
            ConflictsLinkText.Text = ModsStudioLogic.ConflictsLinkText(conflicts);

            DetailsToggleButton.Content = row.Enabled ? "Turn off" : "Turn on";
            System.Windows.Automation.AutomationProperties.SetName(DetailsToggleButton, row.Enabled ? $"Turn off {row.Name}" : $"Turn on {row.Name}");
            System.Windows.Automation.AutomationProperties.SetName(DetailsRemoveButton, $"Remove {row.Name}");
        }

        private void FilesExpander_Expanded(object sender, RoutedEventArgs e)
        {
            if (SelectedRow is not { } row)
                return;

            FilesList.ItemsSource = row.Source.Package.Files
                .Select(f => f.RelativePath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void FilesExpander_Collapsed(object sender, RoutedEventArgs e) => FilesList.ItemsSource = null;

        private void ConflictsLink_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRow is not { } row)
                return;

            _conflictFilter = row.Id;

            if (_snapshot is not null)
                ApplyConflicts(_snapshot);

            StudioTabs.SelectedItem = TabConflicts;
        }

        private void ShowAllConflicts_Click(object sender, RoutedEventArgs e)
        {
            _conflictFilter = null;

            if (_snapshot is not null)
                ApplyConflicts(_snapshot);
        }

        private void StudioTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectionChanged bubbles up from the combo boxes inside the tabs; only react to the tabs themselves.
            if (!ReferenceEquals(e.OriginalSource, StudioTabs))
                return;

            // the filter belongs to the visit that opened it
            if (_conflictFilter is not null && !ReferenceEquals(StudioTabs.SelectedItem, TabConflicts))
            {
                _conflictFilter = null;

                if (_snapshot is not null)
                    ApplyConflicts(_snapshot);
            }

            if (_loaded && StudioTabs.SelectedContent is UIElement content)
                Motion.FadeRise(content, 0, 8, 300);
        }

        // ---------------------------------------------------------------- actions

        private void RowToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton toggle && toggle.DataContext is ModRowVm row)
                _ = ToggleAsync(row, toggle.IsChecked == true);
        }

        private void DetailsToggle_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRow is { } row)
                _ = ToggleAsync(row, !row.Enabled);
        }

        private Task ToggleAsync(ModRowVm row, bool enable)
        {
            if (enable == row.Enabled)
                return Task.CompletedTask;

            if (_busy)
                return RefreshAsync(); // put the switch back

            string id = row.Id;

            return MutateAsync(() =>
            {
                ModStudio.Default.SetEnabled(id, enable);
                return ModStudio.Default.Rebuild();
            });
        }

        private void DetailsRemove_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRow is not { } row || _busy)
                return;

            var answer = Frontend.ShowMessageBox(ModsStudioLogic.RemoveConfirmation(row.Name), MessageBoxImage.Warning, MessageBoxButton.YesNo);

            if (answer != MessageBoxResult.Yes)
                return;

            string id = row.Id;
            _ = MutateAsync(() => ModStudio.Default.RemovePackage(id));
        }

        private void OrderUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);

        private void OrderDown_Click(object sender, RoutedEventArgs e) => Move(sender, 1);

        private void Move(object sender, int delta)
        {
            if (_busy || _snapshot is null || (sender as FrameworkElement)?.DataContext is not OrderRowVm row)
                return;

            var fullOrder = _snapshot.Packages.Select(p => p.Package.Id).ToList();
            var enabled = new HashSet<string>(_snapshot.Packages.Where(p => p.Enabled).Select(p => p.Package.Id), StringComparer.Ordinal);
            var next = ModsStudioLogic.MoveAmongEnabled(fullOrder, enabled, row.Id, delta);

            if (next is null)
                return;

            _ = MutateAsync(() =>
            {
                ModStudio.Default.SetPriority(next);
                return ModStudio.Default.Rebuild();
            });
        }

        private async void Candidate_Chosen(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: CandidateVm candidate })
                return;

            // Checked is also raised when the radio buttons are set from the data; that is not a choice
            if (e.RoutedEvent == System.Windows.Controls.Primitives.ToggleButton.CheckedEvent && candidate.IsCurrent)
                return;

            // a mouse click raises Checked and then Click; the second one is the same choice
            if (_choosing)
                return;

            _choosing = true;

            try
            {
                await ChooseAsync(candidate);
            }
            finally
            {
                _choosing = false;
            }
        }

        private async Task ChooseAsync(CandidateVm candidate)
        {
            if (_busy || !candidate.Selectable)
            {
                await RefreshAsync(); // put the radio buttons back
                return;
            }

            // already the answer and nothing left to confirm
            if (candidate.IsCurrent && candidate.RowStatus == ConflictRowStatus.Resolved)
                return;

            var row = _rows.FirstOrDefault(r => string.Equals(r.Path, candidate.Path, StringComparison.OrdinalIgnoreCase));
            var builtin = row?.Candidates.FirstOrDefault(c => c.IsBuiltin);

            if (!candidate.IsBuiltin && builtin is not null && !candidate.IsCurrent)
            {
                var answer = Frontend.ShowMessageBox(
                    ModsStudioLogic.PresetOffConfirmation(builtin.Label, candidate.Label),
                    MessageBoxImage.Question,
                    MessageBoxButton.YesNo);

                if (answer != MessageBoxResult.Yes)
                {
                    await RefreshAsync();
                    return;
                }
            }

            string path = candidate.Path;
            string owner = candidate.OwnerId;
            var turnedOff = new List<string>();

            await MutateAsync(
                () =>
                {
                    var resolution = ModStudio.Default.ChooseOwner(path, owner);

                    turnedOff.AddRange(resolution.Actions
                        .Where(a => a.Kind == ResolverActionKind.DisablePreset)
                        .Select(a => a.PresetId));

                    // switches the presets off first (their files are only removed when they match the preset itself), then builds
                    return ModStudio.Default.Rebuild(allowPresetDisable: true);
                },
                afterUi: () =>
                {
                    foreach (string presetId in turnedOff)
                    {
                        var preset = ModStudio.Default.Catalog.Find(presetId);

                        if (preset is not null)
                            _presets.SetPresetOff(preset.TaskName);
                    }

                    UpdateCursorPreview();
                },
                prefix: turnedOff.Count == 0 ? null : () => string.Join(", ", turnedOff.Select(LabelOf)) + (turnedOff.Count == 1 ? " is off. " : " are off. "));
        }

        /// <summary>
        /// Runs an engine change off the UI thread with the busy bar on, shows what happened, then re-reads the state so
        /// the screen always matches what is on disk (also after a failure).
        /// </summary>
        private async Task MutateAsync(Func<ModRebuildResult> work, Action? afterUi = null, Func<string?>? prefix = null)
        {
            if (_busy)
                return;

            SetBusy(true);

            try
            {
                var result = await ModsStudioService.RunAsync(work);

                afterUi?.Invoke();

                var r = result.Result;

                if (r.Skipped)
                    Toast($"Couldn't update the files right now: {r.SkipReason} The change is saved and applies on the next launch.", isError: true);
                else if (r.Errors.Count > 0)
                    Toast($"{r.Errors.Count} file{(r.Errors.Count == 1 ? "" : "s")} could not be updated. {r.Errors[0]}", isError: true);
                else
                    Toast((prefix?.Invoke() ?? "") + ModsStudioLogic.UpdatedMessage(r.Copied, r.Deleted, r.Displaced), isError: false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Toast(Plain(ex), isError: true);
            }

            await RefreshAsync();
            SetBusy(false);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            StudioTabs.IsHitTestVisible = !busy;
        }

        private static string Plain(Exception ex) => ex is ModStudioException ? ex.Message : "Something went wrong. " + ex.Message;

        private void Toast(string message, bool isError) => ToastRequested?.Invoke(this, new ModsToastEventArgs(message, isError));

        // ---------------------------------------------------------------- header menu

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            MoreMenu.PlacementTarget = MoreButton;
            MoreMenu.IsOpen = true;
        }

        private void OpenModsFolder_Click(object sender, RoutedEventArgs e) => Run(() => _presets.OpenModsFolderCommand.Execute(null));

        private void OpenCompat_Click(object sender, RoutedEventArgs e) => Run(() => _presets.OpenCompatSettingsCommand.Execute(null));

        private void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Toast(Plain(ex), isError: true);
            }
        }

        // ---------------------------------------------------------------- library

        private void LibraryGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
            LibraryGrid.Columns = e.NewSize.Width >= 700 ? 3 : 2;

        private void CursorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            e.Handled = true;
            UpdateCursorPreview();
        }

        private static ImageSource? LoadEmbedded(string resource)
        {
            try
            {
                using var stream = Resource.GetStream(resource);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        private void UpdateCursorPreview()
        {
            var type = _presets.CursorTypeTask.NewState;

            ImageSource? arrow = null, far = null;

            if (type == ZeroStrap.Enums.CursorType.From2006)
            {
                arrow = _cursor2006Arrow ??= LoadEmbedded("Cursor.From2006.ArrowCursor.png");
                far = _cursor2006Far ??= LoadEmbedded("Cursor.From2006.ArrowFarCursor.png");
            }
            else if (type == ZeroStrap.Enums.CursorType.From2013)
            {
                arrow = _cursor2013Arrow ??= LoadEmbedded("Cursor.From2013.ArrowCursor.png");
                far = _cursor2013Far ??= LoadEmbedded("Cursor.From2013.ArrowFarCursor.png");
            }

            CursorArrowImage.Source = arrow;
            CursorFarImage.Source = far;

            bool show = arrow is not null;
            CursorPreview.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            CursorDefaultHint.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
