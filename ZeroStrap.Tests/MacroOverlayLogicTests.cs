using System;
using System.Linq;

using Xunit;

using ZeroStrap.Macros;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.Tests
{
    public sealed class MacroOverlayLogicTests
    {
        private static Macro M(string id, string? hotkey = "F6", bool enabled = true, string? name = null) =>
            new() { Id = id, Name = name ?? id, Hotkey = hotkey, Enabled = enabled };

        [Fact]
        public void ListsEnabledMacrosWithShortcutsWithNameAndKeybind()
        {
            var entries = MacroOverlayLogic.Entries(new[] { M("a", "E", name: "Farm"), M("b", null), M("c", "F7", enabled: false) },
                Array.Empty<string>(), runningOnly: false);

            var only = Assert.Single(entries);
            Assert.Equal("Farm", only.Name);
            Assert.Equal("E", only.Hotkey);
            Assert.False(only.Running);
        }

        [Fact]
        public void MarksRunningMacrosAndAlwaysListsThemEvenWithoutAShortcut()
        {
            var entries = MacroOverlayLogic.Entries(new[] { M("a"), M("b", null, enabled: false) }, new[] { "b" }, runningOnly: false);

            Assert.Equal(new[] { "b", "a" }, entries.Select(e => e.Id));
            Assert.True(entries[0].Running);
            Assert.Equal("—", entries[0].Hotkey);
            Assert.False(entries[1].Running);
        }

        [Fact]
        public void ShowsAtMostSixAndPutsRunningMacrosFirst()
        {
            var macros = Enumerable.Range(0, 10).Select(i => M("m" + i)).ToList();

            var entries = MacroOverlayLogic.Entries(macros, new[] { "m8", "m9" }, runningOnly: false);

            Assert.Equal(MacroOverlayLogic.MaxEntries, entries.Count);
            Assert.Equal(new[] { "m8", "m9", "m0", "m1", "m2", "m3" }, entries.Select(e => e.Id));
        }

        [Fact]
        public void RunningOnlyHidesIdleMacros()
        {
            var entries = MacroOverlayLogic.Entries(new[] { M("a"), M("b"), M("c") }, new[] { "b" }, runningOnly: true);

            Assert.Equal(new[] { "b" }, entries.Select(e => e.Id));
            Assert.Empty(MacroOverlayLogic.Entries(new[] { M("a") }, Array.Empty<string>(), runningOnly: true));
        }

        [Theory]
        [InlineData(true, true, 1, true)]
        [InlineData(false, true, 1, false)]
        [InlineData(true, false, 1, false)]
        [InlineData(true, true, 0, false)]
        public void ShowsOnlyWhenEnabledRobloxIsActiveAndThereIsSomethingToList(bool enabled, bool roblox, int count, bool expected)
        {
            Assert.Equal(expected, MacroOverlayLogic.ShouldShow(enabled, roblox, count));
        }

        [Fact]
        public void FadesAfterFourSecondsWithoutActivity()
        {
            Assert.Equal(MacroOverlayLogic.ActiveOpacity, MacroOverlayLogic.TargetOpacity(TimeSpan.Zero));
            Assert.Equal(MacroOverlayLogic.ActiveOpacity, MacroOverlayLogic.TargetOpacity(TimeSpan.FromSeconds(3.9)));
            Assert.Equal(MacroOverlayLogic.IdleOpacity, MacroOverlayLogic.TargetOpacity(TimeSpan.FromSeconds(4)));
            Assert.Equal(0.35, MacroOverlayLogic.IdleOpacity);
            Assert.Equal(TimeSpan.FromSeconds(4), MacroOverlayLogic.FadeDelay);
        }

        [Theory]
        [InlineData(OverlayCorner.BottomLeft, 116, 664)]
        [InlineData(OverlayCorner.BottomRight, 1704, 664)]
        [InlineData(OverlayCorner.TopLeft, 116, 216)]
        [InlineData(OverlayCorner.TopRight, 1704, 216)]
        public void SitsInTheChosenCornerOfTheRobloxWindow(OverlayCorner corner, int x, int y)
        {
            // Client area 100,200 - 1920,900; overlay 200 x 220.
            Assert.Equal((x, y), MacroOverlayLogic.Position(corner, 100, 200, 1920, 900, 200, 220));
        }

        [Fact]
        public void StaysInsideATinyWindow()
        {
            var (x, y) = MacroOverlayLogic.Position(OverlayCorner.BottomRight, 0, 0, 100, 100, 300, 300);

            Assert.Equal((MacroOverlayLogic.EdgeMargin, MacroOverlayLogic.EdgeMargin), (x, y));
        }

        [Fact]
        public void WindowStylesMakeItClickThroughAndNeverActivated()
        {
            int style = MacroOverlayLogic.OverlayExStyle(0x100);

            Assert.Equal(0x100, style & 0x100); // existing styles are kept
            Assert.NotEqual(0, style & MacroOverlayLogic.WS_EX_TRANSPARENT);
            Assert.NotEqual(0, style & MacroOverlayLogic.WS_EX_LAYERED);
            Assert.NotEqual(0, style & MacroOverlayLogic.WS_EX_NOACTIVATE);
            Assert.NotEqual(0, style & MacroOverlayLogic.WS_EX_TOOLWINDOW);
        }

        [Fact]
        public void SettingsDefaultToBottomLeftAndPersist()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zs-overlay-" + Guid.NewGuid().ToString("N"));

            try
            {
                var store = new MacroStudioSettingsStore(System.IO.Path.Combine(dir, "settings.json"));
                var defaults = store.Load();

                Assert.True(defaults.OverlayEnabled);
                Assert.Equal(OverlayCorner.BottomLeft, defaults.OverlayCorner);
                Assert.False(defaults.OverlayRunningOnly);

                System.IO.Directory.CreateDirectory(dir);
                store.Save(new MacroStudioSettings { OverlayEnabled = false, OverlayCorner = OverlayCorner.TopRight, OverlayRunningOnly = true });
                var loaded = store.Load();

                Assert.False(loaded.OverlayEnabled);
                Assert.Equal(OverlayCorner.TopRight, loaded.OverlayCorner);
                Assert.True(loaded.OverlayRunningOnly);
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
