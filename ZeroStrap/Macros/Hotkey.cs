namespace ZeroStrap.Macros
{
    /// <summary>Modifier flags. The values match the Win32 MOD_* constants so they can be passed to RegisterHotKey as-is.</summary>
    [Flags]
    public enum HotkeyModifiers
    {
        None = 0,
        Alt = 0x1,
        Ctrl = 0x2,
        Shift = 0x4,
        Win = 0x8
    }

    /// <summary>
    /// A keyboard shortcut: modifier flags plus a Win32 virtual-key code. Pure (no WPF types), parsed from and formatted to
    /// strings like "Ctrl+Shift+End". The canonical text order is Ctrl+Alt+Shift+Win+Key.
    /// </summary>
    public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
    {
        /// <summary>"/" (VK_OEM_2): opens the Roblox chat box.</summary>
        public const int SlashKey = 0xBF;
        public const int EnterKey = 0x0D;
        public const int EscapeKey = 0x1B;

        private static readonly Dictionary<string, int> NameToKey = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, string> KeyToName = new();

        static Hotkey()
        {
            for (char c = 'A'; c <= 'Z'; c++)
                Add(c.ToString(), c);

            for (char c = '0'; c <= '9'; c++)
                Add(c.ToString(), c);

            for (int i = 1; i <= 24; i++)
                Add("F" + i, 0x6F + i);

            for (int i = 0; i <= 9; i++)
            {
                Add("Numpad" + i, 0x60 + i);
                Alias("Num" + i, 0x60 + i);
            }

            Add("NumpadMultiply", 0x6A);
            Add("NumpadAdd", 0x6B);
            Add("NumpadSubtract", 0x6D);
            Add("NumpadDecimal", 0x6E);
            Add("NumpadDivide", 0x6F);

            Add("Backspace", 0x08);
            Add("Tab", 0x09);
            Add("Enter", 0x0D);
            Add("Escape", 0x1B);
            Add("Space", 0x20);
            Add("PageUp", 0x21);
            Add("PageDown", 0x22);
            Add("End", 0x23);
            Add("Home", 0x24);
            Add("Left", 0x25);
            Add("Up", 0x26);
            Add("Right", 0x27);
            Add("Down", 0x28);
            Add("Insert", 0x2D);
            Add("Delete", 0x2E);

            // Punctuation keys by their US-layout position; game shortcuts may use them.
            Add("Semicolon", 0xBA);
            Add("Equals", 0xBB);
            Add("Comma", 0xBC);
            Add("Minus", 0xBD);
            Add("Period", 0xBE);
            Add("Slash", SlashKey);
            Add("Backquote", 0xC0);
            Add("LeftBracket", 0xDB);
            Add("Backslash", 0xDC);
            Add("RightBracket", 0xDD);
            Add("Quote", 0xDE);

            Alias("Esc", 0x1B);
            Alias("Return", 0x0D);
            Alias("Back", 0x08);
            Alias("PgUp", 0x21);
            Alias("PgDn", 0x22);
            Alias("Ins", 0x2D);
            Alias("Del", 0x2E);
        }

        private static void Add(string name, int vk)
        {
            NameToKey[name] = vk;
            KeyToName[vk] = name;
        }

        private static void Alias(string name, int vk) => NameToKey[name] = vk;

        /// <summary>False for an unknown key code (and for a default struct).</summary>
        public bool IsValid => KeyToName.ContainsKey(VirtualKey);

        /// <summary>
        /// Whether this works as a global shortcut (registered with Windows, which captures the key): the emergency stop, the
        /// record stop key, and macro shortcuts with Ctrl/Alt/Win or on F1-F24, End, Home, Insert, Delete, PageUp, PageDown.
        /// Plain typing keys (letters, digits, Space, Enter, Escape, Tab, Backspace, numpad keys, arrows, punctuation) would be
        /// taken away from every app, so they need Ctrl, Alt or Win here (Shift alone does not make a typing key safe).
        /// </summary>
        public bool IsAllowedShortcut
        {
            get
            {
                if (!IsValid)
                    return false;

                if ((Modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0)
                    return true;

                return (VirtualKey >= 0x70 && VirtualKey <= 0x87)
                    || VirtualKey == 0x21 || VirtualKey == 0x22 || VirtualKey == 0x23 || VirtualKey == 0x24
                    || VirtualKey == 0x2D || VirtualKey == 0x2E;
            }
        }

        /// <summary>
        /// A macro shortcut on a typing key (optionally with Shift): it is watched, never captured, so the game still gets the
        /// key, and it only acts while Roblox is the active window. Escape, Enter and "/" are reserved because they open and
        /// close the Roblox chat box, which pauses these shortcuts.
        /// </summary>
        public bool IsGameShortcut =>
            IsValid && !IsAllowedShortcut && VirtualKey != EscapeKey && VirtualKey != EnterKey && VirtualKey != SlashKey;

        /// <summary>Any shortcut a macro may use: global (<see cref="IsAllowedShortcut"/>) or game (<see cref="IsGameShortcut"/>).</summary>
        public bool IsMacroShortcut => IsAllowedShortcut || IsGameShortcut;

        /// <summary>Parses and also requires <see cref="IsAllowedShortcut"/> (the emergency stop and record stop key).</summary>
        public static bool TryParseShortcut(string? text, out Hotkey hotkey) =>
            TryParse(text, out hotkey) && hotkey.IsAllowedShortcut;

        /// <summary>Parses and also requires <see cref="IsMacroShortcut"/>.</summary>
        public static bool TryParseMacroShortcut(string? text, out Hotkey hotkey) =>
            TryParse(text, out hotkey) && hotkey.IsMacroShortcut;

        public static bool TryGetKeyName(int virtualKey, out string name)
        {
            if (KeyToName.TryGetValue(virtualKey, out string? found))
            {
                name = found;
                return true;
            }

            name = "";
            return false;
        }

        public static bool TryGetVirtualKey(string name, out int virtualKey) => NameToKey.TryGetValue(name.Trim(), out virtualKey);

        /// <summary>
        /// Accepts "F6", "Ctrl+Shift+End" and case/alias variants ("control+esc"). Rejects empty text, modifier-only combos,
        /// repeated modifiers, unknown keys and anything with more than one non-modifier key.
        /// </summary>
        public static bool TryParse(string? text, out Hotkey hotkey)
        {
            hotkey = default;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            string[] parts = text.Split('+');
            var modifiers = HotkeyModifiers.None;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!TryParseModifier(parts[i].Trim(), out var flag) || (modifiers & flag) != 0)
                    return false;

                modifiers |= flag;
            }

            string keyText = parts[^1].Trim();

            if (keyText.Length == 0 || TryParseModifier(keyText, out _) || !NameToKey.TryGetValue(keyText, out int vk))
                return false;

            hotkey = new Hotkey(modifiers, vk);
            return true;
        }

        private static bool TryParseModifier(string text, out HotkeyModifiers flag)
        {
            switch (text.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    flag = HotkeyModifiers.Ctrl;
                    return true;
                case "alt":
                    flag = HotkeyModifiers.Alt;
                    return true;
                case "shift":
                    flag = HotkeyModifiers.Shift;
                    return true;
                case "win":
                case "windows":
                    flag = HotkeyModifiers.Win;
                    return true;
                default:
                    flag = HotkeyModifiers.None;
                    return false;
            }
        }

        /// <summary>Canonical text, e.g. "Ctrl+Alt+Shift+Win+F6". An unknown key code is written as "VK_XX".</summary>
        public override string ToString()
        {
            var sb = new StringBuilder();

            if ((Modifiers & HotkeyModifiers.Ctrl) != 0) sb.Append("Ctrl+");
            if ((Modifiers & HotkeyModifiers.Alt) != 0) sb.Append("Alt+");
            if ((Modifiers & HotkeyModifiers.Shift) != 0) sb.Append("Shift+");
            if ((Modifiers & HotkeyModifiers.Win) != 0) sb.Append("Win+");

            sb.Append(KeyToName.TryGetValue(VirtualKey, out string? name) ? name : "VK_" + VirtualKey.ToString("X2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
