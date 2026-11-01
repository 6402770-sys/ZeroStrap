using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using ZeroStrap.Macros;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.Tests
{
    public class MacroStudioLogicTests
    {
        private static string Name(ushort scan, bool ext) => MacroStudioLogic.KeyName(scan, ext, (s, e) => s == 0x11 && !e ? 'W' : s == 0x4B && e ? 0x25 : 0);

        private static MacroAction Key(ushort scan = 0x11) => new() { Kind = MacroActionKind.Key, ScanCode = scan };

        private static MacroAction Group(params MacroAction[] children) =>
            new() { Kind = MacroActionKind.Group, GroupRepeat = 3, Actions = children.ToList() };

        // ---------------------------------------------------------------- key names

        [Fact]
        public void KeyName_UsesResolver_NumpadTable_AndFallback()
        {
            Assert.Equal("W", Name(0x11, false));
            Assert.Equal("Left", Name(0x4B, true));
            Assert.Equal("Numpad 4", Name(0x4B, false));
            Assert.Equal("Numpad Enter", Name(0x1C, true));
            Assert.Equal("Scan 0x1E", Name(0x1E, false));
            Assert.Equal("Scan 0x1E", MacroStudioLogic.KeyName(0x1E, false, null));
            Assert.Equal("Scan 0x05", MacroStudioLogic.KeyName(0x05, false, (_, _) => throw new InvalidOperationException()));
        }

        [Theory]
        [InlineData(0x0011u, 0x11, false, true)]
        [InlineData(0xE04Bu, 0x4B, true, true)]
        [InlineData(0xE11Du, 0x1D, true, true)]
        [InlineData(0x0000u, 0, false, false)]
        [InlineData(0x0080u, 0x80, false, false)]
        public void TryDecodeScan_SplitsExtendedByte(uint mapped, int scan, bool ext, bool ok)
        {
            Assert.Equal(ok, MacroStudioLogic.TryDecodeScan(mapped, out ushort s, out bool e));
            Assert.Equal(scan, s);
            Assert.Equal(ext, e);
        }

        [Fact]
        public void KeyNameFromVirtualKey_KnowsCommonKeys()
        {
            Assert.Equal("Space", MacroStudioLogic.KeyNameFromVirtualKey(0x20));
            Assert.Equal("F5", MacroStudioLogic.KeyNameFromVirtualKey(0x74));
            Assert.Equal("Left Shift", MacroStudioLogic.KeyNameFromVirtualKey(0xA0));
            Assert.Null(MacroStudioLogic.KeyNameFromVirtualKey(0xFE));
        }

        // ---------------------------------------------------------------- summaries

        [Fact]
        public void Summary_ReadsNaturally()
        {
            Func<ushort, bool, string> n = (s, e) => "W";

            Assert.Equal("Key W · tap · 120 ms", MacroStudioLogic.Summary(new MacroAction { Kind = MacroActionKind.Key, HoldMs = 120 }, n));
            Assert.Equal("Key W · tap", MacroStudioLogic.Summary(new MacroAction { Kind = MacroActionKind.Key }, n));
            Assert.Equal("Key W · hold down", MacroStudioLogic.Summary(new MacroAction { Kind = MacroActionKind.Key, KeyMode = MacroKeyMode.Down }, n));
            Assert.Equal("Left click", MacroStudioLogic.Summary(new MacroAction { Kind = MacroActionKind.Mouse }));
            Assert.Equal("Right button down", MacroStudioLogic.Summary(new MacroAction { Kind = MacroActionKind.Mouse, Button = MouseButton.Right, ButtonMode = MacroButtonMode.Down }));
            Assert.Equal("Move to 640, 360", MacroStudioLogic.Summary(new MacroAction { Kind = MacroActionKind.MouseMoveTo, X = 640, Y = 360 }));
            Assert.Equal("Wait 500 ms", MacroStudioLogic.Summary(new MacroAction { Kind = MacroActionKind.Wait, Ms = 500 }));
            Assert.Equal("Camera → 300, ↑ 40 px · 0.8 s", MacroStudioLogic.Summary(new MacroAction { Kind = MacroActionKind.Camera, Dx = 300, Dy = -40, DurationMs = 800 }));
            Assert.Equal("Camera ← 5, ↓ 7 px · 1.5 s", MacroStudioLogic.Summary(new MacroAction { Kind = MacroActionKind.Camera, Dx = -5, Dy = 7, DurationMs = 1500 }));
            Assert.Equal("Group ×3 (2 actions)", MacroStudioLogic.Summary(Group(Key(), Key())));
            Assert.Equal("Group ×3 (1 action)", MacroStudioLogic.Summary(Group(Key())));
        }

        [Fact]
        public void Summary_UnknownKeyFallsBackToScanCode()
        {
            Assert.Equal("Key Scan 0x1E · tap", MacroStudioLogic.Summary(new MacroAction { Kind = MacroActionKind.Key, ScanCode = 0x1E }));
        }

        [Theory]
        [InlineData(0, "0 ms")]
        [InlineData(250, "250 ms")]
        [InlineData(2500, "2.5 s")]
        [InlineData(12000, "12 s")]
        [InlineData(65000, "1 min 5 s")]
        [InlineData(120000, "2 min")]
        [InlineData(long.MaxValue, "a very long time")]
        public void FormatDuration_Variants(long ms, string expected) => Assert.Equal(expected, MacroStudioLogic.FormatDuration(ms));

        [Fact]
        public void EstimateText_SaysPerLoopForUntilStopped()
        {
            var macro = new Macro { Actions = { new MacroAction { Kind = MacroActionKind.Wait, Ms = 1000 } } };
            Assert.Equal("About 1 s", MacroStudioLogic.EstimateText(macro));

            macro.Repeat.Mode = MacroRepeatMode.UntilStopped;
            Assert.Equal("About 1 s per loop", MacroStudioLogic.EstimateText(macro));

            macro.Repeat = new MacroRepeat { Mode = MacroRepeatMode.Count, Count = 3 };
            Assert.Equal("About 3 s", MacroStudioLogic.EstimateText(macro));
        }

        // ---------------------------------------------------------------- shortcuts and state

        [Fact]
        public void ShortcutIssue_FlagsTypingKeys()
        {
            Assert.Null(MacroStudioLogic.ShortcutIssue(null));
            Assert.Null(MacroStudioLogic.ShortcutIssue("F6"));
            Assert.Null(MacroStudioLogic.ShortcutIssue("Ctrl+W"));
            Assert.Null(MacroStudioLogic.ShortcutIssue("W"));
            Assert.Null(MacroStudioLogic.ShortcutIssue("Shift+W"));
            Assert.Null(MacroStudioLogic.ShortcutIssue("Space"));
            Assert.Null(MacroStudioLogic.ShortcutIssue("Tab"));
            Assert.Equal(MacroStudioLogic.ReservedShortcutText, MacroStudioLogic.ShortcutIssue("Escape"));
            Assert.Equal(MacroStudioLogic.ReservedShortcutText, MacroStudioLogic.ShortcutIssue("Enter"));
            Assert.Equal(MacroStudioLogic.ReservedShortcutText, MacroStudioLogic.ShortcutIssue("Slash"));
            Assert.Equal(MacroStudioLogic.NotAllowedShortcutText, MacroStudioLogic.ShortcutIssue("nonsense"));
            Assert.Equal(MacroStudioLogic.EmergencyShortcutText, MacroStudioLogic.ShortcutIssue("Ctrl+Shift+End"));
            Assert.Equal(MacroStudioLogic.EmergencyShortcutText, MacroStudioLogic.ShortcutIssue("Ctrl+Alt+Shift+End"));
            Assert.Equal(MacroStudioLogic.EmergencyShortcutText, MacroStudioLogic.ShortcutIssue("F8", "F8"));
            Assert.Null(MacroStudioLogic.ShortcutIssue("Ctrl+Shift+End", "F8"));
            Assert.Equal(MacroStudioLogic.NotAllowedShortcutText, MacroStudioLogic.EmergencyShortcutIssue("W"));
            Assert.Null(MacroStudioLogic.EmergencyShortcutIssue("Ctrl+F12"));
        }

        [Fact]
        public void FormatShortcut_IsCanonical()
        {
            Assert.Equal("Ctrl+Shift+F7", MacroStudioLogic.FormatShortcut(HotkeyModifiers.Shift | HotkeyModifiers.Ctrl, 0x76));
            Assert.Null(MacroStudioLogic.FormatShortcut(HotkeyModifiers.None, 0xFE));
        }

        [Fact]
        public void HotkeyStatusText_MapsRegistration()
        {
            var macro = new Macro { Enabled = true, Hotkey = "F6" };

            Assert.Equal("", MacroStudioLogic.HotkeyStatusText(macro, null));
            Assert.Equal("", MacroStudioLogic.HotkeyStatusText(macro, HotkeyRegistration.Ok));
            Assert.Equal(MacroStudioLogic.InUseText, MacroStudioLogic.HotkeyStatusText(macro, new HotkeyRegistration(HotkeyStatus.InUse)));
            Assert.Equal(MacroStudioLogic.DuplicateText, MacroStudioLogic.HotkeyStatusText(macro, new HotkeyRegistration(HotkeyStatus.Duplicate)));
            Assert.Equal("", MacroStudioLogic.HotkeyStatusText(new Macro { Enabled = true, Hotkey = "W" }, null));
            Assert.Equal(MacroStudioLogic.ReservedShortcutText, MacroStudioLogic.HotkeyStatusText(new Macro { Enabled = true, Hotkey = "Escape" }, null));
            Assert.Equal("", MacroStudioLogic.HotkeyStatusText(new Macro { Enabled = true }, null));
            Assert.StartsWith("Off: pressing F6 does nothing", MacroStudioLogic.HotkeyStatusText(new Macro { Hotkey = "F6" }, null));
            Assert.StartsWith("Active: press F6", MacroStudioLogic.ActivationText(new Macro { Hotkey = "F6", Enabled = true }));
            Assert.StartsWith("Add a shortcut", MacroStudioLogic.ActivationText(new Macro { Enabled = true }));
        }

        [Fact]
        public void StateLine_CoversStates()
        {
            Assert.Equal("Idle", MacroStudioLogic.StateLine(MacroRuntimeState.Idle, null, false, "F9"));
            Assert.Equal("Running Farm", MacroStudioLogic.StateLine(MacroRuntimeState.Running, new[] { "Farm" }, false, "F9"));
            Assert.StartsWith("Waiting for Roblox", MacroStudioLogic.StateLine(MacroRuntimeState.Running, new[] { "Farm" }, true, "F9"));
            Assert.Equal("Recording — press F10 to stop", MacroStudioLogic.StateLine(MacroRuntimeState.Recording, null, false, "F10"));
        }

        // ---------------------------------------------------------------- creating

        [Fact]
        public void UniqueName_AddsCounter()
        {
            Assert.Equal("New macro", MacroStudioLogic.UniqueName("New macro", new string[0]));
            Assert.Equal("New macro 2", MacroStudioLogic.UniqueName("New macro", new[] { "New macro" }));
            Assert.Equal("New macro 3", MacroStudioLogic.UniqueName("New macro", new[] { "new macro", "New macro 2" }));
        }

        [Fact]
        public void NewMacro_IsValid_AndRecordingNameUsesClock()
        {
            var macro = MacroStudioLogic.NewMacro(new[] { "New macro" });
            Assert.Equal("New macro 2", macro.Name);
            Assert.True(MacroValidator.Process(macro).IsValid);
            Assert.Equal("Recording 09:05", MacroStudioLogic.RecordingName(new DateTime(2026, 1, 1, 9, 5, 0)));
        }

        [Theory]
        [InlineData(MacroActionKind.Key)]
        [InlineData(MacroActionKind.Mouse)]
        [InlineData(MacroActionKind.MouseMoveTo)]
        [InlineData(MacroActionKind.Camera)]
        [InlineData(MacroActionKind.Wait)]
        [InlineData(MacroActionKind.Group)]
        public void NewAction_DefaultsPassValidation(MacroActionKind kind)
        {
            var macro = MacroStudioLogic.NewMacro(Array.Empty<string>());
            macro.Actions.Add(MacroStudioLogic.NewAction(kind));
            Assert.True(MacroValidator.Process(macro).IsValid, string.Join("; ", MacroValidator.Process(macro).Errors));
            Assert.False(string.IsNullOrEmpty(MacroStudioLogic.IconName(kind)));
        }

        // ---------------------------------------------------------------- tree

        [Fact]
        public void Flatten_ListsChildrenIndentedBeneathGroup()
        {
            var inner = Key(2);
            var group = Group(Key(1), inner);
            var after = Key(3);
            var flat = MacroStudioLogic.Flatten(new[] { group, after });

            Assert.Equal(new[] { 0, 1, 1, 0 }, flat.Select(f => f.Depth));
            Assert.Same(group, flat[1].Parent);
            Assert.Null(flat[3].Parent);
            Assert.Equal(4, MacroStudioLogic.CountAll(new[] { group, after }));
        }

        [Fact]
        public void Move_StaysInsideItsList()
        {
            var a = Key(1);
            var b = Key(2);
            var c = Key(3);
            var group = Group(b, c);
            var root = new List<MacroAction> { a, group };

            Assert.False(MacroStudioLogic.MoveUp(root, a));
            Assert.True(MacroStudioLogic.MoveDown(root, a));
            Assert.Same(group, root[0]);
            Assert.False(MacroStudioLogic.CanMoveUp(root, b));
            Assert.True(MacroStudioLogic.MoveDown(root, b));
            Assert.Same(c, group.Actions[0]);
            Assert.False(MacroStudioLogic.CanMoveDown(root, b));
            Assert.True(MacroStudioLogic.CanMoveUp(root, b));
        }

        [Fact]
        public void Delete_ReturnsNeighbourOrParentGroup()
        {
            var a = Key(1);
            var b = Key(2);
            var only = Key(3);
            var group = Group(only);
            var root = new List<MacroAction> { a, b, group };

            Assert.Same(b, MacroStudioLogic.Delete(root, a));
            Assert.Same(group, MacroStudioLogic.Delete(root, only));
            Assert.Empty(group.Actions);
            Assert.Same(b, MacroStudioLogic.Delete(root, group));
            Assert.Null(MacroStudioLogic.Delete(root, Key(9)));
        }

        [Fact]
        public void Duplicate_InsertsDeepCopyAfter()
        {
            var child = Key(5);
            var group = Group(child);
            var root = new List<MacroAction> { group, Key(7) };

            var copy = MacroStudioLogic.Duplicate(root, group)!;

            Assert.NotSame(group, copy);
            Assert.Same(copy, root[1]);
            Assert.NotSame(child, copy.Actions[0]);
            Assert.Equal(5, copy.Actions[0].ScanCode);

            var childCopy = MacroStudioLogic.Duplicate(root, child)!;
            Assert.Same(childCopy, group.Actions[1]);
        }

        [Fact]
        public void InsertAfter_AndAddToGroup()
        {
            var a = Key(1);
            var group = Group();
            var root = new List<MacroAction> { a, group };
            var added = Key(2);

            MacroStudioLogic.InsertAfter(root, a, added);
            Assert.Same(added, root[1]);

            var tail = Key(3);
            MacroStudioLogic.InsertAfter(root, null, tail);
            Assert.Same(tail, root[^1]);

            var inGroup = Key(4);
            Assert.True(MacroStudioLogic.AddToGroup(group, inGroup));
            Assert.Same(inGroup, group.Actions[0]);
            Assert.False(MacroStudioLogic.AddToGroup(a, Key(5)));

            var nested = Key(6);
            MacroStudioLogic.InsertAfter(root, inGroup, nested);
            Assert.Same(nested, group.Actions[1]);
        }

        // ---------------------------------------------------------------- recording merge

        [Fact]
        public void MergeRecording_ReplaceAndAppend()
        {
            var existing = new[] { Key(1) };
            var recorded = new[] { Key(2), Key(3) };

            var replaced = MacroStudioLogic.MergeRecording(existing, recorded, RecordingMergeMode.Replace);
            Assert.Equal(new[] { 2, 3 }, replaced.Select(a => (int)a.ScanCode));

            var appended = MacroStudioLogic.MergeRecording(existing, recorded, RecordingMergeMode.Append);
            Assert.Equal(new[] { 1, 2, 3 }, appended.Select(a => (int)a.ScanCode));
            Assert.NotSame(existing[0], appended[0]);
            Assert.NotSame(recorded[0], appended[1]);
        }

        // ---------------------------------------------------------------- camera link and parsing

        [Fact]
        public void CameraSpeed_AndDurationStayLinked()
        {
            var camera = new MacroAction { Kind = MacroActionKind.Camera, Dx = 300, Dy = 400, DurationMs = 1000 };
            Assert.Equal(500, MacroStudioLogic.CameraSpeed(camera), 3);

            MacroStudioLogic.SetCameraSpeed(camera, 1000);
            Assert.Equal(500, camera.DurationMs);
            Assert.Equal(1000, MacroStudioLogic.CameraSpeed(camera), 3);

            MacroStudioLogic.SetCameraDuration(camera, 250);
            Assert.Equal(2000, MacroStudioLogic.CameraSpeed(camera), 3);

            MacroStudioLogic.SetCameraSpeed(camera, 0);
            Assert.Equal(250, camera.DurationMs);

            var still = new MacroAction { Kind = MacroActionKind.Camera, DurationMs = 700 };
            MacroStudioLogic.SetCameraSpeed(still, 100);
            Assert.Equal(700, still.DurationMs);
            Assert.Equal("0", MacroStudioLogic.FormatSpeed(0));
            Assert.Equal("500", MacroStudioLogic.FormatSpeed(500.04));
        }

        [Fact]
        public void Parsing_RespectsRanges()
        {
            Assert.True(MacroStudioLogic.TryParseInt(" 12 ", 0, 100, out int v));
            Assert.Equal(12, v);
            Assert.False(MacroStudioLogic.TryParseInt("101", 0, 100, out _));
            Assert.False(MacroStudioLogic.TryParseInt("x", 0, 100, out _));
            Assert.True(MacroStudioLogic.TryParseDouble("1.5", 0.25, 4, out double d));
            Assert.Equal(1.5, d);
            Assert.False(MacroStudioLogic.TryParseDouble("NaN", 0.25, 4, out _));
            Assert.Equal(4.0, MacroStudioLogic.ClampSpeed(9));
            Assert.Equal(0.25, MacroStudioLogic.ClampSpeed(0));
            Assert.Equal(1.0, MacroStudioLogic.ClampSpeed(double.NaN));
        }

        [Fact]
        public void ListMeta_ShowsShortcutAndCount()
        {
            var macro = new Macro { Hotkey = "F6", Actions = { Group(Key(), Key()), Key() } };
            Assert.Equal("F6 · 4 actions", MacroStudioLogic.ListMeta(macro));
            Assert.Equal("No shortcut · 0 actions", MacroStudioLogic.ListMeta(new Macro()));
        }

        [Fact]
        public void CameraSummary_ReadsOnOneLine()
        {
            var move = new MacroAction { Kind = MacroActionKind.Camera, Dx = 300, Dy = 0, DurationMs = 800 };
            Assert.Equal("Horizontal 300 px · Vertical 0 px · Duration 0.8 s · Speed 375 px/s", MacroStudioLogic.CameraSummary(move));

            var still = new MacroAction { Kind = MacroActionKind.Camera, DurationMs = 500 };
            Assert.Equal("Horizontal 0 px · Vertical 0 px · Duration 0.5 s", MacroStudioLogic.CameraSummary(still));
        }

        [Fact]
        public void CameraEase_MatchesPlayerCurves()
        {
            Assert.Equal(0.25, MacroStudioLogic.CameraEase(0.25, MacroEasing.Linear));
            Assert.Equal(0.5, MacroStudioLogic.CameraEase(0.5, MacroEasing.EaseInOut), 6);
            Assert.True(MacroStudioLogic.CameraEase(0.25, MacroEasing.EaseInOut) < 0.25);
            Assert.Equal(1.0, MacroStudioLogic.CameraEase(5, MacroEasing.Linear));
            Assert.Equal(0.0, MacroStudioLogic.CameraEase(double.NaN, MacroEasing.EaseInOut));
        }

        [Theory]
        [InlineData("Spin / jump", "Spin _ jump")]
        [InlineData("  Farm  ", "Farm")]
        [InlineData("", "macro")]
        [InlineData(null, "macro")]
        [InlineData("a:b*c?", "a_b_c_")]
        [InlineData("...", "macro")]
        public void ExportFileName_IsSanitised(string? name, string expected) =>
            Assert.Equal(expected, MacroStudioLogic.ExportFileName(name));
    }
}
