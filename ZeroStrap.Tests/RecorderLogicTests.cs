using System;
using System.Diagnostics;

using Xunit;

using ZeroStrap.Macros;

namespace ZeroStrap.Tests
{
    /// <summary>The pure parts of the capture layer: key classification, hook health, the hotkey table and the Roblox focus gate.</summary>
    public sealed class RecorderLogicTests
    {
        private const int VkShift = 0x10, VkLShift = 0xA0, VkCtrl = 0x11, VkLCtrl = 0xA2, VkAlt = 0x12, VkLWin = 0x5B;
        private const int VkF9 = 0x78, VkEnd = 0x23, VkF6 = 0x75, VkW = 0x57;

        private static RecorderKeyFilter Filter(string stop = "F9", string? emergency = "Ctrl+Shift+End", params string[] ignored)
        {
            Hotkey.TryParse(stop, out var stopKey);
            Hotkey? emergencyKey = emergency is not null && Hotkey.TryParse(emergency, out var e) ? e : null;
            var ignoredKeys = new Hotkey[ignored.Length];

            for (int i = 0; i < ignored.Length; i++)
                Assert.True(Hotkey.TryParse(ignored[i], out ignoredKeys[i]));

            return new RecorderKeyFilter(new RecorderOptions { StopKey = stopKey, EmergencyStop = emergencyKey, IgnoredHotkeys = ignoredKeys });
        }

        [Fact]
        public void OrdinaryKeysAreRecorded()
        {
            var filter = Filter();

            Assert.Equal(KeyDecision.Record, filter.Classify(VkW, true));
            Assert.Equal(KeyDecision.Record, filter.Classify(VkW, false));
        }

        [Fact]
        public void StopKeyStopsAndItsUpAndRepeatsAreDropped()
        {
            var filter = Filter();

            Assert.Equal(KeyDecision.Stop, filter.Classify(VkF9, true));
            Assert.Equal(KeyDecision.Drop, filter.Classify(VkF9, true)); // auto-repeat
            Assert.Equal(KeyDecision.Drop, filter.Classify(VkF9, false));
            Assert.Equal(KeyDecision.Record, filter.Classify(VkF9, false)); // nothing left to swallow
        }

        [Fact]
        public void StopKeyWithModifiersNeedsTheExactModifiers()
        {
            var filter = Filter(stop: "Ctrl+F9");

            Assert.Equal(KeyDecision.Record, filter.Classify(VkF9, true)); // no Ctrl held
            filter.Classify(VkF9, false);

            Assert.Equal(KeyDecision.Record, filter.Classify(VkLCtrl, true)); // modifiers themselves are recorded
            Assert.Equal(KeyDecision.Stop, filter.Classify(VkF9, true));
        }

        [Fact]
        public void ExtraModifiersDoNotMatchAStopKey()
        {
            var filter = Filter();

            filter.Classify(VkLShift, true);
            Assert.Equal(KeyDecision.Record, filter.Classify(VkF9, true));
        }

        [Fact]
        public void EmergencyComboIsAnEmergencyStopAndIsNotRecorded()
        {
            var filter = Filter();

            Assert.Equal(KeyDecision.Record, filter.Classify(VkLCtrl, true));
            Assert.Equal(KeyDecision.Record, filter.Classify(VkLShift, true));
            Assert.Equal(KeyDecision.EmergencyStop, filter.Classify(VkEnd, true));
            Assert.Equal(KeyDecision.Drop, filter.Classify(VkEnd, false));
        }

        [Fact]
        public void EmergencyBeatsStopKeyWhenBothAreTheSameKey()
        {
            var filter = Filter(stop: "End", emergency: "End");

            Assert.Equal(KeyDecision.EmergencyStop, filter.Classify(VkEnd, true));
        }

        [Fact]
        public void IgnoredHotkeysAreDroppedButDoNotStop()
        {
            var filter = Filter(ignored: "F6");

            Assert.Equal(KeyDecision.Drop, filter.Classify(VkF6, true));
            Assert.Equal(KeyDecision.Drop, filter.Classify(VkF6, false));
            Assert.Equal(KeyDecision.Record, filter.Classify(VkW, true));
        }

        [Fact]
        public void ModifierStateIsTrackedAcrossLeftRightAndGenericCodes()
        {
            var filter = Filter(emergency: "Ctrl+Alt+Shift+Win+End");

            filter.Classify(VkCtrl, true);
            filter.Classify(VkAlt, true);
            filter.Classify(VkShift, true);
            filter.Classify(VkLWin, true);
            Assert.Equal(KeyDecision.EmergencyStop, filter.Classify(VkEnd, true));
            filter.Classify(VkEnd, false);

            filter.Classify(VkLWin, false);
            Assert.Equal(KeyDecision.Record, filter.Classify(VkEnd, true)); // Win released: no longer the combo
        }

        [Fact]
        public void HookHealthNeedsRecentRawMovementAndLongHookSilence()
        {
            Assert.False(HookHealth.ShouldReinstall(10_000, -1, 0), "no raw movement ever seen");
            Assert.False(HookHealth.ShouldReinstall(10_000, 9_500, 9_000), "hooks were heard 1 s ago");
            Assert.False(HookHealth.ShouldReinstall(10_000, 7_000, 1_000), "raw movement stopped 3 s ago: the mouse is idle");
            Assert.True(HookHealth.ShouldReinstall(10_000, 9_500, 8_000), "raw movement now, hooks silent for 2 s");
            Assert.True(HookHealth.ShouldReinstall(10_000, 9_000, 8_000), "boundaries are inclusive");
        }

