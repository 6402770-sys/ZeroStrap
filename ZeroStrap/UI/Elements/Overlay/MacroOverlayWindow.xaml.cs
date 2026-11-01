using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.UI.Elements.Overlay
{
    /// <summary>Tiny click-through list of macros drawn over the game. Never takes focus; positioned and shown by <c>MacroOverlayHost</c>.</summary>
    public partial class MacroOverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;

        private static readonly Brush s_dotRunning = Frozen(new SolidColorBrush(Color.FromRgb(0x3F, 0xBF, 0x7F)));
        private static readonly Brush s_dotIdleStroke = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush s_nameRunning = Frozen(new SolidColorBrush(Colors.White));
        private static readonly Brush s_nameIdle = Frozen(new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)));

        /// <summary>One displayed row.</summary>
        public sealed record OverlayRow(string Name, string Hotkey, bool Running)
        {
            public Brush DotFill => Running ? s_dotRunning : Brushes.Transparent;

            public Brush DotStroke => Running ? s_dotRunning : s_dotIdleStroke;

            public Brush NameBrush => Running ? s_nameRunning : s_nameIdle;
        }

        private readonly ObservableCollection<OverlayRow> _rows = new();

        public MacroOverlayWindow()
        {
            InitializeComponent();
            Rows.ItemsSource = _rows;
        }

        /// <summary>The rows currently shown.</summary>
        public IReadOnlyList<OverlayRow> DisplayedRows => _rows;

        public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

        public void SetEntries(IReadOnlyList<OverlayEntry> entries)
        {
            var next = entries.Select(e => new OverlayRow(e.Name, e.Hotkey, e.Running)).ToList();

            if (next.SequenceEqual(_rows))
                return;

            _rows.Clear();

            foreach (var row in next)
                _rows.Add(row);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(MacroOverlayLogic.OverlayExStyle((int)style)));
            }
            catch
            {
                // Without the styles the overlay would be clickable; better to have none than a broken one.
            }
        }

        private static Brush Frozen(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        // GetWindowLongPtr only exists on 64-bit Windows; the 32-bit export is GetWindowLong.
        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int index) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index) : new IntPtr(GetWindowLong32(hWnd, index));

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value) =>
            IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, index, value) : new IntPtr(SetWindowLong32(hWnd, index, value.ToInt32()));

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
