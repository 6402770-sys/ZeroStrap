using System.Collections.Generic;
using System.Linq;

using Xunit;

using ZeroStrap.Macros;

namespace ZeroStrap.Tests
{
    public sealed class RecordingCompilerTests
    {
        private const ushort W = 0x11;
        private const ushort A = 0x1E;

        private static RawRecordedEvent KeyDown(double t, ushort scan, bool ext = false) => new(RawEventKind.KeyDown, t, scan, ext);
        private static RawRecordedEvent KeyUp(double t, ushort scan, bool ext = false) => new(RawEventKind.KeyUp, t, scan, ext);
        private static RawRecordedEvent BtnDown(double t, MouseButton b, int x = 0, int y = 0) => new(RawEventKind.MouseDown, t, Button: b, X: x, Y: y);
        private static RawRecordedEvent BtnUp(double t, MouseButton b) => new(RawEventKind.MouseUp, t, Button: b);
        private static RawRecordedEvent Move(double t, int dx, int dy) => new(RawEventKind.MouseDelta, t, X: dx, Y: dy);

        private static List<MacroAction> Compile(params RawRecordedEvent[] events) => RecordingCompiler.Compile(events, new RecorderOptions());

        [Fact]
        public void EmptyRecordingCompilesToNothing()
        {
            Assert.Empty(Compile());
        }

        [Fact]
        public void DownThenUpWithinTheGapBecomesATap()
        {
            var actions = Compile(KeyDown(100, W), KeyUp(180, W));

            var tap = Assert.Single(actions);
            Assert.Equal(MacroActionKind.Key, tap.Kind);
            Assert.Equal(MacroKeyMode.Tap, tap.KeyMode);
            Assert.Equal(W, tap.ScanCode);
            Assert.Equal(80, tap.HoldMs);
            Assert.Equal(0, tap.DelayBeforeMs); // the first action never waits
        }

        [Fact]
        public void ALongHoldStaysDownAndUp()
        {
            var actions = Compile(KeyDown(0, W), KeyUp(501, W));

            Assert.Equal(2, actions.Count);
            Assert.Equal(MacroKeyMode.Down, actions[0].KeyMode);
            Assert.Equal(MacroKeyMode.Up, actions[1].KeyMode);
            Assert.Equal(501, actions[1].DelayBeforeMs);
        }

        [Fact]
        public void ExactlyAtTheTapLimitIsStillATap()
        {
            var actions = Compile(KeyDown(0, W), KeyUp(500, W));

            Assert.Equal(MacroKeyMode.Tap, Assert.Single(actions).KeyMode);
        }

        [Fact]
        public void AnotherEventInBetweenPreventsTheTap()
        {
            var actions = Compile(KeyDown(0, W), KeyDown(20, A), KeyUp(40, W), KeyUp(60, A));

            Assert.Equal(4, actions.Count);
            Assert.All(actions, a => Assert.NotEqual(MacroKeyMode.Tap, a.KeyMode));
        }

        [Fact]
        public void DelayIsTheGapSinceThePreviousActionEnded()
        {
            var actions = Compile(KeyDown(100, W), KeyUp(150, W), KeyDown(400, A), KeyUp(450, A));

            Assert.Equal(2, actions.Count);
            Assert.Equal(0, actions[0].DelayBeforeMs);
            Assert.Equal(250, actions[1].DelayBeforeMs); // 400 - end of first tap (150)
            Assert.Equal(50, actions[1].HoldMs);
        }

        [Fact]
        public void AutoRepeatKeyDownsAreDropped()
        {
            var actions = Compile(KeyDown(0, W), KeyDown(30, W), KeyDown(60, W), KeyDown(90, W), KeyUp(900, W));

            Assert.Equal(2, actions.Count);
            Assert.Equal(MacroKeyMode.Down, actions[0].KeyMode);
            Assert.Equal(MacroKeyMode.Up, actions[1].KeyMode);
            Assert.Equal(900, actions[1].DelayBeforeMs);
        }

        [Fact]
        public void UnmatchedUpsAtTheStartAreDropped()
        {
            var actions = Compile(KeyUp(0, W), BtnUp(5, MouseButton.Left), KeyDown(100, A), KeyUp(130, A));

            var tap = Assert.Single(actions);
            Assert.Equal(A, tap.ScanCode);
        }

        [Fact]
        public void KeysStillDownAtTheEndGetAnUpAppended()
        {
            var actions = Compile(KeyDown(0, W), BtnDown(10, MouseButton.Right));

            Assert.Equal(4, actions.Count);
            Assert.Equal(MacroKeyMode.Down, actions[0].KeyMode);
            Assert.Equal(MacroButtonMode.Down, actions[1].ButtonMode);
            Assert.Equal(MacroKeyMode.Up, actions[2].KeyMode);
            Assert.Equal(W, actions[2].ScanCode);
            Assert.Equal(MacroButtonMode.Up, actions[3].ButtonMode);
            Assert.Equal(MouseButton.Right, actions[3].Button);
            Assert.All(actions.Skip(2), a => Assert.Equal(0, a.DelayBeforeMs));
        }

