using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using Xunit;

using ZeroStrap.Macros;

using static ZeroStrap.Tests.MacroBuilders;

namespace ZeroStrap.Tests
{
    /// <summary>A sink that is safe to call from several threads, with switchable blocked key-ups.</summary>
    internal sealed class SafeSink : IInputSink
    {
        private readonly object _lock = new();
        private readonly List<string> _calls = new();

        public volatile bool BlockUps;

        public List<string> Snapshot()
        {
            lock (_lock)
                return _calls.ToList();
        }

        public int Count
        {
            get
            {
                lock (_lock)
                    return _calls.Count;
            }
        }

        private void Add(string call)
        {
            lock (_lock)
                _calls.Add(call);
        }

        public void KeyDown(ushort scan, bool extended) => Add($"kd:{scan:X2}");

        public void KeyUp(ushort scan, bool extended)
        {
            if (BlockUps)
                throw new InputBlockedException();

            Add($"ku:{scan:X2}");
        }

        public void MouseDown(MouseButton button) => Add("md:" + button);

        public void MouseUp(MouseButton button)
        {
            if (BlockUps)
                throw new InputBlockedException();

            Add("mu:" + button);
        }

        public void MoveRelative(int dx, int dy) => Add($"rel:{dx},{dy}");

        public void MoveAbsolute(int x, int y) => Add($"abs:{x},{y}");
    }

    /// <summary>Regression tests for the adversarial review of the Macros engine (player races, blocked releases, hotkey policy, store and compiler edge cases).</summary>
    public sealed class MacroReviewTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "zs-macro-review-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _logs = new();

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        // ---- player: no press may follow a release from another thread ----

        [Fact]
        public void AReleaseFromAnotherThreadIsNeverFollowedByAPress()
        {
            for (int trial = 0; trial < 60; trial++)
            {
                var sink = new SafeSink();
                var player = new MacroPlayer(sink, new FakeDelay());
                var macro = Of(TapKey(0x1E), TapKey(0x11), Click(MouseButton.Left), TapKey(0x20));
                macro.Repeat = new MacroRepeat { Mode = MacroRepeatMode.UntilStopped };

                PlayResult result = default;
                var thread = new Thread(() => result = player.Play(macro, CancellationToken.None)) { IsBackground = true };
                thread.Start();

                int threshold = 5 + (trial * 7) % 40;
                var spin = SpinWait.SpinUntil(() => sink.Count >= threshold, 5000);
                Assert.True(spin);

                Assert.Equal(0, player.ReleaseHeld());
                int afterRelease = sink.Count;

                Assert.True(thread.Join(5000), "the player did not stop");

                var calls = sink.Snapshot();
                Assert.DoesNotContain(calls.Skip(afterRelease), c => c.StartsWith("kd:") || c.StartsWith("md:"));
                Assert.Equal(PlayOutcome.Cancelled, result.Outcome);

                // Every key and button ends on an Up.
                foreach (var group in calls.Where(c => c.Length > 3).GroupBy(c => c.Substring(3)))
                    Assert.True(group.Last().StartsWith("ku:") || group.Last().StartsWith("mu:"), $"{group.Key} ended on {group.Last()}");

                Assert.Equal(0, player.PendingReleaseCount);
            }
        }

        [Fact]
        public void ReleaseHeldWhileIdleDoesNotBlockTheNextRun()
        {
            var sink = new SafeSink();
            var player = new MacroPlayer(sink, new FakeDelay());

            player.ReleaseHeld();
            var result = player.Play(Of(TapKey(0x1E)), CancellationToken.None);

            Assert.Equal(PlayOutcome.Completed, result.Outcome);
            Assert.Equal(new[] { "kd:1E", "ku:1E" }, sink.Snapshot());
        }

        // ---- player: a blocked Up is not forgotten ----

