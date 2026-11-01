namespace ZeroStrap.Macros
{
    public enum MacroRuntimeState { Idle, Running, Recording }

    public enum MacroStartResult
    {
        Started,

        /// <summary>A macro is already playing (only one runs at a time).</summary>
        AlreadyRunning,

        /// <summary>A recording is in progress; playback and recording never overlap.</summary>
        RecordingInProgress,

        NotFound,
        ShuttingDown,
        Failed
    }

    public enum RecordingStartResult
    {
        Started,

        /// <summary>A macro is playing; playback and recording never overlap.</summary>
        MacroRunning,

        AlreadyRecording,
        ShuttingDown,

        /// <summary>The hooks could not be installed; a Warning event carries the reason.</summary>
        Failed
    }

    /// <summary>What a finished recording produced. <see cref="Cancelled"/> recordings (emergency stop) carry no actions.</summary>
    public sealed class RecordingResult
    {
        public IReadOnlyList<MacroAction> Actions { get; init; } = Array.Empty<MacroAction>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public int RawEventCount { get; init; }
        public bool Cancelled { get; init; }
    }

    /// <summary>Registration outcome of one macro's activation shortcut.</summary>
    public sealed record MacroHotkeyStatus(string MacroId, string Hotkey, HotkeyRegistration Registration);

    /// <summary>Result of <see cref="MacroRuntime.ReloadHotkeys"/>: the emergency stop first, then each enabled macro that has a shortcut.</summary>
    public sealed class HotkeyReloadResult
    {
        public string EmergencyStop { get; init; } = "";
        public HotkeyRegistration EmergencyStopRegistration { get; init; }
        public IReadOnlyList<MacroHotkeyStatus> Macros { get; init; } = Array.Empty<MacroHotkeyStatus>();
    }

    /// <summary>
    /// Owns Macro Studio's moving parts (store, hotkeys, player, recorder) and enforces its rules: one macro at a time,
    /// recording and playback never overlap, the emergency stop is always registered first and always wins, and nothing is
    /// left held down when playback ends, the app closes or it crashes. Events are raised on whichever thread caused them, so
    /// UI code must marshal to its dispatcher. Create the singleton on the UI thread (it owns the hotkey window).
    /// </summary>
    public sealed class MacroRuntime : IDisposable
    {
        public const int StopWaitMs = 500;

        private const string EmergencyName = "emergency-stop";
        private const string MacroPrefix = "macro:";
        private const int ShutdownPlaybackWaitMs = 300;
        private const int ShutdownLockWaitMs = 250;

        private static readonly object InstanceLock = new();
        private static MacroRuntime? _instance;

        private readonly object _gate = new();
        private readonly MacroStore _store;
        private readonly MacroStudioSettingsStore _settingsStore;
        private readonly IHotkeyRegistrar _hotkeys;
        private readonly IInputRecorder _recorder;
        private readonly MacroPlayer _player;
        private readonly IDisposable[] _owned;
        private readonly Action<string> _log;

        // All guarded by _gate.
        private MacroRuntimeState _state = MacroRuntimeState.Idle;
        private string? _currentId;
        private CancellationTokenSource? _cts;
        private Thread? _playThread;
        private int _run;
        private bool _finishingRecording;
        private RecorderOptions? _recordOptions;

        private Hotkey[] _macroHotkeys = Array.Empty<Hotkey>();
        private int _shutdown;
        private int _paused;

        // Player threads alive in this process, across every runtime instance: a new runtime (after Dispose) must not start a macro
        // while an old player thread that failed to stop in time could still be sending input.
        private static int _livePlayers;

        private readonly object _emergencyLock = new();
        private bool _emergencyRegistered;

        private readonly object _retryLock = new();
        private Timer? _retryTimer;
        private long _retryDeadlineTicks;
        private bool _retryWarned;

        /// <summary>How often blocked releases are retried after a run (ms).</summary>
        internal int RetryIntervalMs { get; set; } = 200;

        /// <summary>How long blocked releases keep being retried after a run (ms).</summary>
        internal int RetryWindowMs { get; set; } = 30000;

        /// <summary>How hotkey callbacks hand off slow work. The registrar's thread also serves the emergency stop, so it must never be kept busy.</summary>
        internal Action<Action> BackgroundDispatch { get; set; } = work => ThreadPool.QueueUserWorkItem(_ => work());

        internal static int LivePlayerThreads => Volatile.Read(ref _livePlayers);

        /// <summary>Raised after every state change (outside the internal lock).</summary>
        public event Action<MacroRuntimeState>? StateChanged;

        /// <summary>A user-facing notice: failed shortcut, blocked input, emergency stop, recorder restart, macro failure.</summary>
        public event Action<string>? Warning;

        /// <summary>True when playback paused because Roblox lost focus (RobloxOnly macros), false when it resumed or ended.</summary>
        public event Action<bool>? PausedChanged;

        /// <summary>A playback ended (any outcome). The id is the macro's id.</summary>
        public event Action<string, PlayResult>? PlaybackFinished;

        /// <summary>A recording ended, via Stop key, StopRecording() or the emergency stop (then <see cref="RecordingResult.Cancelled"/>).</summary>
        public event Action<RecordingResult>? RecordingFinished;

        /// <summary>Test/DI constructor. <paramref name="owned"/> are disposed with <see cref="Dispose"/> (not with <see cref="TryShutdown"/>).</summary>
        internal MacroRuntime(
            MacroStore store,
            MacroStudioSettingsStore settingsStore,
            IHotkeyRegistrar hotkeys,
            IInputRecorder recorder,
            MacroPlayer player,
            Action<string>? log = null,
            params IDisposable[] owned)
        {
            _store = store;
            _settingsStore = settingsStore;
            _hotkeys = hotkeys;
            _recorder = recorder;
            _player = player;
            _log = log ?? (_ => { });
            _owned = owned;

            _player.PausedChanged += OnPlayerPausedChanged;
            _recorder.StopRequested += OnRecorderStopRequested;
            _recorder.Warning += RaiseWarning;

            // From the moment the runtime exists the kill switch is armed, whether or not the UI ever reloads the shortcuts.
            try { EnsureEmergencyStop(); } catch (Exception ex) { _log("Could not register the emergency stop: " + ex.Message); }
        }

        // ---- singleton ----

        /// <summary>Test seam: installs (or clears) the singleton without creating the real hotkey window and hooks.</summary>
        internal static void SetInstanceForTests(MacroRuntime? runtime)
        {
            lock (InstanceLock)
                _instance = runtime;
        }

        /// <summary>True once <see cref="Instance"/> has been touched; <see cref="TryShutdown"/> does nothing before that.</summary>
        public static bool IsCreated => Volatile.Read(ref _instance) is not null;

        /// <summary>The app-wide runtime, created on first use. Call from the UI thread.</summary>
        public static MacroRuntime Instance
        {
            get
            {
                lock (InstanceLock)
                    return _instance ??= CreateDefault();
            }
        }

        /// <summary>
        /// Stops playback and releases every held key and button right now, without shutting the runtime down (so a cancelled
        /// Windows SessionEnding leaves it usable). Static, never throws, does nothing if the runtime was never created.
        /// </summary>
        public static void TryStopAll()
        {
            try
            {
                Volatile.Read(ref _instance)?.StopPlaybackNow();
            }
            catch
            {
                // Must never be the thing that throws.
            }
        }

        /// <summary>
        /// Full teardown of the singleton (shell close in a process that keeps running): like <see cref="TryShutdown"/> but the
        /// owned resources are released too and the next <see cref="Instance"/> access creates a fresh runtime. Never throws.
        /// </summary>
        public static void TryDispose()
        {
            try
            {
                Volatile.Read(ref _instance)?.Dispose();
            }
            catch
            {
                // Same as above.
            }
        }

        /// <summary>
        /// Stops playback and recording and releases every held key and button, synchronously. Permanent: nothing can start
        /// afterwards. Static, idempotent and never throws; does nothing if the runtime was never created. Safe from any
        /// thread, including crash handlers.
        /// </summary>
        public static void TryShutdown()
        {
            try
            {
                Volatile.Read(ref _instance)?.Shutdown();
            }
            catch
            {
                // Shutdown must never be the thing that throws.
            }
        }

        private static MacroRuntime CreateDefault()
        {
            static void Log(string message) => App.Logger.WriteLine("MacroRuntime", message);

            var store = new MacroStore(Paths.Macros, Log);
            var settings = new MacroStudioSettingsStore(Path.Combine(Paths.Macros, MacroStudioSettingsStore.FileName), Log);
            var delay = new PrecisionDelay();
            var sink = new SendInputSink(Log);

            // The Roblox gate is always passed: the player only consults it for macros whose focus rule is RobloxOnly.
            var player = new MacroPlayer(sink, delay, new RobloxFocusGate(), Log);
            var recorder = new InputRecorder(Log);
            var hotkeys = new HotkeyService(Log);

            return new MacroRuntime(store, settings, hotkeys, recorder, player, Log, delay);
        }

        // ---- state ----

        public MacroStore Store => _store;

        public MacroStudioSettingsStore SettingsStore => _settingsStore;

        public MacroRuntimeState State
        {
            get
            {
                lock (_gate)
                    return _state;
            }
        }

        /// <summary>The id of the macro that is playing, or null.</summary>
        public string? CurrentMacroId
        {
            get
            {
                lock (_gate)
                    return _state == MacroRuntimeState.Running ? _currentId : null;
            }
        }

        public bool IsPaused => Volatile.Read(ref _paused) == 1;

        // ---- playback ----

        /// <summary>Loads the macro from the store and plays it. Works for disabled macros too (a manual test run).</summary>
        public MacroStartResult Start(string macroId)
        {
            Macro? macro;

            try
            {
                macro = _store.Load(macroId);
            }
            catch (Exception ex)
            {
                _log("Could not load macro: " + ex.Message);
                macro = null;
            }

            return macro is null ? MacroStartResult.NotFound : Start(macro);
        }

        /// <summary>Plays <paramref name="macro"/> (e.g. an unsaved editor draft) on a background thread. Refused while a macro plays or a recording runs.</summary>
        public MacroStartResult Start(Macro macro)
        {
            if (Volatile.Read(ref _shutdown) != 0)
                return MacroStartResult.ShuttingDown;

            // The player gets its own copy: the caller (an editor) may keep changing the draft while it plays.
            macro = macro.Clone();

            // A macro must never run without its kill switch: re-arm it if it was lost, and say so if that is not possible.
            if (!EnsureEmergencyStop())
                RaiseWarning("The emergency stop shortcut is unavailable, so this macro can only be stopped with the Stop button.");

            // The thread is created holding at the gate so the "Running" notification is always raised before it can finish.
            var go = new ManualResetEventSlim(false);

            lock (_gate)
            {
                if (Volatile.Read(ref _shutdown) != 0)
                    return MacroStartResult.ShuttingDown;

                if (_state == MacroRuntimeState.Recording)
                    return MacroStartResult.RecordingInProgress;

                if (_state == MacroRuntimeState.Running || Volatile.Read(ref _livePlayers) > 0)
                    return MacroStartResult.AlreadyRunning;

                var cts = new CancellationTokenSource();
                int run = ++_run;
                var thread = new Thread(() => RunPlayback(macro, cts, run, go)) { IsBackground = true, Name = "ZeroStrap macro player" };

                Interlocked.Increment(ref _livePlayers);

                _state = MacroRuntimeState.Running;
                _currentId = macro.Id;
                _cts = cts;
                _playThread = thread;

                try
                {
                    thread.Start();
                }
                catch (Exception ex)
                {
                    Interlocked.Decrement(ref _livePlayers);
                    _state = MacroRuntimeState.Idle;
                    _currentId = null;
                    _cts = null;
                    _playThread = null;
                    cts.Dispose();
                    _log("Could not start the player thread: " + ex.Message);
                    return MacroStartResult.Failed;
                }
            }

            RaiseStateChanged(MacroRuntimeState.Running);
            go.Set();
            return MacroStartResult.Started;
        }

        private void RunPlayback(Macro macro, CancellationTokenSource cts, int run, ManualResetEventSlim go)
        {
            PlayResult result;

            try
            {
                go.Wait();
                result = _player.Play(macro, cts.Token);
            }
            catch (Exception ex)
            {
                result = PlayResult.Faulted(ex.Message);
            }

            // Belt and braces: the player releases its own held input, this covers anything it could not.
            try { _player.ReleaseHeld(); } catch { }

            lock (_gate)
            {
                if (run == _run)
                {
                    _state = MacroRuntimeState.Idle;
                    _currentId = null;
                    _cts = null;
                    _playThread = null;
                }
            }

            // Disposed outside the lock; a Cancel on it happens under the lock before the state flips, so it is never used after this.
            try { cts.Dispose(); } catch { }

            Interlocked.Exchange(ref _paused, 0);
            Interlocked.Decrement(ref _livePlayers);
            RaiseStateChanged(MacroRuntimeState.Idle);
            RaisePlaybackFinished(macro.Id, result);

            if (result.Outcome == PlayOutcome.Faulted)
                RaiseWarning($"\"{macro.Name}\" stopped because of an error: {result.Error}");

            ScheduleReleaseRetry();
        }

        /// <summary>Cancels the running macro without waiting. Returns false if nothing was running.</summary>
        public bool RequestStop()
        {
            if (!CancelAndRelease())
                return false;

            ScheduleReleaseRetry();
            return true;
        }

        /// <summary>
        /// Cancels the running macro and waits up to <see cref="StopWaitMs"/> ms for it to unwind. Returns true when no macro is
        /// running afterwards. State only becomes Idle once the player thread has really ended, so a stuck thread can never
        /// overlap with a new macro.
        /// </summary>
        public bool Stop()
        {
            Thread? thread;

            lock (_gate)
            {
                if (_state != MacroRuntimeState.Running)
                    return true;

                thread = _playThread;
            }

            CancelAndRelease();

            if (thread is not null && thread != Thread.CurrentThread)
                thread.Join(StopWaitMs);

            ScheduleReleaseRetry();

            lock (_gate)
                return _state != MacroRuntimeState.Running;
        }

        // Cancels and releases while holding the lock: the state cannot change to a *new* run in between, so this can never
        // release (and thereby cancel) a macro that started after the one the caller meant.
        private bool CancelAndRelease()
        {
            lock (_gate)
            {
                if (_state != MacroRuntimeState.Running || _cts is null)
                    return false;

                try { _cts.Cancel(); } catch { }
                try { _player.ReleaseHeld(); } catch { }
                return true;
            }
        }

        /// <summary>Releases every key and button the player is holding. Thread-safe and idempotent. Returns how many releases failed.</summary>
        public int ReleaseAll()
        {
            int failures;

            try
            {
                failures = _player.ReleaseHeld();
            }
            catch
            {
                failures = 1;
            }

            ScheduleReleaseRetry();
            return failures;
        }

        // ---- recording ----

        /// <summary>
        /// Starts capturing input. Refused while a macro plays. The stop key and emergency stop (from the Macro Studio settings)
        /// end it and are not recorded, nor are the activation shortcuts of enabled macros.
        /// </summary>
        public RecordingStartResult StartRecording(bool recordAbsoluteMoves = false)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _shutdown) != 0)
                    return RecordingStartResult.ShuttingDown;

                if (_state == MacroRuntimeState.Running)
                    return RecordingStartResult.MacroRunning;

                if (_state == MacroRuntimeState.Recording)
                    return RecordingStartResult.AlreadyRecording;

                _state = MacroRuntimeState.Recording;
                _finishingRecording = false;
            }

            RaiseStateChanged(MacroRuntimeState.Recording);

            string? error = null;
            bool started = false;
            RecorderOptions? options = null;

            try
            {
                var settings = LoadSettings();

                options = new RecorderOptions
                {
                    StopKey = ParseOrDefault(settings.RecordStopKey, MacroStudioSettings.DefaultRecordStopKey),
                    EmergencyStop = ParseOrDefault(settings.EmergencyStop, MacroStudioSettings.DefaultEmergencyStop),
                    IgnoredHotkeys = Volatile.Read(ref _macroHotkeys),
                    RecordAbsoluteMoves = recordAbsoluteMoves
                };

                lock (_gate)
                    _recordOptions = options;

                started = _recorder.Start(options, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (!started)
            {
                lock (_gate)
                {
                    _state = MacroRuntimeState.Idle;
                    _recordOptions = null;
                }

                RaiseStateChanged(MacroRuntimeState.Idle);
                RaiseWarning("Could not start recording: " + (error ?? "unknown error"));
                return RecordingStartResult.Failed;
            }

            return RecordingStartResult.Started;
        }

        /// <summary>Ends the recording and returns the compiled actions (also raised as <see cref="RecordingFinished"/>). Null if no recording was running.</summary>
        public RecordingResult? StopRecording() => FinishRecording(cancelled: false);

        /// <summary>Ends the recording and discards it.</summary>
        public void CancelRecording() => FinishRecording(cancelled: true);

        private RecordingResult? FinishRecording(bool cancelled)
        {
            RecorderOptions? options;

            lock (_gate)
            {
                if (_state != MacroRuntimeState.Recording || _finishingRecording)
                    return null;

                _finishingRecording = true;
                options = _recordOptions;
            }

            RecordingResult result;

            try
            {
                var events = _recorder.Stop();

                if (cancelled)
                {
                    result = new RecordingResult { Cancelled = true, RawEventCount = events.Count };
                }
                else
                {
                    var actions = RecordingCompiler.Compile(events, options ?? new RecorderOptions(), out var warnings);
                    result = new RecordingResult { Actions = actions, Warnings = warnings, RawEventCount = events.Count };
                }
            }
            catch (Exception ex)
            {
                _log("Finishing the recording failed: " + ex.Message);
                result = new RecordingResult { Cancelled = true, Warnings = new[] { "The recording failed: " + ex.Message } };
            }

            lock (_gate)
            {
                _state = MacroRuntimeState.Idle;
                _finishingRecording = false;
                _recordOptions = null;
            }

            RaiseStateChanged(MacroRuntimeState.Idle);
            RaiseRecordingFinished(result);
            return result;
        }

        private void OnRecorderStopRequested(RecorderStopReason reason)
        {
            // Raised on the recorder's own thread: finishing joins that thread, so it has to happen elsewhere.
            Task.Run(() =>
            {
                try
                {
                    if (reason == RecorderStopReason.EmergencyStop)
                        EmergencyStop();
                    else
                        FinishRecording(cancelled: false);
                }
                catch (Exception ex)
                {
                    _log("Stopping the recording failed: " + ex.Message);
                }
            });
        }

        // ---- hotkeys ----

        /// <summary>
        /// Unregisters everything, registers the emergency stop first (always), then each enabled macro's shortcut. Call after
        /// macros or settings change. A failed emergency-stop registration also raises <see cref="Warning"/>.
        /// </summary>
        public HotkeyReloadResult ReloadHotkeys()
        {
            if (Volatile.Read(ref _shutdown) != 0)
                return new HotkeyReloadResult();

            var settings = LoadSettings();
            var emergency = EmergencyHotkey(settings);
            HotkeyRegistration emergencyRegistration;

            lock (_emergencyLock)
            {
                _hotkeys.UnregisterAll();
                _emergencyRegistered = false;
                emergencyRegistration = RegisterEmergencyStop(emergency);
            }

            if (!emergencyRegistration.IsOk)
                RaiseWarning($"The emergency stop shortcut ({emergency}) is unavailable: {emergencyRegistration.Message}. Use the Stop button instead.");

            var statuses = new List<MacroHotkeyStatus>();
            var registered = new List<Hotkey>();

            foreach (var macro in _store.LoadAll())
            {
                if (!macro.Enabled || string.IsNullOrWhiteSpace(macro.Hotkey))
                    continue;

                HotkeyRegistration registration;

                if (!Hotkey.TryParse(macro.Hotkey, out var hotkey))
                {
                    registration = new HotkeyRegistration(HotkeyStatus.Invalid, "This isn't a valid shortcut");
                }
                else if (!hotkey.IsAllowedShortcut)
                {
                    registration = new HotkeyRegistration(HotkeyStatus.Invalid, "This is a typing key; add Ctrl, Alt or Win");
                }
                else
                {
                    string id = macro.Id;
                    registration = _hotkeys.Register(MacroPrefix + id, hotkey, () => OnMacroHotkey(id));

                    if (registration.IsOk)
                        registered.Add(hotkey);
                }

                statuses.Add(new MacroHotkeyStatus(macro.Id, macro.Hotkey!, registration));
            }

            Volatile.Write(ref _macroHotkeys, registered.ToArray());

            return new HotkeyReloadResult
            {
                EmergencyStop = emergency.ToString(),
                EmergencyStopRegistration = emergencyRegistration,
                Macros = statuses
            };
        }

        /// <summary>
        /// Registers the emergency stop if it is not registered. Returns false if it still cannot be (another app owns the
        /// shortcut). Called when the runtime is created and before every macro start.
        /// </summary>
        private bool EnsureEmergencyStop()
        {
            lock (_emergencyLock)
            {
                // The flag is only a hint; the registrar is asked, so a registration lost for any reason is found and redone.
                if (_emergencyRegistered && _hotkeys.IsRegistered(EmergencyName))
                    return true;

                var registration = RegisterEmergencyStop(EmergencyHotkey(LoadSettings()));
                return registration.IsOk;
            }
        }

        // Caller holds _emergencyLock. RegisterHotKey wants exact modifiers, and a macro that holds Shift or Alt makes Windows
        // see those too, which would turn "Ctrl+Shift+End" into "Ctrl+Alt+Shift+End" and miss. So the same key is also
        // registered with every combination of the other modifiers added. Only the base registration decides the reported status.
        private HotkeyRegistration RegisterEmergencyStop(Hotkey emergency)
        {
            var registration = _hotkeys.Register(EmergencyName, emergency, OnEmergencyHotkey);
            _emergencyRegistered = registration.IsOk;

            if (!registration.IsOk)
                return registration;

            var all = new[] { HotkeyModifiers.Ctrl, HotkeyModifiers.Alt, HotkeyModifiers.Shift, HotkeyModifiers.Win };
            var extras = all.Where(m => (emergency.Modifiers & m) == 0).ToArray();

            for (int mask = 1; mask < (1 << extras.Length); mask++)
            {
                var added = HotkeyModifiers.None;

                for (int bit = 0; bit < extras.Length; bit++)
                {
                    if ((mask & (1 << bit)) != 0)
                        added |= extras[bit];
                }

                var variant = new Hotkey(emergency.Modifiers | added, emergency.VirtualKey);
                var result = _hotkeys.Register($"{EmergencyName}+{(int)added}", variant, OnEmergencyHotkey);

                if (!result.IsOk)
                    _log($"Emergency stop variant {variant} could not be registered: {result.Message}");
            }

            return registration;
        }

        private static Hotkey EmergencyHotkey(MacroStudioSettings settings) =>
            ParseOrDefault(settings.EmergencyStop, MacroStudioSettings.DefaultEmergencyStop);

        // On the registrar's thread: only the instant part (cancel, release) runs here; warnings and recording teardown are handed off.
        private void OnEmergencyHotkey()
        {
            StopNow(out bool wasRunning, out bool wasRecording);
            BackgroundDispatch(() => FinishEmergencyStop(wasRunning, wasRecording));
        }

        private void OnMacroHotkey(string macroId)
        {
            // Loading and starting a macro is slow compared to what the registrar's thread may spend (it also serves the emergency stop).
            BackgroundDispatch(() =>
            {
                try
                {
                    HandleMacroHotkey(macroId);
                }
                catch (Exception ex)
                {
                    _log("Macro shortcut failed: " + ex.Message);
                }
            });
        }

        private void HandleMacroHotkey(string macroId)
        {
            if (Volatile.Read(ref _shutdown) != 0)
                return;

            bool toggleOff;
            bool idle;

            lock (_gate)
            {
                toggleOff = _state == MacroRuntimeState.Running && _currentId == macroId;
                idle = _state == MacroRuntimeState.Idle;
            }

            if (toggleOff)
            {
                RequestStop();
                return;
            }

            if (!idle)
            {
                _log("Ignored a macro shortcut because a macro is playing or a recording is running");
                return;
            }

            Macro? macro = null;

            try { macro = _store.Load(macroId); } catch (Exception ex) { _log("Could not load macro: " + ex.Message); }

            if (macro is null)
            {
                RaiseWarning("That macro could not be loaded. It may have been deleted or damaged.");
                return;
            }

            if (!macro.Enabled)
                return;

            Start(macro);
        }

        /// <summary>
        /// Stops everything now: cancels playback, releases all held input, and discards any recording in progress. Runs from the
        /// emergency shortcut and may also be wired to a Stop-all button.
        /// </summary>
        public void EmergencyStop()
        {
            StopNow(out bool wasRunning, out bool wasRecording);
            FinishEmergencyStop(wasRunning, wasRecording);
        }

        // The instant part: nothing here waits for anything but the short send lock.
        private void StopNow(out bool wasRunning, out bool wasRecording)
        {
            lock (_gate)
            {
                wasRunning = _state == MacroRuntimeState.Running;
                wasRecording = _state == MacroRuntimeState.Recording;

                try { _cts?.Cancel(); } catch { }

                if (wasRunning)
                {
                    // Under the lock, so it is certain to hit the run that was cancelled and not one that starts afterwards.
                    try { _player.ReleaseHeld(); } catch { }
                }
            }
        }

        private void FinishEmergencyStop(bool wasRunning, bool wasRecording)
        {
            if (wasRecording)
                CancelRecording();

            ScheduleReleaseRetry();

            if (wasRunning || wasRecording)
                RaiseWarning(wasRecording ? "Emergency stop: recording discarded." : "Emergency stop: macro stopped and all keys released.");
        }

        // Stops playback and releases input without shutting anything down (used for a SessionEnding that may be cancelled).
        private void StopPlaybackNow()
        {
            Thread? thread;

            lock (_gate)
            {
                thread = _playThread;
                try { _cts?.Cancel(); } catch { }
            }

            ReleaseAll();

            try
            {
                if (thread is not null && thread != Thread.CurrentThread)
                    thread.Join(ShutdownPlaybackWaitMs);
            }
            catch { }

            ReleaseAll();
        }

        // ---- blocked releases ----

        // When Windows refuses a key-up (the foreground window runs elevated) the key would otherwise stay down. The player keeps
        // such releases pending; this retries them in the background for a while and tells the user once.
        private void ScheduleReleaseRetry()
        {
            try
            {
                if (_player.PendingReleaseCount == 0)
                    return;

                lock (_retryLock)
                {
                    if (_retryTimer is not null || Volatile.Read(ref _shutdown) != 0)
                        return;

                    _retryDeadlineTicks = Stopwatch.GetTimestamp() + (long)(RetryWindowMs / 1000.0 * Stopwatch.Frequency);
                    _retryTimer = new Timer(_ => RetryTick(), null, RetryIntervalMs, RetryIntervalMs);

                    if (!_retryWarned)
                    {
                        _retryWarned = true;
                        RaiseWarning("Some keys may still be held: the active window runs as administrator. Switch to another window to release them.");
                    }
                }
            }
            catch (Exception ex)
            {
                _log("Could not schedule the release retry: " + ex.Message);
            }
        }

        private void RetryTick()
        {
            int remaining;

            try
            {
                remaining = _player.RetryPendingReleases();
            }
            catch
            {
                remaining = 1;
            }

            if (remaining > 0 && Stopwatch.GetTimestamp() < Volatile.Read(ref _retryDeadlineTicks) && Volatile.Read(ref _shutdown) == 0)
                return;

            lock (_retryLock)
            {
                _retryTimer?.Dispose();
                _retryTimer = null;

                // Warn again next time only if this episode ended cleanly; after a give-up the user has already been told.
                if (remaining == 0)
                    _retryWarned = false;
            }
        }

        // ---- shutdown ----

        private void Shutdown()
        {
            if (Interlocked.Exchange(ref _shutdown, 1) != 0)
                return;

            Thread? thread = null;
            bool locked = false;

            try
            {
                // A bounded wait: this can run from a crash handler, and a stuck lock must not stop us releasing input.
                locked = Monitor.TryEnter(_gate, ShutdownLockWaitMs);

                try { _cts?.Cancel(); } catch { }
                thread = _playThread;
            }
            finally
            {
                if (locked)
                    Monitor.Exit(_gate);
            }

            // Release synchronously, give the player thread a moment to unwind, then release again for anything it pressed meanwhile.
            ReleaseAll();

            try
            {
                if (thread is not null && thread != Thread.CurrentThread)
                    thread.Join(ShutdownPlaybackWaitMs);
            }
            catch { }

            ReleaseAll();

            try { _player.RetryPendingReleases(); } catch { }
            try { lock (_retryLock) { _retryTimer?.Dispose(); _retryTimer = null; } } catch { }
            try { _recorder.Dispose(); } catch { }
            try { _hotkeys.Dispose(); } catch { }
        }

        /// <summary>Full teardown: <see cref="TryShutdown"/>'s work plus disposing the owned resources. The next <see cref="Instance"/> access creates a fresh runtime.</summary>
        public void Dispose()
        {
            Shutdown();

            // The owned delay is only disposed once the player thread is really gone.
            if (Stop())
            {
                foreach (var resource in _owned)
                {
                    try { resource.Dispose(); } catch { }
                }
            }

            lock (InstanceLock)
            {
                if (ReferenceEquals(_instance, this))
                    _instance = null;
            }
        }

        // ---- helpers ----

        private MacroStudioSettings LoadSettings()
        {
            try
            {
                return _settingsStore.Load();
            }
            catch (Exception ex)
            {
                _log("Could not load Macro Studio settings: " + ex.Message);
                return new MacroStudioSettings();
            }
        }

        private static Hotkey ParseOrDefault(string? text, string fallback)
        {
            if (Hotkey.TryParseShortcut(text, out var hotkey))
                return hotkey;

            Hotkey.TryParse(fallback, out hotkey);
            return hotkey;
        }

        private void OnPlayerPausedChanged(bool paused)
        {
            Volatile.Write(ref _paused, paused ? 1 : 0);

            try { PausedChanged?.Invoke(paused); } catch (Exception ex) { _log("PausedChanged handler failed: " + ex.Message); }
        }

        private void RaiseStateChanged(MacroRuntimeState state)
        {
            try { StateChanged?.Invoke(state); } catch (Exception ex) { _log("StateChanged handler failed: " + ex.Message); }
        }

        private void RaiseWarning(string message)
        {
            _log(message);

            try { Warning?.Invoke(message); } catch (Exception ex) { _log("Warning handler failed: " + ex.Message); }
        }

        private void RaisePlaybackFinished(string id, PlayResult result)
        {
            try { PlaybackFinished?.Invoke(id, result); } catch (Exception ex) { _log("PlaybackFinished handler failed: " + ex.Message); }
        }

        private void RaiseRecordingFinished(RecordingResult result)
        {
            try { RecordingFinished?.Invoke(result); } catch (Exception ex) { _log("RecordingFinished handler failed: " + ex.Message); }
        }
    }
}
