using System;
using System.Collections.Generic;

using Xunit;

using ZeroStrap.Macros;

namespace ZeroStrap.Tests
{
    /// <summary>
    /// Game shortcuts (typing keys watched by the keyboard hook). These drive <see cref="GameKeyMatcher"/> with synthetic events;
    /// they prove the decision logic, not that a physical key press reaches Roblox (that needs a manual check in the game).
    /// </summary>
    public sealed class GameKeyMatcherTests
    {
        private const int E = 0x45, Q = 0x51, W = 0x57, Space = 0x20, Tab = 0x09, LShift = 0xA0, RShift = 0xA1, LCtrl = 0xA2;

        private readonly GameKeyMatcher _matcher = new();
        private readonly HashSet<Hotkey> _bound = new();
        private bool _roblox = true;
        private uint _time = 1000;
        private int _focusChecks;

        private Hotkey? Down(int vk, bool injected = false, uint after = 10)
        {
            _time += after;
            return _matcher.OnKey(vk, true, injected, _time, _bound.Contains, Focus);
        }

        private void Up(int vk, bool injected = false)
        {
            _time += 10;
            Assert.Null(_matcher.OnKey(vk, false, injected, _time, _bound.Contains, Focus));
        }

        private bool Focus()
        {
            _focusChecks++;
            return _roblox;
        }

        private void Bind(string text)
        {
            Assert.True(Hotkey.TryParseMacroShortcut(text, out var hotkey), text);
            _bound.Add(hotkey);
        }

        [Fact]
        public void ABareLetterFiresOnPress()
        {
            Bind("E");

            Assert.Equal(new Hotkey(HotkeyModifiers.None, E), Down(E));
            Up(E);
            Assert.Equal(new Hotkey(HotkeyModifiers.None, E), Down(E));
        }

        [Fact]
        public void ShiftPlusLetterNeedsShiftAndTheBareLetterDoesNot()
        {
            Bind("Shift+Q");
            Bind("Q");

            Assert.Equal(new Hotkey(HotkeyModifiers.None, Q), Down(Q));
            Up(Q);

            Down(LShift);
            Assert.Equal(new Hotkey(HotkeyModifiers.Shift, Q), Down(Q));
            Up(Q);
            Up(LShift);

            // Either Shift counts, and releasing one while the other is held keeps Shift down.
            Down(LShift);
            Down(RShift);
            Up(LShift);
            Assert.Equal(new Hotkey(HotkeyModifiers.Shift, Q), Down(Q));
        }

        [Fact]
        public void SpaceAndTabWork()
        {
            Bind("Space");
            Bind("Tab");

            Assert.Equal(new Hotkey(HotkeyModifiers.None, Space), Down(Space));
            Assert.Equal(new Hotkey(HotkeyModifiers.None, Tab), Down(Tab));
        }

        [Fact]
        public void CtrlCombosAreNotGameShortcuts()
        {
            Bind("E");

            Down(LCtrl);
            Assert.Null(Down(E)); // Ctrl+E is not "E", and Ctrl combos go through RegisterHotKey instead
        }

        [Fact]
        public void NothingFiresOutsideRoblox()
        {
            Bind("E");
            _roblox = false;

            Assert.Null(Down(E));
            Up(E);

            _roblox = true;
            Assert.NotNull(Down(E));
        }

        [Fact]
        public void AutoRepeatDoesNotFireAgain()
        {
            Bind("E");

            Assert.NotNull(Down(E));

            for (int i = 0; i < 20; i++)
                Assert.Null(Down(E, after: 33)); // the keyboard repeats while E is held

            Up(E);
            Assert.NotNull(Down(E));
        }

        [Fact]
        public void HoldingAnotherKeyDoesNotBlockAShortcut()
        {
            Bind("E");

            Down(W); // walking
            Assert.NotNull(Down(E));
        }

        [Fact]
        public void AMissedReleaseDoesNotBlockTheKeyForever()
        {
            Bind("E");

            Assert.NotNull(Down(E));
            // No up arrives (e.g. the secure desktop swallowed it); much later the key is pressed again.
            Assert.NotNull(Down(E, after: GameKeyMatcher.StaleDownMs + 500));
        }

        [Fact]
        public void InjectedInputNeverFiresAndDoesNotChangeState()
        {
            Bind("E");

            Assert.Null(Down(E, injected: true));
            Assert.Equal(0, _focusChecks);

            // An injected E "held" by a macro does not make the physical press look like a repeat.
            Assert.NotNull(Down(E));

            // Injected Shift does not turn E into Shift+E.
            Up(E);
            Down(LShift, injected: true);
            Assert.Equal(new Hotkey(HotkeyModifiers.None, E), Down(E));
        }

