using System.Collections.Concurrent;
using System.Runtime.InteropServices;

using static ZeroStrap.Macros.MacroHookNative;

namespace ZeroStrap.Macros
{
    /// <summary>Captures input for the macro recorder. The real implementation is <see cref="InputRecorder"/>; the runtime tests use a fake.</summary>
    public interface IInputRecorder : IDisposable
    {
        bool IsRecording { get; }

        /// <summary>Raised (on a background thread, never from inside a hook callback) when the stop key or emergency stop was pressed, or the event limit was hit.</summary>
        event Action<RecorderStopReason>? StopRequested;

        /// <summary>Raised with a user-facing message, e.g. when the hooks had to be reinstalled.</summary>
        event Action<string>? Warning;

        bool Start(RecorderOptions options, out string? error);

        /// <summary>Ends capture (waiting a bounded time for the capture thread) and returns everything recorded. Safe to call when not recording.</summary>
        IReadOnlyList<RawRecordedEvent> Stop();
    }

    /// <summary>Why a recording asked to end.</summary>
    public enum RecorderStopReason { StopKey, EmergencyStop, LimitReached }

    internal enum KeyDecision { Record, Drop, Stop, EmergencyStop }

    /// <summary>
    /// Decides what the recorder does with each key event: record it, drop it, or treat it as a stop key. Tracks the modifier
    /// state itself (from the hook's own events) so it can match shortcuts, and remembers which keys were swallowed so their
    /// key-up and auto-repeats are dropped too. Single-threaded: only the hook thread touches it.
    /// </summary>
    internal sealed class RecorderKeyFilter
    {
        private readonly Hotkey _stop;
        private readonly Hotkey? _emergency;
        private readonly Hotkey[] _ignored;
        private readonly HashSet<int> _dropped = new();
        private readonly HashSet<int> _heldModifiers = new();

        public RecorderKeyFilter(RecorderOptions options)
        {
            _stop = options.StopKey;
            _emergency = options.EmergencyStop;
            _ignored = options.IgnoredHotkeys.ToArray();
        }

        /// <summary>The modifiers that were held when the last <see cref="Classify"/> call matched a stop or ignored shortcut; None otherwise.</summary>
        public HotkeyModifiers LastComboModifiers { get; private set; }

        public KeyDecision Classify(int virtualKey, bool isDown)
        {
            LastComboModifiers = HotkeyModifiers.None;

            if (TryGetModifier(virtualKey, out _))
            {
                // Modifier keys are always recorded as ordinary keys.
                if (isDown) _heldModifiers.Add(virtualKey); else _heldModifiers.Remove(virtualKey);
                return KeyDecision.Record;
            }

            if (!isDown)
                return _dropped.Remove(virtualKey) ? KeyDecision.Drop : KeyDecision.Record;

            if (_dropped.Contains(virtualKey))
                return KeyDecision.Drop; // auto-repeat of a swallowed key

            var current = CurrentModifiers();

            // The emergency stop is checked first so it wins if it was configured to the same key as the stop key.
            if (_emergency is { } emergency && Matches(emergency, virtualKey, current))
            {
                _dropped.Add(virtualKey);
                LastComboModifiers = current;
                return KeyDecision.EmergencyStop;
            }

            if (Matches(_stop, virtualKey, current))
            {
                _dropped.Add(virtualKey);
                LastComboModifiers = current;
                return KeyDecision.Stop;
            }

            foreach (var hotkey in _ignored)
            {
                if (Matches(hotkey, virtualKey, current))
                {
                    _dropped.Add(virtualKey);
                    LastComboModifiers = current;
                    return KeyDecision.Drop;
                }
            }

            return KeyDecision.Record;
        }

        private static bool Matches(Hotkey hotkey, int virtualKey, HotkeyModifiers current) =>
            hotkey.VirtualKey == virtualKey && hotkey.Modifiers == current;

        private HotkeyModifiers CurrentModifiers()
        {
            var result = HotkeyModifiers.None;

            foreach (int vk in _heldModifiers)
            {
                if (TryGetModifier(vk, out var flag))
                    result |= flag;
            }

            return result;
        }

