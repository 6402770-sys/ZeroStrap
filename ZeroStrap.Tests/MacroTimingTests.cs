using System;
using System.Linq;

using Xunit;

using ZeroStrap.Macros;

using static ZeroStrap.Tests.MacroBuilders;

namespace ZeroStrap.Tests
{
    public sealed class MacroTimingTests
    {
        private static (int X, int Y) Sum(System.Collections.Generic.IReadOnlyList<(int AtMs, int Dx, int Dy)> steps) =>
            (steps.Sum(s => s.Dx), steps.Sum(s => s.Dy));

        [Theory]
        [InlineData(100, 50, 100)]
        [InlineData(-100, 50, 100)]
        [InlineData(100, -50, 37)]
        [InlineData(-7, -3, 1)]
        [InlineData(1, 0, 1)]
        [InlineData(0, 1, 1)]
        [InlineData(1, 1, 1000)]
        [InlineData(3, 0, 1000)]
        [InlineData(20000, -20000, 600000)]
        [InlineData(20000, 20000, 1)]
        [InlineData(20000, 20000, 0)]
        [InlineData(5, 5, -20)]
        public void CameraStepsSumToTheExactTotal(int dx, int dy, int ms)
        {
            foreach (var easing in new[] { MacroEasing.Linear, MacroEasing.EaseInOut })
            {
                var steps = MacroTiming.CameraSteps(dx, dy, ms, easing);

                Assert.Equal((dx, dy), Sum(steps));
                Assert.All(steps, s => Assert.True(s.Dx != 0 || s.Dy != 0));
                Assert.All(steps, s => Assert.InRange(s.AtMs, 0, Math.Max(1, ms)));
            }
        }

        [Fact]
        public void CameraStepsSumExactlyForManyRandomMoves()
        {
            var rng = new Random(12345);

            for (int i = 0; i < 300; i++)
            {
                int dx = rng.Next(-5000, 5001);
                int dy = rng.Next(-5000, 5001);
                int ms = rng.Next(0, 3000);
                var easing = i % 2 == 0 ? MacroEasing.Linear : MacroEasing.EaseInOut;

                Assert.Equal((dx, dy), Sum(MacroTiming.CameraSteps(dx, dy, ms, easing)));
            }
        }

        [Fact]
        public void ZeroMoveHasNoSteps()
        {
            Assert.Empty(MacroTiming.CameraSteps(0, 0, 500, MacroEasing.Linear));
        }

        [Fact]
        public void OneMillisecondMoveIsASingleStepAtOne()
        {
            var steps = MacroTiming.CameraSteps(40, -9, 1, MacroEasing.Linear);

            Assert.Equal(new[] { (1, 40, -9) }, steps);
        }

        [Fact]
        public void ZeroDurationMovesEverythingAtOnce()
        {
            var steps = MacroTiming.CameraSteps(40, -9, 0, MacroEasing.EaseInOut);

            Assert.Equal(new[] { (0, 40, -9) }, steps);
        }

        [Fact]
        public void LinearStepsAreMonotoneAndTimedOnTheDuration()
        {
            var steps = MacroTiming.CameraSteps(-250, 125, 400, MacroEasing.Linear);

            Assert.All(steps, s => Assert.True(s.Dx <= 0 && s.Dy >= 0));
            Assert.True(steps.Zip(steps.Skip(1), (a, b) => a.AtMs < b.AtMs).All(x => x));
            Assert.Equal(400, steps[^1].AtMs);

            // Linear: roughly constant velocity, so no step is more than 1 px away from the average.
            Assert.All(steps, s => Assert.InRange(Math.Abs(s.Dx), 0, 1));
        }

        [Fact]
        public void EaseInOutStartsSlowEndsSlowAndHitsTheMiddleOnTime()
        {
            var steps = MacroTiming.CameraSteps(1000, 0, 1000, MacroEasing.EaseInOut);

            int upToMiddle = steps.Where(s => s.AtMs <= 500).Sum(s => s.Dx);
            int firstQuarter = steps.Where(s => s.AtMs <= 250).Sum(s => s.Dx);
            int lastQuarter = steps.Where(s => s.AtMs > 750).Sum(s => s.Dx);

            Assert.Equal(500, upToMiddle);
            Assert.True(firstQuarter < 250);
            Assert.True(lastQuarter < 250);
            Assert.Equal(1000, steps.Sum(s => s.Dx));
            Assert.InRange(steps[^1].AtMs, 900, 1000); // trailing zero-delta steps are skipped
            Assert.All(steps, s => Assert.True(s.Dx >= 0));
        }