        [Fact]
        public void AQuickKeyHeldAtTheVeryEndIsNotFoldedIntoATap()
        {
            // The appended Up is synthetic, so the user's "still holding W" intent is kept as Down + Up.
            var actions = Compile(KeyDown(0, W));

            Assert.Equal(2, actions.Count);
            Assert.Equal(MacroKeyMode.Down, actions[0].KeyMode);
            Assert.Equal(MacroKeyMode.Up, actions[1].KeyMode);
        }

        [Fact]
        public void ExtendedFlagDistinguishesKeys()
        {
            var actions = Compile(KeyDown(0, 0x4D, ext: true), KeyDown(5, 0x4D, ext: false), KeyUp(20, 0x4D, ext: true), KeyUp(25, 0x4D, ext: false));

            Assert.Equal(4, actions.Count);
            Assert.True(actions[0].Extended);
            Assert.False(actions[1].Extended);
        }

        [Fact]
        public void MouseButtonsBecomeClicks()
        {
            var actions = Compile(BtnDown(0, MouseButton.Left), BtnUp(60, MouseButton.Left));

            var click = Assert.Single(actions);
            Assert.Equal(MacroActionKind.Mouse, click.Kind);
            Assert.Equal(MacroButtonMode.Click, click.ButtonMode);
            Assert.Equal(MouseButton.Left, click.Button);
            Assert.Equal(60, click.HoldMs);
        }

        [Fact]
        public void RightButtonStaysAMouseActionAndCameraDoesNotHoldIt()
        {
            var actions = Compile(BtnDown(0, MouseButton.Right), Move(10, 30, 4), Move(30, 20, 1), BtnUp(800, MouseButton.Right));

            Assert.Equal(3, actions.Count);
            Assert.Equal(MacroButtonMode.Down, actions[0].ButtonMode);
            Assert.Equal(MacroActionKind.Camera, actions[1].Kind);
            Assert.False(actions[1].HoldRightButton);
            Assert.Equal(MacroButtonMode.Up, actions[2].ButtonMode);
        }

        [Fact]
        public void MovementBurstsAreSummedIntoOneCameraAction()
        {
            var actions = Compile(Move(1000, 5, -2), Move(1010, 6, -3), Move(1030, 4, 0), Move(1060, -1, 1));

            var camera = Assert.Single(actions);
            Assert.Equal(MacroActionKind.Camera, camera.Kind);
            Assert.Equal(14, camera.Dx);
            Assert.Equal(-4, camera.Dy);
            Assert.Equal(60, camera.DurationMs); // first to last delta
            Assert.Equal(MacroEasing.Linear, camera.Easing);
            Assert.Equal(0, camera.DelayBeforeMs);
        }

        [Fact]
        public void AGapOverFiftyMsSplitsTheBurst()
        {
            var actions = Compile(Move(0, 5, 0), Move(20, 5, 0), Move(71, 3, 0), Move(80, 3, 0));

            Assert.Equal(2, actions.Count);
            Assert.Equal(10, actions[0].Dx);
            Assert.Equal(6, actions[1].Dx);
            Assert.Equal(51, actions[1].DelayBeforeMs); // 71 - end of the first burst (20)
        }

        [Fact]
        public void AGapOfExactlyFiftyMsDoesNotSplit()
        {
            var actions = Compile(Move(0, 1, 0), Move(50, 1, 0));

            Assert.Single(actions);
        }

        [Fact]
        public void ASingleDeltaHasAtLeastOneMsDuration()
        {
            var camera = Assert.Single(Compile(Move(10, 7, 0)));

            Assert.Equal(1, camera.DurationMs);
        }

        [Fact]
        public void KeyEventsSplitABurstSoOrderIsKept()
        {
            var actions = Compile(Move(0, 5, 0), KeyDown(10, W), KeyUp(15, W), Move(20, 5, 0));

            Assert.Equal(3, actions.Count);
            Assert.Equal(MacroActionKind.Camera, actions[0].Kind);
            Assert.Equal(MacroActionKind.Key, actions[1].Kind);
            Assert.Equal(MacroActionKind.Camera, actions[2].Kind);
        }

        [Fact]
        public void ABurstThatNetsToNothingIsDropped()
        {
            var actions = Compile(Move(0, 5, 3), Move(10, -5, -3), KeyDown(200, W), KeyUp(220, W));

            var tap = Assert.Single(actions);
            Assert.Equal(MacroActionKind.Key, tap.Kind);
        }

        [Fact]
        public void ZeroDeltasAreIgnored()
        {
            Assert.Empty(Compile(Move(0, 0, 0), Move(5, 0, 0)));
        }

        [Fact]
        public void HugeBurstsAreSplitToStayWithinTheCameraLimit()
        {
            var events = Enumerable.Range(0, 5).Select(i => Move(i * 10, 9000, 0)).ToArray();
            var actions = Compile(events);

            Assert.True(actions.Count >= 3);
            Assert.All(actions, a => Assert.InRange(a.Dx, -20000, 20000));
            Assert.Equal(45000, actions.Sum(a => a.Dx));
            Assert.Empty(MacroValidator.Validate(Wrap(actions)));
        }

