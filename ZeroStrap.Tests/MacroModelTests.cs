using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Xunit;

using ZeroStrap.Macros;

using static ZeroStrap.Tests.MacroBuilders;

namespace ZeroStrap.Tests
{
    public sealed class MacroModelTests
    {
        private static Macro Valid(params MacroAction[] actions)
        {
            var m = Of(actions);
            m.Id = "abc-123";
            m.Name = "Valid";
            return m;
        }

        private static Macro EveryKind() => new()
        {
            Id = "round-trip",
            Name = "Round trip",
            Enabled = true,
            Hotkey = "Ctrl+Shift+F7",
            Speed = 1.5,
            FocusRule = MacroFocusRule.RobloxOnly,
            Repeat = new MacroRepeat { Mode = MacroRepeatMode.Count, Count = 7 },
            Actions =
            {
                new MacroAction { Kind = MacroActionKind.Key, ScanCode = 0x1E, Extended = true, KeyMode = MacroKeyMode.Tap, HoldMs = 30, DelayBeforeMs = 5, Note = "jump" },
                new MacroAction { Kind = MacroActionKind.Mouse, Button = MouseButton.X2, ButtonMode = MacroButtonMode.Down, Enabled = false },
                new MacroAction { Kind = MacroActionKind.MouseMoveTo, X = -1500, Y = 900 },
                new MacroAction { Kind = MacroActionKind.Camera, Dx = -300, Dy = 40, DurationMs = 250, Easing = MacroEasing.EaseInOut, HoldRightButton = true },
                new MacroAction { Kind = MacroActionKind.Wait, Ms = 123 },
                new MacroAction { Kind = MacroActionKind.Group, GroupRepeat = 3, Actions = { new MacroAction { Kind = MacroActionKind.Wait, Ms = 1 } } }
            }
        };

        [Fact]
        public void EveryActionKindSurvivesAJsonRoundTrip()
        {
            var original = EveryKind();
            string json = MacroJson.Serialize(original);
            var back = MacroJson.Deserialize<Macro>(json)!;

            Assert.Equal(json, MacroJson.Serialize(back));
            Assert.Equal(6, back.Actions.Count);
            Assert.Equal(0x1E, back.Actions[0].ScanCode);
            Assert.True(back.Actions[0].Extended);
            Assert.Equal("jump", back.Actions[0].Note);
            Assert.Equal(MouseButton.X2, back.Actions[1].Button);
            Assert.False(back.Actions[1].Enabled);
            Assert.Equal(-1500, back.Actions[2].X);
            Assert.Equal(MacroEasing.EaseInOut, back.Actions[3].Easing);
            Assert.True(back.Actions[3].HoldRightButton);
            Assert.Equal(123, back.Actions[4].Ms);
            Assert.Equal(3, back.Actions[5].GroupRepeat);
            Assert.Single(back.Actions[5].Actions);
            Assert.Equal(MacroRepeatMode.Count, back.Repeat.Mode);
            Assert.Equal("Ctrl+Shift+F7", back.Hotkey);
        }

        [Fact]
        public void EnumsAreWrittenAsCamelCaseStrings()
        {
            string json = MacroJson.Serialize(EveryKind());

            Assert.Contains("\"kind\": \"mouseMoveTo\"", json);
            Assert.Contains("\"easing\": \"easeInOut\"", json);
            Assert.Contains("\"mode\": \"count\"", json);
            Assert.Contains("\"focusRule\": \"robloxOnly\"", json);
            Assert.DoesNotContain("\"kind\": 1", json);
        }

        [Fact]
        public void EnumsAreReadCaseInsensitively()
        {
            var m = MacroJson.Deserialize<Macro>("{\"id\":\"a\",\"actions\":[{\"kind\":\"Wait\",\"ms\":5}]}")!;

            Assert.Equal(MacroActionKind.Wait, m.Actions[0].Kind);
        }

        [Fact]
        public void UnknownKindIsRejected()
        {
            Assert.ThrowsAny<JsonException>(() => MacroJson.Deserialize<Macro>("{\"actions\":[{\"kind\":\"bogus\"}]}"));
        }

        [Fact]
        public void NumericEnumValuesAreRejected()
        {
            Assert.ThrowsAny<JsonException>(() => MacroJson.Deserialize<Macro>("{\"actions\":[{\"kind\":1}]}"));
        }

        [Fact]
        public void UndefinedEnumValueSetInCodeIsRejectedByValidate()
        {
            var m = Valid(new MacroAction { Kind = (MacroActionKind)99 });

            Assert.NotEmpty(MacroValidator.Validate(m));
        }