        [Fact]
        public void SpeedAndDurationRoundTrip()
        {
            Assert.Equal(500.0, MacroTiming.SpeedFromDuration(300, 400, 1000), 6);
            Assert.Equal(1000, MacroTiming.DurationFromSpeed(300, 400, 500.0));

            foreach (var (dx, dy, ms) in new[] { (100, 0, 250), (-640, 480, 1200), (7, 9, 33), (20000, 0, 600000) })
            {
                double speed = MacroTiming.SpeedFromDuration(dx, dy, ms);
                int back = MacroTiming.DurationFromSpeed(dx, dy, speed);

                Assert.InRange(back, ms - 1, ms + 1);
            }
        }

        [Fact]
        public void ZeroDistanceHasNoSpeedAndKeepsTheDuration()
        {
            Assert.Equal(0.0, MacroTiming.SpeedFromDuration(0, 0, 500));
            Assert.Equal(500, MacroTiming.DurationFromSpeed(0, 0, 300.0, 500));
            Assert.Equal(500, MacroTiming.DurationFromSpeed(10, 10, 0, 500));
        }

        [Fact]
        public void DurationFromSpeedHasAOneMillisecondMinimum()
        {
            Assert.Equal(1, MacroTiming.DurationFromSpeed(1, 0, 1_000_000));
        }

        [Theory]
        [InlineData(1000, 1.0, 1000)]
        [InlineData(1000, 2.0, 500)]
        [InlineData(1000, 0.5, 2000)]
        [InlineData(3, 2.0, 2)]
        [InlineData(0, 4.0, 0)]
        [InlineData(-5, 1.0, 0)]
        public void ScaledDividesBySpeedAndRounds(long ms, double speed, long expected)
        {
            Assert.Equal(expected, MacroTiming.Scaled(ms, speed));
        }

        [Fact]
        public void EstimateAddsDelaysHoldsWaitsAndCameras()
        {
            var m = Of(TapKey(0x1E, holdMs: 50, delayMs: 100), Wait(200), Camera(10, 10, 300), KeyDown(0x1F, 7));

            Assert.Equal(100 + 50 + 200 + 300 + 7, MacroTiming.EstimateDurationMs(m));

            m.Speed = 2;
            Assert.Equal(50 + 25 + 100 + 150 + 4, MacroTiming.EstimateDurationMs(m));
        }

        [Fact]
        public void EstimateSkipsDisabledActions()
        {
            var off = Wait(1000);
            off.Enabled = false;

            Assert.Equal(5, MacroTiming.EstimateDurationMs(Of(off, Wait(5))));
        }

        [Fact]
        public void EstimateExpandsCountAndGroupRepeats()
        {
            var m = Of(Group(4, Wait(10)));
            Assert.Equal(40, MacroTiming.EstimateDurationMs(m));

            m.Repeat = new MacroRepeat { Mode = MacroRepeatMode.Count, Count = 3 };
            Assert.Equal(120, MacroTiming.EstimateDurationMs(m));
        }

        [Fact]
        public void EstimateAppliesTheIterationFloors()
        {
            var empty = Of();
            empty.Repeat = new MacroRepeat { Mode = MacroRepeatMode.Count, Count = 3 };
            Assert.Equal(20, MacroTiming.EstimateDurationMs(empty));

            empty.Repeat = new MacroRepeat { Mode = MacroRepeatMode.UntilStopped };
            Assert.Equal(10, MacroTiming.EstimateDurationMs(empty));

            Assert.Equal(2, MacroTiming.EstimateDurationMs(Of(Group(3, TapKey(0x1E)))));
            Assert.Equal(0, MacroTiming.EstimateDurationMs(Of(Group(1000, Wait(0)).Also(g => g.Actions[0].Enabled = false))));
        }

        [Fact]
        public void EstimateSaturatesInsteadOfOverflowing()
        {
            var inner = Group(10000, Wait(600000));
            var m = Of(Group(10000, Group(10000, Group(10000, inner))));
            m.Repeat = new MacroRepeat { Mode = MacroRepeatMode.Count, Count = 100000 };

            Assert.Equal(long.MaxValue, MacroTiming.EstimateDurationMs(m));
        }
    }

    internal static class MacroTestExtensions
    {
        public static T Also<T>(this T value, Action<T> action)
        {
            action(value);
            return value;
        }
    }
}