        private static bool TryGetModifier(int vk, out HotkeyModifiers flag)
        {
            switch (vk)
            {
                case 0x10: case 0xA0: case 0xA1:
                    flag = HotkeyModifiers.Shift;
                    return true;
                case 0x11: case 0xA2: case 0xA3:
                    flag = HotkeyModifiers.Ctrl;
                    return true;
                case 0x12: case 0xA4: case 0xA5:
                    flag = HotkeyModifiers.Alt;
                    return true;
                case 0x5B: case 0x5C:
                    flag = HotkeyModifiers.Win;
                    return true;
                default:
                    flag = HotkeyModifiers.None;
                    return false;
            }
        }
    }

    /// <summary>The "has Windows silently removed our hook?" check (it does that when a callback is too slow).</summary>
    internal static class HookHealth
    {
        public const long RawWindowMs = 1000;
        public const long SilenceMs = 2000;

        /// <summary>
        /// True when Raw Input saw mouse movement within the last second but the low-level hooks have been silent for at least
        /// two seconds: the hooks must have been dropped. <paramref name="lastRawMoveMs"/> is negative when never seen.
        /// </summary>
        public static bool ShouldReinstall(long nowMs, long lastRawMoveMs, long lastHookMs) =>
            lastRawMoveMs >= 0 && nowMs - lastRawMoveMs <= RawWindowMs && nowMs - lastHookMs >= SilenceMs;
    }

    /// <summary>
    /// Records keyboard and mouse input with WH_KEYBOARD_LL / WH_MOUSE_LL hooks plus Raw Input for relative mouse movement,
    /// on its own message-pump thread. Hook callbacks only enqueue a small struct and always pass the event on, so the
    /// recorder can never swallow or delay the user's input. Events injected by anyone (including ZeroStrap itself) are ignored.
    /// Raw Input cannot tell injected movement from real, so recording and playback must not overlap (the runtime enforces it).
    /// </summary>
    public sealed class InputRecorder : IInputRecorder
    {
        private const int MaxEvents = 500_000;
        private const int MsgStop = WM_APP + 1;
        private const int MsgLimit = WM_APP + 2;
        private const int RawBufferSize = 256;
        private const int StartTimeoutMs = 3000;
        private const int StopJoinMs = 500;

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        private readonly object _lock = new();
        private readonly Action<string> _log;
        private readonly ConcurrentQueue<RawRecordedEvent> _queue = new();

        // Hook delegates live in fields for the whole recorder lifetime: the OS only holds a raw function pointer, and a
        // collected delegate would crash the process on the next keystroke.
        private readonly LowLevelHookProc _keyboardProc;
        private readonly LowLevelHookProc _mouseProc;

        private Thread? _thread;
        private ManualResetEventSlim? _ready;
        private string? _startError;
        private volatile bool _quitRequested;
        private volatile bool _disposed;
        private uint _threadId;

        // Owned by the capture thread (hooks, window and filter are created, used and destroyed there).
        private IntPtr _keyboardHook;
        private IntPtr _mouseHook;
        private IntPtr _window;
        private IntPtr _rawBuffer;
        private UIntPtr _timerId;
        private RecorderKeyFilter _filter = null!;
        private long _lastHookMs;
        private long _lastRawMoveMs = -1;

        // Shared with the capture thread.
        private volatile bool _accepting;
        private int _stopSignalled;
        private int _limitSignalled;
        private int _count;

        public event Action<RecorderStopReason>? StopRequested;
        public event Action<string>? Warning;

        public InputRecorder(Action<string>? log = null)
        {
            _log = log ?? (_ => { });
            _keyboardProc = KeyboardHook;
            _mouseProc = MouseHook;
        }

        public bool IsRecording
        {
            get
            {
                lock (_lock)
                    return _thread is not null;
            }
        }

