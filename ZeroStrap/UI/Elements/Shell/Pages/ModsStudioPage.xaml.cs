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

        // ---------------------------------------------------------------- import

        private enum ImportStep
        {
            Choose,
            Analyzing,
            Preview
        }

        private bool _importOpen;
        private bool _analysing;
        private int _analyzeToken;
        private ImportPreview? _preview;

        private bool ImportBusy => _busy || _analysing;

        private void ImportButton_Click(object sender, RoutedEventArgs e) => OpenImport(null);

        /// <summary>Opens the import view (animated), optionally analysing <paramref name="path"/> straight away.</summary>
        private void OpenImport(string? path)
        {
            if (Motion.IsSwapping || _busy)
                return;

            if (!_importOpen)
            {
                _importOpen = true;
                ShowStep(ImportStep.Choose, animate: false);
                ImportChooseError.Visibility = Visibility.Collapsed;
                ImportView.ScrollToTop();

                if (IsLoaded)
                {
                    Motion.WorkspaceSwap(StudioTabs, ImportView, forward: true, completed: () => ImportBackButton.Focus());
                }
                else
                {
                    StudioTabs.Visibility = Visibility.Collapsed;
                    ImportView.Visibility = Visibility.Visible;
                }
            }

            if (path is not null)
                _ = AnalyzeAsync(path);
        }

        private void CloseImport()
        {
            if (!_importOpen || Motion.IsSwapping)
                return;

            _importOpen = false;
            _analyzeToken++; // a result that is still coming is no longer wanted
            _analysing = false;
            _preview = null;

            if (IsLoaded)
            {
                Motion.WorkspaceSwap(ImportView, StudioTabs, forward: false);
            }
            else
            {
                ImportView.Visibility = Visibility.Collapsed;
                StudioTabs.Visibility = Visibility.Visible;
            }
        }

        private void ImportBack_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy)
                CloseImport();
        }

        private void ChooseAnother_Click(object sender, RoutedEventArgs e)
        {
            _analyzeToken++;
            _preview = null;
            ShowStep(ImportStep.Choose, animate: true);
        }

        private void ShowStep(ImportStep step, bool animate)
        {
            ImportChoosePanel.Visibility = step == ImportStep.Choose ? Visibility.Visible : Visibility.Collapsed;
            ImportAnalyzingPanel.Visibility = step == ImportStep.Analyzing ? Visibility.Visible : Visibility.Collapsed;
            ImportPreviewPanel.Visibility = step == ImportStep.Preview ? Visibility.Visible : Visibility.Collapsed;

            if (!animate)
                return;

            UIElement shown = step switch
            {
                ImportStep.Choose => ImportChoosePanel,
                ImportStep.Analyzing => ImportAnalyzingPanel,
                _ => ImportPreviewPanel
            };

            Motion.FadeRise(shown, 0, 8, 300);
        }

        private void ChooseZip_Click(object sender, RoutedEventArgs e)
        {
            if (ImportBusy)
                return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a mod zip",
                Filter = "Zip files (*.zip)|*.zip",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
                _ = AnalyzeAsync(dialog.FileName);
        }

        private void ChooseFolder_Click(object sender, RoutedEventArgs e)
        {
            if (ImportBusy)
                return;

            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose the mod's folder",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                _ = AnalyzeAsync(dialog.SelectedPath);
        }

        private async Task AnalyzeAsync(string path)
        {
            if (ImportBusy)
                return;

            int token = ++_analyzeToken;
            _analysing = true;
            ImportChooseError.Visibility = Visibility.Collapsed;
            ShowStep(ImportStep.Analyzing, animate: true);

            try
            {
                var preview = await ModsStudioService.RunAsync(() => ModStudio.Default.AnalyzeImport(path));

                if (token != _analyzeToken)
                    return;

                _analysing = false;
                ShowPreview(preview);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);

                if (token != _analyzeToken)
                    return;

                _analysing = false;
                ImportChooseError.Text = Plain(ex);
                ImportChooseError.Visibility = Visibility.Visible;
                ShowStep(ImportStep.Choose, animate: true);
            }
            finally
            {
                if (token == _analyzeToken)
                    _analysing = false;
            }
        }

        private void ShowPreview(ImportPreview p)
        {
            _preview = p;

            PreviewSourceName.Text = Path.GetFileName(p.SourcePath.TrimEnd('\\', '/')) is { Length: > 0 } fileName ? fileName : p.SourcePath;
            PreviewSourceName.ToolTip = p.SourcePath;
            PreviewLayoutText.Text = p.CanInstall
                ? ModsStudioLogic.LayoutText(p.Layout, p.WrapperFolder) + " · " + ModsStudioLogic.ImportSummary(p.AcceptedFiles.Count, p.TotalBytes)
                : ModsStudioLogic.LayoutText(p.Layout, p.WrapperFolder);

            string? reason = p.CannotInstallReason;
            PreviewFatalCard.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
            PreviewFatalText.Text = reason ?? "";
            PreviewFormCard.Visibility = reason is null ? Visibility.Visible : Visibility.Collapsed;

            ImportNameBox.Text = p.SuggestedName;
            ImportCategoryBox.ItemsSource = Enum.GetValues<ModCategory>().Select(c => new { Value = c, Label = ModsStudioLogic.CategoryLabel(c) }).ToList();
            ImportCategoryBox.SelectedValue = p.SuggestedCategory;

            string author = string.Join(" · ", new[]
            {
                string.IsNullOrWhiteSpace(p.Author) ? null : "By " + p.Author.Trim(),
                string.IsNullOrWhiteSpace(p.Version) ? null : "Version " + p.Version.Trim()
            }.Where(t => t is not null));
            PreviewAuthorText.Text = author;
            PreviewAuthorText.Visibility = author.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            PreviewFilesCard.Visibility = p.AcceptedFiles.Count > 0 || p.IgnoredEntries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PreviewFilesHeader.Text = ModsStudioLogic.FilesHeader(p.AcceptedFiles.Count);
            PreviewFilesExpander.Visibility = p.AcceptedFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PreviewFilesExpander.IsExpanded = false;
            PreviewFilesList.ItemsSource = null;

            string? ignored = ModsStudioLogic.IgnoredSummary(p.IgnoredEntries);
            PreviewIgnoredText.Text = ignored ?? "";
            PreviewIgnoredText.Visibility = ignored is null ? Visibility.Collapsed : Visibility.Visible;

            PreviewRejectedCard.Visibility = p.RejectedEntries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PreviewRejectedHeader.Text = $"Not imported ({p.RejectedEntries.Count})";
            PreviewRejectedList.ItemsSource = p.RejectedEntries;

            PreviewWarningsList.ItemsSource = p.Warnings;
            PreviewWarningsCard.Visibility = p.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            string? conflict = ModsStudioLogic.ConflictCaption(p.Conflicts);
            PreviewConflictText.Text = conflict ?? "";
            PreviewConflictText.Visibility = conflict is null ? Visibility.Collapsed : Visibility.Visible;

            ImportInstallError.Visibility = Visibility.Collapsed;
            ImportView.IsEnabled = true;
            UpdateInstallState();
            ShowStep(ImportStep.Preview, animate: true);
            ImportView.ScrollToTop();
        }

        private void PreviewFiles_Expanded(object sender, RoutedEventArgs e)
        {
            if (_preview is null)
                return;

            PreviewFilesList.ItemsSource = _preview.AcceptedFiles
                .Select(f => f.Path)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void PreviewFiles_Collapsed(object sender, RoutedEventArgs e) => PreviewFilesList.ItemsSource = null;

        private void ImportNameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateInstallState();

        private void UpdateInstallState()
        {
            string? error = ModsStudioLogic.ValidateName(ImportNameBox.Text);

            ImportNameError.Text = error ?? "";
            ImportNameError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
            ImportInstallButton.IsEnabled = _preview is { CanInstall: true } && error is null;
        }

        private async void ImportInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _preview is not { CanInstall: true } preview || ModsStudioLogic.ValidateName(ImportNameBox.Text) is not null)
                return;

            string name = ImportNameBox.Text.Trim();
            var category = ImportCategoryBox.SelectedValue is ModCategory c ? c : preview.SuggestedCategory;

            ImportInstallError.Visibility = Visibility.Collapsed;
            ImportView.IsEnabled = false;
            SetBusy(true);

            ModImportResult? imported = null;

            try
            {
                imported = await ModsStudioService.RunAsync(() => ModStudio.Default.Import(preview, name, category));
            }
            catch (Exception ex)
            {
                if (ex is not ModStudioException)
                    App.Logger.WriteException(LOG_IDENT, ex);

                ImportInstallError.Text = Plain(ex);
                ImportInstallError.Visibility = Visibility.Visible;
            }

            if (imported is null)
            {
                ImportView.IsEnabled = true;
                SetBusy(false);
                return;
            }

            _selectedId = imported.Package.Id;
            StudioTabs.SelectedItem = TabInstalled;

            var rebuild = imported.Rebuild.Result;

            if (rebuild.Skipped)
                Toast($"Imported {name}, but the files couldn't be updated right now: {rebuild.SkipReason} They apply on the next launch.", isError: true);
            else if (rebuild.Errors.Count > 0)
                Toast($"Imported {name}, but {rebuild.Errors.Count} file{(rebuild.Errors.Count == 1 ? "" : "s")} could not be updated. {rebuild.Errors[0]}", isError: true);
            else
                Toast(ModsStudioLogic.ImportedMessage(name, imported.Package.Files.Count), isError: false);

            // GetStateAsync also publishes the summary (sidebar badge, Overview counts)
            await RefreshAsync();
            ImportView.IsEnabled = true;
            SetBusy(false);
            CloseImport();
        }

        // drag and drop: a .zip or a folder anywhere on the page

        private static string[]? DroppedPaths(IDataObject data) =>
            data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null;

        private static bool IsImportable(string path) =>
            Directory.Exists(path) || (File.Exists(path) && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase));

        private void Page_DragOver(object sender, DragEventArgs e)
        {
            var paths = DroppedPaths(e.Data);
            bool ok = paths is { Length: > 0 } && IsImportable(paths[0]) && !_busy && !_analysing && !Motion.IsSwapping;

            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            DropHighlight.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Page_DragLeave(object sender, DragEventArgs e) => DropHighlight.Visibility = Visibility.Collapsed;

        private void Page_Drop(object sender, DragEventArgs e)
        {
            DropHighlight.Visibility = Visibility.Collapsed;
            e.Handled = true;

            var paths = DroppedPaths(e.Data);

            if (paths is not { Length: > 0 } || ImportBusy || Motion.IsSwapping)
                return;

            if (!IsImportable(paths[0]))
            {
                Toast("Drop a .zip file or a folder to import a mod.", isError: true);
                return;
            }

            string note = ModsStudioLogic.DropIgnoredNote(paths.Length);

            if (note.Length > 0)
                Toast(note, isError: false);

            if (_importOpen)
            {
                _analyzeToken++;
                _ = AnalyzeAsync(paths[0]);
            }
            else
            {
                OpenImport(paths[0]);
            }
        }

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
