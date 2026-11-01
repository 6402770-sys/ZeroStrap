using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

using Xunit;

using ZeroStrap.Macros;

using static ZeroStrap.Tests.MacroBuilders;

namespace ZeroStrap.Tests
{
    /// <summary>Wall-clock delay for runtime tests (the runtime plays on a real thread): waits on the token's handle.</summary>
    internal sealed class RealtimeDelay : IDelay
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public TimeSpan Now => _clock.Elapsed;

        public void Wait(TimeSpan duration, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (duration > TimeSpan.Zero && ct.WaitHandle.WaitOne(duration))
                ct.ThrowIfCancellationRequested();
        }
    }

    internal sealed class FakeRegistrar : IHotkeyRegistrar
    {
        private readonly Dictionary<string, (Hotkey Key, Action Callback)> _entries = new();

        public List<string> Order { get; } = new();
        public Func<Hotkey, HotkeyRegistration>? Refuse { get; set; }
        public bool Disposed { get; private set; }

        public IReadOnlyCollection<string> Names => _entries.Keys;

        public IEnumerable<KeyValuePair<string, Hotkey>> Keys => _entries.Select(e => new KeyValuePair<string, Hotkey>(e.Key, e.Value.Key));

        public HotkeyRegistration Register(string name, Hotkey hotkey, Action onPressed)
        {
            var refused = Refuse?.Invoke(hotkey);

            if (refused is { IsOk: false })
                return refused.Value;

            if (_entries.Any(e => e.Key != name && e.Value.Key == hotkey))
                return new HotkeyRegistration(HotkeyStatus.Duplicate, "duplicate");

            Order.Add(name);
            _entries[name] = (hotkey, onPressed);
            return HotkeyRegistration.Ok;
        }

        public void Unregister(string name) => _entries.Remove(name);

        public bool IsRegistered(string name) => _entries.ContainsKey(name);

        public void UnregisterAll()
        {
            _entries.Clear();
            Order.Clear();
        }

        public void Press(string name) => _entries[name].Callback();

        public void Dispose() => Disposed = true;
    }

    internal sealed class FakeRecorder : IInputRecorder
    {
        public bool IsRecording { get; private set; }
        public RecorderOptions? Options { get; private set; }
        public int StopCalls { get; private set; }
        public bool Disposed { get; private set; }
        public string? FailWith { get; set; }
        public List<RawRecordedEvent> Events { get; } = new();

        public event Action<RecorderStopReason>? StopRequested;
        public event Action<string>? Warning;

        public bool Start(RecorderOptions options, out string? error)
        {
            error = FailWith;

            if (FailWith is not null)
                return false;

            Options = options;
            IsRecording = true;
            return true;
        }

        public IReadOnlyList<RawRecordedEvent> Stop()
        {
            StopCalls++;
            IsRecording = false;
            return Events.ToList();
        }

        public void Dispose()
        {
            Disposed = true;
            IsRecording = false;
        }

        public void RaiseStop(RecorderStopReason reason) => StopRequested?.Invoke(reason);

        public void RaiseWarning(string message) => Warning?.Invoke(message);
    }

    public sealed class MacroRuntimeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "zs-macro-runtime-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _logs = new();
        private readonly List<string> _warnings = new();
        private readonly List<MacroRuntimeState> _states = new();
        private readonly object _eventLock = new();
        private readonly MacroStore _store;
        private readonly MacroStudioSettingsStore _settings;
        private readonly RecordingSink _sink = new();
        private readonly FakeRegistrar _registrar = new();
        private readonly FakeRecorder _recorder = new();
        private readonly MacroRuntime _runtime;

        public MacroRuntimeTests()
        {
            _store = new MacroStore(Path.Combine(_root, "Macros"), _logs.Add);
            _settings = new MacroStudioSettingsStore(Path.Combine(_root, "Macros", "settings.json"), _logs.Add);

            var player = new MacroPlayer(_sink, new RealtimeDelay(), new FakeGate(), _logs.Add);
            _runtime = new MacroRuntime(_store, _settings, _registrar, _recorder, player, _logs.Add);

            // Shortcut callbacks normally hop to the thread pool; tests run them inline so they stay deterministic.
            _runtime.BackgroundDispatch = work => work();

            _runtime.Warning += message => { lock (_eventLock) _warnings.Add(message); };
            _runtime.StateChanged += state => { lock (_eventLock) _states.Add(state); };
        }

        public void Dispose()
        {
            _runtime.Dispose();

            try { Directory.Delete(_root, true); } catch { }
        }

        private Macro Save(string id, string? hotkey = null, bool enabled = true, params MacroAction[] actions)
        {
            var macro = Of(actions.Length > 0 ? actions : new[] { TapKey(0x1E) });
            macro.Id = id;
            macro.Name = id;
            macro.Enabled = enabled;
            macro.Hotkey = hotkey;
            return _store.Save(macro);
        }

        /// <summary>A macro that holds W and then waits a long time, so it is still running when the test acts.</summary>
        private Macro SaveLongRunning(string id, string? hotkey = null, bool enabled = true)
        {
            var macro = Save(id, hotkey, enabled, KeyDown(0x11), Wait(30000));
            return macro;
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs = 3000)
        {
            var clock = Stopwatch.StartNew();

            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                    return true;

                Thread.Sleep(5);
            }

            return condition();
        }

        private bool SinkHas(string call)
        {
            lock (_sink)
                return _sink.Calls.Contains(call);
        }

        // ---- playback ----

        [Fact]
        public void StartPlaysTheMacroThenReturnsToIdle()
        {
            Save("quick", actions: TapKey(0x1E));

            Assert.Equal(MacroStartResult.Started, _runtime.Start("quick"));
            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));

            Assert.Equal(new[] { "kd:1E", "ku:1E" }, _sink.Calls);
            Assert.Contains(MacroRuntimeState.Running, _states);
            Assert.Equal(MacroRuntimeState.Idle, _states[^1]);
            Assert.True(_states.IndexOf(MacroRuntimeState.Running) < _states.LastIndexOf(MacroRuntimeState.Idle));
        }

        [Fact]
        public void PlaybackFinishedReportsTheOutcome()
        {
            PlayResult? seen = null;
            string? seenId = null;
            _runtime.PlaybackFinished += (id, result) => { seenId = id; seen = result; };
            Save("quick", actions: TapKey(0x1E));

            _runtime.Start("quick");
            Assert.True(WaitUntil(() => seen is not null));

            Assert.Equal("quick", seenId);
            Assert.Equal(PlayOutcome.Completed, seen!.Value.Outcome);
        }

        [Fact]
        public void UnknownMacroIsNotFound()
        {
            Assert.Equal(MacroStartResult.NotFound, _runtime.Start("nope"));
            Assert.Equal(MacroStartResult.NotFound, _runtime.Start("../evil"));
            Assert.Equal(MacroRuntimeState.Idle, _runtime.State);
        }

        [Fact]
        public void NeverTwoMacrosAtOnce()
        {
            SaveLongRunning("one");
            Save("two");

            Assert.Equal(MacroStartResult.Started, _runtime.Start("one"));
            Assert.Equal("one", _runtime.CurrentMacroId);
            Assert.Equal(MacroStartResult.AlreadyRunning, _runtime.Start("two"));
            Assert.Equal(MacroStartResult.AlreadyRunning, _runtime.Start("one"));
            Assert.Equal("one", _runtime.CurrentMacroId);

            Assert.True(_runtime.Stop());
        }

        [Fact]
        public void StopCancelsReleasesHeldInputAndWaitsOnlyBriefly()
        {
            SaveLongRunning("held");
            _runtime.Start("held");
            Assert.True(WaitUntil(() => SinkHas("kd:11")));

            var clock = Stopwatch.StartNew();
            bool stopped = _runtime.Stop();
            clock.Stop();

            Assert.True(stopped);
            Assert.True(clock.ElapsedMilliseconds <= MacroRuntime.StopWaitMs, $"Stop took {clock.ElapsedMilliseconds} ms");
            Assert.Equal(MacroRuntimeState.Idle, _runtime.State);
            Assert.Empty(_sink.Down);
            Assert.True(SinkHas("ku:11"));
        }

        [Fact]
        public void StopWhenIdleIsHarmless()
        {
            Assert.True(_runtime.Stop());
            Assert.False(_runtime.RequestStop());
            Assert.Equal(0, _runtime.ReleaseAll());
        }

        [Fact]
        public void ReleaseAllWorksFromAnotherThreadAndRepeatsAreHarmless()
        {
            SaveLongRunning("held");
            _runtime.Start("held");
            Assert.True(WaitUntil(() => SinkHas("kd:11")));

            // (The recording sink is not thread-safe, so the calls are made one after another, each from a pool thread.)
            Assert.Equal(0, System.Threading.Tasks.Task.Run(() => _runtime.ReleaseAll()).Result);
            Assert.True(SinkHas("ku:11"));
            Assert.Empty(_sink.Down);

            int callsAfterFirst = _sink.Calls.Count;
            Assert.Equal(0, System.Threading.Tasks.Task.Run(() => _runtime.ReleaseAll()).Result);
            Assert.Equal(callsAfterFirst, _sink.Calls.Count); // nothing left to release

            _runtime.Stop();
        }

        [Fact]
        public void StartCanRunAnUnsavedDraft()
        {
            var draft = Of(TapKey(0x30));
            draft.Id = "draft";

            Assert.Equal(MacroStartResult.Started, _runtime.Start(draft));
            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle && _sink.Calls.Count == 2));
        }

        [Fact]
        public void AFailingMacroRaisesAWarning()
        {
            var bad = Of(TapKey(0x1E));
            bad.Id = "bad";
            bad.Name = "Bad one";
            bad.Actions[0].ScanCode = 0; // invalid: the player refuses it

            _runtime.Start(bad);
            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));
            Assert.True(WaitUntil(() => { lock (_eventLock) return _warnings.Any(w => w.Contains("Bad one")); }));
        }

        // ---- recording / playback exclusion ----

        [Fact]
        public void RecordingIsRefusedWhilePlayingAndPlaybackWhileRecording()
        {
            SaveLongRunning("held");
            Save("other");

            _runtime.Start("held");
            Assert.Equal(RecordingStartResult.MacroRunning, _runtime.StartRecording());
            Assert.False(_recorder.IsRecording);
            _runtime.Stop();

            Assert.Equal(RecordingStartResult.Started, _runtime.StartRecording());
            Assert.Equal(MacroRuntimeState.Recording, _runtime.State);
            Assert.Equal(MacroStartResult.RecordingInProgress, _runtime.Start("other"));
            Assert.Equal(RecordingStartResult.AlreadyRecording, _runtime.StartRecording());
            Assert.Empty(_sink.Calls.Where(c => c.StartsWith("kd:1E")));

            _runtime.CancelRecording();
        }

        [Fact]
        public void StopRecordingCompilesTheCapturedEvents()
        {
            _recorder.Events.Add(new RawRecordedEvent(RawEventKind.KeyDown, 100, 0x11));
            _recorder.Events.Add(new RawRecordedEvent(RawEventKind.KeyUp, 160, 0x11));
            RecordingResult? finished = null;
            _runtime.RecordingFinished += r => finished = r;

            _runtime.StartRecording();
            var result = _runtime.StopRecording();

            Assert.NotNull(result);
            Assert.False(result!.Cancelled);
            Assert.Equal(2, result.RawEventCount);
            var tap = Assert.Single(result.Actions);
            Assert.Equal(MacroKeyMode.Tap, tap.KeyMode);
            Assert.Same(result, finished);
            Assert.Equal(MacroRuntimeState.Idle, _runtime.State);
            Assert.Null(_runtime.StopRecording()); // nothing left to stop
        }

        [Fact]
        public void TheStopKeyEndsTheRecordingAndDeliversTheResult()
        {
            _recorder.Events.Add(new RawRecordedEvent(RawEventKind.KeyDown, 0, 0x1E));
            _recorder.Events.Add(new RawRecordedEvent(RawEventKind.KeyUp, 50, 0x1E));
            RecordingResult? finished = null;
            _runtime.RecordingFinished += r => finished = r;

            _runtime.StartRecording();
            _recorder.RaiseStop(RecorderStopReason.StopKey);

            Assert.True(WaitUntil(() => finished is not null));
            Assert.False(finished!.Cancelled);
            Assert.Single(finished.Actions);
            Assert.Equal(MacroRuntimeState.Idle, _runtime.State);
        }

        [Fact]
        public void TheRecorderIsGivenTheConfiguredKeysAndMacroHotkeysToIgnore()
        {
            _settings.Save(new MacroStudioSettings { RecordStopKey = "F10", EmergencyStop = "Ctrl+Alt+End" });
            Save("m", hotkey: "Ctrl+F7");
            _runtime.ReloadHotkeys();

            _runtime.StartRecording(recordAbsoluteMoves: true);

            var options = _recorder.Options!;
            Assert.Equal("F10", options.StopKey.ToString());
            Assert.Equal("Ctrl+Alt+End", options.EmergencyStop!.Value.ToString());
            Assert.Equal(new[] { "Ctrl+F7" }, options.IgnoredHotkeys.Select(h => h.ToString()).ToArray());
            Assert.True(options.RecordAbsoluteMoves);

            _runtime.CancelRecording();
        }

        [Fact]
        public void RecorderStartFailureRevertsToIdleWithAWarning()
        {
            _recorder.FailWith = "hooks refused";

            Assert.Equal(RecordingStartResult.Failed, _runtime.StartRecording());
            Assert.Equal(MacroRuntimeState.Idle, _runtime.State);
            Assert.Contains(_warnings, w => w.Contains("hooks refused"));

            // The runtime is not wedged: playback still works.
            Save("quick");
            Assert.Equal(MacroStartResult.Started, _runtime.Start("quick"));
            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));
        }

        [Fact]
        public void RecorderWarningsReachTheUi()
        {
            _recorder.RaiseWarning("Input capture restarted");

            Assert.Contains("Input capture restarted", _warnings);
        }

        // ---- hotkeys ----

        [Fact]
        public void ReloadRegistersEmergencyStopFirstThenEnabledMacros()
        {
            Save("a", hotkey: "F6");
            Save("b", hotkey: "Ctrl+F7");
            Save("off", hotkey: "F8", enabled: false);
            Save("none");

            var result = _runtime.ReloadHotkeys();

            Assert.Equal("emergency-stop", _registrar.Order[0]);
            Assert.Equal(new[] { "macro:a", "macro:b" }, _registrar.Order.Where(n => n.StartsWith("macro:")).OrderBy(x => x).ToArray());
            Assert.Equal("Ctrl+Shift+End", result.EmergencyStop);
            Assert.True(result.EmergencyStopRegistration.IsOk);
            Assert.Equal(2, result.Macros.Count);
            Assert.All(result.Macros, m => Assert.True(m.Registration.IsOk));
        }

        [Fact]
        public void ReloadReportsPerMacroFailures()
        {
            Save("a", hotkey: "F6");
            Save("b", hotkey: "F7");
            Save("c", hotkey: "F6"); // same combo as a
            _registrar.Refuse = hk => hk.ToString() == "F7" ? new HotkeyRegistration(HotkeyStatus.InUse, "This shortcut is used by another app") : HotkeyRegistration.Ok;

            var result = _runtime.ReloadHotkeys();

            Assert.Equal(HotkeyStatus.InUse, result.Macros.Single(m => m.MacroId == "b").Registration.Status);
            Assert.Equal(1, result.Macros.Count(m => m.Registration.Status == HotkeyStatus.Duplicate));
            Assert.Equal(1, result.Macros.Count(m => m.Registration.IsOk));
        }

        [Fact]
        public void ReloadReplacesOldRegistrations()
        {
            Save("a", hotkey: "F6");
            _runtime.ReloadHotkeys();
            _store.Delete("a");
            Save("b", hotkey: "F7");
            _runtime.ReloadHotkeys();

            Assert.Equal(new[] { "macro:b" }, _registrar.Names.Where(n => n.StartsWith("macro:")).ToArray());
            Assert.Contains("emergency-stop", _registrar.Names);
        }

        [Fact]
        public void EmergencyStopFailureWarnsTheUserButStillRegistersMacros()
        {
            Save("a", hotkey: "F6");
            _registrar.Refuse = hk => hk.ToString() == "Ctrl+Shift+End" ? new HotkeyRegistration(HotkeyStatus.InUse, "This shortcut is used by another app") : HotkeyRegistration.Ok;

            var result = _runtime.ReloadHotkeys();

            Assert.Equal(HotkeyStatus.InUse, result.EmergencyStopRegistration.Status);
            Assert.Contains(_warnings, w => w.Contains("emergency stop") && w.Contains("Stop button"));
            Assert.Contains("macro:a", _registrar.Names);
        }

        [Fact]
        public void AMacroHotkeyUsedAsEmergencyStopIsADuplicate()
        {
            Save("a", hotkey: "Ctrl+Shift+End");

            var result = _runtime.ReloadHotkeys();

            Assert.Equal(HotkeyStatus.Duplicate, result.Macros.Single().Registration.Status);
            Assert.Contains("emergency-stop", _registrar.Names);
        }

        [Fact]
        public void BadEmergencyStopSettingFallsBackToTheDefault()
        {
            _settings.Save(new MacroStudioSettings { EmergencyStop = "nonsense+" });

            Assert.Equal("Ctrl+Shift+End", _runtime.ReloadHotkeys().EmergencyStop);
        }

        [Fact]
        public void PressingAMacroHotkeyStartsItAndPressingAgainStopsIt()
        {
            SaveLongRunning("a", hotkey: "F6");
            _runtime.ReloadHotkeys();

            _registrar.Press("macro:a");
            Assert.Equal("a", _runtime.CurrentMacroId);
            Assert.True(WaitUntil(() => SinkHas("kd:11")));

            _registrar.Press("macro:a");
            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void AnotherMacrosHotkeyIsIgnoredWhileOneIsRunning()
        {
            SaveLongRunning("a", hotkey: "F6");
            Save("b", hotkey: "F7");
            _runtime.ReloadHotkeys();

            _registrar.Press("macro:a");
            _registrar.Press("macro:b");

            Assert.Equal("a", _runtime.CurrentMacroId);
            Assert.False(SinkHas("kd:1E"));

            _runtime.Stop();
        }

        [Fact]
        public void HotkeysDoNothingWhileRecording()
        {
            Save("a", hotkey: "F6");
            _runtime.ReloadHotkeys();
            _runtime.StartRecording();

            _registrar.Press("macro:a");

            Assert.Equal(MacroRuntimeState.Recording, _runtime.State);
            Assert.Empty(_sink.Calls);

            _runtime.CancelRecording();
        }

        [Fact]
        public void ADisabledMacroDoesNotStartFromAStaleHotkey()
        {
            Save("a", hotkey: "F6");
            _runtime.ReloadHotkeys();
            Save("a", hotkey: "F6", enabled: false); // edited after the shortcuts were registered

            _registrar.Press("macro:a");

            Assert.Equal(MacroRuntimeState.Idle, _runtime.State);
            Assert.Empty(_sink.Calls);
        }

        // ---- emergency stop ----

        [Fact]
        public void EmergencyStopCancelsPlaybackAndReleasesEverything()
        {
            SaveLongRunning("held");
            _runtime.ReloadHotkeys();
            _runtime.Start("held");
            Assert.True(WaitUntil(() => SinkHas("kd:11")));

            _registrar.Press("emergency-stop");

            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));
            Assert.Empty(_sink.Down);
            Assert.Contains(_warnings, w => w.StartsWith("Emergency stop"));
        }

        [Fact]
        public void EmergencyStopDiscardsARecording()
        {
            RecordingResult? finished = null;
            _runtime.RecordingFinished += r => finished = r;
            _runtime.ReloadHotkeys();
            _runtime.StartRecording();

            _registrar.Press("emergency-stop");

            Assert.NotNull(finished);
            Assert.True(finished!.Cancelled);
            Assert.Empty(finished.Actions);
            Assert.Equal(MacroRuntimeState.Idle, _runtime.State);
            Assert.False(_recorder.IsRecording);
        }

        [Fact]
        public void TheRecordersEmergencyComboCancelsInsteadOfSaving()
        {
            _recorder.Events.Add(new RawRecordedEvent(RawEventKind.KeyDown, 0, 0x1E));
            _recorder.Events.Add(new RawRecordedEvent(RawEventKind.KeyUp, 50, 0x1E));
            RecordingResult? finished = null;
            _runtime.RecordingFinished += r => finished = r;
            _runtime.StartRecording();

            _recorder.RaiseStop(RecorderStopReason.EmergencyStop);

            Assert.True(WaitUntil(() => finished is not null));
            Assert.True(finished!.Cancelled);
        }

        [Fact]
        public void EmergencyStopWhenIdleIsQuiet()
        {
            _runtime.EmergencyStop();

            Assert.Empty(_warnings);
            Assert.Equal(MacroRuntimeState.Idle, _runtime.State);
        }

        // ---- shutdown ----

        [Fact]
        public void DisposeReleasesHeldInputStopsTheRecorderAndHotkeys()
        {
            SaveLongRunning("held");
            _runtime.Start("held");
            Assert.True(WaitUntil(() => SinkHas("kd:11")));

            _runtime.Dispose();

            Assert.Empty(_sink.Down);
            Assert.True(_recorder.Disposed);
            Assert.True(_registrar.Disposed);
            Assert.Equal(MacroRuntimeState.Idle, _runtime.State);
        }

        [Fact]
        public void AfterShutdownNothingNewStarts()
        {
            Save("quick");
            _runtime.Dispose();

            Assert.Equal(MacroStartResult.ShuttingDown, _runtime.Start("quick"));
            Assert.Equal(RecordingStartResult.ShuttingDown, _runtime.StartRecording());
            Assert.Empty(_runtime.ReloadHotkeys().Macros);
            Assert.Empty(_sink.Calls);
        }

        [Fact]
        public void DisposeTwiceIsFine()
        {
            _runtime.Dispose();
            _runtime.Dispose();
        }

        [Fact]
        public void ShutdownFromAnotherThreadWhilePlayingReleasesSynchronously()
        {
            SaveLongRunning("held");
            _runtime.Start("held");
            Assert.True(WaitUntil(() => SinkHas("kd:11")));

            var clock = Stopwatch.StartNew();
            System.Threading.Tasks.Task.Run(() => _runtime.Dispose()).Wait();
            clock.Stop();

            Assert.Empty(_sink.Down);
            Assert.True(clock.ElapsedMilliseconds < 2000, $"shutdown took {clock.ElapsedMilliseconds} ms");
        }

        [Fact]
        public void HandlerExceptionsCannotBreakTheStateMachine()
        {
            _runtime.StateChanged += _ => throw new InvalidOperationException("ui bug");
            _runtime.PlaybackFinished += (_, _) => throw new InvalidOperationException("ui bug");
            Save("quick");

            Assert.Equal(MacroStartResult.Started, _runtime.Start("quick"));
            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));
            Assert.Equal(MacroStartResult.Started, _runtime.Start("quick"));
            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle && _sink.Calls.Count == 4));
        }

        // ---- review regressions ----

        [Fact]
        public void TheEmergencyStopIsRegisteredAsSoonAsTheRuntimeExists()
        {
            Assert.Equal("emergency-stop", _registrar.Order[0]);
        }

        [Fact]
        public void StartingBeforeAnyReloadStillArmsTheEmergencyStop()
        {
            _registrar.UnregisterAll(); // as if the registration had been lost
            Assert.DoesNotContain("emergency-stop", _registrar.Names);

            Save("quick");
            Assert.Equal(MacroStartResult.Started, _runtime.Start("quick"));

            Assert.Contains("emergency-stop", _registrar.Names);
            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));
            Assert.DoesNotContain(_warnings, w => w.Contains("emergency stop"));
        }

        [Fact]
        public void AMacroStillStartsWithoutAnEmergencyStopButTheUserIsWarned()
        {
            _registrar.UnregisterAll();
            _registrar.Refuse = hk => hk.ToString().EndsWith("End") ? new HotkeyRegistration(HotkeyStatus.InUse, "taken") : HotkeyRegistration.Ok;
            Save("quick");

            Assert.Equal(MacroStartResult.Started, _runtime.Start("quick"));

            Assert.Contains(_warnings, w => w.Contains("emergency stop") && w.Contains("Stop button"));
            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));
        }

        [Fact]
        public void TheEmergencyStopIsAlsoRegisteredWithEveryExtraModifierCombination()
        {
            var names = _registrar.Names.Where(n => n.StartsWith("emergency-stop")).ToList();

            // Ctrl+Shift+End can be hit with any mix of Alt and Win also held (a macro may be holding them): 3 more combinations.
            Assert.Equal(4, names.Count);
            Assert.Equal(
                new[] { "Ctrl+Alt+Shift+End", "Ctrl+Alt+Shift+Win+End", "Ctrl+Shift+End", "Ctrl+Shift+Win+End" },
                _registrar.Keys.Where(kv => kv.Key.StartsWith("emergency-stop")).Select(kv => kv.Value.ToString()).OrderBy(x => x).ToArray());

            // Every variant stops the macro.
            SaveLongRunning("held");
            _runtime.Start("held");
            Assert.True(WaitUntil(() => SinkHas("kd:11")));
            _registrar.Press(names.First(n => n != "emergency-stop"));
            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));
        }

        [Fact]
        public void ExtraModifierVariantsFailingDoesNotChangeTheReportedStatus()
        {
            _registrar.Refuse = hk => hk.ToString() is "Ctrl+Shift+End" ? HotkeyRegistration.Ok : new HotkeyRegistration(HotkeyStatus.InUse, "taken");

            var result = _runtime.ReloadHotkeys();

            Assert.True(result.EmergencyStopRegistration.IsOk);
            Assert.Equal(new[] { "emergency-stop" }, _registrar.Names.Where(n => n.StartsWith("emergency-stop")).ToArray());
        }

        [Fact]
        public void VariantsAreNotRegisteredWhenTheBaseShortcutFails()
        {
            _registrar.UnregisterAll();
            _registrar.Refuse = hk => hk.ToString() == "Ctrl+Shift+End" ? new HotkeyRegistration(HotkeyStatus.InUse, "taken") : HotkeyRegistration.Ok;

            var result = _runtime.ReloadHotkeys();

            Assert.False(result.EmergencyStopRegistration.IsOk);
            Assert.DoesNotContain(_registrar.Names, n => n.StartsWith("emergency-stop"));
        }

        [Fact]
        public void TypingKeysAreNotUsedForTheEmergencyStopOrMacros()
        {
            _settings.Save(new MacroStudioSettings { EmergencyStop = "W" });
            Assert.Equal("Ctrl+Shift+End", _runtime.ReloadHotkeys().EmergencyStop);

            // A macro whose file still carries a plain-key shortcut (written by hand) gets an Invalid status, not a registration.
            Save("typing", hotkey: "F6");
            string path = Path.Combine(_store.Directory, "typing.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"F6\"", "\"Space\""));
            Assert.Contains("Space", File.ReadAllText(path));
            var result = _runtime.ReloadHotkeys();

            Assert.DoesNotContain("macro:typing", _registrar.Names);
            Assert.DoesNotContain(result.Macros, m => m.Registration.IsOk && m.MacroId == "typing");
        }

        [Fact]
        public void MacroShortcutsAreHandedOffToABackgroundThread()
        {
            Save("a", hotkey: "F6");
            _runtime.ReloadHotkeys();
            var queued = new List<Action>();
            _runtime.BackgroundDispatch = queued.Add;

            _registrar.Press("macro:a");

            Assert.Single(queued);
            Assert.Equal(MacroRuntimeState.Idle, _runtime.State); // nothing ran on the registrar's thread
            queued[0]();
            Assert.True(WaitUntil(() => _sink.Calls.Count == 2));
        }

        [Fact]
        public void TheEmergencyShortcutStopsInlineAndDefersTheRest()
        {
            SaveLongRunning("held");
            _runtime.Start("held");
            Assert.True(WaitUntil(() => SinkHas("kd:11")));
            var queued = new List<Action>();
            _runtime.BackgroundDispatch = queued.Add;

            _registrar.Press("emergency-stop");

            Assert.Single(queued); // warnings and recording teardown are deferred...
            Assert.True(WaitUntil(() => SinkHas("ku:11"))); // ...but the release already happened on the registrar's thread
            Assert.Empty(_sink.Down);
            queued[0]();
            Assert.Contains(_warnings, w => w.StartsWith("Emergency stop"));
        }

        [Fact]
        public void TheRunningMacroIsACopyOfTheDraft()
        {
            var draft = Of(KeyDown(0x11), Wait(30000));
            draft.Id = "orig";
            draft.Name = "Orig";
            string? finishedId = null;
            _runtime.PlaybackFinished += (id, _) => finishedId = id;

            _runtime.Start(draft);
            draft.Id = "changed";
            draft.Actions.Clear();
            Assert.True(WaitUntil(() => SinkHas("kd:11")));
            Assert.Equal(MacroRuntimeState.Running, _runtime.State);

            _runtime.Stop();
            Assert.True(WaitUntil(() => finishedId is not null));
            Assert.Equal("orig", finishedId);
        }

        // ---- blocked releases ----

        [Fact]
        public void ABlockedReleaseIsRetriedInTheBackgroundAndWarnedAboutOnce()
        {
            _runtime.RetryIntervalMs = 20;
            bool blocked = true;
            _sink.ThrowOn = call => blocked && call == "ku:11";
            SaveLongRunning("held");

            _runtime.Start("held");
            Assert.True(WaitUntil(() => SinkHas("kd:11")));
            _runtime.Stop();

            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));
            Assert.Equal(1, _warnings.Count(w => w.Contains("Some keys may still be held")));
            Assert.Contains(_warnings, w => w.Contains("Switch to another window"));

            // Keeps trying while blocked...
            Assert.True(WaitUntil(() => _sink.Calls.Count(c => c == "ku:11") >= 4));

            // ...and stops once the release goes through.
            blocked = false;
            Assert.True(WaitUntil(() =>
            {
                int before = _sink.Calls.Count(c => c == "ku:11");
                Thread.Sleep(80);
                return _sink.Calls.Count(c => c == "ku:11") == before;
            }));
            Assert.Equal(1, _warnings.Count(w => w.Contains("Some keys may still be held")));
        }

        [Fact]
        public void BlockedReleasesStopBeingRetriedAfterTheWindow()
        {
            _runtime.RetryIntervalMs = 10;
            _runtime.RetryWindowMs = 150;
            _sink.ThrowOn = call => call == "ku:11";
            SaveLongRunning("held");

            _runtime.Start("held");
            Assert.True(WaitUntil(() => SinkHas("kd:11")));
            _runtime.Stop();
            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));

            Thread.Sleep(400);
            int settled = _sink.Calls.Count(c => c == "ku:11");
            Thread.Sleep(200);

            Assert.Equal(settled, _sink.Calls.Count(c => c == "ku:11"));
        }

        // ---- singleton and shutdown ----

        [Fact]
        public void TryShutdownReleasesInputOfALiveSingleton()
        {
            MacroRuntime.SetInstanceForTests(_runtime);

            try
            {
                Assert.True(MacroRuntime.IsCreated);
                SaveLongRunning("held");
                _runtime.Start("held");
                Assert.True(WaitUntil(() => SinkHas("kd:11")));

                MacroRuntime.TryShutdown();

                Assert.Empty(_sink.Down);
                Assert.True(SinkHas("ku:11"));
                Assert.True(_recorder.Disposed);
                Assert.True(_registrar.Disposed);
                Assert.Equal(MacroStartResult.ShuttingDown, _runtime.Start("held"));

                MacroRuntime.TryShutdown(); // idempotent
            }
            finally
            {
                MacroRuntime.SetInstanceForTests(null);
            }
        }

        [Fact]
        public void TryShutdownWithoutASingletonDoesNothing()
        {
            MacroRuntime.SetInstanceForTests(null);

            Assert.False(MacroRuntime.IsCreated);
            MacroRuntime.TryShutdown();
            MacroRuntime.TryStopAll();
            MacroRuntime.TryDispose();

            Assert.False(MacroRuntime.IsCreated);
            Assert.Empty(_sink.Calls);
            Assert.False(_registrar.Disposed);
            Assert.False(_recorder.Disposed);
        }

        [Fact]
        public void TryStopAllStopsAndReleasesButLeavesTheRuntimeUsable()
        {
            MacroRuntime.SetInstanceForTests(_runtime);

            try
            {
                SaveLongRunning("held");
                Save("quick");
                _runtime.Start("held");
                Assert.True(WaitUntil(() => SinkHas("kd:11")));

                MacroRuntime.TryStopAll();

                Assert.Empty(_sink.Down);
                Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));
                Assert.False(_registrar.Disposed);
                Assert.Equal(MacroStartResult.Started, _runtime.Start("quick")); // a cancelled SessionEnding leaves everything working
                Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));
            }
            finally
            {
                MacroRuntime.SetInstanceForTests(null);
            }
        }

        [Fact]
        public void TryDisposeTearsTheSingletonDownAndClearsIt()
        {
            MacroRuntime.SetInstanceForTests(_runtime);

            MacroRuntime.TryDispose();

            Assert.False(MacroRuntime.IsCreated);
            Assert.True(_registrar.Disposed);
            Assert.True(_recorder.Disposed);
        }

        [Fact]
        public void ANewRuntimeCannotStartWhileAnOldPlayerThreadIsStillAlive()
        {
            // The first runtime's player gets stuck inside a send (as if SendInput hung).
            using var release = new ManualResetEventSlim(false);
            var stuckSink = new RecordingSink { OnCall = call => { if (call == "kd:11") release.Wait(10000); } };
            var stuckPlayer = new MacroPlayer(stuckSink, new RealtimeDelay(), new FakeGate());
            var store = new MacroStore(Path.Combine(_root, "Macros2"), _logs.Add);
            var macro = Of(KeyDown(0x11), Wait(30000));
            macro.Id = "stuck";
            macro.Name = "Stuck";
            store.Save(macro);

            var old = new MacroRuntime(store, _settings, new FakeRegistrar(), new FakeRecorder(), stuckPlayer, _logs.Add);

            try
            {
                Assert.Equal(MacroStartResult.Started, old.Start("stuck"));
                Assert.True(WaitUntil(() => stuckSink.Calls.Contains("kd:11")));
                Assert.False(old.Stop()); // cannot stop in time: the thread is inside the send
                old.Dispose(); // takes the registrations down

                Assert.True(MacroRuntime.LivePlayerThreads > 0);

                Save("quick");
                Assert.Equal(MacroStartResult.AlreadyRunning, _runtime.Start("quick"));
                Assert.Empty(_sink.Calls);
            }
            finally
            {
                release.Set();
            }

            Assert.True(WaitUntil(() => MacroRuntime.LivePlayerThreads == 0));
            Assert.Equal(MacroStartResult.Started, _runtime.Start("quick"));
            Assert.True(WaitUntil(() => _runtime.State == MacroRuntimeState.Idle));
        }
    }
}
