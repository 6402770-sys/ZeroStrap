namespace ZeroStrap.Macros
{
    public enum MacroActionKind { Key, Mouse, MouseMoveTo, Camera, Wait, Group }

    public enum MacroKeyMode { Down, Up, Tap }

    public enum MouseButton { Left, Right, Middle, X1, X2 }

    public enum MacroButtonMode { Down, Up, Click }

    public enum MacroEasing { Linear, EaseInOut }

    public enum MacroRepeatMode { Once, Count, UntilStopped }

    public enum MacroFocusRule { AnyWindow, RobloxOnly }

    /// <summary>How many times a macro runs.</summary>
    public sealed class MacroRepeat
    {
        public MacroRepeatMode Mode { get; set; } = MacroRepeatMode.Once;

        /// <summary>Only used by <see cref="MacroRepeatMode.Count"/>.</summary>
        public int Count { get; set; } = 1;

        public MacroRepeat Clone() => new() { Mode = Mode, Count = Count };
    }

    /// <summary>
    /// One step of a macro. This is a flat class with a <see cref="Kind"/> discriminator (System.Text.Json on .NET 6 has no
    /// polymorphic support); only the fields that belong to the kind are meaningful, the others stay at their defaults.
    /// </summary>
    public sealed class MacroAction
    {
        public MacroActionKind Kind { get; set; } = MacroActionKind.Wait;
        public int DelayBeforeMs { get; set; }
        public bool Enabled { get; set; } = true;
        public string? Note { get; set; }

        // Key
        public ushort ScanCode { get; set; }
        public bool Extended { get; set; }
        public MacroKeyMode KeyMode { get; set; } = MacroKeyMode.Tap;

        /// <summary>Hold time for Key Tap and Mouse Click.</summary>
        public int HoldMs { get; set; }

        // Mouse
        public MouseButton Button { get; set; } = MouseButton.Left;
        public MacroButtonMode ButtonMode { get; set; } = MacroButtonMode.Click;

        // MouseMoveTo (absolute, virtual-desktop pixels)
        public int X { get; set; }
        public int Y { get; set; }

        // Camera (relative movement spread over DurationMs)
        public int Dx { get; set; }
        public int Dy { get; set; }
        public int DurationMs { get; set; }
        public MacroEasing Easing { get; set; } = MacroEasing.Linear;
        public bool HoldRightButton { get; set; }

        // Wait
        public int Ms { get; set; }

        // Group
        public List<MacroAction> Actions { get; set; } = new();
        public int GroupRepeat { get; set; } = 1;

        public MacroAction Clone()
        {
            var copy = (MacroAction)MemberwiseClone();
            copy.Actions = (Actions ?? new()).Select(a => a.Clone()).ToList();
            return copy;
        }
    }

    /// <summary>A saved macro (Macros\&lt;id&gt;.json / .zeromacro).</summary>
    public sealed class Macro
    {
        public int SchemaVersion { get; set; } = 1;
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }

        /// <summary>Activation shortcut such as "F6" or "Ctrl+Shift+F7"; null when unbound.</summary>
        public string? Hotkey { get; set; }

        public MacroRepeat Repeat { get; set; } = new();
        public double Speed { get; set; } = 1.0;
        public MacroFocusRule FocusRule { get; set; } = MacroFocusRule.RobloxOnly;
        public List<MacroAction> Actions { get; set; } = new();

        public Macro Clone()
        {
            var copy = (Macro)MemberwiseClone();
            copy.Repeat = (Repeat ?? new()).Clone();
            copy.Actions = (Actions ?? new()).Select(a => a.Clone()).ToList();
            return copy;
        }
    }

    /// <summary>JSON settings shared by the macro store, import and export. Enums are strings; unknown values are rejected.</summary>
    internal static class MacroJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keeps "Ctrl+F7" readable in the file
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
        };

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

        public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
    }
}
