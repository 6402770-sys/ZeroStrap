namespace ZeroStrap.Macros
{
    /// <summary>
    /// Cross-process ownership of macro shortcuts and playback. Exactly one ZeroStrap process may run a <see cref="MacroRuntime"/>:
    /// the settings window while Macro Studio is open, otherwise the background macro host (<c>-macrohost</c>). Without it a
    /// game shortcut would fire in both processes and two players would fight over the same keys.
    /// The lock is a named mutex held by a dedicated thread (mutexes belong to the thread that took them, and that must not be
    /// a UI thread that later has to wait), plus a named event that asks the background host to let go.
    /// </summary>
    public sealed class MacroHostLock : IDisposable
    {
        public const string DefaultName = "ZeroStrap-MacroHost";

        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _thread;
        private int _disposed;

        /// <summary>The lock this process holds, if any (set by whoever acquired it).</summary>
        public static MacroHostLock? Current { get; set; }

        public bool IsAcquired { get; private set; }

        private MacroHostLock(string name, TimeSpan timeout)
        {
            using var ready = new ManualResetEventSlim(false);

            _thread = new Thread(() =>
            {
                using var mutex = new Mutex(false, name);
                bool acquired;

                try
                {
                    acquired = mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true; // the previous owner died; ownership passes to us
                }

                IsAcquired = acquired;
                ready.Set();

                if (!acquired)
                    return;

                _release.Wait();

                try { mutex.ReleaseMutex(); } catch { }
            })
            { IsBackground = true, Name = "ZeroStrap macro host lock" };

            _thread.Start();
            ready.Wait();
        }

        /// <summary>
        /// Takes the lock, waiting up to <paramref name="timeout"/>. With <paramref name="requestRelease"/>, a background host
        /// that holds it is first asked to quit (it releases everything and exits). Returns null when the lock stays taken.
        /// Blocks the calling thread; see <see cref="AcquireAsync"/>.
        /// </summary>
        public static MacroHostLock? Acquire(TimeSpan timeout, bool requestRelease, string name = DefaultName)
        {
            var hostLock = new MacroHostLock(name, TimeSpan.Zero);

            if (!hostLock.IsAcquired && requestRelease)
            {
                try
                {
                    using var request = OpenReleaseRequest(name);
                    request.Set();
                }
                catch
                {
                    // Without the event the holder simply keeps the lock; the wait below then times out.
                }

                hostLock = new MacroHostLock(name, timeout);
            }
            else if (!hostLock.IsAcquired && timeout > TimeSpan.Zero)
            {
                hostLock = new MacroHostLock(name, timeout);
            }

            if (hostLock.IsAcquired)
                return hostLock;

            hostLock.Dispose();
            return null;
        }

        public static Task<MacroHostLock?> AcquireAsync(TimeSpan timeout, bool requestRelease, string name = DefaultName) =>
            Task.Run(() => Acquire(timeout, requestRelease, name));

        /// <summary>The event a background host waits on; signalled when another process wants the lock. Auto-reset.</summary>
        public static EventWaitHandle OpenReleaseRequest(string name = DefaultName) =>
            new(false, EventResetMode.AutoReset, name + "-Release");

        /// <summary>Releases the lock (if held). Idempotent, never throws.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _release.Set();

            try
            {
                if (Thread.CurrentThread != _thread)
                    _thread.Join(1000);
            }
            catch
            {
            }

            IsAcquired = false;
        }
    }

    /// <summary>
    /// When the background macro host quits on its own: it stays while Roblox runs, gives Roblox <see cref="StartupGrace"/> to
    /// appear after the host starts, and leaves <see cref="AbsenceGrace"/> after Roblox closed (a rejoin starts a new Roblox
    /// process quickly; the bootstrapper also starts a new host for it).
    /// </summary>
    public sealed class MacroHostLifetime
    {
        public static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan AbsenceGrace = TimeSpan.FromSeconds(15);

        private readonly TimeSpan _started;
        private TimeSpan? _lastSeen;

        public MacroHostLifetime(TimeSpan now) => _started = now;

        public bool ShouldExit(bool robloxRunning, TimeSpan now)
        {
            if (robloxRunning)
            {
                _lastSeen = now;
                return false;
            }

            return _lastSeen is TimeSpan seen ? now - seen >= AbsenceGrace : now - _started >= StartupGrace;
        }
    }
}
