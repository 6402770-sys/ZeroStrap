using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ZeroStrap.Macros;

namespace ZeroStrap.UI.ViewModels.Shell
{
    /// <summary>One row of the action list: the action, how deep it is nested and the group it lives in (null at the top level).</summary>
    public sealed record FlatAction(MacroAction Action, int Depth, MacroAction? Parent);

    public enum RecordingMergeMode
    {
        Replace,
        Append
    }

    /// <summary>Pure presentation and editing logic of Macro Studio (no WPF, no I/O) so it can be tested.</summary>
    public static class MacroStudioLogic
    {
        public const string DefaultMacroName = "New macro";

        public const string AutomationNotice =
            "Some Roblox experiences don't allow automation. You're responsible for following each experience's rules and Roblox's Terms of Use.";

        public const string NotAllowedShortcutText = "Not allowed: use a function key or add Ctrl/Alt";

        public const string InUseText = "This shortcut is used by another app";

        public const string DuplicateText = "Already used in ZeroStrap";

        // ---------------------------------------------------------------- key names

        private static readonly Dictionary<int, string> VkNames = BuildVkNames();

        private static Dictionary<int, string> BuildVkNames()
        {
            var names = new Dictionary<int, string>();

            for (char c = 'A'; c <= 'Z'; c++)
                names[c] = c.ToString();

            for (char c = '0'; c <= '9'; c++)
                names[c] = c.ToString();

            for (int i = 1; i <= 24; i++)
                names[0x6F + i] = "F" + i.ToString(CultureInfo.InvariantCulture);

            for (int i = 0; i <= 9; i++)
                names[0x60 + i] = "Numpad " + i.ToString(CultureInfo.InvariantCulture);

            names[0x6A] = "Numpad *";
            names[0x6B] = "Numpad +";
            names[0x6D] = "Numpad -";
            names[0x6E] = "Numpad .";
            names[0x6F] = "Numpad /";
            names[0x08] = "Backspace";
            names[0x09] = "Tab";
            names[0x0D] = "Enter";
            names[0x10] = "Shift";
            names[0x11] = "Ctrl";
            names[0x12] = "Alt";
            names[0x13] = "Pause";
            names[0x14] = "Caps Lock";
            names[0x1B] = "Esc";
            names[0x20] = "Space";
            names[0x21] = "Page Up";
            names[0x22] = "Page Down";
            names[0x23] = "End";
            names[0x24] = "Home";
            names[0x25] = "Left";
            names[0x26] = "Up";
            names[0x27] = "Right";
            names[0x28] = "Down";
            names[0x2C] = "Print Screen";
            names[0x2D] = "Insert";
            names[0x2E] = "Delete";
            names[0x5B] = "Left Win";
            names[0x5C] = "Right Win";
            names[0x5D] = "Menu";
            names[0x90] = "Num Lock";
            names[0x91] = "Scroll Lock";
            names[0xA0] = "Left Shift";
            names[0xA1] = "Right Shift";
            names[0xA2] = "Left Ctrl";
            names[0xA3] = "Right Ctrl";
            names[0xA4] = "Left Alt";
            names[0xA5] = "Right Alt";
            names[0xBA] = ";";
            names[0xBB] = "=";
            names[0xBC] = ",";
            names[0xBD] = "-";
            names[0xBE] = ".";
            names[0xBF] = "/";
            names[0xC0] = "`";
            names[0xDB] = "[";
            names[0xDC] = "\\";
            names[0xDD] = "]";
            names[0xDE] = "'";

            return names;
        }

        // Non-extended scan codes 0x47..0x53 are the numpad even though Windows reports the navigation VK for them.
        private static readonly Dictionary<ushort, string> NumpadScans = new()
        {
            [0x47] = "Numpad 7", [0x48] = "Numpad 8", [0x49] = "Numpad 9",
            [0x4B] = "Numpad 4", [0x4C] = "Numpad 5", [0x4D] = "Numpad 6",
            [0x4F] = "Numpad 1", [0x50] = "Numpad 2", [0x51] = "Numpad 3",
            [0x52] = "Numpad 0", [0x53] = "Numpad ."
        };

