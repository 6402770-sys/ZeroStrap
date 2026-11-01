namespace ZeroStrap.Macros
{
    public enum MacroRuntimeState { Idle, Running, Recording }

    public enum MacroStartResult
    {
        Started,

        /// <summary>This macro is already playing.</summary>
        AlreadyRunning,

        /// <summary><see cref="MacroRuntime.MaxConcurrentMacros"/> macros are already playing; nothing was stopped to make room.</summary>
        TooManyRunning,

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
    /// Owns Macro Studio's moving parts (store, hotkeys, players, recorder) and enforces its rules: up to
    /// <see cref="MaxConcurrentMacros"/> macros play at once, each on its own thread with its own player, recording and playback
    /// never overlap, the emergency stop is always registered first and always wins, and nothing is left held down when a
    /// macro ends, the app closes or it crashes. The players share the real input through a <see cref="SharedInputArbiter"/>,
    /// so a key is only released once no playing macro still holds it. Macros with the same shortcut are toggled together.
    /// Events are raised on whichever thread caused them, so UI code must marshal to its dispatcher.
    /// </summary>
    public sealed class MacroRuntime : IDisposable
    {
        public const int StopWaitMs = 500;

        /// <summary>How many macros may play at the same time.</summary>
        public const int MaxConcurrentMacros = 8;

        private const string EmergencyName = "emergency-stop";
        private const string MacroPrefix = "macro:";
        private const int ShutdownPlaybackWaitMs = 300;
        private const int ShutdownLockWaitMs = 250;

        private static readonly object InstanceLock = new();
        private static MacroRuntime? _instance;

        /// <summary>One playing macro: its own player, delay, input client and thread.</summary>
        private sealed class PlaybackRun
        {
            public PlaybackRun(Macro macro, MacroPlayer player, SharedInputArbiter.Client client, IDelay delay, CancellationTokenSource cts)
            {
                Macro = macro;
                Player = player;
                Client = client;
                Delay = delay;
                Cts = cts;
            }

            public Macro Macro { get; }
            public MacroPlayer Player { get; }
            public SharedInputArbiter.Client Client { get; }
            public IDelay Delay { get; }
            public CancellationTokenSource Cts { get; }
            public Thread? Thread { get; set; }

            /// <summary>Guarded by the runtime's _gate.</summary>
            public bool Paused { get; set; }
        }

        private readonly object _gate = new();
        private readonly MacroStore _store;
        private readonly MacroStudioSettingsStore _settingsStore;
        private readonly IHotkeyRegistrar _hotkeys;
        private readonly IInputRecorder _recorder;
        private readonly SharedInputArbiter _arbiter;
        private readonly Func<IDelay> _delayFactory;
        private readonly IFocusGate? _focusGate;
        private readonly IDisposable[] _owned;
        private readonly Action<string> _log;

        // All guarded by _gate.
        private readonly Dictionary<string, PlaybackRun> _runs = new(StringComparer.Ordinal);
        private int _ownLivePlayers;
        private bool _recording;
        private bool _finishingRecording;
        private RecorderOptions? _recordOptions;
        private bool _anyPaused;

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

        /// <summary>The arbiter every player sends through (tests inspect ownership).</summary>
        internal SharedInputArbiter Arbiter => _arbiter;

        /// <summary>
        /// Raised after every state change and whenever a macro starts or stops (the state may stay Running), outside the
        /// internal lock. Read <see cref="RunningMacroIds"/> for the details.
        /// </summary>
        public event Action<MacroRuntimeState>? StateChanged;

        /// <summary>A user-facing notice: failed shortcut, blocked input, emergency stop, recorder restart, macro failure, too many macros.</summary>
        public event Action<string>? Warning;

        /// <summary>True when any playing macro paused because Roblox lost focus (RobloxOnly macros), false once none is paused.</summary>
        public event Action<bool>? PausedChanged;

        /// <summary>A playback ended (any outcome). The id is the macro's id.</summary>
        public event Action<string, PlayResult>? PlaybackFinished;

        /// <summary>A recording ended, via Stop key, StopRecording() or the emergency stop (then <see cref="RecordingResult.Cancelled"/>).</summary>
        public event Action<RecordingResult>? RecordingFinished;

        /// <summary>
        /// Test/DI constructor. Every macro run gets a new player over its own <see cref="SharedInputArbiter"/> client of
        /// <paramref name="sink"/> and a delay from <paramref name="delayFactory"/> (disposed when the run ends, if disposable),
        /// because a delay serialises concurrent waits. <paramref name="owned"/> are disposed with <see cref="Dispose"/>.
        /// </summary>
        internal MacroRuntime(
            MacroStore store,
            MacroStudioSettingsStore settingsStore,
            IHotkeyRegistrar hotkeys,
            IInputRecorder recorder,
            IInputSink sink,
            Func<IDelay> delayFactory,
            IFocusGate? focusGate,
            Action<string>? log = null,
            params IDisposable[] owned)
        {
            _store = store;
            _settingsStore = settingsStore;
            _hotkeys = hotkeys;
            _recorder = recorder;
            _log = log ?? (_ => { });
            _arbiter = new SharedInputArbiter(sink, _log);
            _delayFactory = delayFactory;
            _focusGate = focusGate;
            _owned = owned;

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
        /// Stops every macro and releases every held key and button right now, without shutting the runtime down (so a cancelled
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
            var sink = new SendInputSink(Log);
            var robloxGate = new RobloxFocusGate();
            var recorder = new InputRecorder(Log);
            var hotkeys = new HotkeyService(Log, robloxGate);

            // The Roblox gate is always passed: a player only consults it for macros whose focus rule is RobloxOnly.
            return new MacroRuntime(store, settings, hotkeys, recorder, sink, () => new PrecisionDelay(), robloxGate, Log);
        }

        // ---- state ----

        public MacroStore Store => _store;

        public MacroStudioSettingsStore SettingsStore => _settingsStore;

        public MacroRuntimeState State
        {
            get
            {
                lock (_gate)
                    return StateLocked();
            }
        }

        private MacroRuntimeState StateLocked() =>
            _recording ? MacroRuntimeState.Recording : _runs.Count > 0 ? MacroRuntimeState.Running : MacroRuntimeState.Idle;

        /// <summary>Ids of the macros that are playing, oldest first.</summary>
        public IReadOnlyList<string> RunningMacroIds
        {
            get
            {
                lock (_gate)
                    return _runs.Keys.ToList();
            }
        }

        public bool IsRunning(string macroId)
        {
            lock (_gate)
                return _runs.ContainsKey(macroId);
        }

        public int RunningCount
        {
            get
            {
                lock (_gate)
                    return _runs.Count;
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

        /// <summary>
        /// Plays <paramref name="macro"/> (e.g. an unsaved editor draft) on its own background thread, next to any other playing
        /// macro. Refused while the same macro plays, when <see cref="MaxConcurrentMacros"/> already play, or while recording.
        /// </summary>
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

                if (_recording)
                    return MacroStartResult.RecordingInProgress;

                if (_runs.ContainsKey(macro.Id))
                    return MacroStartResult.AlreadyRunning;

                // A player thread of an earlier runtime that never stopped could still be sending input.
                if (Volatile.Read(ref _livePlayers) > _ownLivePlayers)
                    return MacroStartResult.AlreadyRunning;

                if (_runs.Count >= MaxConcurrentMacros)
                    return MacroStartResult.TooManyRunning;

                IDelay delay;

                try
                {
                    delay = _delayFactory();
                }
                catch (Exception ex)
                {
                    _log("Could not create the playback timer: " + ex.Message);
                    return MacroStartResult.Failed;
                }

                var client = _arbiter.CreateClient();
                var player = new MacroPlayer(client, delay, _focusGate, _log);
                var cts = new CancellationTokenSource();
                var run = new PlaybackRun(macro, player, client, delay, cts);

                player.PausedChanged += paused => OnRunPausedChanged(run, paused);
                run.Thread = new Thread(() => RunPlayback(run, go)) { IsBackground = true, Name = "ZeroStrap macro player" };

                Interlocked.Increment(ref _livePlayers);
                _ownLivePlayers++;
                _runs[macro.Id] = run;

                try
                {
                    run.Thread.Start();
                }
                catch (Exception ex)
                {
                    Interlocked.Decrement(ref _livePlayers);
                    _ownLivePlayers--;
                    _runs.Remove(macro.Id);
                    cts.Dispose();
                    DisposeDelay(delay);
                    _log("Could not start the player thread: " + ex.Message);
                    return MacroStartResult.Failed;
                }
            }

            RaiseStateChanged(MacroRuntimeState.Running);
            go.Set();
            return MacroStartResult.Started;
        }

        private void RunPlayback(PlaybackRun run, ManualResetEventSlim go)
        {
            PlayResult result;

            try
            {
                go.Wait();
                result = run.Player.Play(run.Macro, run.Cts.Token);
            }
            catch (Exception ex)
            {
                result = PlayResult.Faulted(ex.Message);
            }

            // Belt and braces: the player releases its own held input, and the client then gives up anything it still owns
            // (a release that failed inside the player), so this macro can never keep a key down once it has ended.
            try { run.Player.ReleaseHeld(); } catch { }
            try { run.Client.ReleaseAllOwned(); } catch { }

            MacroRuntimeState state;
            bool pausedChanged;

            lock (_gate)
            {
                if (_runs.TryGetValue(run.Macro.Id, out var current) && ReferenceEquals(current, run))
                    _runs.Remove(run.Macro.Id);

                _ownLivePlayers--;
                Interlocked.Decrement(ref _livePlayers);
                run.Paused = false;
                pausedChanged = UpdatePausedLocked();
                state = StateLocked();
            }

            // Disposed outside the lock; a Cancel on it happens under the lock while the run is listed, so it is never used after this.
            try { run.Cts.Dispose(); } catch { }
            DisposeDelay(run.Delay);

            if (pausedChanged)
                RaisePausedChanged(false);

            RaiseStateChanged(state);
            RaisePlaybackFinished(run.Macro.Id, result);

            if (result.Outcome == PlayOutcome.Faulted)
                RaiseWarning($"\"{run.Macro.Name}\" stopped because of an error: {result.Error}");

            ScheduleReleaseRetry();
        }

        /// <summary>Cancels every playing macro without waiting. Returns false if nothing was playing.</summary>
        public bool RequestStop()
        {
            if (!CancelAndRelease(null))
                return false;

            ScheduleReleaseRetry();
            return true;
        }

        /// <summary>Cancels one playing macro without waiting; the others keep playing. Returns false if it was not playing.</summary>
        public bool RequestStop(string macroId)
        {
            if (!CancelAndRelease(macroId))
                return false;

            ScheduleReleaseRetry();
            return true;
        }

        /// <summary>
        /// Stop all: cancels every playing macro and waits up to <see cref="StopWaitMs"/> ms for them to unwind. Returns true
        /// when no macro is playing afterwards. A run only ends once its player thread has really ended, so a stuck thread
        /// can never overlap with the same macro started again.
        /// </summary>
        public bool Stop() => StopAndWait(null);

        /// <summary>Like <see cref="Stop()"/> for one macro; the others keep playing. True when that macro is not playing afterwards.</summary>
        public bool Stop(string macroId) => StopAndWait(macroId);

        private bool StopAndWait(string? macroId)
        {
            Thread?[] threads;

            lock (_gate)
            {
                threads = Select(macroId).Select(r => r.Thread).ToArray();

                if (threads.Length == 0)
                    return true;
            }

            CancelAndRelease(macroId);

            var deadline = Stopwatch.StartNew();

            foreach (var thread in threads)
            {
                if (thread is null || thread == Thread.CurrentThread)
                    continue;

                int left = StopWaitMs - (int)deadline.ElapsedMilliseconds;
                thread.Join(Math.Max(0, left));
            }

            bool done;

            lock (_gate)
            {
                done = !Select(macroId).Any();

                // With nothing playing any more, nobody may own input: whatever is left is released now. Under the lock, so a
                // macro started meanwhile cannot have its fresh presses swept away.
                if (_runs.Count == 0)
                    ReleaseArbiter();
            }

            ScheduleReleaseRetry();
            return done;
        }

        // Caller holds _gate.
        private IEnumerable<PlaybackRun> Select(string? macroId)
        {
            if (macroId is null)
                return _runs.Values.ToList();

            return _runs.TryGetValue(macroId, out var run) ? new[] { run } : Array.Empty<PlaybackRun>();
        }

        // Cancels and releases while holding the lock: the run list cannot change in between, so this can never cancel (or
        // release the input of) a run that started after the caller decided. Null means every run.
        private bool CancelAndRelease(string? macroId)
        {
            lock (_gate)
            {
                var runs = Select(macroId).ToList();

                foreach (var run in runs)
                {
                    try { run.Cts.Cancel(); } catch { }
                    try { run.Player.ReleaseHeld(); } catch { }
                }

                return runs.Count > 0;
            }
        }

        /// <summary>
        /// Releases every key and button any macro holds (which also stops every playing macro: a player never presses again
        /// after its input was released). Thread-safe and idempotent. Returns how many releases failed.
        /// </summary>
        public int ReleaseAll()
        {
            int failures = 0;

            // Under the lock (the send locks inside are bounded), so the sweep cannot hit a macro that starts in between.
            lock (_gate)
            {
                foreach (var run in _runs.Values)
                {
                    try { failures += run.Player.ReleaseHeld(); } catch { failures++; }
                }

                failures = Math.Max(failures, ReleaseArbiter());
            }

            ScheduleReleaseRetry();
            return failures;
        }

        private int ReleaseArbiter()
        {
            try
            {
                return _arbiter.ReleaseAll();
            }
            catch
            {
                return 1;
            }
        }

        private static void DisposeDelay(IDelay delay)
        {
            if (delay is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }

        // ---- recording ----

        /// <summary>
        /// Starts capturing input. Refused while a macro plays. The stop key and emergency stop (from the Macro Studio settings)
        /// end it and are not recorded, nor are the global activation shortcuts of enabled macros (game shortcuts are ordinary
        /// keys while recording, so they are recorded).
        /// </summary>
        public RecordingStartResult StartRecording(bool recordAbsoluteMoves = false)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _shutdown) != 0)
                    return RecordingStartResult.ShuttingDown;

                if (_runs.Count > 0)
                    return RecordingStartResult.MacroRunning;

                if (_recording)
                    return RecordingStartResult.AlreadyRecording;

                _recording = true;
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
                    _recording = false;
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
                if (!_recording || _finishingRecording)
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
                _recording = false;
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
        /// Unregisters everything, registers the emergency stop first (always), then one registration per distinct shortcut of
        /// the enabled macros: macros sharing a shortcut are toggled together. Call after macros or settings change. A failed
        /// emergency-stop registration also raises <see cref="Warning"/>.
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
            var groups = new Dictionary<Hotkey, List<Macro>>();
            var order = new List<Hotkey>();

            foreach (var macro in _store.LoadAll())
            {
                if (!macro.Enabled || string.IsNullOrWhiteSpace(macro.Hotkey))
                    continue;

                HotkeyRegistration? refused = null;

                if (!Hotkey.TryParse(macro.Hotkey, out var hotkey))
                    refused = new HotkeyRegistration(HotkeyStatus.Invalid, "This isn't a valid shortcut");
                else if (!hotkey.IsMacroShortcut)
                    refused = new HotkeyRegistration(HotkeyStatus.Invalid, "Escape, Enter and / are reserved for the Roblox chat");
                else if (CollidesWithEmergencyStop(hotkey, emergency))
                    refused = new HotkeyRegistration(HotkeyStatus.Invalid, "This is the emergency stop shortcut");

                if (refused is not null)
                {
                    statuses.Add(new MacroHotkeyStatus(macro.Id, macro.Hotkey!, refused.Value));
                    continue;
                }

                if (!groups.TryGetValue(hotkey, out var members))
                {
                    members = new List<Macro>();
                    groups[hotkey] = members;
                    order.Add(hotkey);
                }

                members.Add(macro);
            }

            var registered = new List<Hotkey>();

            foreach (var hotkey in order)
            {
                var members = groups[hotkey];
                string[] ids = members.Select(m => m.Id).ToArray();

                // A lone macro keeps the name it always had; a shared shortcut is named after all of its macros.
                var registration = _hotkeys.Register(MacroPrefix + string.Join(",", ids), hotkey, () => OnMacroHotkey(ids));

                // Only global shortcuts are captured by Windows; game shortcuts reach the recorder like any other key.
                if (registration.IsOk && hotkey.IsAllowedShortcut)
                    registered.Add(hotkey);

                foreach (var macro in members)
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
        /// True when <paramref name="hotkey"/> would be taken by the emergency stop: the same key with at least its modifiers
        /// (the emergency stop is also registered with every extra modifier added, see <see cref="RegisterEmergencyStop"/>).
        /// </summary>
        public static bool CollidesWithEmergencyStop(Hotkey hotkey, Hotkey emergency) =>
            hotkey.VirtualKey == emergency.VirtualKey && (hotkey.Modifiers & emergency.Modifiers) == emergency.Modifiers;

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

        private void OnMacroHotkey(string[] macroIds)
        {
            // Loading and starting macros is slow compared to what the registrar's thread may spend (it also serves the emergency stop).
            BackgroundDispatch(() =>
            {
                try
                {
                    HandleMacroHotkey(macroIds);
                }
                catch (Exception ex)
                {
                    _log("Macro shortcut failed: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// A shortcut toggles its macros as a group: if any of them is playing, the playing ones stop; otherwise every enabled
        /// one starts. Other macros are never touched. When the concurrency limit is reached, the macros that fit start and
        /// the user is told about the rest (nothing already playing is stopped to make room).
        /// </summary>
        private void HandleMacroHotkey(string[] macroIds)
        {
            if (Volatile.Read(ref _shutdown) != 0)
                return;

            List<string> playing;
            bool recording;

            lock (_gate)
            {
                recording = _recording;
                playing = macroIds.Where(_runs.ContainsKey).ToList();
            }

            if (playing.Count > 0)
            {
                foreach (string id in playing)
                    RequestStop(id);

                return;
            }

            if (recording)
            {
                _log("Ignored a macro shortcut because a recording is running");
                return;
            }

            var skipped = new List<string>();

            foreach (string id in macroIds)
            {
                Macro? macro = null;

                try { macro = _store.Load(id); } catch (Exception ex) { _log("Could not load macro: " + ex.Message); }

                if (macro is null)
                {
                    RaiseWarning("That macro could not be loaded. It may have been deleted or damaged.");
                    continue;
                }

                if (!macro.Enabled)
                    continue;

                if (Start(macro) == MacroStartResult.TooManyRunning)
                    skipped.Add(macro.Name);
            }

            if (skipped.Count > 0)
                RaiseWarning(TooManyText(skipped));
        }

        /// <summary>The notice for macros that could not start because <see cref="MaxConcurrentMacros"/> already play.</summary>
        public static string TooManyText(IReadOnlyList<string> names)
        {
            string list = string.Join(", ", names.Select(n => "\"" + n + "\""));
            return $"{MaxConcurrentMacros} macros are already running, so {list} didn't start. Stop one first.";
        }

        /// <summary>
        /// Stops everything now: cancels every playing macro, releases all held input, and discards any recording in progress.
        /// Runs from the emergency shortcut and may also be wired to a Stop-all button.
        /// </summary>
        public void EmergencyStop()
        {
            StopNow(out bool wasRunning, out bool wasRecording);
            FinishEmergencyStop(wasRunning, wasRecording);
        }

        // The instant part: nothing here waits for anything but the short, bounded send locks.
        private void StopNow(out bool wasRunning, out bool wasRecording)
        {
            lock (_gate)
            {
                wasRunning = _runs.Count > 0;
                wasRecording = _recording;

                // Under the lock, so it is certain to hit the runs that were cancelled and not one that starts afterwards.
                foreach (var run in _runs.Values)
                {
                    try { run.Cts.Cancel(); } catch { }
                    try { run.Player.ReleaseHeld(); } catch { }
                }

                // Every player has been told to stop, so nothing may stay held: anything they could not release goes now.
                if (wasRunning)
                    ReleaseArbiter();
            }
        }

        private void FinishEmergencyStop(bool wasRunning, bool wasRecording)
        {
            if (wasRecording)
                CancelRecording();

            ScheduleReleaseRetry();

            if (wasRunning || wasRecording)
                RaiseWarning(wasRecording ? "Emergency stop: recording discarded." : "Emergency stop: every macro stopped and all keys released.");
        }

        // Stops playback and releases input without shutting anything down (used for a SessionEnding that may be cancelled).
        private void StopPlaybackNow()
        {
            Thread?[] threads;

            lock (_gate)
            {
                threads = _runs.Values.Select(r => r.Thread).ToArray();

                foreach (var run in _runs.Values)
                {
                    try { run.Cts.Cancel(); } catch { }
                }
            }

            ReleaseAll();
            JoinAll(threads, ShutdownPlaybackWaitMs);
            ReleaseAll();
        }

        private static void JoinAll(Thread?[] threads, int budgetMs)
        {
            var clock = Stopwatch.StartNew();

            foreach (var thread in threads)
            {
                try
                {
                    if (thread is not null && thread != Thread.CurrentThread)
                        thread.Join(Math.Max(0, budgetMs - (int)clock.ElapsedMilliseconds));
                }
                catch { }
            }
        }

        // ---- blocked releases ----

        // When Windows refuses a key-up (the foreground window runs elevated) the key would otherwise stay down. The arbiter keeps
        // such releases pending; this retries them in the background for a while and tells the user once.
        private void ScheduleReleaseRetry()
        {
            try
            {
                if (_arbiter.PendingCount == 0)
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
                remaining = _arbiter.RetryPending();
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

            Thread?[] threads = Array.Empty<Thread?>();
            bool locked = false;

            try
            {
                // A bounded wait: this can run from a crash handler, and a stuck lock must not stop us releasing input.
                locked = Monitor.TryEnter(_gate, ShutdownLockWaitMs);

                if (locked)
                {
                    threads = _runs.Values.Select(r => r.Thread).ToArray();

                    foreach (var run in _runs.Values)
                    {
                        try { run.Cts.Cancel(); } catch { }
                    }
                }
            }
            finally
            {
                if (locked)
                    Monitor.Exit(_gate);
            }

            // Release synchronously, give the player threads a moment to unwind, then release again for anything pressed meanwhile.
            ReleaseAllForShutdown(locked);
            JoinAll(threads, ShutdownPlaybackWaitMs);
            ReleaseAllForShutdown(locked);

            try { _arbiter.RetryPending(); } catch { }
            try { lock (_retryLock) { _retryTimer?.Dispose(); _retryTimer = null; } } catch { }
            try { _recorder.Dispose(); } catch { }
            try { _hotkeys.Dispose(); } catch { }
        }

        // When the gate could not be had (a crash mid-update) the run list is not safe to read, but the arbiter still knows
        // everything that is held, so it is released directly.
        private void ReleaseAllForShutdown(bool gateUsable)
        {
            if (gateUsable)
                ReleaseAll();
            else
                ReleaseArbiter();
        }

        /// <summary>Full teardown: <see cref="TryShutdown"/>'s work plus disposing the owned resources. The next <see cref="Instance"/> access creates a fresh runtime.</summary>
        public void Dispose()
        {
            Shutdown();

            // Owned resources are only disposed once every player thread is really gone.
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

        private void OnRunPausedChanged(PlaybackRun run, bool paused)
        {
            bool changed;
            bool now;

            lock (_gate)
            {
                // A run that already ended was taken off the list; its late "resumed" must not count.
                if (!_runs.TryGetValue(run.Macro.Id, out var current) || !ReferenceEquals(current, run))
                    return;

                run.Paused = paused;
                changed = UpdatePausedLocked();
                now = _anyPaused;
            }

            if (changed)
                RaisePausedChanged(now);
        }

        // Caller holds _gate. Returns true when the "any run paused" answer changed.
        private bool UpdatePausedLocked()
        {
            bool any = _runs.Values.Any(r => r.Paused);

            if (any == _anyPaused)
                return false;

            _anyPaused = any;
            Volatile.Write(ref _paused, any ? 1 : 0);
            return true;
        }

        private void RaisePausedChanged(bool paused)
        {
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