        [Fact]
        public void ABlockedReleaseStaysPendingUntilItGoesThrough()
        {
            var sink = new SafeSink { BlockUps = true };
            var player = new MacroPlayer(sink, new FakeDelay(), log: _logs.Add);

            var result = player.Play(Of(KeyDown(0x11), MouseDown(MouseButton.Right)), CancellationToken.None);

            Assert.Equal(PlayOutcome.Faulted, result.Outcome);
            Assert.Contains("release", result.Error);
            Assert.Equal(2, player.PendingReleaseCount);
            Assert.Equal(2, player.RetryPendingReleases()); // still blocked

            sink.BlockUps = false;

            Assert.Equal(0, player.RetryPendingReleases());
            Assert.Equal(0, player.PendingReleaseCount);
            Assert.Contains("ku:11", sink.Snapshot());
            Assert.Contains("mu:Right", sink.Snapshot());
        }

        [Fact]
        public void PendingReleasesAreRetriedWhenTheNextRunStarts()
        {
            var sink = new SafeSink { BlockUps = true };
            var player = new MacroPlayer(sink, new FakeDelay());
            player.Play(Of(KeyDown(0x11)), CancellationToken.None);
            Assert.Equal(1, player.PendingReleaseCount);

            sink.BlockUps = false;
            player.Play(Of(TapKey(0x1E)), CancellationToken.None);

            Assert.Equal(0, player.PendingReleaseCount);
            Assert.Contains("ku:11", sink.Snapshot());
        }

        [Fact]
        public void ABlockedPressFaultsTheRunInsteadOfPretending()
        {
            var sink = new BlockingPressSink();
            var player = new MacroPlayer(sink, new FakeDelay());

            var result = player.Play(Of(TapKey(0x1E)), CancellationToken.None);

            Assert.Equal(PlayOutcome.Faulted, result.Outcome);
            Assert.Contains("administrator", result.Error);
        }

        private sealed class BlockingPressSink : IInputSink
        {
            public void KeyDown(ushort scan, bool extended) => throw new InputBlockedException();
            public void KeyUp(ushort scan, bool extended) { }
            public void MouseDown(MouseButton button) => throw new InputBlockedException();
            public void MouseUp(MouseButton button) { }
            public void MoveRelative(int dx, int dy) { }
            public void MoveAbsolute(int x, int y) { }
        }

        // ---- hotkey policy ----

        [Theory]
        [InlineData("F6", true)]
        [InlineData("F24", true)]
        [InlineData("End", true)]
        [InlineData("Home", true)]
        [InlineData("Insert", true)]
        [InlineData("Delete", true)]
        [InlineData("PageUp", true)]
        [InlineData("PageDown", true)]
        [InlineData("Shift+F6", true)]
        [InlineData("Ctrl+A", true)]
        [InlineData("Alt+Space", true)]
        [InlineData("Ctrl+Left", true)]
        [InlineData("Win+Numpad1", true)]
        [InlineData("Ctrl+Shift+End", true)]
        [InlineData("A", false)]
        [InlineData("5", false)]
        [InlineData("Space", false)]
        [InlineData("Enter", false)]
        [InlineData("Escape", false)]
        [InlineData("Tab", false)]
        [InlineData("Backspace", false)]
        [InlineData("Numpad5", false)]
        [InlineData("NumpadAdd", false)]
        [InlineData("Left", false)]
        [InlineData("Up", false)]
        [InlineData("Shift+A", false)]
        [InlineData("Shift+Left", false)]
        [InlineData("Shift+Space", false)]
        public void OnlyNonTypingKeysAreAllowedWithoutAControlAltOrWin(string text, bool allowed)
        {
            Assert.True(Hotkey.TryParse(text, out var hotkey));
            Assert.Equal(allowed, hotkey.IsAllowedShortcut);
            Assert.Equal(allowed, Hotkey.TryParseShortcut(text, out _));
        }

        [Fact]
        public void ADefaultHotkeyIsNotAnAllowedShortcut()
        {
            Assert.False(default(Hotkey).IsAllowedShortcut);
            Assert.False(Hotkey.TryParseShortcut("nonsense", out _));
        }

