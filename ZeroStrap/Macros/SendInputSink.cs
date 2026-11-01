using static ZeroStrap.Macros.MacroNative;

namespace ZeroStrap.Macros
{
    /// <summary>
    /// Injects input with SendInput only (scan-code keys, relative and absolute mouse). Every event carries
    /// <see cref="MacroNative.InjectionMarker"/> so the recorder can tell it apart from real input.
    /// </summary>
    public sealed class SendInputSink : IInputSink
    {
        private readonly Func<INPUT[], uint> _send;
        private readonly Func<(int Left, int Top, int Width, int Height)> _virtualScreen;
        private readonly Action<string> _log;
        private bool _loggedBlocked;

        /// <summary>True when the last SendInput call injected fewer events than requested (typically UIPI blocking an elevated window).</summary>
        public bool LastSendBlocked { get; private set; }

        public SendInputSink(Action<string>? log = null)
            : this(Send, ReadVirtualScreen, log)
        {
        }

        /// <summary>Test seam: replaces the real SendInput call and the virtual-screen query.</summary>
        internal SendInputSink(Func<INPUT[], uint> send, Func<(int Left, int Top, int Width, int Height)> virtualScreen, Action<string>? log)
        {
            _send = send;
            _virtualScreen = virtualScreen;
            _log = log ?? (_ => { });
        }

        private static uint Send(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, InputSize);

        private static (int Left, int Top, int Width, int Height) ReadVirtualScreen() =>
            (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
             GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

        public void KeyDown(ushort scan, bool extended) => Dispatch(BuildKey(scan, extended, keyUp: false));

        public void KeyUp(ushort scan, bool extended) => Dispatch(BuildKey(scan, extended, keyUp: true));

        public void MouseDown(MouseButton button) => Dispatch(BuildButton(button, down: true));

        public void MouseUp(MouseButton button) => Dispatch(BuildButton(button, down: false));

        public void MoveRelative(int dx, int dy) => Dispatch(BuildMouse(MOUSEEVENTF_MOVE, dx, dy, 0));

        // Known limitation: with monitors at mixed DPI scaling, SendInput's normalised virtual-desktop coordinates can land a few
        // pixels off the requested point. Relative (camera) movement is unaffected.
        public void MoveAbsolute(int x, int y)
        {
            var (left, top, width, height) = _virtualScreen();

            Dispatch(BuildMouse(
                MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                NormalizeAbsolute(x, left, width),
                NormalizeAbsolute(y, top, height),
                0));
        }

        private void Dispatch(INPUT input)
        {
            uint sent = _send(new[] { input });
            LastSendBlocked = sent < 1;

            if (!LastSendBlocked)
                return;

            if (!_loggedBlocked)
            {
                _loggedBlocked = true;
                _log("SendInput injected fewer events than requested; input is probably blocked because the target window is elevated.");
            }

            // A blocked event is a failure, not a no-op: the player must know an Up did not land so it can retry the release.
            throw new InputBlockedException();
        }

        internal static INPUT BuildKey(ushort scan, bool extended, bool keyUp)
        {
            uint flags = KEYEVENTF_SCANCODE;

            if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
            if (keyUp) flags |= KEYEVENTF_KEYUP;

            var input = new INPUT { type = INPUT_KEYBOARD };
            input.U.ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = flags, time = 0, dwExtraInfo = (UIntPtr)InjectionMarker };
            return input;
        }

        internal static INPUT BuildButton(MouseButton button, bool down)
        {
            (uint flags, uint data) = button switch
            {
                MouseButton.Left => (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0u),
                MouseButton.Right => (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0u),
                MouseButton.Middle => (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0u),
                MouseButton.X1 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON1),
                MouseButton.X2 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON2),
                _ => throw new ArgumentOutOfRangeException(nameof(button))
            };

            return BuildMouse(flags, 0, 0, data);
        }

        internal static INPUT BuildMouse(uint flags, int dx, int dy, uint data)
        {
            var input = new INPUT { type = INPUT_MOUSE };
            input.U.mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, time = 0, dwExtraInfo = (UIntPtr)InjectionMarker };
            return input;
        }
    }
}