        public static string? KeyNameFromVirtualKey(int vk) => VkNames.TryGetValue(vk, out string? name) ? name : null;

        /// <summary>Fallback label for a scan code nothing could name.</summary>
        public static string ScanFallback(ushort scan) => "Scan 0x" + scan.ToString("X2", CultureInfo.InvariantCulture);

        /// <summary>
        /// Readable name of a key from its scan code. <paramref name="virtualKeyOf"/> turns (scan, extended) into a virtual key
        /// (MapVirtualKey with MAPVK_VSC_TO_VK_EX); without it, or when it returns 0 or an unknown key, the name is "Scan 0x1E".
        /// </summary>
        public static string KeyName(ushort scan, bool extended, Func<ushort, bool, int>? virtualKeyOf)
        {
            if (!extended && NumpadScans.TryGetValue(scan, out string? numpad))
                return numpad;

            if (extended && scan == 0x1C)
                return "Numpad Enter";

            if (extended && scan == 0x35)
                return "Numpad /";

            int vk = 0;

            try
            {
                vk = virtualKeyOf?.Invoke(scan, extended) ?? 0;
            }
            catch
            {
                // A failing resolver only costs the readable name.
            }

            return KeyNameFromVirtualKey(vk) ?? ScanFallback(scan);
        }

        /// <summary>
        /// Splits a MapVirtualKey(vk, MAPVK_VK_TO_VSC_EX) result into scan code and extended flag (high byte 0xE0 or 0xE1).
        /// False when Windows has no scan code for the key or it is outside the supported 1-127 range.
        /// </summary>
        public static bool TryDecodeScan(uint mapped, out ushort scan, out bool extended)
        {
            scan = (ushort)(mapped & 0xFF);
            int high = (int)((mapped >> 8) & 0xFF);
            extended = high == 0xE0 || high == 0xE1;

            return scan >= 1 && scan <= MacroValidator.MaxScanCode;
        }

        // ---------------------------------------------------------------- summaries

        public static string ButtonName(MouseButton button) => button switch
        {
            MouseButton.Left => "Left",
            MouseButton.Right => "Right",
            MouseButton.Middle => "Middle",
            MouseButton.X1 => "Back",
            MouseButton.X2 => "Forward",
            _ => button.ToString()
        };

        /// <summary>"Key W · tap · 120 ms", "Left click", "Camera → 300, ↑ 40 px · 0.8 s", "Wait 500 ms", "Group ×3 (4 actions)".</summary>
        public static string Summary(MacroAction a, Func<ushort, bool, string>? keyName = null)
        {
            switch (a.Kind)
            {
                case MacroActionKind.Key:
                {
                    string key = keyName?.Invoke(a.ScanCode, a.Extended) ?? ScanFallback(a.ScanCode);

                    return a.KeyMode switch
                    {
                        MacroKeyMode.Down => $"Key {key} · hold down",
                        MacroKeyMode.Up => $"Key {key} · release",
                        _ => a.HoldMs > 0 ? $"Key {key} · tap · {a.HoldMs} ms" : $"Key {key} · tap"
                    };
                }

                case MacroActionKind.Mouse:
                {
                    string button = ButtonName(a.Button);

                    return a.ButtonMode switch
                    {
                        MacroButtonMode.Down => $"{button} button down",
                        MacroButtonMode.Up => $"{button} button up",
                        _ => a.HoldMs > 0 ? $"{button} click · {a.HoldMs} ms" : $"{button} click"
                    };
                }

                case MacroActionKind.MouseMoveTo:
                    return $"Move to {a.X}, {a.Y}";

                case MacroActionKind.Camera:
                {
                    var parts = new List<string>();

                    if (a.Dx != 0)
                        parts.Add((a.Dx > 0 ? "→ " : "← ") + Math.Abs((long)a.Dx).ToString(CultureInfo.InvariantCulture));

                    if (a.Dy != 0)
                        parts.Add((a.Dy < 0 ? "↑ " : "↓ ") + Math.Abs((long)a.Dy).ToString(CultureInfo.InvariantCulture));

                    string move = parts.Count == 0 ? "no movement" : string.Join(", ", parts) + " px";
                    return $"Camera {move} · {FormatSeconds(a.DurationMs)}";
                }

                case MacroActionKind.Wait:
                    return $"Wait {a.Ms} ms";

                case MacroActionKind.Group:
                {
                    int count = a.Actions?.Count ?? 0;
                    return $"Group ×{a.GroupRepeat} ({count} {(count == 1 ? "action" : "actions")})";
                }

                default:
                    return a.Kind.ToString();
            }
        }

