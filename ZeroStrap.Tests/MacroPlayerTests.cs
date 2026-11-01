using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;
using Xunit.Abstractions;

using ZeroStrap.Macros;

using static ZeroStrap.Tests.MacroBuilders;

namespace ZeroStrap.Tests
{
    public sealed class MacroPlayerTests
    {
        private readonly ITestOutputHelper _output;
        private readonly RecordingSink _sink = new();
        private readonly FakeDelay _delay = new();
        private readonly FakeGate _gate = new();
        private readonly List<string> _logs = new();
        private readonly List<bool> _paused = new();

        public MacroPlayerTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private MacroPlayer NewPlayer(bool withGate = false)
        {
            var player = new MacroPlayer(_sink, _delay, withGate ? _gate : null, _logs.Add);
            player.PausedChanged += _paused.Add;
            return player;
        }

        private PlayResult Play(Macro macro, TimeSpan? cancelAt = null, bool withGate = false)
        {
            using var cts = new CancellationTokenSource();
            _delay.Cts = cts;
            _delay.CancelAt = cancelAt;
            return NewPlayer(withGate).Play(macro, cts.Token);
        }

        private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

        [Fact]
        public void TapPressesWaitsForTheHoldAndReleases()
        {
            var result = Play(Of(TapKey(0x1E, holdMs: 50)));

            Assert.Equal(PlayOutcome.Completed, result.Outcome);
            Assert.Equal(new[] { "kd:1E", "ku:1E" }, _sink.Calls);
            Assert.Equal(Ms(50), _delay.Time);
        }

