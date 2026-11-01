using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;

using Xunit;

using ZeroStrap.UI.Elements.Overlay;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.Tests
{
    /// <summary>Window flags, native styles and row rendering of the in-game overlay window (STA thread, no app resources).</summary>
    public class MacroOverlayWindowTests
    {
        private const int GWL_EXSTYLE = -20;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        private static void OnSta(Action body)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            failure?.Throw();
        }

        [Fact]
        public void Window_HasNonActivatingFlags()
        {
            OnSta(() =>
            {
                var w = new MacroOverlayWindow();

                Assert.False(w.ShowActivated);
                Assert.False(w.ShowInTaskbar);
                Assert.True(w.Topmost);
                Assert.False(w.Focusable);
                Assert.False(w.IsHitTestVisible);
                w.Close();
            });
        }

        [Fact]
        public void Window_AppliesClickThroughExStyle()
        {
            OnSta(() =>
            {
                var w = new MacroOverlayWindow();
                IntPtr hwnd = new WindowInteropHelper(w).EnsureHandle();

                Assert.Equal(hwnd, w.Handle);

                int style = GetWindowLong(hwnd, GWL_EXSTYLE);
                int required = MacroOverlayLogic.WS_EX_TRANSPARENT | MacroOverlayLogic.WS_EX_LAYERED | MacroOverlayLogic.WS_EX_NOACTIVATE | MacroOverlayLogic.WS_EX_TOOLWINDOW;
                Assert.Equal(required, style & required);
                w.Close();
            });
        }

        [Fact]
        public void SetEntries_RendersRowsWithNamesKeysAndState()
        {
            OnSta(() =>
            {
                var w = new MacroOverlayWindow();
                w.SetEntries(new[]
                {
                    new OverlayEntry("a", "Sprint", "Shift+E", true),
                    new OverlayEntry("b", "Dance", "F5", false)
                });

                Assert.Equal(2, w.DisplayedRows.Count);
                Assert.Equal(("Sprint", "Shift+E", true), (w.DisplayedRows[0].Name, w.DisplayedRows[0].Hotkey, w.DisplayedRows[0].Running));
                Assert.Equal(("Dance", "F5", false), (w.DisplayedRows[1].Name, w.DisplayedRows[1].Hotkey, w.DisplayedRows[1].Running));

                Assert.Equal(2, ((System.Windows.Controls.ItemsControl)w.FindName("Rows")).Items.Count);

                // Same entries again: nothing is rebuilt.
                var first = w.DisplayedRows[0];
                w.SetEntries(new[]
                {
                    new OverlayEntry("a", "Sprint", "Shift+E", true),
                    new OverlayEntry("b", "Dance", "F5", false)
                });
                Assert.Same(first, w.DisplayedRows[0]);

                w.SetEntries(Array.Empty<OverlayEntry>());
                Assert.Empty(w.DisplayedRows);
                w.Close();
            });
        }

        [Fact]
        public void Close_Works_AfterShowWasRequestedOnHandle()
        {
            OnSta(() =>
            {
                var w = new MacroOverlayWindow();
                new WindowInteropHelper(w).EnsureHandle();
                w.SetEntries(new[] { new OverlayEntry("a", "A", "E", false) });
                w.Close();
                Assert.Empty(System.Windows.Application.Current?.Windows.OfType<MacroOverlayWindow>() ?? Enumerable.Empty<MacroOverlayWindow>());
            });
        }
    }
}