        [Fact]
        public void ATypingKeyHotkeyIsDroppedOnNormalizeAndRejectedByValidate()
        {
            var macro = Of(TapKey(0x1E));
            macro.Hotkey = "w";

            var result = MacroValidator.Process(macro);

            Assert.True(result.IsValid);
            Assert.Null(result.Macro.Hotkey);
            Assert.Contains(result.Warnings, w => w.Contains("typing keys"));

            Assert.Contains(MacroValidator.Validate(macro), e => e.Contains("typing key"));
        }

        [Fact]
        public void SettingsFallBackToTheDefaultsForTypingKeys()
        {
            var store = new MacroStudioSettingsStore(Path.Combine(_root, "settings.json"), _logs.Add);
            store.Save(new MacroStudioSettings { EmergencyStop = "A", RecordStopKey = "Space" });

            var loaded = store.Load();

            Assert.Equal(MacroStudioSettings.DefaultEmergencyStop, loaded.EmergencyStop);
            Assert.Equal(MacroStudioSettings.DefaultRecordStopKey, loaded.RecordStopKey);

            store.Save(new MacroStudioSettings { EmergencyStop = "ctrl+alt+x", RecordStopKey = "f10" });
            loaded = store.Load();
            Assert.Equal("Ctrl+Alt+X", loaded.EmergencyStop);
            Assert.Equal("F10", loaded.RecordStopKey);
        }

        // ---- hotkey table ids ----

        [Fact]
        public void HotkeyIdsAreReusedAndStayInRange()
        {
            var table = new HotkeyTable();
            Hotkey.TryParse("F6", out var f6);

            for (int i = 0; i < 100_000; i++)
            {
                Assert.True(table.TryAdd("x", f6, () => { }, out int id));
                Assert.InRange(id, HotkeyTable.FirstId, HotkeyTable.LastId);
                Assert.True(table.Remove("x", out _));
            }
        }

        [Fact]
        public void HotkeyIdsSkipOnesInUseAfterWrapping()
        {
            var table = new HotkeyTable();
            Hotkey.TryParse("F6", out var f6);
            Hotkey.TryParse("F7", out var f7);

            table.TryAdd("keep", f6, () => { }, out int kept);

            var seen = new HashSet<int>();

            for (int i = 0; i < HotkeyTable.LastId - HotkeyTable.FirstId + 5; i++)
            {
                table.TryAdd("cycle", f7, () => { }, out int id);
                Assert.NotEqual(kept, id);
                seen.Add(id);
                table.Remove("cycle", out _);
            }

            Assert.True(seen.Count > 1000);
        }

        // ---- real hotkey service: runs without any UI thread ----

        [Fact]
        public void TheHotkeyServiceWorksFromThreadsWithoutADispatcher()
        {
            using var service = new HotkeyService(_logs.Add);
            Hotkey.TryParse("Ctrl+Alt+Shift+Win+F24", out var obscure); // not something a user presses by accident

            HotkeyRegistration first = default, duplicate = default;
            var worker = new Thread(() =>
            {
                first = service.Register("a", obscure, () => { });
                duplicate = service.Register("b", obscure, () => { });
                service.Unregister("a");
            });
            worker.Start();
            Assert.True(worker.Join(10000), "Register hung");

            // In a session where the shortcut is already owned by something else, InUse is the honest answer; Duplicate must never come first.
            Assert.True(first.Status is HotkeyStatus.Ok or HotkeyStatus.InUse, $"unexpected {first.Status}");

            if (first.IsOk)
                Assert.Equal(HotkeyStatus.Duplicate, duplicate.Status);

            Assert.Equal(HotkeyStatus.Invalid, service.Register("bad", default, () => { }).Status);
        }

        [Fact]
        public void TheHotkeyServiceStopsPromptlyAndRefusesAfterwards()
        {
            var service = new HotkeyService(_logs.Add);
            Hotkey.TryParse("Ctrl+Alt+Shift+Win+F23", out var obscure);
            service.Register("a", obscure, () => { });

            var clock = System.Diagnostics.Stopwatch.StartNew();
            service.Dispose();
            service.Dispose();
            clock.Stop();

            Assert.True(clock.ElapsedMilliseconds < 2500, $"Dispose took {clock.ElapsedMilliseconds} ms");
            Assert.Equal(HotkeyStatus.Invalid, service.Register("b", obscure, () => { }).Status);
        }