        [Fact]
        public void SlashPausesUntilEnter()
        {
            Bind("E");

            Assert.Null(Down(Hotkey.SlashKey));
            Assert.True(_matcher.ChatOpen);
            Up(Hotkey.SlashKey);

            Assert.Null(Down(E)); // typing "e" in the chat
            Up(E);

            Assert.Null(Down(Hotkey.EnterKey));
            Assert.False(_matcher.ChatOpen);
            Up(Hotkey.EnterKey);

            Assert.NotNull(Down(E));
        }

        [Fact]
        public void EscapeAlsoClosesTheChat()
        {
            Bind("E");

            Down(Hotkey.SlashKey);
            Up(Hotkey.SlashKey);
            Down(Hotkey.EscapeKey);
            Up(Hotkey.EscapeKey);

            Assert.False(_matcher.ChatOpen);
            Assert.NotNull(Down(E));
        }

        [Fact]
        public void ChatKeysOutsideRobloxAreIgnored()
        {
            Bind("E");

            _roblox = false;
            Down(Hotkey.SlashKey); // typing "/" in Discord
            Up(Hotkey.SlashKey);
            Assert.False(_matcher.ChatOpen);

            _roblox = true;
            Down(Hotkey.SlashKey);
            Up(Hotkey.SlashKey);

            _roblox = false;
            Down(Hotkey.EnterKey); // Enter in another app does not close the Roblox chat
            Up(Hotkey.EnterKey);
            Assert.True(_matcher.ChatOpen);
        }

        [Fact]
        public void ResetForgetsHeldKeysAndChat()
        {
            Bind("E");

            Down(E);
            Down(Hotkey.SlashKey);
            _matcher.Reset();

            Assert.False(_matcher.ChatOpen);
            Assert.NotNull(Down(E));
        }

        [Fact]
        public void AModifierWhoseReleaseWasMissedIsDropped()
        {
            Bind("E");
            Down(LShift);

            var hit = _matcher.OnKey(E, true, false, _time + 10, _bound.Contains, Focus, vk => vk != LShift);

            Assert.Equal(new Hotkey(HotkeyModifiers.None, E), hit);
        }

        [Fact]
        public void UnboundKeysDoNotAskForFocus()
        {
            Bind("E");

            Assert.Null(Down(W));
            Assert.Equal(0, _focusChecks);
        }

        [Theory]
        [InlineData("E", true)]
        [InlineData("Shift+Q", true)]
        [InlineData("Space", true)]
        [InlineData("Tab", true)]
        [InlineData("Semicolon", true)]
        [InlineData("Escape", false)]
        [InlineData("Enter", false)]
        [InlineData("Slash", false)]
        [InlineData("Shift+Enter", false)]
        [InlineData("F6", false)]
        [InlineData("Ctrl+E", false)]
        public void GameShortcutClassification(string text, bool game)
        {
            Assert.True(Hotkey.TryParse(text, out var hotkey));
            Assert.Equal(game, hotkey.IsGameShortcut);
        }

        [Theory]
        [InlineData("F6")]
        [InlineData("Ctrl+E")]
        [InlineData("Alt+1")]
        [InlineData("Win+Q")]
        [InlineData("End")]
        public void ExistingGlobalShortcutsStayGlobal(string text)
        {
            Assert.True(Hotkey.TryParse(text, out var hotkey));
            Assert.True(hotkey.IsAllowedShortcut);
            Assert.False(hotkey.IsGameShortcut);
            Assert.True(hotkey.IsMacroShortcut);
        }

        [Fact]
        public void TheHotkeyTableFindsGameKeysByCombination()
        {
            var table = new HotkeyTable();
            int fired = 0;

            Assert.True(table.TryAdd("macro:a", new Hotkey(HotkeyModifiers.None, E), () => fired++, out _));
            Assert.True(table.TryAdd("macro:b", new Hotkey(HotkeyModifiers.None, 0x75), () => { }, out _));

            Assert.True(table.HasGameKeys);
            Assert.True(table.IsGameKey("macro:a"));
            Assert.False(table.IsGameKey("macro:b"));
            Assert.True(table.TryGetCallback(new Hotkey(HotkeyModifiers.None, E), out var callback));
            callback();
            Assert.Equal(1, fired);

            table.Remove("macro:a", out _);
            Assert.False(table.HasGameKeys);
        }
    }
}