        /// <summary>The WPF-UI symbol name (a SymbolRegular member) for an action row.</summary>
        public static string IconName(MacroActionKind kind) => kind switch
        {
            MacroActionKind.Key => "Keyboard24",
            MacroActionKind.Mouse => "CursorClick24",
            MacroActionKind.MouseMoveTo => "ArrowMove24",
            MacroActionKind.Camera => "Eye24",
            MacroActionKind.Wait => "Timer24",
            MacroActionKind.Group => "ArrowRepeatAll24",
            _ => "Box24"
        };

        public static string KindLabel(MacroActionKind kind) => kind switch
        {
            MacroActionKind.MouseMoveTo => "Move to",
            _ => kind.ToString()
        };

        /// <summary>"0.8 s", "1.5 s", "12 s".</summary>
        public static string FormatSeconds(long ms)
        {
            double seconds = Math.Max(0, ms) / 1000.0;
            return seconds.ToString(seconds >= 10 ? "0" : "0.##", CultureInfo.InvariantCulture) + " s";
        }

        /// <summary>"250 ms", "2.5 s", "1 min 5 s".</summary>
        public static string FormatDuration(long ms)
        {
            if (ms == long.MaxValue)
                return "a very long time";

            if (ms < 1000)
                return Math.Max(0, ms).ToString(CultureInfo.InvariantCulture) + " ms";

            if (ms < 60000)
                return FormatSeconds(ms);

            long totalSeconds = (long)Math.Round(ms / 1000.0, MidpointRounding.AwayFromZero);
            long minutes = totalSeconds / 60;
            long seconds = totalSeconds % 60;

            if (minutes >= 60)
                return $"{minutes / 60} h {minutes % 60} min";

            return seconds == 0 ? $"{minutes} min" : $"{minutes} min {seconds} s";
        }

        /// <summary>Estimated run time line, "per loop" when the macro repeats until stopped.</summary>
        public static string EstimateText(Macro macro)
        {
            long ms = MacroTiming.EstimateDurationMs(macro);
            string text = "About " + FormatDuration(ms);
            return macro.Repeat?.Mode == MacroRepeatMode.UntilStopped ? text + " per loop" : text;
        }

        public static string ActionCountText(int count) => count == 1 ? "1 action" : $"{count} actions";

        // ---------------------------------------------------------------- shortcuts and status

        /// <summary>Canonical shortcut text for a captured key press; null when the key is not one the runtime can register.</summary>
        public static string? FormatShortcut(HotkeyModifiers modifiers, int virtualKey)
        {
            var hotkey = new Hotkey(modifiers, virtualKey);
            return hotkey.IsValid ? hotkey.ToString() : null;
        }

        /// <summary>Why a shortcut cannot be used, or null when it is fine (or empty).</summary>
        public static string? ShortcutIssue(string? shortcut)
        {
            if (string.IsNullOrWhiteSpace(shortcut))
                return null;

            return Hotkey.TryParseShortcut(shortcut, out _) ? null : NotAllowedShortcutText;
        }