        // ---- validator: bounded copying ----

        [Fact]
        public void ANormalizedHugeMacroIsCutOffAtTheNodeCapAndRejected()
        {
            var macro = Of();
            macro.Actions = Enumerable.Range(0, 300_000).Select(_ => Wait(1)).ToList();

            var (normalized, _) = MacroValidator.Normalize(macro);

            Assert.True(normalized.Actions.Count <= MacroValidator.MaxNodes + 1);
            Assert.Contains(MacroValidator.Validate(normalized), e => e.Contains("Too many actions"));
        }

        [Fact]
        public void NodesInsideGroupsCountTowardsTheSameCap()
        {
            var inner = Enumerable.Range(0, 6000).Select(_ => Wait(1)).ToArray();
            var macro = Of(Group(1, inner), Group(1, inner), Group(1, inner));

            var result = MacroValidator.Process(macro);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("Too many actions"));
        }

        // ---- store: bounded read, transient errors, newer schema ----

        private MacroStore NewStore()
        {
            return new MacroStore(Path.Combine(_root, "Macros"), _logs.Add);
        }

        [Fact]
        public void AnOversizedImportIsRefusedWhileReading()
        {
            var store = NewStore();
            Directory.CreateDirectory(_root);
            string path = Path.Combine(_root, "big.zeromacro");

            using (var file = File.Create(path))
                file.SetLength(MacroStore.MaxFileBytes + 1);

            var result = store.Import(path);

            Assert.False(result.Success);
            Assert.Contains("larger than 2 MB", result.Errors[0]);
        }

        [Fact]
        public void AFileOfExactlyTheLimitIsStillRead()
        {
            var store = NewStore();
            var macro = store.Save(Of(TapKey(0x1E)));
            string json = File.ReadAllText(Path.Combine(store.Directory, macro.Id + ".json"));
            string padded = json.TrimEnd() + new string(' ', (int)MacroStore.MaxFileBytes - Encoding.UTF8.GetByteCount(json.TrimEnd()));
            Assert.Equal(MacroStore.MaxFileBytes, Encoding.UTF8.GetByteCount(padded));

            string path = Path.Combine(_root, "exact.zeromacro");
            File.WriteAllText(path, padded);

            Assert.True(NewStore().Import(path).Success);
        }

        [Fact]
        public void AByteOrderMarkDoesNotBreakLoadingOrImport()
        {
            var store = NewStore();
            var saved = store.Save(Of(TapKey(0x1E)));
            string path = Path.Combine(store.Directory, saved.Id + ".json");
            File.WriteAllText(path, File.ReadAllText(path), new UTF8Encoding(true));

            Assert.NotNull(store.Load(saved.Id));
            Assert.True(store.Import(path).Success);
        }

        [Fact]
        public void ATransientReadErrorLeavesTheFileAlone()
        {
            var store = NewStore();
            var saved = store.Save(Of(TapKey(0x1E)));
            string path = Path.Combine(store.Directory, saved.Id + ".json");

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Empty(store.LoadAll());
                Assert.Null(store.Load(saved.Id));
            }

            Assert.True(File.Exists(path), "a locked file must not be quarantined");
            Assert.Empty(Directory.GetFiles(store.Directory, "*.corrupt*"));
            Assert.NotNull(store.Load(saved.Id));
        }

        [Fact]
        public void ANewerSchemaIsSkippedButNeverQuarantined()
        {
            var store = NewStore();
            var saved = store.Save(Of(TapKey(0x1E)));
            string path = Path.Combine(store.Directory, saved.Id + ".json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));
            Assert.Contains("\"schemaVersion\": 2", File.ReadAllText(path));

            Assert.Empty(store.LoadAll());
            Assert.True(File.Exists(path));
            Assert.Contains(_logs, l => l.Contains("schema version 2"));
        }

        [Fact]
        public void InvalidJsonIsStillQuarantined()
        {
            var store = NewStore();
            Directory.CreateDirectory(store.Directory);
            string path = Path.Combine(store.Directory, "broken.json");
            File.WriteAllText(path, "{ not json");

            Assert.Empty(store.LoadAll());
            Assert.False(File.Exists(path));
        }

        // ---- compiler: caps, burst splitting, consumed shortcuts ----

        private static RawRecordedEvent RKeyDown(double t, ushort scan, bool ext = false) => new(RawEventKind.KeyDown, t, scan, ext);
        private static RawRecordedEvent RKeyUp(double t, ushort scan, bool ext = false) => new(RawEventKind.KeyUp, t, scan, ext);
        private static RawRecordedEvent BtnDown(double t, MouseButton b, int x = 0, int y = 0) => new(RawEventKind.MouseDown, t, Button: b, X: x, Y: y);
        private static RawRecordedEvent BtnUp(double t, MouseButton b) => new(RawEventKind.MouseUp, t, Button: b);
        private static RawRecordedEvent Move(double t, int dx, int dy) => new(RawEventKind.MouseDelta, t, X: dx, Y: dy);
        private static RawRecordedEvent Consumed(double t, HotkeyModifiers mods) => new(RawEventKind.HotkeyConsumed, t, X: (int)mods);

        private static Macro Wrap(List<MacroAction> actions) => new() { Id = "rec", Name = "Recorded", Actions = actions };

        [Fact]
        public void TheActionCapCountsEmittedActionsIncludingAbsoluteMoves()
        {
            // Every click adds a MouseMoveTo, so 6000 clicks are 12000 actions: more than the validator allows.
            var events = new List<RawRecordedEvent>();

            for (int i = 0; i < 6000; i++)
            {
                events.Add(BtnDown(i * 10, MouseButton.Left, i, i));
                events.Add(BtnUp(i * 10 + 2, MouseButton.Left));
            }

            var actions = RecordingCompiler.Compile(events, new RecorderOptions { RecordAbsoluteMoves = true }, out var warnings);

            Assert.True(actions.Count <= MacroValidator.MaxNodes, $"{actions.Count} actions");
            Assert.Contains(warnings, w => w.Contains("too long"));
            Assert.Empty(MacroValidator.Validate(Wrap(actions)));
        }

        [Fact]
        public void ACutRecordingStillReleasesWhatWasHeld()
        {
            var events = new List<RawRecordedEvent> { RKeyDown(0, 0x11), BtnDown(1, MouseButton.Right, 5, 5) };

            for (int i = 0; i < 6000; i++)
            {
                events.Add(BtnDown(10 + i * 10, MouseButton.Left, i, i));
                events.Add(BtnUp(12 + i * 10, MouseButton.Left));
            }

            var actions = RecordingCompiler.Compile(events, new RecorderOptions { RecordAbsoluteMoves = true });

            Assert.Equal(MacroKeyMode.Up, actions.Last(a => a.Kind == MacroActionKind.Key).KeyMode);
            Assert.Equal(MacroButtonMode.Up, actions.Last(a => a.Kind == MacroActionKind.Mouse && a.Button == MouseButton.Right).ButtonMode);

            var sink = new RecordingSink();
            new MacroPlayer(sink, new FakeDelay()).Play(Wrap(actions), default);
            Assert.Empty(sink.Down);
        }

        [Fact]
        public void HoldingAKeyWhileMovingTheCameraDoesNotSplitTheBurst()
        {
            // W is held, so the OS repeats its key-down every ~33 ms while the mouse keeps moving.
            var events = new List<RawRecordedEvent> { RKeyDown(0, 0x11) };

            for (int t = 10; t <= 300; t += 10)
            {
                events.Add(Move(t, 2, 1));

                if (t % 30 == 0)
                    events.Add(RKeyDown(t + 1, 0x11)); // auto-repeat
            }

            var actions = RecordingCompiler.Compile(events, new RecorderOptions());
            var cameras = actions.Where(a => a.Kind == MacroActionKind.Camera).ToList();

            Assert.Single(cameras);
            Assert.Equal(60, cameras[0].Dx);
            Assert.Equal(30, cameras[0].Dy);
        }

        [Fact]
        public void EventsWithUnsupportedScanCodesDoNotSplitABurstEither()
        {
            var actions = RecordingCompiler.Compile(
                new[] { Move(0, 5, 0), RKeyDown(5, 0xE1), RKeyUp(8, 0), Move(10, 5, 0) }, new RecorderOptions());

            var camera = Assert.Single(actions);
            Assert.Equal(10, camera.Dx);
        }

        [Fact]
        public void ModifiersOfAnIgnoredShortcutAreTakenOutOfTheRecording()
        {
            // Ctrl+F7 (an ignored macro shortcut) in the middle of a recording: Ctrl down, [F7 dropped by the recorder], Ctrl up.
            var events = new[]
            {
                RKeyDown(0, 0x11), RKeyUp(40, 0x11),
                RKeyDown(100, 0x1D), Consumed(130, HotkeyModifiers.Ctrl), RKeyDown(160, 0x1D), RKeyUp(200, 0x1D),
                RKeyDown(300, 0x1E), RKeyUp(340, 0x1E)
            };

            var actions = RecordingCompiler.Compile(events, new RecorderOptions());

            Assert.Equal(2, actions.Count);
            Assert.All(actions, a => Assert.NotEqual(0x1D, (int)a.ScanCode));
            Assert.Equal(0x11, actions[0].ScanCode);
            Assert.Equal(0x1E, actions[1].ScanCode);
        }

        [Fact]
        public void ModifiersOfTheStopCombinationAreNotLeftHeldAtTheEnd()
        {
            // "Ctrl+Shift+F9": both modifiers went down, F9 stopped the recording, so no key-up for them was ever seen.
            var events = new[]
            {
                RKeyDown(0, 0x11), RKeyUp(40, 0x11),
                RKeyDown(100, 0x1D), RKeyDown(110, 0x2A), Consumed(120, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift)
            };

            var actions = RecordingCompiler.Compile(events, new RecorderOptions());

            var tap = Assert.Single(actions);
            Assert.Equal(0x11, tap.ScanCode);
        }

        [Fact]
        public void AConsumedShortcutOnlyRemovesTheModifiersItUsed()
        {
            var events = new[]
            {
                RKeyDown(0, 0x2A), RKeyDown(10, 0x1D), Consumed(20, HotkeyModifiers.Ctrl), RKeyUp(500, 0x1D), RKeyUp(520, 0x2A)
            };

            var actions = RecordingCompiler.Compile(events, new RecorderOptions());

            Assert.Equal(2, actions.Count); // Shift stays: it was not part of the shortcut
            Assert.All(actions, a => Assert.Equal(0x2A, (int)a.ScanCode));
        }

        [Fact]
        public void RecorderFilterReportsTheModifiersOfAMatchedShortcut()
        {
            Hotkey.TryParse("Ctrl+F9", out var stop);
            Hotkey.TryParse("Alt+F7", out var ignored);
            var filter = new RecorderKeyFilter(new RecorderOptions { StopKey = stop, IgnoredHotkeys = new[] { ignored } });

            filter.Classify(0xA2, true); // Ctrl
            Assert.Equal(KeyDecision.Stop, filter.Classify(0x78, true));
            Assert.Equal(HotkeyModifiers.Ctrl, filter.LastComboModifiers);

            filter.Classify(0x78, false);
            filter.Classify(0xA2, false);
            filter.Classify(0xA4, true); // Alt
            Assert.Equal(KeyDecision.Drop, filter.Classify(0x76, true));
            Assert.Equal(HotkeyModifiers.Alt, filter.LastComboModifiers);

            Assert.Equal(KeyDecision.Drop, filter.Classify(0x76, true)); // auto-repeat: reported only once
            Assert.Equal(HotkeyModifiers.None, filter.LastComboModifiers);

            Assert.Equal(KeyDecision.Record, filter.Classify(0x57, true));
            Assert.Equal(HotkeyModifiers.None, filter.LastComboModifiers);
        }
    }
}
