using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using ZeroStrap.Macros;
using ZeroStrap.UI.Elements.Overlay;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.UI.Utility
{
    /// <summary>
    /// Owns the in-game macro overlay: shows it only while Roblox is the active window (event driven, no polling while it is
    /// not), keeps it in the chosen corner of the game's client area and dims it when nothing has happened for a while.
    /// UI-thread only.
    /// </summary>
    public sealed class MacroOverlayHost
    {
        private const string LOG_IDENT = "MacroOverlayHost";

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        public static MacroOverlayHost? Current { get; private set; }

        /// <summary>Starts the overlay once; later calls only refresh the macro list.</summary>
        public static void EnsureStarted(MacroRuntime runtime)
        {
            try
            {
                if (Current is not null)
                {
                    Current.RefreshMacros();
                    return;
                }

                var host = new MacroOverlayHost(runtime);
                Current = host;
                host.Start();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        public static void Shutdown()
        {
            try
            {
                var host = Current;
                Current = null;
                host?.Stop();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        private readonly MacroRuntime _runtime;
        private readonly Dispatcher _dispatcher;
        private readonly RobloxFocusGate _gate = new();
        private readonly WinEventProc _callback;
        private readonly DispatcherTimer _trackTimer;
        private readonly DispatcherTimer _fadeTimer;

        private MacroStudioSettings _settings = new();
        private List<Macro> _macros = new();
        private IReadOnlyList<OverlayEntry> _entries = Array.Empty<OverlayEntry>();
        private MacroOverlayWindow? _window;
        private IntPtr _hook;
        private IntPtr _minimizeHook;
        private IntPtr _minimizeEndHook;
        private string _runningKey = "";
        private int _refreshVersion;
        private bool _visible;
        private bool _stopped;

        private MacroOverlayHost(MacroRuntime runtime)
        {
            _runtime = runtime;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _callback = OnWinEvent;

            _trackTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _trackTimer.Tick += (_, _) => Safe(() =>
            {
                Evaluate();
                Reposition();
            });

            _fadeTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = MacroOverlayLogic.FadeDelay };
            _fadeTimer.Tick += (_, _) => Safe(() =>
            {
                _fadeTimer.Stop();
                AnimateOpacity(MacroOverlayLogic.IdleOpacity, MacroOverlayLogic.FadeDuration);
            });
        }

        private void Start()
        {
            try
            {
                _settings = _runtime.SettingsStore.Load();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }

            _runtime.StateChanged += OnStateChanged;

            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
            _minimizeHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
            _minimizeEndHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);

            RefreshMacros();
        }

        private void Stop()
        {
            _stopped = true;

            try
            {
                _runtime.StateChanged -= OnStateChanged;
            }
            catch
            {
                // Runtime may already be disposed.
            }

            foreach (var hook in new[] { _hook, _minimizeHook, _minimizeEndHook })
            {
                if (hook != IntPtr.Zero)
                    UnhookWinEvent(hook);
            }

            _hook = _minimizeHook = _minimizeEndHook = IntPtr.Zero;
            _trackTimer.Stop();
            _fadeTimer.Stop();

            try
            {
                _window?.Close();
            }
            catch
            {
                // Already closing with the app.
            }

            _window = null;
            _visible = false;
        }

        /// <summary>Re-reads the macros from disk (after a save, delete, import or shortcut change) and updates the overlay.</summary>
        public void RefreshMacros()
        {
            if (_stopped)
                return;

            int version = ++_refreshVersion;
            var runtime = _runtime;

            Task.Run(() =>
            {
                try
                {
                    return runtime.Store.LoadAll().ToList();
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT, ex);
                    return null;
                }
            }).ContinueWith(task =>
            {
                var loaded = task.Status == TaskStatus.RanToCompletion ? task.Result : null;

                if (loaded is null)
                    return;

                _dispatcher.BeginInvoke(new Action(() => Safe(() =>
                {
                    if (_stopped || version != _refreshVersion)
                        return;

                    _macros = loaded;
                    Recompute();
                })));
            }, TaskScheduler.Default);
        }

        public void ApplySettings(MacroStudioSettings settings)
        {
            if (_stopped)
                return;

            _settings = settings;
            Recompute();
            Reposition();
        }

        private void OnStateChanged(MacroRuntimeState state) => _dispatcher.BeginInvoke(new Action(() => Safe(() =>
        {
            if (_stopped)
                return;

            string before = _runningKey;
            Recompute();

            if (_runningKey != before && _visible)
                Pulse();
        })));

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) =>
            Safe(() =>
            {
                if (_stopped)
                    return;

                Evaluate();

                if (_visible)
                    Reposition();
            });

        private void Recompute()
        {
            var running = _runtime.RunningMacroIds.ToList();
            _entries = MacroOverlayLogic.Entries(_macros, running, _settings.OverlayRunningOnly);
            _runningKey = string.Join("|", running.OrderBy(i => i, StringComparer.Ordinal));

            _window?.SetEntries(_entries);
            Evaluate();
        }

        /// <summary>Shows or hides the overlay according to the current rules.</summary>
        private void Evaluate()
        {
            bool show = MacroOverlayLogic.ShouldShow(_settings.OverlayEnabled, _gate.IsAllowed(), _entries.Count);

            if (show == _visible)
                return;

            if (show)
            {
                _window ??= new MacroOverlayWindow();
                _window.SetEntries(_entries);
                _visible = true;
                _window.Opacity = MacroOverlayLogic.IdleOpacity;
                _window.Show();
                _window.UpdateLayout();
                Reposition();
                _trackTimer.Start();
                Pulse();
            }
            else
            {
                _visible = false;
                _trackTimer.Stop();
                _fadeTimer.Stop();
                _window?.Hide();
            }
        }

        /// <summary>Something happened: go bright now and dim again after the fade delay.</summary>
        private void Pulse()
        {
            if (_window is null)
                return;

            AnimateOpacity(MacroOverlayLogic.ActiveOpacity, TimeSpan.FromMilliseconds(150));
            _fadeTimer.Stop();
            _fadeTimer.Start();
        }

        private void AnimateOpacity(double target, TimeSpan duration)
        {
            if (_window is null)
                return;

            if (SystemParameters.ClientAreaAnimation)
                _window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(target, duration));
            else
            {
                _window.BeginAnimation(UIElement.OpacityProperty, null);
                _window.Opacity = target;
            }
        }

        private void Reposition()
        {
            if (!_visible || _window is null)
                return;

            IntPtr foreground = GetForegroundWindow();

            if (foreground == IntPtr.Zero || !GetClientRect(foreground, out var client))
                return;

            var origin = new POINT();

            if (!ClientToScreen(foreground, ref origin))
                return;

            var dpi = VisualTreeHelper.GetDpi(_window);
            int width = (int)Math.Ceiling(_window.ActualWidth * dpi.DpiScaleX);
            int height = (int)Math.Ceiling(_window.ActualHeight * dpi.DpiScaleY);

            var (x, y) = MacroOverlayLogic.Position(_settings.OverlayCorner, origin.X, origin.Y, origin.X + client.Right, origin.Y + client.Bottom, width, height);

            SetWindowPos(_window.Handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        private static void Safe(Action work)
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc proc, uint idProcess, uint idThread, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    }
}
