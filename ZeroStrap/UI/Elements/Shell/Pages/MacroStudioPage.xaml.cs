using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using Wpf.Ui.Common;

using ZeroStrap.Macros;
using ZeroStrap.UI.Utility;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.UI.Elements.Shell.Pages
{
    public partial class MacroStudioPage : Page
    {
        private const string LOG_IDENT = "MacroStudioPage";

        // ---------------------------------------------------------------- row view models

        public abstract class RowBase : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

            public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        public sealed class MacroRow : RowBase
        {
            public MacroRow(Macro macro) => Macro = macro;

            public Macro Macro { get; }

            /// <summary>Short chip text when the macro's shortcut could not be registered.</summary>
            public string? Problem { get; set; }

            public string Name => string.IsNullOrWhiteSpace(Macro.Name) ? "Untitled" : Macro.Name;

            public string Meta => MacroStudioLogic.ListMeta(Macro);

            public bool Enabled => Macro.Enabled;

            public string StatusText => Problem ?? "";

            public Visibility StatusVisibility => Problem is null ? Visibility.Collapsed : Visibility.Visible;
        }

        public sealed class ActionRow : RowBase
        {
            public ActionRow(FlatAction flat)
            {
                Action = flat.Action;
                Depth = flat.Depth;
            }

            public MacroAction Action { get; }

            public int Depth { get; }

            public Thickness Indent => new(Depth * 22, 0, 0, 0);

            public SymbolRegular Glyph =>
                Enum.TryParse(MacroStudioLogic.IconName(Action.Kind), out SymbolRegular symbol) ? symbol : SymbolRegular.Box24;

            public string Summary => MacroStudioLogic.Summary(Action, MacroStudioService.KeyName);

            public string Note => Action.Note ?? "";

            public Visibility NoteVisibility => string.IsNullOrWhiteSpace(Action.Note) ? Visibility.Collapsed : Visibility.Visible;

            public string DelayText => Action.DelayBeforeMs > 0 ? "+" + Action.DelayBeforeMs + " ms" : "";

            public bool Enabled => Action.Enabled;

            public double RowOpacity => Action.Enabled ? 1.0 : 0.5;
        }

        // ---------------------------------------------------------------- state

        private readonly ObservableCollection<MacroRow> _rows = new();
        private readonly ObservableCollection<ActionRow> _actionRows = new();
        private readonly HashSet<Macro> _dirty = new();
        private readonly Dictionary<string, HotkeyRegistration> _registrations = new();
        private readonly List<string> _messages = new();
        private readonly DispatcherTimer _saveTimer;

        private MacroRuntime? _runtime;
        private MacroStudioSettings _settings = new();
        private Macro? _current;
        private MacroAction? _action;

        private MacroRuntimeState _state = MacroRuntimeState.Idle;
        private bool _paused;
        private bool _started;
        private bool _loaded;
        private bool _loading;
        private bool _suppressActionSelection;
        private bool _hotkeysDirty;
        private bool _reloading;
        private bool _reloadAgain;
        private bool _addToGroup;

        public MacroStudioPage()
        {
            InitializeComponent();

            MacroList.ItemsSource = _rows;
            ActionList.ItemsSource = _actionRows;

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (_, _) => FlushSave();

            AddMenu.Closed += (_, _) => Dispatcher.BeginInvoke(new Action(() => _addToGroup = false), DispatcherPriority.Background);

            AutomationNoticeText.Text = MacroStudioLogic.AutomationNotice;
            Loaded += Page_Loaded;
            Unloaded += Page_Unloaded;
        }

        // ---------------------------------------------------------------- start / stop

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;

            try
            {
                _runtime ??= MacroRuntime.Instance;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                SetMessage("Macro Studio couldn't start: " + ex.Message);
                RecordButton.IsEnabled = RunButton.IsEnabled = NewMacroButton.IsEnabled = ImportButton.IsEnabled = false;
                return;
            }

            Subscribe(_runtime);
            _state = _runtime.State;
            _paused = _runtime.IsPaused;
            UpdateState();

            if (_started)
                return;

            _started = true;
            BusyBar.Visibility = Visibility.Visible;

            try
            {
                var (macros, settings) = await MacroStudioService.LoadAsync(_runtime);

                if (!_loaded)
                    return;

                _settings = settings;
                EmergencyText.Text = "Emergency stop: " + _settings.EmergencyStop;
                AutomationNotice.Visibility = _settings.NoticeAcknowledged ? Visibility.Collapsed : Visibility.Visible;

                foreach (var macro in macros)
                    _rows.Add(new MacroRow(macro));

                UpdateEmptyStates();
                UpdateState();

                if (_rows.Count > 0)
                    MacroList.SelectedIndex = 0;

                RequestReload();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                SetMessage("Couldn't load your macros: " + ex.Message);
            }
            finally
            {
                BusyBar.Visibility = Visibility.Hidden;
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            _saveTimer.Stop();

            try
            {
                FlushSave();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }

            if (_runtime is null)
                return;

            Unsubscribe(_runtime);
            SaveOrphanRecording();
        }

        /// <summary>Leaving the page mid-recording must not lose it: stop it and keep it as a new macro.</summary>
        private void SaveOrphanRecording()
        {
            try
            {
                if (_runtime is null || _runtime.State != MacroRuntimeState.Recording)
                    return;

                var result = _runtime.StopRecording();

                if (result is null || result.Cancelled || result.Actions.Count == 0)
                    return;

                var macro = MacroStudioLogic.NewMacro(_rows.Select(r => r.Macro.Name), MacroStudioLogic.RecordingName(DateTime.Now), result.Actions);
                _runtime.Store.Save(macro);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        private void Subscribe(MacroRuntime runtime)
        {
            Unsubscribe(runtime);
            runtime.StateChanged += OnStateChanged;
            runtime.Warning += OnWarning;
            runtime.PausedChanged += OnPausedChanged;
            runtime.PlaybackFinished += OnPlaybackFinished;
            runtime.RecordingFinished += OnRecordingFinished;
        }

        private void Unsubscribe(MacroRuntime runtime)
        {
            runtime.StateChanged -= OnStateChanged;
            runtime.Warning -= OnWarning;
            runtime.PausedChanged -= OnPausedChanged;
            runtime.PlaybackFinished -= OnPlaybackFinished;
            runtime.RecordingFinished -= OnRecordingFinished;
        }

        // ---------------------------------------------------------------- runtime events (any thread: always BeginInvoke)

        private void OnStateChanged(MacroRuntimeState state) => Post(() =>
        {
            _state = state;

            if (state != MacroRuntimeState.Running)
                _paused = false;

            UpdateState();
        });

        private void OnWarning(string message) => Post(() => AddMessage(message));

        private void OnPausedChanged(bool paused) => Post(() =>
        {
            _paused = paused;
            UpdateState();
        });

        private void OnPlaybackFinished(string id, PlayResult result) => Post(() =>
        {
            if (result.Outcome == PlayOutcome.Faulted)
                AddMessage("The macro stopped because of an error: " + (result.Error ?? "unknown error"));
        });

        private void OnRecordingFinished(RecordingResult result) => Post(() => HandleRecording(result));

        private void Post(Action work) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_loaded)
                return;

            try
            {
                work();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }));

        // ---------------------------------------------------------------- header state

        private void UpdateState()
        {
            bool idle = _state == MacroRuntimeState.Idle;
            string? runningName = null;

            if (_state == MacroRuntimeState.Running)
            {
                string? id = _runtime?.CurrentMacroId;
                runningName = _rows.FirstOrDefault(r => r.Macro.Id == id)?.Macro.Name;
            }

            StateText.Text = MacroStudioLogic.StateLine(_state, runningName, _paused, _settings.RecordStopKey);

            bool recording = _state == MacroRuntimeState.Recording;
            RecDot.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;

            if (recording && SystemParameters.ClientAreaAnimation)
            {
                RecDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.25, TimeSpan.FromMilliseconds(650))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                });
            }
            else
            {
                RecDot.BeginAnimation(OpacityProperty, null);
                RecDot.Opacity = 1.0;
            }

            RecordButton.IsEnabled = idle && _runtime is not null;
            RunButton.IsEnabled = idle && _current is not null;
            StopButton.IsEnabled = !idle;
        }

        private void UpdateEmptyStates()
        {
            bool none = _rows.Count == 0;
            NoMacrosState.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            MacroListEmpty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            NoSelectionText.Visibility = !none && _current is null ? Visibility.Visible : Visibility.Collapsed;
            EditorScroll.Visibility = _current is null ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------------------------------------------------------------- messages

        private void SetMessage(params string[] lines)
        {
            _messages.Clear();
            _messages.AddRange(lines.Where(l => !string.IsNullOrWhiteSpace(l)));
            ShowMessages();
        }

        private void AddMessage(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || _messages.Contains(line))
                return;

            _messages.Add(line);

            if (_messages.Count > 6)
                _messages.RemoveAt(0);

            ShowMessages();
        }

        private void ShowMessages()
        {
            MessageText.Text = string.Join(Environment.NewLine, _messages);
            MessagePanel.Visibility = _messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void DismissMessage_Click(object sender, RoutedEventArgs e)
        {
            _messages.Clear();
            ShowMessages();
        }

        private void DismissAutomationNotice_Click(object sender, RoutedEventArgs e)
        {
            AutomationNotice.Visibility = Visibility.Collapsed;
            _settings.NoticeAcknowledged = true;
            SaveSettings();
        }

        private void SaveSettings()
        {
            try
            {
                _runtime?.SettingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                AddMessage("Couldn't save Macro Studio settings: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- saving

        private void MarkDirty(Macro macro, bool hotkeys = false)
        {
            _dirty.Add(macro);

            if (hotkeys)
                _hotkeysDirty = true;

            _rows.FirstOrDefault(r => ReferenceEquals(r.Macro, macro))?.Refresh();

            if (ReferenceEquals(macro, _current))
                EstimateText.Text = MacroStudioLogic.EstimateText(macro);

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>Saves every changed macro that is valid. False when something was left unsaved (invalid or an I/O error).</summary>
        private bool FlushSave()
        {
            _saveTimer.Stop();

            if (_runtime is null || _dirty.Count == 0)
                return true;

            bool allSaved = true;
            bool saved = false;

            foreach (var macro in _dirty.ToList())
            {
                try
                {
                    var result = MacroValidator.Process(macro);

                    if (!result.IsValid)
                    {
                        allSaved = false;

                        if (ReferenceEquals(macro, _current))
                            ShowValidation(result.Errors);

                        continue;
                    }

                    _runtime.Store.Save(macro);
                    _dirty.Remove(macro);
                    saved = true;

                    if (ReferenceEquals(macro, _current))
                        ShowValidation(null);
                }
                catch (Exception ex)
                {
                    allSaved = false;
                    App.Logger.WriteException(LOG_IDENT, ex);
                    AddMessage($"Couldn't save \"{macro.Name}\": {ex.Message}");
                }
            }

            if (saved && _hotkeysDirty)
            {
                _hotkeysDirty = false;
                RequestReload();
            }

            return allSaved;
        }

        private void ShowValidation(IEnumerable<string>? errors)
        {
            var list = errors?.ToList();

            if (list is null || list.Count == 0)
            {
                ValidationPanel.Visibility = Visibility.Collapsed;
                ValidationText.Text = "";
                return;
            }

            ValidationText.Text = "This macro has problems and isn't saved yet:" + Environment.NewLine
                + string.Join(Environment.NewLine, list.Take(6).Select(l => "• " + l));
            ValidationPanel.Visibility = Visibility.Visible;
        }

        private async void RequestReload()
        {
            if (_runtime is null)
                return;

            if (_reloading)
            {
                _reloadAgain = true;
                return;
            }

            _reloading = true;
            HotkeyReloadResult? result = null;

            try
            {
                result = await MacroStudioService.ReloadHotkeysAsync(_runtime);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
            finally
            {
                _reloading = false;
            }

            if (_loaded && result is not null)
                ApplyRegistrations(result);

            if (_reloadAgain)
            {
                _reloadAgain = false;
                RequestReload();
            }
        }

        private void ApplyRegistrations(HotkeyReloadResult result)
        {
            _registrations.Clear();

            foreach (var status in result.Macros)
                _registrations[status.MacroId] = status.Registration;

            foreach (var row in _rows)
            {
                row.Problem = null;

                if (_registrations.TryGetValue(row.Macro.Id, out var reg) && !reg.IsOk)
                {
                    row.Problem = reg.Status switch
                    {
                        HotkeyStatus.InUse => "Shortcut in use",
                        HotkeyStatus.Duplicate => "Duplicate shortcut",
                        _ => "Shortcut unavailable"
                    };
                }

                row.Refresh();
            }

            UpdateShortcutStatus();
        }

        private void UpdateShortcutStatus()
        {
            if (_current is null)
                return;

            _registrations.TryGetValue(_current.Id, out var reg);
            ShortcutStatusText.Text = MacroStudioLogic.HotkeyStatusText(_current, _registrations.ContainsKey(_current.Id) ? reg : null);
        }

        // ---------------------------------------------------------------- macro list

        private void MacroList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _current = (MacroList.SelectedItem as MacroRow)?.Macro;
            LoadMacroIntoControls();
        }

        private void LoadMacroIntoControls()
        {
            _loading = true;

            try
            {
                UpdateEmptyStates();

                if (_current is not null)
                {
                    NameBox.Text = _current.Name;
                    ShortcutBox.Text = _current.Hotkey ?? "";
                    ClearShortcutButton.IsEnabled = !string.IsNullOrEmpty(_current.Hotkey);

                    var repeat = _current.Repeat ??= new MacroRepeat();
                    RepeatCombo.SelectedIndex = (int)repeat.Mode;
                    RepeatCountBox.Text = repeat.Count.ToString();
                    RepeatCountPanel.Visibility = repeat.Mode == MacroRepeatMode.Count ? Visibility.Visible : Visibility.Collapsed;

                    SpeedSlider.Value = MacroStudioLogic.ClampSpeed(_current.Speed);
                    SpeedText.Text = MacroStudioLogic.FormatSpeedMultiplier(_current.Speed);
                    FocusToggle.IsChecked = _current.FocusRule == MacroFocusRule.RobloxOnly;
                    EstimateText.Text = MacroStudioLogic.EstimateText(_current);

                    UpdateShortcutStatus();
                    ShowValidation(_dirty.Contains(_current) ? MacroValidator.Process(_current).Errors : null);
                }
            }
            finally
            {
                _loading = false;
            }

            RebuildActionList(null);
            UpdateState();
        }

        private void NewMacro_Click(object sender, RoutedEventArgs e)
        {
            var macro = MacroStudioLogic.NewMacro(_rows.Select(r => r.Macro.Name));
            AddMacro(macro);
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void AddMacro(Macro macro)
        {
            var row = new MacroRow(macro);
            _rows.Add(row);
            MacroList.SelectedItem = row;
            MacroList.ScrollIntoView(row);
            MarkDirty(macro, hotkeys: true);
            FlushSave();
        }

        private void RowToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: MacroRow row } element)
                return;

            row.Macro.Enabled = (element as System.Windows.Controls.Primitives.ToggleButton)?.IsChecked == true;
            MarkDirty(row.Macro, hotkeys: true);

            if (ReferenceEquals(row.Macro, _current))
                UpdateShortcutStatus();
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            if (_runtime is null)
                return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import a macro",
                Filter = "Macro files (*.zeromacro)|*.zeromacro|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true)
                return;

            BusyBar.Visibility = Visibility.Visible;

            try
            {
                var result = await MacroStudioService.ImportAsync(_runtime, dialog.FileName);

                if (!_loaded)
                    return;

                if (!result.Success)
                {
                    SetMessage(new[] { "That file couldn't be imported." }.Concat(result.Errors.Take(4)).ToArray());
                    return;
                }

                AddMacroFromStore(result.Macro!);
                SetMessage(new[] { $"Imported \"{result.Macro!.Name}\". It is off until you turn it on." }.Concat(result.Warnings.Take(4)).ToArray());
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                SetMessage("Import failed: " + ex.Message);
            }
            finally
            {
                BusyBar.Visibility = Visibility.Hidden;
            }
        }

        /// <summary>Adds a macro the store already wrote (imported) without saving it again.</summary>
        private void AddMacroFromStore(Macro macro)
        {
            var row = new MacroRow(macro);
            _rows.Add(row);
            MacroList.SelectedItem = row;
            MacroList.ScrollIntoView(row);
        }

        private void DuplicateMacro_Click(object sender, RoutedEventArgs e)
        {
            if (_current is null)
                return;

            var copy = _current.Clone();
            copy.Id = MacroValidator.NewId();
            copy.Enabled = false;
            copy.Hotkey = null;
            copy.Name = MacroStudioLogic.UniqueName(string.IsNullOrWhiteSpace(copy.Name) ? MacroStudioLogic.DefaultMacroName : copy.Name, _rows.Select(r => r.Macro.Name));
            AddMacro(copy);
        }

        private async void ExportMacro_Click(object sender, RoutedEventArgs e)
        {
            if (_runtime is null || _current is null)
                return;

            var macro = _current;

            if (!FlushSave() && _dirty.Contains(macro))
            {
                SetMessage("Fix the errors in this macro before exporting it.");
                return;
            }

            string fileName = string.Concat((string.IsNullOrWhiteSpace(macro.Name) ? "macro" : macro.Name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export macro",
                Filter = "Macro files (*.zeromacro)|*.zeromacro",
                DefaultExt = MacroStore.ShareExtension,
                FileName = fileName
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                string path = dialog.FileName;
                bool ok = await Task.Run(() => _runtime.Store.Export(macro.Id, path));
                SetMessage(ok ? $"Exported \"{macro.Name}\"." : "Couldn't export this macro.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                SetMessage("Export failed: " + ex.Message);
            }
        }

        private void DeleteMacro_Click(object sender, RoutedEventArgs e)
        {
            if (_runtime is null || _current is null)
                return;

            var macro = _current;
            var answer = Frontend.ShowMessageBox($"Delete \"{macro.Name}\"? This can't be undone.", MessageBoxImage.Warning, MessageBoxButton.YesNo);

            if (answer != MessageBoxResult.Yes)
                return;

            try
            {
                _dirty.Remove(macro);
                _runtime.Store.Delete(macro.Id);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                SetMessage("Couldn't delete the macro: " + ex.Message);
                return;
            }

            int index = MacroList.SelectedIndex;
            _rows.Remove(_rows.First(r => ReferenceEquals(r.Macro, macro)));

            if (_rows.Count > 0)
                MacroList.SelectedIndex = Math.Min(index, _rows.Count - 1);
            else
            {
                _current = null;
                LoadMacroIntoControls();
            }

            RequestReload();
        }

        // ---------------------------------------------------------------- macro settings

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _current is null)
                return;

            _current.Name = NameBox.Text;
            MarkDirty(_current);
        }

        private void RepeatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _current is null || RepeatCombo.SelectedIndex < 0)
                return;

            _current.Repeat.Mode = (MacroRepeatMode)RepeatCombo.SelectedIndex;
            RepeatCountPanel.Visibility = _current.Repeat.Mode == MacroRepeatMode.Count ? Visibility.Visible : Visibility.Collapsed;
            MarkDirty(_current);
        }

        private void RepeatCountBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _current is null)
                return;

            if (MacroStudioLogic.TryParseInt(RepeatCountBox.Text, 1, MacroValidator.MaxRepeatCount, out int count))
            {
                _current.Repeat.Count = count;
                MarkDirty(_current);
            }
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading || _current is null)
                return;

            _current.Speed = SpeedSlider.Value;
            SpeedText.Text = MacroStudioLogic.FormatSpeedMultiplier(_current.Speed);
            MarkDirty(_current);
        }

        private void FocusToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _current is null)
                return;

            _current.FocusRule = FocusToggle.IsChecked == true ? MacroFocusRule.RobloxOnly : MacroFocusRule.AnyWindow;
            MarkDirty(_current);
        }

        // ---------------------------------------------------------------- shortcut capture

        private void CaptureBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox box)
                box.Text = sender == KeyCaptureBox ? "Press a key…" : "Press a shortcut…";
        }

        private void ShortcutBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ShortcutBox.Text = _current?.Hotkey ?? "";

        private void ShortcutBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = MacroStudioService.RealKey(e);

            if (key == Key.Tab)
                return;

            e.Handled = true;

            if (_current is null)
                return;

            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                ShortcutBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                return;
            }

            if (MacroStudioService.IsModifierKey(key))
                return;

            string? text = MacroStudioService.ShortcutFromKey(key);

            if (text is null || MacroStudioLogic.ShortcutIssue(text) is not null)
            {
                ShortcutStatusText.Text = MacroStudioLogic.NotAllowedShortcutText;
                return;
            }

            _current.Hotkey = text;
            ShortcutBox.Text = text;
            ClearShortcutButton.IsEnabled = true;
            MarkDirty(_current, hotkeys: true);
            UpdateShortcutStatus();
            ShortcutBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        private void ClearShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (_current is null)
                return;

            _current.Hotkey = null;
            ShortcutBox.Text = "";
            ClearShortcutButton.IsEnabled = false;
            MarkDirty(_current, hotkeys: true);
            UpdateShortcutStatus();
        }

        private void ChangeEmergency_Click(object sender, RoutedEventArgs e)
        {
            EmergencyIssue.Visibility = Visibility.Collapsed;
            EmergencyCapture.Text = "Press the new shortcut…";
            EmergencyCapture.Visibility = Visibility.Visible;
            ChangeEmergencyButton.Visibility = Visibility.Collapsed;
            EmergencyCapture.Focus();
        }

        private void EndEmergencyCapture()
        {
            EmergencyCapture.Visibility = Visibility.Collapsed;
            ChangeEmergencyButton.Visibility = Visibility.Visible;
        }

        private void EmergencyCapture_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndEmergencyCapture();

        private void EmergencyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = MacroStudioService.RealKey(e);

            if (key == Key.Tab)
                return;

            e.Handled = true;

            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                EndEmergencyCapture();
                ChangeEmergencyButton.Focus();
                return;
            }

            if (MacroStudioService.IsModifierKey(key))
                return;

            string? text = MacroStudioService.ShortcutFromKey(key);

            if (text is null || MacroStudioLogic.ShortcutIssue(text) is not null)
            {
                EmergencyIssue.Text = MacroStudioLogic.NotAllowedShortcutText;
                EmergencyIssue.Visibility = Visibility.Visible;
                return;
            }

            EmergencyIssue.Visibility = Visibility.Collapsed;
            _settings.EmergencyStop = text;
            EmergencyText.Text = "Emergency stop: " + text;
            SaveSettings();
            EndEmergencyCapture();
            ChangeEmergencyButton.Focus();
            RequestReload();
        }

        // ---------------------------------------------------------------- action list

        private void RebuildActionList(MacroAction? select)
        {
            _suppressActionSelection = true;

            try
            {
                _actionRows.Clear();

                if (_current is not null)
                {
                    foreach (var flat in MacroStudioLogic.Flatten(_current.Actions))
                        _actionRows.Add(new ActionRow(flat));
                }

                ActionsEmptyText.Visibility = _actionRows.Count == 0 && _current is not null ? Visibility.Visible : Visibility.Collapsed;

                var row = select is null ? null : _actionRows.FirstOrDefault(r => ReferenceEquals(r.Action, select));
                ActionList.SelectedItem = row;

                if (row is not null)
                    ActionList.ScrollIntoView(row);
            }
            finally
            {
                _suppressActionSelection = false;
            }

            OnActionSelectionChanged();
        }

        private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_suppressActionSelection)
                OnActionSelectionChanged();
        }

        private void OnActionSelectionChanged()
        {
            _action = (ActionList.SelectedItem as ActionRow)?.Action;
            LoadInspector();
            UpdateToolbar();
        }

        private void UpdateToolbar()
        {
            bool has = _action is not null && _current is not null;
            MoveUpButton.IsEnabled = has && MacroStudioLogic.CanMoveUp(_current!.Actions, _action!);
            MoveDownButton.IsEnabled = has && MacroStudioLogic.CanMoveDown(_current!.Actions, _action!);
            DuplicateActionButton.IsEnabled = has;
            DeleteActionButton.IsEnabled = has;
            AddToGroupButton.Visibility = has && _action!.Kind == MacroActionKind.Group ? Visibility.Visible : Visibility.Collapsed;
            AddButton.IsEnabled = _current is not null;
        }

        private void ActionList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_action is null)
                return;

            var key = MacroStudioService.RealKey(e);
            var mods = Keyboard.Modifiers;

            if (key == Key.Delete && mods == ModifierKeys.None)
            {
                e.Handled = true;
                DeleteAction_Click(sender, e);
            }
            else if (key == Key.Up && mods == ModifierKeys.Alt)
            {
                e.Handled = true;
                MoveUp_Click(sender, e);
            }
            else if (key == Key.Down && mods == ModifierKeys.Alt)
            {
                e.Handled = true;
                MoveDown_Click(sender, e);
            }
            else if (key == Key.D && mods == ModifierKeys.Control)
            {
                e.Handled = true;
                DuplicateAction_Click(sender, e);
            }
        }

        private void FocusSelectedActionRow()
        {
            if (ActionList.SelectedItem is null)
                return;

            ActionList.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ActionList.ItemContainerGenerator.ContainerFromItem(ActionList.SelectedItem) is ListBoxItem item)
                    item.Focus();
            }), DispatcherPriority.Background);
        }

        private void AddButton_Click(object sender, RoutedEventArgs e) => OpenAddMenu(AddButton, toGroup: false);

        private void AddToGroupButton_Click(object sender, RoutedEventArgs e) => OpenAddMenu(AddToGroupButton, toGroup: true);

        private void OpenAddMenu(FrameworkElement target, bool toGroup)
        {
            _addToGroup = toGroup;
            AddMenu.PlacementTarget = target;
            AddMenu.IsOpen = true;
        }

        private void AddMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_current is null || sender is not MenuItem { Tag: string tag } || !Enum.TryParse(tag, out MacroActionKind kind))
                return;

            var added = MacroStudioLogic.NewAction(kind);

            if (_addToGroup && _action is { Kind: MacroActionKind.Group })
                MacroStudioLogic.AddToGroup(_action, added);
            else
                MacroStudioLogic.InsertAfter(_current.Actions, _action, added);

            MarkDirty(_current);
            RebuildActionList(added);
            FocusSelectedActionRow();
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (_current is null || _action is null || !MacroStudioLogic.MoveUp(_current.Actions, _action))
                return;

            var moved = _action;
            MarkDirty(_current);
            RebuildActionList(moved);
            FocusSelectedActionRow();
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (_current is null || _action is null || !MacroStudioLogic.MoveDown(_current.Actions, _action))
                return;

            var moved = _action;
            MarkDirty(_current);
            RebuildActionList(moved);
            FocusSelectedActionRow();
        }

        private void DuplicateAction_Click(object sender, RoutedEventArgs e)
        {
            if (_current is null || _action is null)
                return;

            var copy = MacroStudioLogic.Duplicate(_current.Actions, _action);

            if (copy is null)
                return;

            MarkDirty(_current);
            RebuildActionList(copy);
            FocusSelectedActionRow();
        }

        private void DeleteAction_Click(object sender, RoutedEventArgs e)
        {
            if (_current is null || _action is null)
                return;

            var next = MacroStudioLogic.Delete(_current.Actions, _action);
            MarkDirty(_current);
            RebuildActionList(next);
            FocusSelectedActionRow();
        }

        private void ActionRowCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_current is null || sender is not FrameworkElement { DataContext: ActionRow row } element)
                return;

            row.Action.Enabled = (element as System.Windows.Controls.Primitives.ToggleButton)?.IsChecked == true;
            row.Refresh();

            if (ReferenceEquals(row.Action, _action))
            {
                _loading = true;
                ActionEnabledCheck.IsChecked = row.Action.Enabled;
                _loading = false;
            }

            MarkDirty(_current);
        }

        // ---------------------------------------------------------------- inspector

        private void LoadInspector()
        {
            _loading = true;

            try
            {
                InspectorPanel.Visibility = _action is null ? Visibility.Collapsed : Visibility.Visible;

                if (_action is null)
                    return;

                var a = _action;
                InspectorTitle.Text = MacroStudioLogic.KindLabel(a.Kind) + " action";

                KeyPanel.Visibility = Show(a.Kind == MacroActionKind.Key);
                MousePanel.Visibility = Show(a.Kind == MacroActionKind.Mouse);
                CameraPanel.Visibility = Show(a.Kind == MacroActionKind.Camera);
                WaitPanel.Visibility = Show(a.Kind == MacroActionKind.Wait);
                MovePanel.Visibility = Show(a.Kind == MacroActionKind.MouseMoveTo);
                GroupPanel.Visibility = Show(a.Kind == MacroActionKind.Group);

                DelayBox.Text = a.DelayBeforeMs.ToString();
                NoteBox.Text = a.Note ?? "";
                ActionEnabledCheck.IsChecked = a.Enabled;

                switch (a.Kind)
                {
                    case MacroActionKind.Key:
                        KeyCaptureBox.Text = MacroStudioService.KeyName(a.ScanCode, a.Extended);
                        KeyModeCombo.SelectedIndex = (int)a.KeyMode;
                        KeyHoldBox.Text = a.HoldMs.ToString();
                        KeyHoldBox.IsEnabled = a.KeyMode == MacroKeyMode.Tap;
                        break;
                    case MacroActionKind.Mouse:
                        MouseButtonCombo.SelectedIndex = (int)a.Button;
                        MouseModeCombo.SelectedIndex = (int)a.ButtonMode;
                        MouseHoldBox.Text = a.HoldMs.ToString();
                        MouseHoldBox.IsEnabled = a.ButtonMode == MacroButtonMode.Click;
                        break;
                    case MacroActionKind.Camera:
                        CamDxBox.Text = a.Dx.ToString();
                        CamDyBox.Text = a.Dy.ToString();
                        CamDurationBox.Text = a.DurationMs.ToString();
                        CamSpeedBox.Text = MacroStudioLogic.FormatSpeed(MacroStudioLogic.CameraSpeed(a));
                        CamEasingCombo.SelectedIndex = (int)a.Easing;
                        CamHoldRightCheck.IsChecked = a.HoldRightButton;
                        break;
                    case MacroActionKind.Wait:
                        WaitMsBox.Text = a.Ms.ToString();
                        break;
                    case MacroActionKind.MouseMoveTo:
                        MoveXBox.Text = a.X.ToString();
                        MoveYBox.Text = a.Y.ToString();
                        break;
                    case MacroActionKind.Group:
                        GroupRepeatBox.Text = a.GroupRepeat.ToString();
                        break;
                }
            }
            finally
            {
                _loading = false;
            }
        }

        private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>An edit to the selected action: refresh its row and schedule a save.</summary>
        private void ActionEdited()
        {
            if (_current is null || _action is null)
                return;

            _actionRows.FirstOrDefault(r => ReferenceEquals(r.Action, _action))?.Refresh();
            MarkDirty(_current);
        }

        /// <summary>Puts the model's value back into a box whose text was invalid when focus left it.</summary>
        private void NumberBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, RepeatCountBox))
            {
                if (_current is null)
                    return;

                _loading = true;
                RepeatCountBox.Text = _current.Repeat.Count.ToString();
                _loading = false;
            }
            else
            {
                LoadInspector();
            }
        }

        private bool TryInt(System.Windows.Controls.TextBox box, int min, int max, out int value)
        {
            value = 0;
            return !_loading && _action is not null && MacroStudioLogic.TryParseInt(box.Text, min, max, out value);
        }

        private void DelayBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TryInt(DelayBox, 0, MacroValidator.MaxTimeMs, out int v))
            {
                _action!.DelayBeforeMs = v;
                ActionEdited();
            }
        }

        private void NoteBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _action is null)
                return;

            _action.Note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text;
            ActionEdited();
        }

        private void ActionEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _action is null)
                return;

            _action.Enabled = ActionEnabledCheck.IsChecked == true;
            ActionEdited();
        }

        // Key
        private void KeyCaptureBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
            KeyCaptureBox.Text = _action is { Kind: MacroActionKind.Key } a ? MacroStudioService.KeyName(a.ScanCode, a.Extended) : "";

        private void KeyCaptureBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = MacroStudioService.RealKey(e);

            if (key == Key.Tab)
                return;

            e.Handled = true;

            if (_action is not { Kind: MacroActionKind.Key } a)
                return;

            if (!MacroStudioService.TryGetScan(key, out ushort scan, out bool extended))
            {
                AddMessage("That key can't be used in a macro.");
                return;
            }

            a.ScanCode = scan;
            a.Extended = extended;
            KeyCaptureBox.Text = MacroStudioService.KeyName(scan, extended);
            ActionEdited();
            KeyCaptureBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        private void KeyModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _action is null || KeyModeCombo.SelectedIndex < 0)
                return;

            _action.KeyMode = (MacroKeyMode)KeyModeCombo.SelectedIndex;
            KeyHoldBox.IsEnabled = _action.KeyMode == MacroKeyMode.Tap;
            ActionEdited();
        }

        private void KeyHoldBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TryInt(KeyHoldBox, 0, MacroValidator.MaxTimeMs, out int v))
            {
                _action!.HoldMs = v;
                ActionEdited();
            }
        }

        // Mouse
        private void MouseButtonCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _action is null || MouseButtonCombo.SelectedIndex < 0)
                return;

            _action.Button = (ZeroStrap.Macros.MouseButton)MouseButtonCombo.SelectedIndex;
            ActionEdited();
        }

        private void MouseModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _action is null || MouseModeCombo.SelectedIndex < 0)
                return;

            _action.ButtonMode = (MacroButtonMode)MouseModeCombo.SelectedIndex;
            MouseHoldBox.IsEnabled = _action.ButtonMode == MacroButtonMode.Click;
            ActionEdited();
        }

        private void MouseHoldBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TryInt(MouseHoldBox, 0, MacroValidator.MaxTimeMs, out int v))
            {
                _action!.HoldMs = v;
                ActionEdited();
            }
        }

        // Camera: duration and speed edit each other, Dx/Dy keep the duration and refresh the speed.
        private void SetCameraSpeedBox()
        {
            if (_action is null)
                return;

            _loading = true;
            CamSpeedBox.Text = MacroStudioLogic.FormatSpeed(MacroStudioLogic.CameraSpeed(_action));
            _loading = false;
        }

        private void CamDxBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TryInt(CamDxBox, -MacroValidator.MaxCameraDelta, MacroValidator.MaxCameraDelta, out int v))
            {
                _action!.Dx = v;
                SetCameraSpeedBox();
                ActionEdited();
            }
        }

        private void CamDyBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TryInt(CamDyBox, -MacroValidator.MaxCameraDelta, MacroValidator.MaxCameraDelta, out int v))
            {
                _action!.Dy = v;
                SetCameraSpeedBox();
                ActionEdited();
            }
        }

        private void CamDurationBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TryInt(CamDurationBox, 0, MacroValidator.MaxTimeMs, out int v))
            {
                MacroStudioLogic.SetCameraDuration(_action!, v);
                SetCameraSpeedBox();
                ActionEdited();
            }
        }

        private void CamSpeedBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _action is null)
                return;

            if (MacroStudioLogic.TryParseDouble(CamSpeedBox.Text, 0.01, 10_000_000, out double speed))
            {
                MacroStudioLogic.SetCameraSpeed(_action, speed);

                _loading = true;
                CamDurationBox.Text = Math.Min(_action.DurationMs, MacroValidator.MaxTimeMs).ToString();
                _loading = false;

                _action.DurationMs = Math.Min(_action.DurationMs, MacroValidator.MaxTimeMs);
                ActionEdited();
            }
        }

        private void CamEasingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _action is null || CamEasingCombo.SelectedIndex < 0)
                return;

            _action.Easing = (MacroEasing)CamEasingCombo.SelectedIndex;
            ActionEdited();
        }

        private void CamHoldRight_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _action is null)
                return;

            _action.HoldRightButton = CamHoldRightCheck.IsChecked == true;
            ActionEdited();
        }

        // Wait / Move to / Group
        private void WaitMsBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TryInt(WaitMsBox, 0, MacroValidator.MaxTimeMs, out int v))
            {
                _action!.Ms = v;
                ActionEdited();
            }
        }

        private void MoveXBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TryInt(MoveXBox, -MacroValidator.MaxCoordinate, MacroValidator.MaxCoordinate, out int v))
            {
                _action!.X = v;
                ActionEdited();
            }
        }

        private void MoveYBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TryInt(MoveYBox, -MacroValidator.MaxCoordinate, MacroValidator.MaxCoordinate, out int v))
            {
                _action!.Y = v;
                ActionEdited();
            }
        }

        private void GroupRepeatBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TryInt(GroupRepeatBox, 1, MacroValidator.MaxGroupRepeat, out int v))
            {
                _action!.GroupRepeat = v;
                ActionEdited();
            }
        }

        // ---------------------------------------------------------------- run / record / stop

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            if (_runtime is null || _current is null)
                return;

            var macro = _current;
            FlushSave();

            if (_dirty.Contains(macro))
            {
                SetMessage("Fix the errors in this macro before running it.");
                return;
            }

            try
            {
                var runtime = _runtime;
                string id = macro.Id;
                var result = await Task.Run(() => runtime.Start(id));
                string text = MacroStudioLogic.StartResultText(result);

                if (_loaded && text.Length > 0)
                    SetMessage(text);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                SetMessage("The macro couldn't be started: " + ex.Message);
            }
        }

        private async void Record_Click(object sender, RoutedEventArgs e)
        {
            if (_runtime is null)
                return;

            try
            {
                var runtime = _runtime;
                var result = await Task.Run(() => runtime.StartRecording());
                string text = MacroStudioLogic.RecordResultText(result);

                if (_loaded && text.Length > 0)
                    SetMessage(text);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                SetMessage("Recording couldn't start: " + ex.Message);
            }
        }

        private async void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (_runtime is null)
                return;

            try
            {
                if (_runtime.State == MacroRuntimeState.Recording)
                    await MacroStudioService.StopRecordingAsync(_runtime);
                else
                    await MacroStudioService.StopAsync(_runtime);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                SetMessage("Stop failed: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- recording result

        private void HandleRecording(RecordingResult result)
        {
            if (result.Cancelled)
            {
                SetMessage("Recording cancelled.");
                return;
            }

            var lines = new List<string>();

            if (result.Actions.Count == 0)
            {
                lines.Add("Nothing was recorded.");
                lines.AddRange(result.Warnings.Take(5));
                SetMessage(lines.ToArray());
                return;
            }

            Macro target;

            if (_current is null)
            {
                target = MacroStudioLogic.NewMacro(_rows.Select(r => r.Macro.Name), MacroStudioLogic.RecordingName(DateTime.Now), result.Actions);
                AddMacro(target);
            }
            else
            {
                target = _current;
                var mode = RecordingMergeMode.Replace;

                if (target.Actions.Count > 0)
                {
                    var answer = Frontend.ShowMessageBox(
                        $"\"{target.Name}\" already has {MacroStudioLogic.ActionCountText(MacroStudioLogic.CountAll(target.Actions))}." + Environment.NewLine + Environment.NewLine
                        + "Yes: replace them with the recording" + Environment.NewLine
                        + "No: add the recording to the end" + Environment.NewLine
                        + "Cancel: discard the recording",
                        MessageBoxImage.Question,
                        MessageBoxButton.YesNoCancel);

                    if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.None)
                    {
                        SetMessage("Recording discarded.");
                        return;
                    }

                    mode = answer == MessageBoxResult.Yes ? RecordingMergeMode.Replace : RecordingMergeMode.Append;
                }

                target.Actions = MacroStudioLogic.MergeRecording(target.Actions, result.Actions, mode);
                MarkDirty(target);
                RebuildActionList(null);
            }

            lines.Add($"Recorded {MacroStudioLogic.ActionCountText(MacroStudioLogic.CountAll(result.Actions))} into \"{target.Name}\".");
            lines.AddRange(result.Warnings.Take(5));
            SetMessage(lines.ToArray());
        }
    }
}