        [Fact]
        public void ValidMacroHasNoErrorsOrWarnings()
        {
            var result = MacroValidator.Process(Valid(TapKey(0x1E, 30), Wait(100), Camera(10, 10, 100)));

            Assert.True(result.IsValid);
            Assert.Empty(result.Warnings);
        }

        [Theory]
        [InlineData("../x")]
        [InlineData("..\\x")]
        [InlineData("A")]
        [InlineData("")]
        [InlineData("-abc")]
        [InlineData("a b")]
        [InlineData("abc\n")]
        [InlineData("a/b")]
        [InlineData("settings")]
        [InlineData("con")]
        [InlineData("com1")]
        public void BadIdsAreRejected(string id)
        {
            var m = Valid(Wait(1));
            m.Id = id;

            Assert.False(MacroValidator.IsValidId(id));
            Assert.NotEmpty(MacroValidator.Validate(m));
        }

        [Fact]
        public void IdLengthLimitIs64()
        {
            Assert.True(MacroValidator.IsValidId(new string('a', 64)));
            Assert.False(MacroValidator.IsValidId(new string('a', 65)));
            Assert.False(MacroValidator.IsValidId(null));
        }

        [Fact]
        public void GeneratedIdsAreValid()
        {
            Assert.True(MacroValidator.IsValidId(MacroValidator.NewId()));
            Assert.True(MacroValidator.IsValidId("0-1-a"));
        }

        [Fact]
        public void NameIsTrimmedStrippedAndShortened()
        {
            var m = Valid(Wait(1));
            m.Name = "  Hello\u0000\r\nWorld\t ";

            var (n, _) = MacroValidator.Normalize(m);
            Assert.Equal("HelloWorld", n.Name);

            m.Name = new string('x', 200);
            var (long1, warnings) = MacroValidator.Normalize(m);
            Assert.Equal(80, long1.Name.Length);
            Assert.NotEmpty(warnings);

            m.Name = " \t ";
            Assert.NotEmpty(MacroValidator.Process(m).Errors);
        }

