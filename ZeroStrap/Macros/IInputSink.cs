namespace ZeroStrap.Macros
{
    /// <summary>Where macro input goes. The real implementation is <see cref="SendInputSink"/>; tests use a recording fake.</summary>
    public interface IInputSink
    {
        void KeyDown(ushort scan, bool extended);
        void KeyUp(ushort scan, bool extended);
        void MouseDown(MouseButton button);
        void MouseUp(MouseButton button);
        void MoveRelative(int dx, int dy);
        void MoveAbsolute(int x, int y);
    }

    /// <summary>Decides whether macro input may currently be sent (for the RobloxOnly focus rule).</summary>
    public interface IFocusGate
    {
        bool IsAllowed();
    }

    /// <summary>Thrown by a sink when the OS refused to inject an event (UIPI: the foreground window runs elevated).</summary>
    public sealed class InputBlockedException : InvalidOperationException
    {
        public InputBlockedException()
            : base("Windows blocked the input: the active window runs as administrator.")
        {
        }
    }
}
