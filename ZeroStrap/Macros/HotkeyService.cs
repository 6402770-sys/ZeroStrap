using System.Collections.Concurrent;


namespace ZeroStrap.Macros
{
    public enum HotkeyStatus
    {
        Ok,

        /// <summary>Another application already owns this shortcut.</summary>
        InUse,

        /// <summary>Not a registrable shortcut (unknown key, or the OS refused the parameters).</summary>
        Invalid,

        /// <summary>The same shortcut is already registered inside ZeroStrap under another name.</summary>
        Duplicate
    }

    /// <summary>Outcome of <see cref="IHotkeyRegistrar.Register"/>. <see cref="Message"/> is user-facing when the status is not Ok.</summary>
    public readonly record struct HotkeyRegistration(HotkeyStatus Status, string? Message = null)
    {
        public bool IsOk => Status == HotkeyStatus.Ok;

        public static HotkeyRegistration Ok => new(HotkeyStatus.Ok);
    }

    /// <summary>Registers global shortcuts. The real implementation is <see cref="HotkeyService"/>; the runtime tests use a fake.</summary>
    public interface IHotkeyRegistrar : IDisposable
    {
        /// <summary>Registers (or re-registers) <paramref name="name"/>. <paramref name="onPressed"/> runs on the registrar's own thread and must return quickly.</summary>
        HotkeyRegistration Register(string name, Hotkey hotkey, Action onPressed);

        void Unregister(string name);

        void UnregisterAll();

        /// <summary>Whether <paramref name="name"/> is currently registered.</summary>
        bool IsRegistered(string name);
    }

    /// <summary>Name/id/callback bookkeeping for <see cref="HotkeyService"/>, kept free of Win32 so it can be tested.</summary>
    internal sealed class HotkeyTable
    {
        // RegisterHotKey application ids are 0x0000-0xBFFF. Ids cycle through this range and skip ones in use, so a long-running
        // session that reloads shortcuts many times never runs off the end.
        internal const int FirstId = 0x2000;
        internal const int LastId = 0xBFFF;

        private sealed record Entry(string Name, Hotkey Hotkey, Action Callback, int Id);

        private readonly Dictionary<string, Entry> _byName = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Entry> _byId = new();
        private int _nextId = FirstId;

        public int Count => _byName.Count;

        /// <summary>Adds an entry. False when a different name already holds the same combination.</summary>
        public bool TryAdd(string name, Hotkey hotkey, Action callback, out int id)
        {
            id = 0;

            foreach (var entry in _byName.Values)
            {
                if (entry.Hotkey == hotkey && !string.Equals(entry.Name, name, StringComparison.Ordinal))
                    return false;
            }

            if (_byId.Count > LastId - FirstId)
                return false; // every id is taken

            while (_byId.ContainsKey(_nextId))
                _nextId = _nextId >= LastId ? FirstId : _nextId + 1;

            id = _nextId;
            _nextId = _nextId >= LastId ? FirstId : _nextId + 1;

            var added = new Entry(name, hotkey, callback, id);
            _byName[name] = added;
            _byId[id] = added;
            return true;
        }

        public bool Remove(string name, out int id)
        {
            if (_byName.Remove(name, out var entry))
            {
                _byId.Remove(entry.Id);
                id = entry.Id;
                return true;
            }

            id = 0;
            return false;
        }

        public bool Contains(string name) => _byName.ContainsKey(name);

        public bool TryGetCallback(int id, out Action callback)
        {
            if (_byId.TryGetValue(id, out var entry))
            {
                callback = entry.Callback;
                return true;
            }

            callback = () => { };
            return false;
        }

        public List<int> Ids() => _byId.Keys.ToList();

        public void Clear()
        {
            _byName.Clear();
            _byId.Clear();
        }
    }

    /// <summary>
    /// Global hotkeys via RegisterHotKey. The service owns a dedicated background thread with a hidden message-only window and
    /// its own message loop, so shortcuts (above all the emergency stop) keep working when the UI thread is busy or hung.
    /// Register/Unregister can be called from any thread: they run on the service thread and wait (up to three seconds) for the
    /// answer. Callbacks run on the service thread, so they must return quickly and must never wait for the UI thread.
    /// </summary>
    public sealed class HotkeyService : IHotkeyRegistrar
    {
        private const int MsgWake = MacroHookNative.WM_APP + 1;
        private const int RequestTimeoutMs = 3000;
        private const int StartTimeoutMs = 3000;
        private const int JoinTimeoutMs = 1000;

        private sealed class Request
        {
            public Func<object> Work = null!;
            public object? Result;
            public readonly ManualResetEventSlim Done = new(false);
            public volatile bool Abandoned;
        }

        private readonly Action<string> _log;
        private readonly HotkeyTable _table = new(); // touched only on the service thread
        private readonly ConcurrentQueue<Request> _requests = new();
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private uint _threadId;
        private IntPtr _window;
        private string? _failure;
        private volatile bool _disposed;

        public HotkeyService(Action<string>? log = null)
        {
            _log = log ?? (_ => { });
            _thread = new Thread(ThreadMain) { IsBackground = true, Name = "ZeroStrap hotkeys" };
            _thread.Start();

            if (!_ready.Wait(StartTimeoutMs))
                _failure = "The shortcut thread did not start";

            if (_failure is not null)
                _log("Shortcuts are unavailable: " + _failure);
        }

        public HotkeyRegistration Register(string name, Hotkey hotkey, Action onPressed)
        {
            var unavailable = new HotkeyRegistration(HotkeyStatus.Invalid, "Shortcuts are unavailable");
            return Run(() => RegisterCore(name, hotkey, onPressed), unavailable);
        }

