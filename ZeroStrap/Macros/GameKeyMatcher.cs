namespace ZeroStrap.Macros
{
    /// <summary>
    /// Decides which game shortcut (<see cref="Hotkey.IsGameShortcut"/>) a low-level keyboard event triggers. A pure state
    /// machine fed by <see cref="HotkeyService"/>'s keyboard hook, so it can be tested without Win32. It never decides whether a
    /// key reaches the game (the hook always passes it on); it only answers "fire this shortcut or not". Rules:
    /// <list type="bullet">
    /// <item>Injected input (macro playback, other tools) is ignored entirely, so playback can never trigger a shortcut.</item>
    /// <item>A shortcut fires on the physical down transition only: keyboard auto-repeat while the key is held does nothing.</item>
    /// <item>Modifiers must match exactly ("Q" does not fire for Shift+Q, and the other way round).</item>
    /// <item>Nothing fires unless Roblox is the active window.</item>
    /// <item>"/" in Roblox opens the chat box: matching pauses until Enter or Escape is pressed in Roblox.</item>
    /// </list>
    /// Not thread-safe: the hook calls it from one thread.
    /// </summary>
    internal sealed class GameKeyMatcher
    {
        // A held key auto-repeats at least once a second (the longest Windows repeat delay), so a second down for a key that saw
        // no up for longer than this means its up was missed (for example while the secure desktop was showing): a new press.
        internal const uint StaleDownMs = 1500;

        // Physically held keys (left/right modifier codes are kept apart) and the hook time of their last down.
        private readonly Dictionary<int, uint> _down = new();

        /// <summary>True while the Roblox chat box is assumed open ("/" seen, no Enter or Escape yet).</summary>
        public bool ChatOpen { get; private set; }

        /// <summary>
        /// Feeds one keyboard event. Returns the shortcut to fire, or null.
        /// <paramref name="isBound"/> says whether a shortcut is registered; <paramref name="robloxFocused"/> is only asked
        /// when the answer matters (a candidate match or a chat key), because it may look up the foreground process.
        /// <paramref name="isPhysicallyDown"/>, when given, lets a modifier whose release was missed be dropped.
        /// </summary>
        public Hotkey? OnKey(int vk, bool down, bool injected, uint timeMs, Func<Hotkey, bool> isBound, Func<bool> robloxFocused,
            Func<int, bool>? isPhysicallyDown = null)
        {
            if (injected)
                return null;

            if (!down)
            {
                _down.Remove(vk);
                return null;
            }

            if (_down.TryGetValue(vk, out uint last) && unchecked(timeMs - last) <= StaleDownMs)
            {
                _down[vk] = timeMs; // auto-repeat
                return null;
            }

            _down[vk] = timeMs;

            if (IsModifier(vk))
                return null;

            var modifiers = CurrentModifiers(isPhysicallyDown);
            bool plain = (modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0;

            if (vk == Hotkey.SlashKey && plain && !ChatOpen)
            {
                if (robloxFocused())
                    ChatOpen = true;

                return null;
            }

            if (ChatOpen)
            {
                if ((vk == Hotkey.EnterKey || vk == Hotkey.EscapeKey) && robloxFocused())
                    ChatOpen = false;

                return null;
            }

            var hotkey = new Hotkey(modifiers, vk);

            if (!hotkey.IsGameShortcut || !isBound(hotkey) || !robloxFocused())
                return null;

            return hotkey;
        }

        /// <summary>Forgets every held key and the chat state (the hook was removed and will miss the releases).</summary>
        public void Reset()
        {
            _down.Clear();
            ChatOpen = false;
        }

        private HotkeyModifiers CurrentModifiers(Func<int, bool>? isPhysicallyDown)
        {
            var result = HotkeyModifiers.None;

            foreach (int key in _down.Keys.ToList())
            {
                var flag = ModifierOf(key);

                if (flag == HotkeyModifiers.None)
                    continue;

                if (isPhysicallyDown is not null && !SafeIsDown(isPhysicallyDown, key))
                {
                    _down.Remove(key); // its release was missed
                    continue;
                }

                result |= flag;
            }

            return result;
        }

        private static bool SafeIsDown(Func<int, bool> isPhysicallyDown, int key)
        {
            try
            {
                return isPhysicallyDown(key);
            }
            catch
            {
                return true; // keep what the hook saw
            }
        }

        private static bool IsModifier(int vk) => ModifierOf(vk) != HotkeyModifiers.None;

        internal static HotkeyModifiers ModifierOf(int vk) => vk switch
        {
            0x10 or 0xA0 or 0xA1 => HotkeyModifiers.Shift,
            0x11 or 0xA2 or 0xA3 => HotkeyModifiers.Ctrl,
            0x12 or 0xA4 or 0xA5 => HotkeyModifiers.Alt,
            0x5B or 0x5C => HotkeyModifiers.Win,
            _ => HotkeyModifiers.None
        };
    }
}