        [Fact]
        public void NumbersAreClamped()
        {
            var m = Valid(
                new MacroAction { Kind = MacroActionKind.Wait, Ms = 99999999, DelayBeforeMs = -5 },
                new MacroAction { Kind = MacroActionKind.Key, ScanCode = 1, HoldMs = 700000 },
                new MacroAction { Kind = MacroActionKind.Camera, Dx = 50000, Dy = -50000, DurationMs = 5_000_000 },
                new MacroAction { Kind = MacroActionKind.MouseMoveTo, X = 1_000_000, Y = -1_000_000 },
                new MacroAction { Kind = MacroActionKind.Group, GroupRepeat = 0 },
                new MacroAction { Kind = MacroActionKind.Group, GroupRepeat = 99999999 });
            m.Speed = 10;
            m.Repeat = new MacroRepeat { Mode = MacroRepeatMode.Count, Count = 1_000_000_000 };

            var result = MacroValidator.Process(m);
            var a = result.Macro.Actions;

            Assert.True(result.IsValid);
            Assert.Equal(4.0, result.Macro.Speed);
            Assert.Equal(100000, result.Macro.Repeat.Count);
            Assert.Equal(600000, a[0].Ms);
            Assert.Equal(0, a[0].DelayBeforeMs);
            Assert.Equal(600000, a[1].HoldMs);
            Assert.Equal(20000, a[2].Dx);
            Assert.Equal(-20000, a[2].Dy);
            Assert.Equal(600000, a[2].DurationMs);
            Assert.Equal(100000, a[3].X);
            Assert.Equal(-100000, a[3].Y);
            Assert.Equal(1, a[4].GroupRepeat);
            Assert.Equal(10000, a[5].GroupRepeat);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void LowSpeedAndNaNAreClamped()
        {
            var m = Valid(Wait(1));
            m.Speed = 0.01;
            Assert.Equal(0.25, MacroValidator.Normalize(m).Macro.Speed);

            m.Speed = double.NaN;
            Assert.Equal(1.0, MacroValidator.Normalize(m).Macro.Speed);
        }

        [Fact]
        public void CameraWithMovementGetsAtLeastOneMillisecond()
        {
            var m = Valid(Camera(10, 0, 0), Camera(0, 0, 0));

            var a = MacroValidator.Normalize(m).Macro.Actions;

            Assert.Equal(1, a[0].DurationMs);
            Assert.Equal(0, a[1].DurationMs);
        }

        [Fact]
        public void NormalizeDoesNotModifyTheInput()
        {
            var m = Valid(new MacroAction { Kind = MacroActionKind.Wait, Ms = 99999999 });
            m.Speed = 10;

            MacroValidator.Normalize(m);

            Assert.Equal(99999999, m.Actions[0].Ms);
            Assert.Equal(10, m.Speed);
        }

        private static Macro Nested(int groups)
        {
            var inner = Wait(1);
            MacroAction current = inner;

            for (int i = 0; i < groups; i++)
                current = Group(1, current);

            return Valid(current);
        }

        [Fact]
        public void GroupNestingUpToFourIsAllowed()
        {
            Assert.Empty(MacroValidator.Process(Nested(4)).Errors);
        }

        [Fact]
        public void GroupNestingBeyondFourIsRejected()
        {
            Assert.NotEmpty(MacroValidator.Process(Nested(5)).Errors);
            Assert.NotEmpty(MacroValidator.Process(Nested(200)).Errors);
        }

        [Fact]
        public void TooManyActionNodesAreRejected()
        {
            var ok = Valid(Enumerable.Repeat(0, 10000).Select(_ => Wait(1)).ToArray());
            var tooMany = Valid(Enumerable.Repeat(0, 10001).Select(_ => Wait(1)).ToArray());

            Assert.Empty(MacroValidator.Process(ok).Errors);
            Assert.NotEmpty(MacroValidator.Process(tooMany).Errors);
        }

        [Fact]
        public void NodesInsideGroupsCountTowardsTheLimit()
        {
            var children = Enumerable.Repeat(0, 6000).Select(_ => Wait(1)).ToArray();
            var m = Valid(Group(1, children), Group(1, children));

            Assert.NotEmpty(MacroValidator.Process(m).Errors);
        }

        [Fact]
        public void EmptyGroupIsAllowed()
        {
            Assert.Empty(MacroValidator.Process(Valid(Group(5))).Errors);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(0x7F, true)]
        [InlineData(0x80, false)]
        [InlineData(0x100, false)]
        public void KeyScanCodesMustBeInRange(int scan, bool valid)
        {
            foreach (bool extended in new[] { false, true })
            {
                var a = new MacroAction { Kind = MacroActionKind.Key, ScanCode = (ushort)scan, Extended = extended };

                Assert.Equal(valid, !MacroValidator.Process(Valid(a)).Errors.Any());
            }
        }

        [Fact]
        public void ScanCodeIsIgnoredForNonKeyActions()
        {
            Assert.Empty(MacroValidator.Process(Valid(Wait(5))).Errors);
        }

        [Fact]
        public void HotkeyMustParseOrBeNull()
        {
            var m = Valid(Wait(1));

            m.Hotkey = null;
            Assert.Empty(MacroValidator.Process(m).Errors);

            m.Hotkey = "  ";
            var blank = MacroValidator.Process(m);
            Assert.Empty(blank.Errors);
            Assert.Null(blank.Macro.Hotkey);

            m.Hotkey = "ctrl+shift+f7";
            Assert.Equal("Ctrl+Shift+F7", MacroValidator.Process(m).Macro.Hotkey);

            foreach (string bad in new[] { "bogus", "Ctrl", "Ctrl+", "F6+F7", "Ctrl+Ctrl+A" })
            {
                m.Hotkey = bad;
                Assert.NotEmpty(MacroValidator.Process(m).Errors);
            }
        }

        [Fact]
        public void UnsupportedSchemaVersionIsRejected()
        {
            var m = Valid(Wait(1));
            m.SchemaVersion = 2;

            Assert.NotEmpty(MacroValidator.Validate(m));
        }

        [Fact]
        public void WarningListIsCappedForHostileFiles()
        {
            var m = Valid(Enumerable.Repeat(0, 500).Select(_ => new MacroAction { Kind = MacroActionKind.Wait, Ms = int.MaxValue }).ToArray());

            var (_, warnings) = MacroValidator.Normalize(m);

            Assert.True(warnings.Count <= 26);
        }

        [Fact]
        public void CloneIsDeep()
        {
            var original = EveryKind();
            var copy = original.Clone();

            copy.Actions[5].Actions[0].Ms = 999;
            copy.Repeat.Count = 1;

            Assert.Equal(1, original.Actions[5].Actions[0].Ms);
            Assert.Equal(7, original.Repeat.Count);
        }
    }
}