        [Fact]
        public void ExtendedFlagIsPassedThrough()
        {
            var tap = TapKey(0x4D);
            tap.Extended = true;

            Play(Of(tap));

            Assert.Equal(new[] { "kd:4D", "ku:4D" }, _sink.Calls);
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void HoldOrderingFollowsTheMacro()
        {
            Play(Of(KeyDown(0x11), MouseDown(MouseButton.Left), KeyDown(0x1E, delayMs: 10), KeyUp(0x11, delayMs: 20), KeyUp(0x1E)));

            Assert.Equal(new[] { "kd:11", "md:Left", "kd:1E", "ku:11", "ku:1E", "mu:Left" }, _sink.Calls);
            Assert.Equal(Ms(30), _delay.Time);
        }

        [Fact]
        public void DownOnlyInputsAreReleasedAtTheEndNewestFirst()
        {
            var result = Play(Of(KeyDown(0x1E), MouseDown(MouseButton.Right), KeyDown(0x1F)));

            Assert.Equal(PlayOutcome.Completed, result.Outcome);
            Assert.Equal(new[] { "kd:1E", "md:Right", "kd:1F", "ku:1F", "mu:Right", "ku:1E" }, _sink.Calls);
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void ClickAndAbsoluteMoveAreSent()
        {
            Play(Of(
                new MacroAction { Kind = MacroActionKind.MouseMoveTo, X = 640, Y = -20 },
                Click(MouseButton.Middle, holdMs: 15)));

            Assert.Equal(new[] { "abs:640,-20", "md:Middle", "mu:Middle" }, _sink.Calls);
            Assert.Equal(Ms(15), _delay.Time);
        }

        [Fact]
        public void CancelMidWaitReleasesEveryHeldKeyAndButton()
        {
            var macro = Of(
                KeyDown(0x1E),
                MouseDown(MouseButton.Left),
                Camera(500, 0, 1000, holdRight: true));

            var result = Play(macro, cancelAt: Ms(200));

            Assert.Equal(PlayOutcome.Cancelled, result.Outcome);
            Assert.Empty(_sink.Down);
            Assert.Contains("mu:Right", _sink.Calls);
            Assert.Contains("mu:Left", _sink.Calls);
            Assert.Contains("ku:1E", _sink.Calls);
            Assert.True(_sink.RelativeTotal().Dx < 500);
        }

        [Fact]
        public void CancelInsideAPlainWaitReleasesHeldKeys()
        {
            var result = Play(Of(KeyDown(0x1E), Wait(5000)), cancelAt: Ms(1000));

            Assert.Equal(PlayOutcome.Cancelled, result.Outcome);
            Assert.Empty(_sink.Down);
            Assert.Equal(Ms(1000), _delay.Time);
        }

        [Fact]
        public void AlreadyCancelledTokenSendsNothing()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = NewPlayer().Play(Of(TapKey(0x1E)), cts.Token);

            Assert.Equal(PlayOutcome.Cancelled, result.Outcome);
            Assert.Empty(_sink.Calls);
        }

        [Fact]
        public void SinkExceptionMidMacroStillReleasesOtherInputs()
        {
            _sink.ThrowOn = call => call == "kd:30";

            var result = Play(Of(KeyDown(0x1E), MouseDown(MouseButton.Left), KeyDown(0x30), KeyDown(0x31)));

            Assert.Equal(PlayOutcome.Faulted, result.Outcome);
            Assert.NotNull(result.Error);
            Assert.Empty(_sink.Down);
            Assert.DoesNotContain("kd:31", _sink.Calls);
            Assert.Contains("ku:30", _sink.Calls);
        }

        [Fact]
        public void OneFailingReleaseDoesNotSkipTheOthers()
        {
            _sink.ThrowOn = call => call == "ku:1F";

            var result = Play(Of(KeyDown(0x1E), KeyDown(0x1F), MouseDown(MouseButton.Left)));

            // Release order is newest first: Left, 1F (throws), 1E.
            Assert.Equal(new[] { "kd:1E", "kd:1F", "md:Left", "mu:Left", "ku:1F", "ku:1E" }, _sink.Calls);
            Assert.Empty(_sink.Down);
            Assert.Equal(PlayOutcome.Faulted, result.Outcome);
            Assert.Contains("1 held input", result.Error);
        }

        [Fact]
        public void FailedUpMidMacroIsRetriedByTheFinalRelease()
        {
            int attempts = 0;
            _sink.ThrowOn = call => call == "ku:1E" && ++attempts == 1;

            var result = Play(Of(KeyDown(0x1E), KeyUp(0x1E), Wait(10)));

            Assert.Equal(PlayOutcome.Faulted, result.Outcome);
            Assert.Equal(new[] { "kd:1E", "ku:1E", "ku:1E" }, _sink.Calls);
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void ThrowingLoggerCannotBreakTheRelease()
        {
            _sink.ThrowOn = call => call == "kd:30";
            var player = new MacroPlayer(_sink, _delay, null, _ => throw new InvalidOperationException("log failed"));

            var result = player.Play(Of(KeyDown(0x1E), KeyDown(0x30)), CancellationToken.None);

            Assert.Equal(PlayOutcome.Faulted, result.Outcome);
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void CountRepeatRunsExactlyThatManyTimes()
        {
            var macro = Of(TapKey(0x1E, holdMs: 20));
            macro.Repeat = new MacroRepeat { Mode = MacroRepeatMode.Count, Count = 5 };

            var result = Play(macro);

            Assert.Equal(PlayOutcome.Completed, result.Outcome);
            Assert.Equal(5, _sink.Calls.Count(c => c == "kd:1E"));
            Assert.Equal(5, _sink.Calls.Count(c => c == "ku:1E"));
            Assert.Equal(Ms(100), _delay.Time);
        }

        [Fact]
        public void OnceRunsOnce()
        {
            Play(Of(TapKey(0x1E)));

            Assert.Equal(1, _sink.Calls.Count(c => c == "kd:1E"));
        }

        [Fact]
        public void UntilStoppedRunsUntilCancelled()
        {
            var macro = Of(TapKey(0x1E), Wait(100));
            macro.Repeat = new MacroRepeat { Mode = MacroRepeatMode.UntilStopped };

            var result = Play(macro, cancelAt: Ms(1050));

            Assert.Equal(PlayOutcome.Cancelled, result.Outcome);
            Assert.Equal(11, _sink.Calls.Count(c => c == "kd:1E"));
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void EmptyUntilStoppedMacroIsPacedAtTenMilliseconds()
        {
            var macro = Of();
            macro.Repeat = new MacroRepeat { Mode = MacroRepeatMode.UntilStopped };

            var result = Play(macro, cancelAt: Ms(55));

            Assert.Equal(PlayOutcome.Cancelled, result.Outcome);
            Assert.All(_delay.Waits, w => Assert.Equal(Ms(10), w));
            Assert.InRange(_delay.Waits.Count, 5, 6);
        }

        [Fact]
        public void InstantMacroUntilStoppedIsPacedToo()
        {
            var macro = Of(TapKey(0x1E));
            macro.Repeat = new MacroRepeat { Mode = MacroRepeatMode.UntilStopped };

            Play(macro, cancelAt: Ms(35));

            // Iterations start at 0, 10, 20 and 30 ms.
            Assert.Equal(4, _sink.Calls.Count(c => c == "kd:1E"));
        }

        [Fact]
        public void FloorOnlyPadsTheShortfall()
        {
            var macro = Of(Wait(4));
            macro.Repeat = new MacroRepeat { Mode = MacroRepeatMode.Count, Count = 3 };

            Play(macro);

            Assert.Equal(Ms(24), _delay.Time); // 4 + 6 + 4 + 6 + 4
        }

        [Fact]
        public void SpeedScalesEveryDelayHoldAndWait()
        {
            var macro = Of(TapKey(0x1E, holdMs: 100, delayMs: 200), Wait(1000), Camera(10, 0, 300));

            macro.Speed = 1;
            Play(macro);
            Assert.Equal(Ms(1600), _delay.Time);

            _delay.Time = TimeSpan.Zero;
            macro.Speed = 2;
            Play(macro);
            Assert.Equal(Ms(800), _delay.Time);

            _delay.Time = TimeSpan.Zero;
            macro.Speed = 0.5;
            Play(macro);
            Assert.Equal(Ms(3200), _delay.Time);
        }

        [Fact]
        public void DisabledActionsAreSkippedWithTheirDelays()
        {
            var skipped = TapKey(0x1F, holdMs: 500, delayMs: 700);
            skipped.Enabled = false;

            Play(Of(TapKey(0x1E), skipped, TapKey(0x20)));

            Assert.Equal(new[] { "kd:1E", "ku:1E", "kd:20", "ku:20" }, _sink.Calls);
            Assert.Equal(TimeSpan.Zero, _delay.Time);
        }

        [Fact]
        public void GroupRepeatsItsChildren()
        {
            Play(Of(Group(3, TapKey(0x1E), TapKey(0x1F)), TapKey(0x20)));

            Assert.Equal(
                new[] { "kd:1E", "ku:1E", "kd:1F", "ku:1F", "kd:1E", "ku:1E", "kd:1F", "ku:1F", "kd:1E", "ku:1E", "kd:1F", "ku:1F", "kd:20", "ku:20" },
                _sink.Calls);
        }

        [Fact]
        public void InstantGroupIterationsAreSpacedOneMillisecondApart()
        {
            Play(Of(Group(4, TapKey(0x1E))));

            Assert.Equal(Ms(3), _delay.Time);
        }

        [Fact]
        public void GroupWithOnlyDisabledOrEmptyChildrenDoesNothing()
        {
            var off = TapKey(0x1E);
            off.Enabled = false;

            Play(Of(Group(10000, off), Group(10000, Group(10000))));

            Assert.Empty(_sink.Calls);
            Assert.Equal(TimeSpan.Zero, _delay.Time);
        }

        [Fact]
        public void NestedGroupsMultiply()
        {
            Play(Of(Group(2, Group(3, TapKey(0x1E)))));

            Assert.Equal(6, _sink.Calls.Count(c => c == "kd:1E"));
        }

        [Fact]
        public void CameraSendsTheExactTotalAndHoldsTheRightButtonAroundIt()
        {
            var result = Play(Of(Camera(100, -40, 100, holdRight: true)));

            Assert.Equal(PlayOutcome.Completed, result.Outcome);
            Assert.Equal("md:Right", _sink.Calls[0]);
            Assert.Equal("mu:Right", _sink.Calls[^1]);
            Assert.Equal((100, -40), _sink.RelativeTotal());
            Assert.Equal(Ms(100), _delay.Time);
        }

        [Fact]
        public void CameraWithoutRightButtonDoesNotTouchIt()
        {
            Play(Of(Camera(30, 0, 50)));

            Assert.DoesNotContain(_sink.Calls, c => c.StartsWith("md:") || c.StartsWith("mu:"));
        }

        [Fact]
        public void CameraLeavesAnAlreadyHeldRightButtonAlone()
        {
            Play(Of(MouseDown(MouseButton.Right), Camera(30, 0, 20, holdRight: true), Wait(10)));

            Assert.Equal(1, _sink.Calls.Count(c => c == "md:Right"));
            Assert.Equal(1, _sink.Calls.Count(c => c == "mu:Right"));
            Assert.Equal("mu:Right", _sink.Calls[^1]);
        }

        [Fact]
        public void CameraWithNoMovementJustWaits()
        {
            Play(Of(Camera(0, 0, 250, holdRight: true)));

            Assert.Empty(_sink.Calls);
            Assert.Equal(Ms(250), _delay.Time);
        }

        [Fact]
        public void LateWakeUpBatchesDueCameraStepsIntoOneCall()
        {
            // A delay that always oversleeps by 10 ms: steps due in the meantime must go out together.
            var slow = new OversleepingDelay(Ms(10));
            var player = new MacroPlayer(_sink, slow, null, null);

            player.Play(Of(Camera(100, 0, 100)), CancellationToken.None);

            Assert.Equal(100, _sink.RelativeTotal().Dx);
            Assert.True(_sink.Calls.Count <= 12);
        }

        [Fact]
        public void FocusLossReleasesAndResumesByRepressing()
        {
            _gate.Allowed = () => !(_delay.Time >= Ms(100) && _delay.Time < Ms(300));

            var macro = Of(KeyDown(0x1E), Wait(500), KeyUp(0x1E));
            macro.FocusRule = MacroFocusRule.RobloxOnly;

            var result = Play(macro, withGate: true);

            Assert.Equal(PlayOutcome.Completed, result.Outcome);
            Assert.Equal(new[] { "kd:1E", "ku:1E", "kd:1E", "ku:1E" }, _sink.Calls);
            Assert.Equal(new[] { true, false }, _paused);
            Assert.Equal(Ms(700), _delay.Time); // 500 ms of waiting plus 200 ms paused
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void NothingIsSentWhileFocusIsMissing()
        {
            bool focused = false;
            _gate.Allowed = () => focused;
            _delay.OnAdvance = t => focused = t >= Ms(120);

            var macro = Of(TapKey(0x1E));
            macro.FocusRule = MacroFocusRule.RobloxOnly;

            Play(macro, withGate: true);

            Assert.Equal(new[] { "kd:1E", "ku:1E" }, _sink.Calls);
            Assert.True(_delay.Time >= Ms(120));
            Assert.Equal(new[] { true, false }, _paused);
        }

        [Fact]
        public void CancelWhilePausedDoesNotRepressAndReportsResumed()
        {
            _gate.Allowed = () => _delay.Time < Ms(100);

            var macro = Of(KeyDown(0x1E), Wait(1000));
            macro.FocusRule = MacroFocusRule.RobloxOnly;

            var result = Play(macro, cancelAt: Ms(160), withGate: true);

            Assert.Equal(PlayOutcome.Cancelled, result.Outcome);
            Assert.Equal(new[] { "kd:1E", "ku:1E" }, _sink.Calls);
            Assert.Equal(new[] { true, false }, _paused);
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void AnyWindowIgnoresTheGate()
        {
            _gate.Allowed = () => false;

            var result = Play(Of(TapKey(0x1E)), withGate: true);

            Assert.Equal(PlayOutcome.Completed, result.Outcome);
            Assert.Empty(_paused);
        }

        [Fact]
        public void ThrowingGateCountsAsNotAllowed()
        {
            int calls = 0;
            _gate.Allowed = () => ++calls > 3 ? true : throw new InvalidOperationException("gate down");

            var macro = Of(TapKey(0x1E));
            macro.FocusRule = MacroFocusRule.RobloxOnly;

            var result = Play(macro, withGate: true);

            Assert.Equal(PlayOutcome.Completed, result.Outcome);
            Assert.Equal(new[] { true, false }, _paused);
            Assert.Single(_logs, l => l.Contains("Focus check failed"));
        }

        [Fact]
        public void InvalidMacroIsNotPlayed()
        {
            var bad = Of(new MacroAction { Kind = MacroActionKind.Key, ScanCode = 0 });

            var result = Play(bad);

            Assert.Equal(PlayOutcome.Faulted, result.Outcome);
            Assert.Empty(_sink.Calls);
        }

        [Fact]
        public void SecondConcurrentPlayIsRefusedAndDoesNotDisturbTheFirst()
        {
            PlayResult inner = default;
            MacroPlayer? player = null;
            player = NewPlayer();
            _sink.OnCall = call =>
            {
                if (call == "kd:1E")
                    inner = player!.Play(Of(TapKey(0x30)), CancellationToken.None);
            };

            var result = player.Play(Of(KeyDown(0x1E)), CancellationToken.None);

            Assert.Equal(PlayOutcome.Faulted, inner.Outcome);
            Assert.Equal(PlayOutcome.Completed, result.Outcome);
            Assert.DoesNotContain("kd:30", _sink.Calls);
            Assert.False(player.IsPlaying);
        }

        [Fact]
        public void PlayerCanBeReusedAfterACancelledRun()
        {
            var player = NewPlayer();
            using (var cts = new CancellationTokenSource())
            {
                _delay.Cts = cts;
                _delay.CancelAt = Ms(10);
                Assert.Equal(PlayOutcome.Cancelled, player.Play(Of(KeyDown(0x1E), Wait(100)), cts.Token).Outcome);
            }

            _sink.Calls.Clear();
            _delay.CancelAt = null;

            Assert.Equal(PlayOutcome.Completed, player.Play(Of(TapKey(0x1F)), CancellationToken.None).Outcome);
            Assert.Equal(new[] { "kd:1F", "ku:1F" }, _sink.Calls);
        }

        [Fact]
        public void ReleaseHeldFromAnotherThreadSendsUpForEverything()
        {
            var player = NewPlayer();
            int failures = -1;
            _sink.OnCall = call =>
            {
                if (call == "kd:1F")
                {
                    _sink.OnCall = null;
                    failures = player.ReleaseHeld();
                }
            };

            player.Play(Of(KeyDown(0x1E), KeyDown(0x1F)), CancellationToken.None);

            Assert.Equal(0, failures);
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void PlayedTimeMatchesTheEstimate()
        {
            var macro = Of(
                TapKey(0x1E, holdMs: 30, delayMs: 15),
                Group(3, Wait(7), TapKey(0x1F, holdMs: 2)),
                Group(5, TapKey(0x20)),
                Camera(50, 50, 120, easing: MacroEasing.EaseInOut),
                Wait(33));
            macro.Speed = 1.5;
            macro.Repeat = new MacroRepeat { Mode = MacroRepeatMode.Count, Count = 4 };

            Play(macro);

            Assert.Equal(MacroTiming.EstimateDurationMs(macro), (long)_delay.Time.TotalMilliseconds);
        }

        [Fact]
        public void CancelLatencyWithTheRealDelayIsShort()
        {
            using var delay = new PrecisionDelay();
            var sink = new RecordingSink();
            var player = new MacroPlayer(sink, delay, null, null);
            var macro = Of(KeyDown(0x1E), MouseDown(MouseButton.Left), Wait(5000));
            macro.Repeat = new MacroRepeat { Mode = MacroRepeatMode.UntilStopped };

            var latencies = new List<double>();

            for (int run = 0; run < 5; run++)
            {
                sink.Calls.Clear();
                using var cts = new CancellationTokenSource();
                PlayResult result = default;
                var started = new ManualResetEventSlim();
                sink.OnCall = call => { if (call == "md:Left") started.Set(); };

                var thread = new Thread(() => result = player.Play(macro, cts.Token)) { IsBackground = true };
                thread.Start();

                Assert.True(started.Wait(2000));
                Thread.Sleep(100);

                var watch = Stopwatch.StartNew();
                cts.Cancel();
                Assert.True(thread.Join(2000));
                watch.Stop();

                latencies.Add(watch.Elapsed.TotalMilliseconds);

                Assert.Equal(PlayOutcome.Cancelled, result.Outcome);
                Assert.Empty(sink.Down);
            }

            _output.WriteLine($"CANCEL-LATENCY-MS: {string.Join(", ", latencies.Select(l => l.ToString("F2")))} (highRes={delay.IsHighResolution})");
            Assert.True(latencies.Max() < 100, "cancel took " + latencies.Max() + " ms");
        }

        // Always wakes late by a fixed amount, as a coarse system timer would.
        private sealed class OversleepingDelay : IDelay
        {
            private readonly TimeSpan _extra;
            private TimeSpan _time;

            public OversleepingDelay(TimeSpan extra) { _extra = extra; }

            public TimeSpan Now => _time;

            public void Wait(TimeSpan duration, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                _time += duration + _extra;
            }
        }
    }
}