        /// <summary>Registration status line under the shortcut box; empty when everything is fine or there is no shortcut.</summary>
        public static string HotkeyStatusText(Macro macro, HotkeyRegistration? registration)
        {
            if (string.IsNullOrWhiteSpace(macro.Hotkey))
                return "";

            string? issue = ShortcutIssue(macro.Hotkey);

            if (issue is not null)
                return issue;

            if (!macro.Enabled)
                return "Turn this macro on to use its shortcut";

            if (registration is null)
                return "";

            return registration.Value.Status switch
            {
                HotkeyStatus.InUse => InUseText,
                HotkeyStatus.Duplicate => DuplicateText,
                HotkeyStatus.Invalid => NotAllowedShortcutText,
                _ => ""
            };
        }

        /// <summary>The state line under the header.</summary>
        public static string StateLine(MacroRuntimeState state, string? macroName, bool paused, string recordStopKey)
        {
            switch (state)
            {
                case MacroRuntimeState.Recording:
                    return $"Recording — press {recordStopKey} to stop";

                case MacroRuntimeState.Running:
                    string running = string.IsNullOrWhiteSpace(macroName) ? "Running" : "Running " + macroName;
                    return paused ? "Paused — Roblox isn't focused" : running;

                default:
                    return "Idle";
            }
        }

        public static string StartResultText(MacroStartResult result) => result switch
        {
            MacroStartResult.Started => "",
            MacroStartResult.AlreadyRunning => "A macro is already running. Stop it first.",
            MacroStartResult.RecordingInProgress => "Finish the recording before running a macro.",
            MacroStartResult.NotFound => "That macro could not be loaded. Check it for errors.",
            MacroStartResult.ShuttingDown => "ZeroStrap is closing.",
            _ => "The macro could not be started."
        };

        public static string RecordResultText(RecordingStartResult result) => result switch
        {
            RecordingStartResult.Started => "",
            RecordingStartResult.MacroRunning => "Stop the running macro before recording.",
            RecordingStartResult.AlreadyRecording => "A recording is already in progress.",
            RecordingStartResult.ShuttingDown => "ZeroStrap is closing.",
            _ => "Recording could not start."
        };

        public static string ListMeta(Macro macro)
        {
            string shortcut = string.IsNullOrWhiteSpace(macro.Hotkey) ? "No shortcut" : macro.Hotkey!;
            return $"{shortcut} · {ActionCountText(CountAll(macro.Actions))}";
        }

        // ---------------------------------------------------------------- creating things

        public static string UniqueName(string baseName, IEnumerable<string> existing)
        {
            var taken = new HashSet<string>(existing, StringComparer.CurrentCultureIgnoreCase);

            if (!taken.Contains(baseName))
                return baseName;

            for (int i = 2; ; i++)
            {
                string candidate = $"{baseName} {i}";

                if (!taken.Contains(candidate))
                    return candidate;
            }
        }

        public static string RecordingName(DateTime now) => "Recording " + now.ToString("HH:mm", CultureInfo.InvariantCulture);

        public static Macro NewMacro(IEnumerable<string> existingNames, string? name = null, IEnumerable<MacroAction>? actions = null)
        {
            var macro = new Macro
            {
                Id = MacroValidator.NewId(),
                Name = UniqueName(string.IsNullOrWhiteSpace(name) ? DefaultMacroName : name!, existingNames),
                Enabled = true
            };

            if (actions is not null)
                macro.Actions = actions.Select(a => a.Clone()).ToList();

            return macro;
        }

        /// <summary>A new action with sensible, valid defaults for its kind.</summary>
        public static MacroAction NewAction(MacroActionKind kind)
        {
            var action = new MacroAction { Kind = kind };

            switch (kind)
            {
                case MacroActionKind.Key:
                    action.ScanCode = 0x11; // W
                    action.KeyMode = MacroKeyMode.Tap;
                    action.HoldMs = 50;
                    break;
                case MacroActionKind.Mouse:
                    action.Button = MouseButton.Left;
                    action.ButtonMode = MacroButtonMode.Click;
                    action.HoldMs = 50;
                    break;
                case MacroActionKind.Camera:
                    action.Dx = 200;
                    action.DurationMs = 300;
                    break;
                case MacroActionKind.Wait:
                    action.Ms = 500;
                    break;
                case MacroActionKind.Group:
                    action.GroupRepeat = 2;
                    break;
            }

            return action;
        }