        public bool Start(RecorderOptions options, out string? error)
        {
            ManualResetEventSlim ready;
            Thread thread;

            lock (_lock)
            {
                if (_disposed)
                {
                    error = "The recorder has been shut down";
                    return false;
                }

                if (_thread is not null)
                {
                    error = "A recording is already in progress";
                    return false;
                }

                while (_queue.TryDequeue(out _)) { }

                _filter = new RecorderKeyFilter(options);
                _startError = null;
                _quitRequested = false;
                _stopSignalled = 0;
                _limitSignalled = 0;
                _count = 0;
                _accepting = false;
                _threadId = 0;
                _lastRawMoveMs = -1;
                ready = _ready = new ManualResetEventSlim(false);
                thread = _thread = new Thread(CaptureMain) { IsBackground = true, Name = "ZeroStrap macro recorder" };
                thread.Start();
            }

            if (!ready.Wait(StartTimeoutMs))
            {
                Stop();
                error = "Input capture did not start in time";
                return false;
            }

            string? failure = _startError;

            if (failure is not null)
            {
                thread.Join(StopJoinMs);
                error = failure;
                return false;
            }

            error = null;
            return true;
        }

        public IReadOnlyList<RawRecordedEvent> Stop()
        {
            Thread? thread;
            ManualResetEventSlim? ready;

            lock (_lock)
            {
                thread = _thread;
                ready = _ready;
                _quitRequested = true;
            }

            if (thread is not null)
            {
                try { ready?.Wait(StopJoinMs); } catch { }

                uint threadId = _threadId;

                if (threadId != 0)
                    PostThreadMessageW(threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);

                // Called from inside a capture-thread event handler: joining ourselves would hang, and the posted quit does the job.
                if (Thread.CurrentThread != thread && !thread.Join(StopJoinMs))
                {
                    _log("Input capture thread did not stop in time");

                    // Unhooking is allowed from any thread of the process; this is only a fallback for a wedged capture thread.
                    try { if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook); } catch { }
                    try { if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook); } catch { }
                }
            }

            var events = new List<RawRecordedEvent>(Math.Min(_queue.Count, MaxEvents));

            while (_queue.TryDequeue(out var ev))
                events.Add(ev);

