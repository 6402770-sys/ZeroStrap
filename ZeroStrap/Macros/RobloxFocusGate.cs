using static ZeroStrap.Macros.MacroHookNative;

namespace ZeroStrap.Macros
{
    /// <summary>
    /// Allows macro input only while the foreground window belongs to the Roblox player (RobloxPlayerBeta.exe). The answer is
    /// cached per process id for a second so the player's 10 ms focus polling does not open a process handle every time; a
    /// different foreground process is looked up immediately. Fails closed: if the process cannot be identified, input is not allowed.
    /// </summary>
    public sealed class RobloxFocusGate : IFocusGate
    {
        public const string RobloxImageName = "RobloxPlayerBeta.exe";

        private static readonly long CacheTicks = Stopwatch.Frequency; // one second
        private const int MaxCacheEntries = 16;

        private readonly Func<uint> _foregroundProcessId;
        private readonly Func<uint, string?> _imageNameOf;
        private readonly Func<long> _ticks;
        private readonly object _lock = new();
        private readonly Dictionary<uint, (bool Allowed, long Expires)> _cache = new();

        public RobloxFocusGate() : this(GetForegroundProcessId, GetImageName, Stopwatch.GetTimestamp)
        {
        }

        internal RobloxFocusGate(Func<uint> foregroundProcessId, Func<uint, string?> imageNameOf, Func<long> ticks)
        {
            _foregroundProcessId = foregroundProcessId;
            _imageNameOf = imageNameOf;
            _ticks = ticks;
        }

        public bool IsAllowed()
        {
            try
            {
                uint pid = _foregroundProcessId();

                if (pid == 0)
                    return false;

                long now = _ticks();

                lock (_lock)
                {
                    if (_cache.TryGetValue(pid, out var cached) && now < cached.Expires)
                        return cached.Allowed;
                }

                string? name = _imageNameOf(pid);
                bool allowed = name is not null && string.Equals(Path.GetFileName(name), RobloxImageName, StringComparison.OrdinalIgnoreCase);

                lock (_lock)
                {
                    if (_cache.Count >= MaxCacheEntries)
                        _cache.Clear();

                    _cache[pid] = (allowed, now + CacheTicks);
                }

                return allowed;
            }
            catch
            {
                return false;
            }
        }

        private static uint GetForegroundProcessId()
        {
            IntPtr window = GetForegroundWindow();

            if (window == IntPtr.Zero)
                return 0;

            GetWindowThreadProcessId(window, out uint pid);
            return pid;
        }

        private static string? GetImageName(uint pid)
        {
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);

            if (process == IntPtr.Zero)
                return null;

            try
            {
                var builder = new StringBuilder(1024);
                uint size = (uint)builder.Capacity;
                return QueryFullProcessImageNameW(process, 0, builder, ref size) ? builder.ToString(0, (int)size) : null;
            }
            finally
            {
                CloseHandle(process);
            }
        }
    }
}