        // ---------------------------------------------------------------- the action tree

        public static IReadOnlyList<FlatAction> Flatten(IEnumerable<MacroAction> actions)
        {
            var rows = new List<FlatAction>();
            Flatten(actions, 0, null, rows);
            return rows;
        }

        private static void Flatten(IEnumerable<MacroAction> actions, int depth, MacroAction? parent, List<FlatAction> into)
        {
            foreach (var action in actions)
            {
                into.Add(new FlatAction(action, depth, parent));

                if (action.Kind == MacroActionKind.Group && action.Actions is not null)
                    Flatten(action.Actions, depth + 1, action, into);
            }
        }

        public static int CountAll(IEnumerable<MacroAction> actions) =>
            actions.Sum(a => 1 + (a.Kind == MacroActionKind.Group && a.Actions is not null ? CountAll(a.Actions) : 0));

        /// <summary>Finds the list that holds <paramref name="target"/> (by reference) and its index there.</summary>
        public static bool TryFind(List<MacroAction> root, MacroAction target, out List<MacroAction> list, out int index)
        {
            index = root.FindIndex(a => ReferenceEquals(a, target));

            if (index >= 0)
            {
                list = root;
                return true;
            }

            foreach (var group in root.Where(a => a.Kind == MacroActionKind.Group && a.Actions is not null))
            {
                if (TryFind(group.Actions, target, out list, out index))
                    return true;
            }

            list = root;
            index = -1;
            return false;
        }

        public static bool CanMoveUp(List<MacroAction> root, MacroAction target) =>
            TryFind(root, target, out _, out int index) && index > 0;

        public static bool CanMoveDown(List<MacroAction> root, MacroAction target) =>
            TryFind(root, target, out var list, out int index) && index < list.Count - 1;

        public static bool MoveUp(List<MacroAction> root, MacroAction target)
        {
            if (!TryFind(root, target, out var list, out int index) || index <= 0)
                return false;

            (list[index - 1], list[index]) = (list[index], list[index - 1]);
            return true;
        }

        public static bool MoveDown(List<MacroAction> root, MacroAction target)
        {
            if (!TryFind(root, target, out var list, out int index) || index >= list.Count - 1)
                return false;

            (list[index + 1], list[index]) = (list[index], list[index + 1]);
            return true;
        }

        /// <summary>Removes the action and returns the one that should be selected next (neighbour or its group), or null.</summary>
        public static MacroAction? Delete(List<MacroAction> root, MacroAction target)
        {
            if (!TryFind(root, target, out var list, out int index))
                return null;

            list.RemoveAt(index);

            if (list.Count > 0)
                return list[Math.Min(index, list.Count - 1)];

            return FindParentGroup(root, list);
        }

        private static MacroAction? FindParentGroup(List<MacroAction> root, List<MacroAction> child)
        {
            foreach (var group in Flatten(root).Select(f => f.Action).Where(a => a.Kind == MacroActionKind.Group))
            {
                if (ReferenceEquals(group.Actions, child))
                    return group;
            }

            return null;
        }

        /// <summary>Inserts a deep copy right after the action (inside the same group) and returns it.</summary>
        public static MacroAction? Duplicate(List<MacroAction> root, MacroAction target)
        {
            if (!TryFind(root, target, out var list, out int index))
                return null;

            var copy = target.Clone();
            list.Insert(index + 1, copy);
            return copy;
        }

        /// <summary>Adds a new action after the selected one (same list), or at the end when nothing is selected.</summary>
        public static void InsertAfter(List<MacroAction> root, MacroAction? selected, MacroAction added)
        {
            if (selected is not null && TryFind(root, selected, out var list, out int index))
                list.Insert(index + 1, added);
            else
                root.Add(added);
        }

