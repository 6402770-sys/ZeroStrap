using System;
using System.Collections.Generic;
using System.Linq;

using ZeroStrap.Macros;

namespace ZeroStrap.UI.ViewModels.Shell
{
    /// <summary>One line of the in-game overlay.</summary>
    public sealed record OverlayEntry(string Id, string Name, string Hotkey, bool Running);

    /// <summary>
    /// Pure rules of the in-game macro overlay (no WPF): which macros it lists, when it shows, how bright it is, where it
    /// sits and which window styles keep it from ever taking focus or clicks.
    /// </summary>
    public static class MacroOverlayLogic
    {
        public const int MaxEntries = 6;

        /// <summary>Opacity right after a macro starts or stops, or when the overlay appears.</summary>
        public const double ActiveOpacity = 0.92;

        /// <summary>Opacity once nothing has happened for <see cref="FadeDelay"/>.</summary>
        public const double IdleOpacity = 0.35;

        public static readonly TimeSpan FadeDelay = TimeSpan.FromSeconds(4);

        public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(600);

        /// <summary>Distance from the edges of the Roblox window, in device pixels.</summary>
        public const int EdgeMargin = 16;

        // Extended window styles: click-through (TRANSPARENT needs LAYERED), never activated, kept out of Alt+Tab and the taskbar.
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        /// <summary>
        /// The macros to list: playing ones, plus enabled macros with a shortcut (unless <paramref name="runningOnly"/>). Playing
        /// macros come first so they are never cut off; within each part the store order is kept. At most <see cref="MaxEntries"/>.
        /// </summary>
        public static IReadOnlyList<OverlayEntry> Entries(IEnumerable<Macro> macros, IReadOnlyCollection<string> running, bool runningOnly)
        {
            var playing = new List<OverlayEntry>();
            var idle = new List<OverlayEntry>();

            foreach (var macro in macros)
            {
                bool isRunning = running.Contains(macro.Id);
                bool configured = macro.Enabled && !string.IsNullOrWhiteSpace(macro.Hotkey);

                if (isRunning)
                    playing.Add(ToEntry(macro, true));
                else if (configured && !runningOnly)
                    idle.Add(ToEntry(macro, false));
            }

            return playing.Concat(idle).Take(MaxEntries).ToList();
        }

        private static OverlayEntry ToEntry(Macro macro, bool running) => new(
            macro.Id,
            string.IsNullOrWhiteSpace(macro.Name) ? "Untitled" : macro.Name,
            string.IsNullOrWhiteSpace(macro.Hotkey) ? "—" : macro.Hotkey!,
            running);

        /// <summary>The overlay is on screen only while it is enabled, Roblox is the active window and there is something to list.</summary>
        public static bool ShouldShow(bool enabled, bool robloxForeground, int entryCount) =>
            enabled && robloxForeground && entryCount > 0;

        /// <summary>Full brightness for <see cref="FadeDelay"/> after the last activity, dimmed afterwards.</summary>
        public static double TargetOpacity(TimeSpan sinceActivity) =>
            sinceActivity < FadeDelay ? ActiveOpacity : IdleOpacity;

        /// <summary>
        /// Top-left corner (device pixels) for an overlay of <paramref name="width"/> × <paramref name="height"/> in
        /// <paramref name="corner"/> of the area (the Roblox window's client area on screen). Kept inside the area when it is small.
        /// </summary>
        public static (int X, int Y) Position(OverlayCorner corner, int left, int top, int right, int bottom, int width, int height)
        {
            int minX = left + EdgeMargin;
            int minY = top + EdgeMargin;
            int maxX = Math.Max(minX, right - EdgeMargin - width);
            int maxY = Math.Max(minY, bottom - EdgeMargin - height);

            return corner switch
            {
                OverlayCorner.TopLeft => (minX, minY),
                OverlayCorner.TopRight => (maxX, minY),
                OverlayCorner.BottomRight => (maxX, maxY),
                _ => (minX, maxY)
            };
        }

        /// <summary>Adds the styles that make the overlay click-through and impossible to activate.</summary>
        public static int OverlayExStyle(int exStyle) =>
            exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;

        public static string CornerLabel(OverlayCorner corner) => corner switch
        {
            OverlayCorner.TopLeft => "Top left",
            OverlayCorner.TopRight => "Top right",
            OverlayCorner.BottomRight => "Bottom right",
            _ => "Bottom left"
        };
    }
}
