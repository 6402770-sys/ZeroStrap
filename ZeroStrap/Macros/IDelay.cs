using Microsoft.Win32.SafeHandles;

using static ZeroStrap.Macros.MacroNative;

namespace ZeroStrap.Macros
{
    /// <summary>Time source and cancellable wait used by the player, so tests can run on a virtual clock.</summary>
    public interface IDelay
    {
        /// <summary>Monotonic time since an arbitrary origin; advances by at least the time spent in <see cref="Wait"/>.</summary>
        TimeSpan Now { get; }

        /// <summary>Blocks for <paramref name="duration"/>, returning early by throwing <see cref="OperationCanceledException"/> on cancel.</summary>
        void Wait(TimeSpan duration, CancellationToken ct);
    }

    /// <summary>
    /// Real delay: waits in slices of at most 10 ms (checking the token between slices, so cancel is prompt) on a
    /// high-resolution waitable timer, falling back to a normal waitable timer and then to Thread.Sleep. When the
    /// high-resolution timer is unavailable the system timer period is raised to 1 ms and the last ~2 ms are spun.
    /// One thread should wait at a time; concurrent waits are serialised.
    /// </summary>
    public sealed class PrecisionDelay : IDelay, IDisposable
    {
        private const int SliceMs = 10;
        private const double SpinThresholdMs = 2.0;

        private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

        private readonly object _gate = new();
        private SafeWaitHandle? _timer;
        private readonly bool _highResolution;
        private bool _periodRaised;
        private bool _disposed;

        /// <summary>True when the high-resolution waitable timer is in use.</summary>
        public bool IsHighResolution => _highResolution;

        public PrecisionDelay()
        {
            _timer = TryCreateTimer(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION);

            if (_timer is not null)
            {
                _highResolution = true;
                return;
            }

            _timer = TryCreateTimer(0);

            try
            {
                _periodRaised = timeBeginPeriod(1) == 0;
            }
            catch
            {
                _periodRaised = false;
            }
        }

        private static SafeWaitHandle? TryCreateTimer(uint flags)
        {
            try
            {
                var handle = CreateWaitableTimerExW(IntPtr.Zero, null, flags, TIMER_ALL_ACCESS);

                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return null;
                }

                return handle;
            }
            catch
            {
                return null;
            }
        }

        public TimeSpan Now => TimeSpan.FromTicks((long)(Stopwatch.GetTimestamp() * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));

        public void Wait(TimeSpan duration, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (duration <= TimeSpan.Zero)
                return;

            long deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalMilliseconds * TicksPerMs);

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                double remainingMs = (deadline - Stopwatch.GetTimestamp()) / TicksPerMs;

                if (remainingMs <= 0)
                    return;

                if (!_highResolution && remainingMs <= SpinThresholdMs)
                {
                    Thread.SpinWait(40);
                    continue;
                }

                SleepSlice(Math.Min(remainingMs, SliceMs));
            }
        }

        private void SleepSlice(double ms)
        {
            lock (_gate)
            {
                if (_timer is { IsClosed: false } timer)
                {
                    long due = -(long)Math.Max(1, ms * 10000.0); // negative = relative, in 100 ns units

                    if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        WaitForSingleObject(timer, (uint)(Math.Ceiling(ms) + 20));
                        return;
                    }
                }
            }

            Thread.Sleep(Math.Max(1, (int)ms));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _timer?.Dispose();
                _timer = null;

                if (_periodRaised)
                {
                    try { timeEndPeriod(1); } catch { }
                    _periodRaised = false;
                }
            }
        }
    }
}