        public static bool AddToGroup(MacroAction group, MacroAction added)
        {
            if (group.Kind != MacroActionKind.Group)
                return false;

            group.Actions ??= new List<MacroAction>();
            group.Actions.Add(added);
            return true;
        }

        /// <summary>Result list of a recording applied to a macro's existing actions (deep copies; the inputs are untouched).</summary>
        public static List<MacroAction> MergeRecording(IEnumerable<MacroAction> existing, IEnumerable<MacroAction> recorded, RecordingMergeMode mode)
        {
            var result = mode == RecordingMergeMode.Append ? existing.Select(a => a.Clone()).ToList() : new List<MacroAction>();
            result.AddRange(recorded.Select(a => a.Clone()));
            return result;
        }

        // ---------------------------------------------------------------- camera speed <-> duration

        /// <summary>Pixels per second of a camera move, 0 when it has no distance.</summary>
        public static double CameraSpeed(MacroAction a) => MacroTiming.SpeedFromDuration(a.Dx, a.Dy, a.DurationMs);

        /// <summary>Editing the speed recomputes the duration (kept as-is without distance or with an unusable speed).</summary>
        public static void SetCameraSpeed(MacroAction a, double pxPerSec) =>
            a.DurationMs = MacroTiming.DurationFromSpeed(a.Dx, a.Dy, pxPerSec, a.DurationMs);

        /// <summary>Editing the duration is stored as-is; the speed field is derived by <see cref="CameraSpeed"/>.</summary>
        public static void SetCameraDuration(MacroAction a, int ms) => a.DurationMs = ms;

        public static string FormatSpeed(double pxPerSec) =>
            pxPerSec <= 0 ? "0" : Math.Round(pxPerSec, pxPerSec >= 100 ? 0 : 1).ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>"Horizontal 300 px · Vertical 0 px · Duration 0.8 s · Speed 375 px/s" (the camera inspector's one-line summary).</summary>
        public static string CameraSummary(MacroAction a)
        {
            string text = $"Horizontal {a.Dx} px · Vertical {a.Dy} px · Duration {FormatSeconds(a.DurationMs)}";
            double speed = CameraSpeed(a);
            return speed > 0 ? text + $" · Speed {FormatSpeed(speed)} px/s" : text;
        }

        /// <summary>Progress (0..1) of a camera move at time fraction t, the same curve the player uses; for the inspector preview.</summary>
        public static double CameraEase(double t, MacroEasing easing)
        {
            t = Math.Clamp(double.IsNaN(t) ? 0 : t, 0, 1);
            return easing == MacroEasing.EaseInOut ? t * t * (3.0 - 2.0 * t) : t;
        }

        /// <summary>Default export file name: the macro name with characters a file name can't hold replaced by "_" (no extension).</summary>
        public static string ExportFileName(string? name)
        {
            string text = string.IsNullOrWhiteSpace(name) ? "macro" : name.Trim();
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            string clean = string.Concat(text.Select(c => invalid.Contains(c) ? '_' : c)).TrimEnd('.', ' ');
            return clean.Length == 0 ? "macro" : clean;
        }

        // ---------------------------------------------------------------- field parsing

        public static bool TryParseInt(string? text, int min, int max, out int value)
        {
            value = 0;

            if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return false;

            if (parsed < min || parsed > max)
                return false;

            value = parsed;
            return true;
        }

        public static bool TryParseDouble(string? text, double min, double max, out double value)
        {
            value = 0;

            if (!double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                || double.IsNaN(parsed) || double.IsInfinity(parsed))
                return false;

            if (parsed < min || parsed > max)
                return false;

            value = parsed;
            return true;
        }

        public static double ClampSpeed(double speed) =>
            double.IsNaN(speed) ? 1.0 : Math.Min(MacroValidator.MaxSpeed, Math.Max(MacroValidator.MinSpeed, speed));

        public static string FormatSpeedMultiplier(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";
    }
}
