using System.Collections.Generic;

using Xunit;

using ZeroStrap.Macros;

namespace ZeroStrap.Tests
{
    public sealed class HotkeyTests
    {
        private static Hotkey Parse(string text)
        {
            Assert.True(Hotkey.TryParse(text, out var hk), text);
            return hk;
        }

        [Fact]
        public void ParsesPlainKeys()
        {
            Assert.Equal(new Hotkey(HotkeyModifiers.None, 0x75), Parse("F6"));
            Assert.Equal(new Hotkey(HotkeyModifiers.None, 'A'), Parse("a"));
            Assert.Equal(new Hotkey(HotkeyModifiers.None, '7'), Parse("7"));
        }

        [Fact]
        public void ParsesModifiersInAnyOrderAndCase()
        {
            var hk = Parse("shift+WIN+alt+Control+End");

            Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win, hk.Modifiers);
            Assert.Equal(0x23, hk.VirtualKey);
        }

        [Fact]
        public void ToStringIsCanonicalOrder()
        {
            Assert.Equal("Ctrl+Alt+Shift+Win+End", Parse("Win+Shift+Alt+Ctrl+End").ToString());
            Assert.Equal("Ctrl+Shift+End", Parse("shift+ctrl+end").ToString());
            Assert.Equal("F6", Parse(" f6 ").ToString());
        }

        [Fact]
        public void ModifierValuesMatchWin32()
        {
            Assert.Equal(1, (int)HotkeyModifiers.Alt);
            Assert.Equal(2, (int)HotkeyModifiers.Ctrl);
            Assert.Equal(4, (int)HotkeyModifiers.Shift);
            Assert.Equal(8, (int)HotkeyModifiers.Win);
        }

        [Theory]
        [InlineData("F1", 0x70)]
        [InlineData("F12", 0x7B)]
        [InlineData("F24", 0x87)]
        [InlineData("End", 0x23)]
        [InlineData("Home", 0x24)]
        [InlineData("Insert", 0x2D)]
        [InlineData("Delete", 0x2E)]
        [InlineData("PageUp", 0x21)]
        [InlineData("PageDown", 0x22)]
        [InlineData("Left", 0x25)]
        [InlineData("Up", 0x26)]
        [InlineData("Right", 0x27)]
        [InlineData("Down", 0x28)]
        [InlineData("Space", 0x20)]
        [InlineData("Tab", 0x09)]
        [InlineData("Enter", 0x0D)]
        [InlineData("Escape", 0x1B)]
        [InlineData("Numpad0", 0x60)]
        [InlineData("Numpad9", 0x69)]
        [InlineData("NumpadMultiply", 0x6A)]
        [InlineData("NumpadAdd", 0x6B)]
        [InlineData("NumpadSubtract", 0x6D)]
        [InlineData("NumpadDecimal", 0x6E)]
        [InlineData("NumpadDivide", 0x6F)]
        [InlineData("Esc", 0x1B)]
        [InlineData("Return", 0x0D)]
        [InlineData("PgUp", 0x21)]
        [InlineData("Del", 0x2E)]
        [InlineData("Num5", 0x65)]
        public void KeyNamesMapToWin32VirtualKeys(string name, int vk)
        {
            Assert.Equal(vk, Parse(name).VirtualKey);
        }

        [Fact]
        public void EveryKnownKeyRoundTripsThroughItsName()
        {
            int known = 0;

            for (int vk = 0; vk < 256; vk++)
            {
                if (!Hotkey.TryGetKeyName(vk, out string name))
                    continue;

                known++;
                var hk = Parse("Ctrl+Alt+" + name);

                Assert.Equal(vk, hk.VirtualKey);
                Assert.Equal("Ctrl+Alt+" + name, hk.ToString());
                Assert.True(hk.IsValid);
            }

            // 26 letters + 10 digits + 24 F-keys + 15 numpad + 15 navigation/editing keys
            Assert.True(known >= 90);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("+")]
        [InlineData("Ctrl")]
        [InlineData("Ctrl+Shift")]
        [InlineData("Ctrl+")]
        [InlineData("+F6")]
        [InlineData("Ctrl++F6")]
        [InlineData("Ctrl+Ctrl+F6")]
        [InlineData("F6+F7")]
        [InlineData("Ctrl+Banana")]
        [InlineData("F25")]
        [InlineData("Hyper+F6")]
        public void InvalidTextIsRejected(string? text)
        {
            Assert.False(Hotkey.TryParse(text, out var hk));
            Assert.Equal(default, hk);
        }

        [Fact]
        public void DefaultStructIsNotValidAndUnknownKeysPrintAsHex()
        {
            Assert.False(default(Hotkey).IsValid);
            Assert.Equal("Ctrl+VK_FF", new Hotkey(HotkeyModifiers.Ctrl, 0xFF).ToString());
        }

        [Fact]
        public void EqualityIsByValue()
        {
            var seen = new HashSet<Hotkey> { Parse("Ctrl+F6") };

            Assert.Contains(Parse("control+f6"), seen);
            Assert.DoesNotContain(Parse("Alt+F6"), seen);
        }
    }
}