            return events;
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();
        }

        private long NowMs => (long)(Stopwatch.GetTimestamp() * MsPerTick);

        private void CaptureMain()
        {
            var ready = _ready!;
            bool hooksInstalled = false;

            try
            {
                // Creates this thread's message queue so a WM_QUIT posted by Stop() cannot be lost.
                PeekMessageW(out _, IntPtr.Zero, WM_APP, WM_APP, PM_NOREMOVE);
                _threadId = GetCurrentThreadId();
                _rawBuffer = Marshal.AllocHGlobal(RawBufferSize);

                _window = CreateWindowExW(0, "STATIC", "ZeroStrapMacroRecorder", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (_window == IntPtr.Zero)
                    throw new InvalidOperationException("Could not create the input window (error " + Marshal.GetLastWin32Error() + ")");

                var device = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_INPUTSINK, hwndTarget = _window };

                if (!RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                    throw new InvalidOperationException("Could not register for mouse input (error " + Marshal.GetLastWin32Error() + ")");

                _lastHookMs = NowMs;
                hooksInstalled = InstallHooks();

                if (!hooksInstalled)
                    throw new InvalidOperationException("Could not install the input hooks (error " + Marshal.GetLastWin32Error() + ")");

                // With no window, SetTimer ignores the requested id and returns the one to kill later.
                _timerId = SetTimer(IntPtr.Zero, UIntPtr.Zero, 1000, IntPtr.Zero);

                // Stop() may have been called while we were starting.
                _accepting = !_quitRequested;
                ready.Set();

                if (!_quitRequested)
                    RunMessageLoop();
            }
            catch (Exception ex)
            {
                if (!ready.IsSet)
                    _startError = ex.Message;
                else
                    _log("Input capture failed: " + ex.Message);
            }
            finally
            {
                // Unhook on the thread that installed the hooks.
                _accepting = false;

                try { if (_timerId != UIntPtr.Zero) KillTimer(IntPtr.Zero, _timerId); } catch { }
                try { RemoveHooks(); } catch { }

                try
                {
                    var remove = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero };
                    RegisterRawInputDevices(new[] { remove }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
                }
                catch { }

                try { if (_window != IntPtr.Zero) DestroyWindow(_window); } catch { }

                _window = IntPtr.Zero;

                if (_rawBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_rawBuffer);
                    _rawBuffer = IntPtr.Zero;
                }

                lock (_lock)
                {
                    if (ReferenceEquals(_thread, Thread.CurrentThread))
                    {
                        _thread = null;
                        _threadId = 0;
                    }
                }

                if (!ready.IsSet)
                    ready.Set();
            }
        }

        private void RunMessageLoop()
        {
            int result;

            while ((result = GetMessageW(out var msg, IntPtr.Zero, 0, 0)) > 0)
            {
                try
                {
                    switch ((int)msg.message)
                    {
                        case WM_INPUT:
                            OnRawInput(msg.lParam);
                            break;
                        case WM_TIMER:
                            OnHealthTimer();
                            break;
                        case MsgStop:
                            RaiseStop(msg.wParam == UIntPtr.Zero ? RecorderStopReason.StopKey : RecorderStopReason.EmergencyStop);
                            break;
                        case MsgLimit:
                            RaiseWarning("The recording reached its size limit and was stopped.");
                            RaiseStop(RecorderStopReason.LimitReached);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _log("Input capture message failed: " + ex.Message);
                }

                // WM_INPUT must reach DefWindowProc for cleanup, so everything is dispatched.
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            if (result < 0)
                _log("GetMessage failed (error " + Marshal.GetLastWin32Error() + ")");
        }

        private bool InstallHooks()
        {
            IntPtr module = GetModuleHandleW(null);

            _keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyboardProc, module, 0);
            _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, module, 0);

            if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            {
                RemoveHooks();
                return false;
            }

            return true;
        }

        private void RemoveHooks()
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        private void OnHealthTimer()
        {
            long now = NowMs;

            if (!_accepting || !HookHealth.ShouldReinstall(now, _lastRawMoveMs, _lastHookMs))
                return;

            RemoveHooks();
            bool ok = InstallHooks();

            // Reset either way so a hook that cannot be restored is retried once every two seconds, not in a tight loop.
            _lastHookMs = now;

            RaiseWarning(ok
                ? "Input capture restarted because Windows had removed it. Keys pressed in the last moments may be missing."
                : "Input capture stopped working and could not be restarted. Stop and start the recording again.");
        }

        // ---- hook callbacks: never block, never swallow, always CallNextHookEx ----

        private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try { OnKeyboard(wParam, lParam); } catch { }
            }

            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try { OnMouse(wParam, lParam); } catch { }
            }

            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private void OnKeyboard(IntPtr wParam, IntPtr lParam)
        {
            double now = Stopwatch.GetTimestamp() * MsPerTick;
            _lastHookMs = (long)now;

            if (!_accepting)
                return;

            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            if ((info.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0 || IsOurs(info.dwExtraInfo))
                return;

            int message = (int)wParam;
            bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
            bool up = message == WM_KEYUP || message == WM_SYSKEYUP;

            if (!down && !up)
                return;

            var decision = _filter.Classify((int)info.vkCode, down);

            // The Ctrl of "Ctrl+F9" was already recorded when F9 arrived; this tells the compiler to take it back out.
            if (_filter.LastComboModifiers != HotkeyModifiers.None)
                Enqueue(new RawRecordedEvent(RawEventKind.HotkeyConsumed, now, X: (int)_filter.LastComboModifiers));

            switch (decision)
            {
                case KeyDecision.Stop:
                    SignalStop(emergency: false);
                    return;
                case KeyDecision.EmergencyStop:
                    SignalStop(emergency: true);
                    return;
                case KeyDecision.Drop:
                    return;
            }

            Enqueue(new RawRecordedEvent(down ? RawEventKind.KeyDown : RawEventKind.KeyUp, now, (ushort)info.scanCode, (info.flags & LLKHF_EXTENDED) != 0));
        }

        private void OnMouse(IntPtr wParam, IntPtr lParam)
        {
            double now = Stopwatch.GetTimestamp() * MsPerTick;
            _lastHookMs = (long)now;

            if (!_accepting)
                return;

            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if ((info.flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0 || IsOurs(info.dwExtraInfo))
                return;

            RawEventKind kind;
            MouseButton button;

            switch ((int)wParam)
            {
                case WM_LBUTTONDOWN: kind = RawEventKind.MouseDown; button = MouseButton.Left; break;
                case WM_LBUTTONUP: kind = RawEventKind.MouseUp; button = MouseButton.Left; break;
                case WM_RBUTTONDOWN: kind = RawEventKind.MouseDown; button = MouseButton.Right; break;
                case WM_RBUTTONUP: kind = RawEventKind.MouseUp; button = MouseButton.Right; break;
                case WM_MBUTTONDOWN: kind = RawEventKind.MouseDown; button = MouseButton.Middle; break;
                case WM_MBUTTONUP: kind = RawEventKind.MouseUp; button = MouseButton.Middle; break;
                case WM_XBUTTONDOWN:
                case WM_XBUTTONUP:
                {
                    uint which = info.mouseData >> 16;

                    if (which != MacroNative.XBUTTON1 && which != MacroNative.XBUTTON2)
                        return;

                    kind = (int)wParam == WM_XBUTTONDOWN ? RawEventKind.MouseDown : RawEventKind.MouseUp;
                    button = which == MacroNative.XBUTTON1 ? MouseButton.X1 : MouseButton.X2;
                    break;
                }
                default:
                    return; // movement comes from Raw Input; the wheel is not a supported action
            }

            Enqueue(new RawRecordedEvent(kind, now, 0, false, button, info.pt.x, info.pt.y));
        }

        private void OnRawInput(IntPtr handle)
        {
            if (!_accepting)
                return;

            uint size = RawBufferSize;
            uint headerSize = (uint)RawHeaderSize;
            uint read = GetRawInputData(handle, RID_INPUT, _rawBuffer, ref size, headerSize);

            if (read == uint.MaxValue || read <= headerSize)
                return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(_rawBuffer);

            if (header.dwType != RIM_TYPEMOUSE)
                return;

            var mouse = Marshal.PtrToStructure<RAWMOUSE>(_rawBuffer + RawHeaderSize);

            // Absolute devices (remote desktop, tablets) report positions, not movement.
            if ((mouse.usFlags & MOUSE_MOVE_ABSOLUTE) != 0 || (mouse.lLastX == 0 && mouse.lLastY == 0))
                return;

            double now = Stopwatch.GetTimestamp() * MsPerTick;
            _lastRawMoveMs = (long)now;
            Enqueue(new RawRecordedEvent(RawEventKind.MouseDelta, now, X: mouse.lLastX, Y: mouse.lLastY));
        }

        private static bool IsOurs(UIntPtr extraInfo) => unchecked((uint)(ulong)extraInfo) == MacroNative.InjectionMarker;

        private void Enqueue(RawRecordedEvent ev)
        {
            if (Interlocked.Increment(ref _count) > MaxEvents)
            {
                _accepting = false;

                if (Interlocked.Exchange(ref _limitSignalled, 1) == 0)
                    PostToSelf(MsgLimit, UIntPtr.Zero);

                return;
            }

            _queue.Enqueue(ev);
        }

        private void SignalStop(bool emergency)
        {
            // Events after the stop key are not part of the recording.
            _accepting = false;

            if (Interlocked.Exchange(ref _stopSignalled, 1) == 0)
                PostToSelf(MsgStop, emergency ? new UIntPtr(1) : UIntPtr.Zero);
        }

        // PostThreadMessage returns immediately, so this is safe to call from inside a hook callback.
        private void PostToSelf(int message, UIntPtr wParam)
        {
            uint threadId = _threadId;

            if (threadId != 0)
                PostThreadMessageW(threadId, (uint)message, wParam, IntPtr.Zero);
        }

        // Subscribers run on the thread pool: this thread pumps the low-level hooks, and a slow handler here would get them timed out.
        private void RaiseStop(RecorderStopReason reason)
        {
            var handler = StopRequested;

            if (handler is null)
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { handler(reason); } catch (Exception ex) { _log("Recorder handler failed: " + ex.Message); }
            });
        }

        private void RaiseWarning(string message)
        {
            _log(message);

            var handler = Warning;

            if (handler is null)
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { handler(message); } catch (Exception ex) { _log("Recorder handler failed: " + ex.Message); }
            });
        }
    }
}