        [Fact]
        public void HotkeyTableRejectsTheSameCombinationUnderAnotherName()
        {
            var table = new HotkeyTable();
            Hotkey.TryParse("Ctrl+F7", out var combo);

            Assert.True(table.TryAdd("a", combo, () => { }, out int idA));
            Assert.False(table.TryAdd("b", combo, () => { }, out _));
            Assert.Equal(1, table.Count);

            Assert.True(table.Remove("a", out int removed));
            Assert.Equal(idA, removed);
            Assert.True(table.TryAdd("b", combo, () => { }, out _));
        }

        [Fact]
        public void HotkeyTableResolvesCallbacksAndForgetsRemovedIds()
        {
            var table = new HotkeyTable();
            Hotkey.TryParse("F6", out var f6);
            int calls = 0;

            table.TryAdd("a", f6, () => calls++, out int id);

            Assert.True(table.TryGetCallback(id, out var callback));
            callback();
            Assert.Equal(1, calls);

            table.Remove("a", out _);
            Assert.False(table.TryGetCallback(id, out _));

            table.TryAdd("x", f6, () => { }, out int first);
            Hotkey.TryParse("F7", out var f7);
            table.TryAdd("y", f7, () => { }, out int second);
            Assert.NotEqual(first, second);
            Assert.Equal(2, table.Ids().Count);

            table.Clear();
            Assert.Equal(0, table.Count);
            Assert.Empty(table.Ids());
        }

        // ---- RobloxFocusGate ----

        private sealed class GateEnv
        {
            public uint Pid = 100;
            public string? Image = @"C:\Roblox\Versions\x\RobloxPlayerBeta.exe";
            public long Ticks;
            public int Lookups;

            public RobloxFocusGate Gate() => new(() => Pid, _ => { Lookups++; return Image; }, () => Ticks);
        }

        [Fact]
        public void GateAllowsRobloxPlayerOnly()
        {
            var env = new GateEnv();
            var gate = env.Gate();

            Assert.True(gate.IsAllowed());

            env.Pid = 200;
            env.Image = @"C:\Windows\notepad.exe";
            Assert.False(gate.IsAllowed());

            env.Pid = 300;
            env.Image = @"C:\Roblox\robloxplayerbeta.EXE";
            Assert.True(gate.IsAllowed(), "case-insensitive");

            env.Pid = 400;
            env.Image = @"C:\Roblox\RobloxStudioBeta.exe";
            Assert.False(gate.IsAllowed());
        }

        [Fact]
        public void GateFailsClosed()
        {
            var env = new GateEnv { Image = null };
            Assert.False(env.Gate().IsAllowed());

            env = new GateEnv { Pid = 0 };
            Assert.False(env.Gate().IsAllowed());

            var throwing = new RobloxFocusGate(() => throw new InvalidOperationException(), _ => null, () => 0);
            Assert.False(throwing.IsAllowed());
        }

        [Fact]
        public void GateCachesPerProcessForOneSecond()
        {
            var env = new GateEnv();
            var gate = env.Gate();

            Assert.True(gate.IsAllowed());
            Assert.True(gate.IsAllowed());
            Assert.Equal(1, env.Lookups);

            env.Ticks = Stopwatch.Frequency / 2;
            Assert.True(gate.IsAllowed());
            Assert.Equal(1, env.Lookups);

            env.Ticks = Stopwatch.Frequency + 1;
            gate.IsAllowed();
            Assert.Equal(2, env.Lookups);
        }

        [Fact]
        public void GateReactsImmediatelyToAForegroundChange()
        {
            var env = new GateEnv();
            var gate = env.Gate();

            Assert.True(gate.IsAllowed());

            env.Pid = 555;
            env.Image = @"C:\Windows\explorer.exe";
            Assert.False(gate.IsAllowed()); // different pid: no stale answer from the cache

            env.Pid = 100;
            env.Image = null;
            Assert.True(gate.IsAllowed()); // still cached for the Roblox pid
        }

        // ---- real recorder lifecycle (no input is injected; skips quietly where hooks are unavailable) ----

        [Fact]
        public void RealRecorderStartsAndStopsPromptlyWithoutHanging()
        {
            using var recorder = new InputRecorder();

            Assert.Empty(recorder.Stop()); // stopping when never started is harmless

            if (!recorder.Start(new RecorderOptions(), out string? error))
            {
                Assert.False(string.IsNullOrEmpty(error)); // e.g. a session without a desktop
                Assert.False(recorder.IsRecording);
                return;
            }

            Assert.True(recorder.IsRecording);
            Assert.False(recorder.Start(new RecorderOptions(), out string? again));
            Assert.NotNull(again);

            var clock = Stopwatch.StartNew();
            recorder.Stop();
            clock.Stop();

            Assert.True(clock.ElapsedMilliseconds < 1500, $"Stop took {clock.ElapsedMilliseconds} ms");
            Assert.False(recorder.IsRecording);

            // It can be started again after a stop.
            Assert.True(recorder.Start(new RecorderOptions(), out _));
            recorder.Stop();
        }
    }
}
