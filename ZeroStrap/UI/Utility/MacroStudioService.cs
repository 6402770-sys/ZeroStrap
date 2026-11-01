using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;

using ZeroStrap.Macros;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.UI.Utility
{
    /// <summary>
    /// The seam between Macro Studio's page and the engine: key-name / scan-code lookups through the keyboard layout, and the
    /// engine calls that may take a moment (loading, hotkey reload, stop) run off the UI thread.
    /// </summary>
    public static class MacroStudioService
    {
        private const uint MAPVK_VSC_TO_VK_EX = 3;
        private const uint MAPVK_VK_TO_VSC_EX = 4;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        // ---------------------------------------------------------------- keys

        /// <summary>Virtual key for a scan code (0 when unknown).</summary>
        public static int VirtualKeyOf(ushort scan, bool extended)
        {
            try
            {
                return (int)MapVirtualKey((uint)(scan | (extended ? 0xE000 : 0)), MAPVK_VSC_TO_VK_EX);
            }
            catch
            {
                return 0;
            }
        }

        public static string KeyName(ushort scan, bool extended) => MacroStudioLogic.KeyName(scan, extended, VirtualKeyOf);

        /// <summary>Scan code and extended flag for a WPF key (VirtualKeyFromKey, then MapVirtualKey with MAPVK_VK_TO_VSC_EX).</summary>
        public static bool TryGetScan(Key key, out ushort scan, out bool extended)
        {
            scan = 0;
            extended = false;

            try
            {
                int vk = KeyInterop.VirtualKeyFromKey(key);

                if (vk == 0)
                    return false;

                uint mapped = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC_EX);
                return MacroStudioLogic.TryDecodeScan(mapped, out scan, out extended);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsModifierKey(Key key) =>
            key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

        /// <summary>Resolves the key a PreviewKeyDown really means (Alt arrives as System, IME input as ImeProcessed).</summary>
        public static Key RealKey(KeyEventArgs e) =>
            e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

        public static HotkeyModifiers CurrentModifiers()
        {
            var mods = Keyboard.Modifiers;
            var result = HotkeyModifiers.None;

            if ((mods & ModifierKeys.Control) != 0) result |= HotkeyModifiers.Ctrl;
            if ((mods & ModifierKeys.Alt) != 0) result |= HotkeyModifiers.Alt;
            if ((mods & ModifierKeys.Shift) != 0) result |= HotkeyModifiers.Shift;
            if ((mods & ModifierKeys.Windows) != 0) result |= HotkeyModifiers.Win;

            return result;
        }

        /// <summary>Canonical shortcut text for a key press, or null for a key the runtime cannot register.</summary>
        public static string? ShortcutFromKey(Key key) =>
            MacroStudioLogic.FormatShortcut(CurrentModifiers(), KeyInterop.VirtualKeyFromKey(key));

        // ---------------------------------------------------------------- engine calls

        public static Task<(List<Macro> Macros, MacroStudioSettings Settings)> LoadAsync(MacroRuntime runtime) =>
            Task.Run(() => (runtime.Store.LoadAll(), runtime.SettingsStore.Load()));

        /// <summary>Re-registers every shortcut off the UI thread and returns the per-macro outcome.</summary>
        public static Task<HotkeyReloadResult> ReloadHotkeysAsync(MacroRuntime runtime) => Task.Run(() => runtime.ReloadHotkeys());

        public static Task<bool> StopAsync(MacroRuntime runtime) => Task.Run(() => runtime.Stop());

        public static Task<RecordingResult?> StopRecordingAsync(MacroRuntime runtime) => Task.Run(() => runtime.StopRecording());

        public static Task<MacroImportResult> ImportAsync(MacroRuntime runtime, string path) => Task.Run(() => runtime.Store.Import(path));
    }
}
