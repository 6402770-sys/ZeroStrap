namespace ZeroStrap.Macros
{
    /// <summary>Pure timing maths shared by the player, the editor preview and the duration estimate.</summary>
    public static class MacroTiming
    {
        /// <summary>An iteration of a top-level macro that schedules less than this gets padded up to it (between iterations).</summary>
        public const int MinIterationMs = 10;

        /// <summary>An iteration of a group that schedules less than this gets padded up to it (between iterations).</summary>
        public const int MinGroupIterationMs = 1;

        /// <summary>
        /// Splits a camera move into ~1 ms steps. Step i (1..n, n = max(1, durationMs)) lands at the rounded position
        /// total * ease(i / n) and emits the delta from the previous rounded position, so the deltas sum to exactly
        /// (dx, dy). Zero steps are skipped. EaseInOut is smoothstep (3t^2 - 2t^3). A zero duration moves everything at AtMs 0.
        /// </summary>
        public static IReadOnlyList<(int AtMs, int Dx, int Dy)> CameraSteps(int dx, int dy, int durationMs, MacroEasing easing)
        {
            var steps = new List<(int AtMs, int Dx, int Dy)>();

            if (dx == 0 && dy == 0)
                return steps;

            int duration = Math.Max(0, durationMs);
            int n = Math.Max(1, duration);
            int prevX = 0, prevY = 0;

            for (int i = 1; i <= n; i++)
            {
                double t = i == n ? 1.0 : (double)i / n;
                double e = Ease(t, easing);

                int x = i == n ? dx : (int)Math.Round(dx * e, MidpointRounding.AwayFromZero);
                int y = i == n ? dy : (int)Math.Round(dy * e, MidpointRounding.AwayFromZero);

                int stepX = x - prevX;
                int stepY = y - prevY;

                if (stepX != 0 || stepY != 0)
                {
                    int at = (int)Math.Round((double)i * duration / n, MidpointRounding.AwayFromZero);
                    steps.Add((at, stepX, stepY));
                }

                prevX = x;
                prevY = y;
            }

            return steps;
        }

        private static double Ease(double t, MacroEasing easing) =>
            easing == MacroEasing.EaseInOut ? t * t * (3.0 - 2.0 * t) : t;

        /// <summary>Pixels per second for a camera move; 0 when there is no distance.</summary>
        public static double SpeedFromDuration(int dx, int dy, int durationMs)
        {
            double distance = Math.Sqrt((double)dx * dx + (double)dy * dy);

            if (distance == 0)
                return 0;

            return distance / (Math.Max(1, durationMs) / 1000.0);
        }

        /// <summary>
        /// Duration in ms (rounded, at least 1) that moves (dx, dy) at the given speed. With no distance or no usable speed
        /// the current duration is kept.
        /// </summary>
        public static int DurationFromSpeed(int dx, int dy, double pxPerSec, int currentDurationMs = 1)
        {
            double distance = Math.Sqrt((double)dx * dx + (double)dy * dy);

            if (distance == 0 || double.IsNaN(pxPerSec) || pxPerSec <= 0)
                return Math.Max(1, currentDurationMs);

            double ms = Math.Round(distance / pxPerSec * 1000.0, MidpointRounding.AwayFromZero);
            return (int)Math.Min(int.MaxValue, Math.Max(1, ms));
        }

        /// <summary>A delay, hold, wait or camera duration at the given playback speed: ms / speed, rounded.</summary>
        public static long Scaled(long ms, double speed)
        {
            if (ms <= 0)
                return 0;

            if (double.IsNaN(speed) || speed <= 0)
                speed = 1.0;

            return (long)Math.Round(ms / speed, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// True when running the actions would send input or take time. Groups made only of disabled or empty actions are
        /// skipped outright, so a hostile nest of empty groups cannot keep the player busy.
        /// </summary>
        internal static bool HasEffect(IEnumerable<MacroAction> actions) =>
            actions.Any(a => a.Enabled && (a.Kind != MacroActionKind.Group || HasEffect(a.Actions)));

        /// <summary>
        /// Estimated run time of one run: Count repeats are expanded, group repeats multiplied, disabled actions skipped, the
        /// per-iteration floors applied. UntilStopped is estimated as a single iteration. Saturates at long.MaxValue.
        /// </summary>
        public static long EstimateDurationMs(Macro macro)
        {
            double speed = macro.Speed;
            long pass = SumList(macro.Actions, speed);

            return macro.Repeat?.Mode switch
            {
                MacroRepeatMode.Count => Repeated(pass, Math.Max(1, macro.Repeat.Count), MinIterationMs),
                MacroRepeatMode.UntilStopped => Math.Max(pass, MinIterationMs),
                _ => pass
            };
        }

        private static long SumList(List<MacroAction> actions, double speed)
        {
            long total = 0;

            foreach (var a in actions)
            {
                if (!a.Enabled)
                    continue;

                total = SatAdd(total, Scaled(a.DelayBeforeMs, speed));
                total = SatAdd(total, a.Kind switch
                {
                    MacroActionKind.Key => a.KeyMode == MacroKeyMode.Tap ? Scaled(a.HoldMs, speed) : 0,
                    MacroActionKind.Mouse => a.ButtonMode == MacroButtonMode.Click ? Scaled(a.HoldMs, speed) : 0,
                    MacroActionKind.Camera => Scaled(a.DurationMs, speed),
                    MacroActionKind.Wait => Scaled(a.Ms, speed),
                    MacroActionKind.Group => HasEffect(a.Actions)
                        ? Repeated(SumList(a.Actions, speed), Math.Max(1, a.GroupRepeat), MinGroupIterationMs)
                        : 0,
                    _ => 0
                });
            }

            return total;
        }

        /// <summary>times * pass, where every iteration but the last is padded up to floor.</summary>
        private static long Repeated(long pass, long times, long floor)
        {
            long padded = Math.Max(pass, floor);
            return SatAdd(SatMul(padded, times - 1), pass);
        }

        private static long SatAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

        private static long SatMul(long a, long b)
        {
            if (a == 0 || b == 0)
                return 0;

            return a > long.MaxValue / b ? long.MaxValue : a * b;
        }
    }
}
