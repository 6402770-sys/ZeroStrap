using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

using ZeroStrap.Macros;

namespace ZeroStrap.UI.Utility
{
    /// <summary>
    /// Keeps macros working while no ZeroStrap window is open: a small background process (<c>ZeroStrap.exe -macrohost</c>)
    /// that owns the macro shortcuts, playback and the in-game overlay, with a notification-area icon to stop or turn them off.
    /// It is started when the settings window closes after using Macro Studio and when the bootstrapper launches Roblox, but
    /// only if a macro is enabled with a shortcut. It hands over to the settings window when Macro Studio opens there
    /// (<see cref="MacroHostLock"/>), and quits by itself once Roblox has been closed for a little while.
    /// </summary>
    public static class MacroBackgroundHost
    {
        private const string LOG_IDENT = "MacroBackgroundHost";

        /// <summary>How long a new host waits for a closing settings window (or an older host) to let go.</summary>
        private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        private static System.Windows.Forms.NotifyIcon? _tray;
        private static MacroHostLock? _lock;
        private static int _exiting;

        /// <summary>True when at least one macro is enabled and has a shortcut (otherwise a background host has nothing to do).</summary>
        public static bool HasActiveShortcuts()
        {
            try
            {
                return new MacroStore(Paths.Macros).LoadAll().Any(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Hotkey));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }
        }

        /// <summary>Starts the background host process when there is something for it to do. Never throws.</summary>
        public static void StartProcess()
        {
            try
            {
                if (!HasActiveShortcuts())
                    return;

                App.Logger.WriteLine(LOG_IDENT, "Starting the background macro host");
                Process.Start(Paths.Process, "-macrohost");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        /// <summary>Entry point of the <c>-macrohost</c> process, on the UI thread. Returns at once; the process ends through <see cref="Exit"/>.</summary>
        public static void Run()
        {
            if (!HasActiveShortcuts())
            {
                App.Logger.WriteLine(LOG_IDENT, "No enabled macro has a shortcut, closing");
                App.Terminate();
                return;
            }

            _lock = MacroHostLock.Acquire(AcquireTimeout, requestRelease: false);

            if (_lock is null)
            {
                App.Logger.WriteLine(LOG_IDENT, "Another ZeroStrap process runs the macros, closing");
                App.Terminate();
                return;
            }

            MacroHostLock.Current = _lock;

            EventWaitHandle releaseRequest = MacroHostLock.OpenReleaseRequest();
            releaseRequest.Reset(); // a request left over from before we owned the lock is not meant for us

            var dispatcher = Dispatcher.CurrentDispatcher;
            MacroRuntime runtime;

            try
            {
                runtime = MacroRuntime.Instance;
                runtime.ReloadHotkeys();
                MacroOverlayHost.EnsureStarted(runtime);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Exit("the macros could not start");
                return;
            }

            ShowTrayIcon(runtime);

            var watch = new Thread(() => Watch(releaseRequest, dispatcher)) { IsBackground = true, Name = "ZeroStrap macro host watch" };
            watch.Start();

            App.Logger.WriteLine(LOG_IDENT, "Macros are running in the background");
        }

        // Waits for a hand-over request and checks every couple of seconds whether Roblox is still around.
        private static void Watch(EventWaitHandle releaseRequest, Dispatcher dispatcher)
        {
            var clock = Stopwatch.StartNew();
            var lifetime = new MacroHostLifetime(clock.Elapsed);

            try
            {
                while (Volatile.Read(ref _exiting) == 0)
                {
                    if (releaseRequest.WaitOne(PollInterval))
                    {
                        dispatcher.BeginInvoke(new Action(() => Exit("the settings window took over the macros")));
                        return;
                    }

                    if (lifetime.ShouldExit(IsRobloxRunning(), clock.Elapsed))
                    {
                        dispatcher.BeginInvoke(new Action(() => Exit("Roblox is not running")));
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                dispatcher.BeginInvoke(new Action(() => Exit("the watch failed")));
            }
            finally
            {
                releaseRequest.Dispose();
            }
        }

        private static bool IsRobloxRunning()
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(RobloxFocusGate.RobloxImageName);
            var processes = Process.GetProcessesByName(name);

            foreach (var process in processes)
                process.Dispose();

            return processes.Length > 0;
        }

        private static void ShowTrayIcon(MacroRuntime runtime)
        {
            var menu = new System.Windows.Forms.ContextMenuStrip { ShowItemToolTips = true };

            var stop = menu.Items.Add("Stop playing macros", null, (_, _) => Task.Run(() => runtime.Stop()));
            stop.ToolTipText = "Stops the macros that are playing right now and releases their keys. Their shortcuts keep working.";

            var open = menu.Items.Add("Open ZeroStrap", null, (_, _) => OpenSettings());
            open.ToolTipText = "Opening Macro Studio moves the macros back into the ZeroStrap window.";

            var off = menu.Items.Add("Turn off macros until next launch", null, (_, _) => Exit("turned off from the notification area"));
            off.ToolTipText = "Stops everything and closes the background macros: no shortcut works until you open ZeroStrap or launch Roblox again.";

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = Properties.Resources.IconZeroStrap,
                Text = "ZeroStrap macros: shortcuts active",
                ContextMenuStrip = menu,
                Visible = true
            };

            // The only way to hear about a refused start (8 already running), a blocked key or the emergency stop without a window.
            runtime.Warning += message =>
            {
                try { _tray?.ShowBalloonTip(3000, "ZeroStrap macros", message, System.Windows.Forms.ToolTipIcon.Info); } catch { }
            };
        }

        private static void OpenSettings()
        {
            try
            {
                Process.Start(Paths.Process, "-settings");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        /// <summary>Releases every key, removes the shortcuts, overlay and icon, gives up the lock and ends the process.</summary>
        private static void Exit(string reason)
        {
            if (Interlocked.Exchange(ref _exiting, 1) != 0)
                return;

            App.Logger.WriteLine(LOG_IDENT, "Closing: " + reason);

            try { MacroOverlayHost.Shutdown(); } catch { }
            try { MacroRuntime.TryDispose(); } catch { }

            try
            {
                if (_tray is not null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
            }
            catch
            {
            }

            // Only after the runtime is gone: the next owner must never overlap with our shortcuts or held keys.
            MacroHostLock.Current = null;
            _lock?.Dispose();

            App.Terminate();
        }
    }
}