        [Fact]
        public void AbsolutePositionsAreRecordedOnlyWhenAsked()
        {
            var events = new[] { BtnDown(0, MouseButton.Left, 640, 360), BtnUp(40, MouseButton.Left) };

            Assert.Equal(MacroActionKind.Mouse, Assert.Single(RecordingCompiler.Compile(events, new RecorderOptions())).Kind);

            var withMoves = RecordingCompiler.Compile(events, new RecorderOptions { RecordAbsoluteMoves = true });

            Assert.Equal(2, withMoves.Count);
            Assert.Equal(MacroActionKind.MouseMoveTo, withMoves[0].Kind);
            Assert.Equal(640, withMoves[0].X);
            Assert.Equal(360, withMoves[0].Y);
            Assert.Equal(MacroButtonMode.Click, withMoves[1].ButtonMode);
            Assert.Equal(0, withMoves[1].DelayBeforeMs);
        }

        [Fact]
        public void RepeatedPositionIsNotRecordedTwice()
        {
            var events = new[]
            {
                BtnDown(0, MouseButton.Left, 10, 10), BtnUp(20, MouseButton.Left),
                BtnDown(300, MouseButton.Left, 10, 10), BtnUp(320, MouseButton.Left),
                BtnDown(600, MouseButton.Left, 99, 5), BtnUp(620, MouseButton.Left)
            };

            var actions = RecordingCompiler.Compile(events, new RecorderOptions { RecordAbsoluteMoves = true });

            Assert.Equal(2, actions.Count(a => a.Kind == MacroActionKind.MouseMoveTo));
            Assert.Equal(3, actions.Count(a => a.Kind == MacroActionKind.Mouse));
        }

        [Fact]
        public void UnsupportedScanCodesAreSkippedWithAWarning()
        {
            var actions = RecordingCompiler.Compile(
                new[] { KeyDown(0, 0), KeyUp(5, 0), KeyDown(10, 0xE1), KeyUp(15, 0xE1), KeyDown(20, A), KeyUp(30, A) },
                new RecorderOptions(),
                out var warnings);

            Assert.Equal(A, Assert.Single(actions).ScanCode);
            Assert.Single(warnings);
            Assert.Contains("4", warnings[0]);
        }

        [Fact]
        public void OutOfOrderTimestampsNeverProduceNegativeDelays()
        {
            var actions = Compile(KeyDown(100, W), KeyUp(90, W), KeyDown(80, A), KeyUp(200, A));

            Assert.All(actions, a => Assert.True(a.DelayBeforeMs >= 0 && a.HoldMs >= 0));
        }

        [Fact]
        public void LongPausesAreClampedToTheValidatorLimit()
        {
            var actions = Compile(KeyDown(0, W), KeyUp(10, W), KeyDown(5_000_000, A), KeyUp(5_000_010, A));

            Assert.Equal(600000, actions[1].DelayBeforeMs);
        }

        [Fact]
        public void VeryLongRecordingsAreCutOffAndStillEndReleased()
        {
            // Every iteration yields three items (tap, camera burst split by the key events), so 6000 of them overflow the limit.
            var events = new List<RawRecordedEvent> { KeyDown(0, W) };

            for (int i = 0; i < 6000; i++)
            {
                events.Add(KeyDown(10 + i * 4, A));
                events.Add(KeyUp(12 + i * 4, A));
                events.Add(Move(13 + i * 4, 1, 1));
            }

            var actions = RecordingCompiler.Compile(events, new RecorderOptions(), out var warnings);

            Assert.True(actions.Count <= 10000);
            Assert.Contains(warnings, w => w.Contains("too long"));
            Assert.Equal(MacroKeyMode.Up, actions[^1].KeyMode);
            Assert.Empty(MacroValidator.Validate(Wrap(actions)));

            var sink = new RecordingSink();
            Assert.Equal(PlayOutcome.Completed, new MacroPlayer(sink, new FakeDelay()).Play(Wrap(actions), default).Outcome);
            Assert.Empty(sink.Down);
        }

        [Fact]
        public void CompiledOutputPassesTheValidatorAndPlaysBack()
        {
            var actions = Compile(
                KeyDown(0, W), Move(50, 10, 2), Move(70, 10, 2), BtnDown(200, MouseButton.Right), Move(260, -20, 5),
                BtnUp(900, MouseButton.Right), KeyUp(1000, W), KeyDown(1100, A), KeyUp(1150, A));

            var macro = Wrap(actions);

            Assert.Empty(MacroValidator.Validate(macro));

            var sink = new RecordingSink();
            var result = new MacroPlayer(sink, new FakeDelay()).Play(macro, default);

            Assert.Equal(PlayOutcome.Completed, result.Outcome);
            Assert.Empty(sink.Down);
            Assert.Equal((0, 9), sink.RelativeTotal()); // 10+10-20 horizontally, 2+2+5 vertically
        }

        private static Macro Wrap(List<MacroAction> actions) => new() { Id = "rec", Name = "Recorded", Actions = actions };
    }
}