        public void Unregister(string name) => Run(() =>
        {
            UnregisterCore(name);
            return true;
        }, false);

        public bool IsRegistered(string name) => Run(() => _table.Contains(name), false);

        public void UnregisterAll() => Run(() =>
        {
            UnregisterAllCore();
            return true;
        }, false);

        /// <summary>Stops the service thread (which unregisters every shortcut on its way out) and waits briefly for it. Idempotent; safe from any thread.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            uint threadId = _threadId;

            if (threadId != 0)
                MacroHookNative.PostThreadMessageW(threadId, MacroHookNative.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);

            // Called from one of our own callbacks: joining ourselves would hang, and the posted quit does the job.
            if (Thread.CurrentThread != _thread && !_thread.Join(JoinTimeoutMs))
                _log("The shortcut thread did not stop in time");
        }

        private T Run<T>(Func<T> action, T fallback)
        {
            if (_disposed || _failure is not null)
                return fallback;

            if (Thread.CurrentThread == _thread)
                return Guard(action, fallback);

            var request = new Request { Work = () => Guard(action, fallback)! };
            _requests.Enqueue(request);

            if (!MacroHookNative.PostThreadMessageW(_threadId, MsgWake, UIntPtr.Zero, IntPtr.Zero))
            {
                request.Abandoned = true;
                return fallback;
            }

            if (!request.Done.Wait(RequestTimeoutMs))
            {
                request.Abandoned = true;
                _log("A shortcut request timed out");
                return fallback;
            }

            return request.Result is T result ? result : fallback;
        }

        private T Guard<T>(Func<T> action, T fallback)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                _log("Hotkey operation failed: " + ex.Message);
                return fallback;
            }
        }

        private void ThreadMain()
        {
            try
            {
                // Creates this thread's message queue before anyone posts to it.
                MacroHookNative.PeekMessageW(out _, IntPtr.Zero, MacroHookNative.WM_APP, MacroHookNative.WM_APP, MacroHookNative.PM_NOREMOVE);
                _threadId = MacroHookNative.GetCurrentThreadId();

                _window = MacroHookNative.CreateWindowExW(0, "STATIC", "ZeroStrapMacroHotkeys", 0, 0, 0, 0, 0,
                    MacroHookNative.HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (_window == IntPtr.Zero)
                    throw new InvalidOperationException("Could not create the shortcut window (error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")");
            }
            catch (Exception ex)
            {
                _failure = ex.Message;
                _ready.Set();
                return;
            }

            _ready.Set();

            try
            {
                while (MacroHookNative.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    try
                    {
                        if ((int)msg.message == MacroHookNative.WM_HOTKEY)
                            Dispatch((int)(long)msg.wParam);
                        else if ((int)msg.message == MsgWake)
                            DrainRequests();
                    }
                    catch (Exception ex)
                    {
                        _log("Shortcut message failed: " + ex.Message);
                    }

                    MacroHookNative.TranslateMessage(ref msg);
                    MacroHookNative.DispatchMessageW(ref msg);
                }
            }
            finally
            {
                // Everything is unregistered and the window destroyed on the thread that owns them.
                try { UnregisterAllCore(); } catch { }
                try { MacroHookNative.DestroyWindow(_window); } catch { }
                _window = IntPtr.Zero;

                // Anyone still waiting gets their fallback right away instead of a timeout.
                while (_requests.TryDequeue(out var pending))
                    pending.Done.Set();
            }
        }

        private void DrainRequests()
        {
            while (_requests.TryDequeue(out var request))
            {
                if (!request.Abandoned)
                {
                    try { request.Result = request.Work(); } catch { }
                }

                request.Done.Set();
            }
        }

        private void Dispatch(int id)
        {
            // The callback is copied out first, so it may safely re-register shortcuts.
            if (!_table.TryGetCallback(id, out var callback))
                return; // a stale WM_HOTKEY for something already unregistered

            try
            {
                callback();
            }
            catch (Exception ex)
            {
                _log("Hotkey callback failed: " + ex.Message);
            }
        }

        private HotkeyRegistration RegisterCore(string name, Hotkey hotkey, Action onPressed)
        {
            if (_window == IntPtr.Zero)
                return new HotkeyRegistration(HotkeyStatus.Invalid, "Shortcuts are unavailable");

            if (!hotkey.IsValid)
                return new HotkeyRegistration(HotkeyStatus.Invalid, "That key can't be used as a shortcut");

            UnregisterCore(name);

            if (!_table.TryAdd(name, hotkey, onPressed, out int id))
                return new HotkeyRegistration(HotkeyStatus.Duplicate, "This shortcut is already used by another ZeroStrap action");

            if (MacroHookNative.RegisterHotKey(_window, id, (uint)hotkey.Modifiers | MacroHookNative.MOD_NOREPEAT, (uint)hotkey.VirtualKey))
                return HotkeyRegistration.Ok;

            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _table.Remove(name, out _);
            _log($"RegisterHotKey({hotkey}) failed with error {error}");

            return error == MacroHookNative.ERROR_HOTKEY_ALREADY_REGISTERED
                ? new HotkeyRegistration(HotkeyStatus.InUse, "This shortcut is used by another app")
                : new HotkeyRegistration(HotkeyStatus.Invalid, "Windows refused this shortcut");
        }

        private void UnregisterCore(string name)
        {
            if (_table.Remove(name, out int id) && _window != IntPtr.Zero)
                MacroHookNative.UnregisterHotKey(_window, id);
        }

        private void UnregisterAllCore()
        {
            if (_window != IntPtr.Zero)
            {
                foreach (int id in _table.Ids())
                    MacroHookNative.UnregisterHotKey(_window, id);
            }

            _table.Clear();
        }
    }
}
