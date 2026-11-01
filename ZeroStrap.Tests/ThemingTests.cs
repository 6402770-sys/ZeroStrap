using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

using ZeroStrap.Models.Persistable;
using ZeroStrap.Models.Theming;
using ZeroStrap.UI.Theming;

using LegacyTheme = ZeroStrap.Enums.Theme;

namespace ZeroStrap.Tests
{
    public class ThemeDefinitionTests
    {
        [Fact]
        public void JsonRoundTripPreservesEverything()
        {
            var original = BuiltinThemes.Light();
            original.Id = "abc123";
            original.Kind = ThemeKind.Custom;
            original.BasedOn = "light";
            original.Typography.Scale = 1.1;
            original.Layout.Density = ThemeDensity.Compact;
            original.Components.ButtonStyle = ThemeButtonStyle.Subtle;
            original.Components.SurfaceStyle = ThemeSurfaceStyle.Flat;
            original.Background = new ThemeBackground { Type = ThemeBackgroundType.Image, Media = "bg.png", Opacity = 0.4, Blur = 12, Speed = 1.5 };

            var json = original.ToJson();
            var copy = ThemeDefinition.FromJson(json);

            Assert.NotNull(copy);
            Assert.Equal(json, copy!.ToJson());
            Assert.Equal(ThemeKind.Custom, copy.Kind);
            Assert.Equal(ThemeBase.Light, copy.Base);
            Assert.Equal(ThemeBackgroundType.Image, copy.Background.Type);
            Assert.Equal("bg.png", copy.Background.Media);
            Assert.Contains("\"Kind\": \"Custom\"", json);
            Assert.Equal(1, copy.SchemaVersion);
        }

        [Fact]
        public void MalformedJsonReturnsNull()
        {
            Assert.Null(ThemeDefinition.FromJson("{ not json"));
            Assert.Null(ThemeDefinition.FromJson("[]"));
            Assert.Null(ThemeDefinition.FromJson("{\"Kind\":\"Sparkly\"}"));
        }

        [Fact]
        public void BuiltinsAreValidAndDistinct()
        {
            foreach (var theme in BuiltinThemes.All())
                Assert.Empty(ThemeValidator.Validate(theme));

            Assert.Equal(ThemeBase.Dark, BuiltinThemes.Obsidian().Base);
            Assert.Equal(ThemeBase.Light, BuiltinThemes.Light().Base);
            Assert.All(BuiltinThemes.All(), t => Assert.Equal(ThemeKind.Builtin, t.Kind));
        }

        [Fact]
        public void DefaultsForStoredButUnappliedSettingsMatchTodaysLook()
        {
            var t = new ThemeDefinition();
            Assert.Equal(1.0, t.Typography.Scale);
            Assert.Equal(ThemeDensity.Comfortable, t.Layout.Density);
            Assert.Equal(ThemeBackgroundType.None, t.Background.Type);
            Assert.Equal(6, t.Components.Radius);
        }
    }

    public class ThemeValidatorTests
    {
        private static ThemeDefinition Custom()
        {
            var t = BuiltinThemes.Obsidian();
            t.Id = "abc123";
            t.Kind = ThemeKind.Custom;
            t.Name = "Mine";
            t.BasedOn = "obsidian";
            return t;
        }

        [Fact]
        public void ValidCustomThemePasses() => Assert.Empty(ThemeValidator.Validate(Custom()));

        [Theory]
        [InlineData("#FFF")]
        [InlineData("FFFFFF")]
        [InlineData("#GGGGGG")]
        [InlineData("#FFFFFFFF")]
        [InlineData("")]
        [InlineData("red")]
        public void BadHexIsRejected(string value)
        {
            Assert.False(ThemeValidator.IsValidHex(value));

            var t = Custom();
            t.Colors.Accent = value;
            Assert.Contains(ThemeValidator.Validate(t), e => e.Contains("Accent"));
        }

        [Theory]
        [InlineData("#000000")]
        [InlineData("#ffffff")]
        [InlineData("#1a2B3c")]
        public void GoodHexIsAccepted(string value) => Assert.True(ThemeValidator.IsValidHex(value));

        [Theory]
        [InlineData(0.89)]
        [InlineData(1.16)]
        [InlineData(double.NaN)]
        public void ScaleOutOfRange(double scale)
        {
            var t = Custom();
            t.Typography.Scale = scale;
            Assert.NotEmpty(ThemeValidator.Validate(t));
        }

        [Theory]
        [InlineData(0.9)]
        [InlineData(1.15)]
        [InlineData(1.0)]
        public void ScaleInRange(double scale)
        {
            var t = Custom();
            t.Typography.Scale = scale;
            Assert.Empty(ThemeValidator.Validate(t));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(17)]
        public void RadiusOutOfRange(int radius)
        {
            var t = Custom();
            t.Components.Radius = radius;
            Assert.NotEmpty(ThemeValidator.Validate(t));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(31)]
        public void BlurOutOfRange(int blur)
        {
            var t = Custom();
            t.Background.Blur = blur;
            Assert.NotEmpty(ThemeValidator.Validate(t));
        }

        [Theory]
        [InlineData(0.24)]
        [InlineData(2.01)]
        public void SpeedOutOfRange(double speed)
        {
            var t = Custom();
            t.Background.Speed = speed;
            Assert.NotEmpty(ThemeValidator.Validate(t));
        }

        [Theory]
        [InlineData(-0.1)]
        [InlineData(0.91)]
        public void ScrimOutOfRange(double opacity)
        {
            var t = Custom();
            t.Background.Opacity = opacity;
            Assert.NotEmpty(ThemeValidator.Validate(t));
        }

        [Fact]
        public void RangeBoundariesAreAccepted()
        {
            var t = Custom();
            t.Components.Radius = 16;
            t.Background.Blur = 30;
            t.Background.Speed = 0.25;
            t.Background.Opacity = 0.9;
            Assert.Empty(ThemeValidator.Validate(t));

            t.Components.Radius = 0;
            t.Background.Blur = 0;
            t.Background.Speed = 2.0;
            t.Background.Opacity = 0;
            Assert.Empty(ThemeValidator.Validate(t));
        }

        [Theory]
        [InlineData("Night")]
        [InlineData("My Theme 2")]
        [InlineData("x")]
        public void GoodNames(string name) => Assert.Null(ThemeValidator.ValidateName(name));

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(" lead")]
        [InlineData("trail ")]
        [InlineData("a/b")]
        [InlineData("a:b")]
        [InlineData("con")]
        [InlineData("NUL")]
        [InlineData("dots.")]
        public void BadNames(string name) => Assert.NotNull(ThemeValidator.ValidateName(name));

        [Fact]
        public void NameLengthIsOneToForty()
        {
            Assert.Null(ThemeValidator.ValidateName(new string('a', 40)));
            Assert.NotNull(ThemeValidator.ValidateName(new string('a', 41)));
            Assert.NotNull(ThemeValidator.ValidateName(null));
        }

        [Theory]
        [InlineData("abc123", true)]
        [InlineData("a-b_c", true)]
        [InlineData("obsidian", false)]
        [InlineData("light", false)]
        [InlineData("system", false)]
        [InlineData("../x", false)]
        [InlineData("A", false)]
        [InlineData("", false)]
        [InlineData("a b", false)]
        public void CustomIdRules(string id, bool expected) => Assert.Equal(expected, ThemeValidator.IsValidCustomId(id));

        [Theory]
        [InlineData("bg.png", ThemeBackgroundType.Image, true)]
        [InlineData("BG.JPG", ThemeBackgroundType.Image, true)]
        [InlineData("a.jpeg", ThemeBackgroundType.Image, true)]
        [InlineData("clip.mp4", ThemeBackgroundType.Video, true)]
        [InlineData("clip.mp4", ThemeBackgroundType.Image, false)]
        [InlineData("bg.png", ThemeBackgroundType.Video, false)]
        [InlineData("run.exe", ThemeBackgroundType.Image, false)]
        [InlineData("sub/bg.png", ThemeBackgroundType.Image, false)]
        [InlineData("..\\bg.png", ThemeBackgroundType.Image, false)]
        [InlineData(null, ThemeBackgroundType.Image, false)]
        [InlineData(null, ThemeBackgroundType.None, true)]
        public void MediaRules(string? media, ThemeBackgroundType type, bool ok) =>
            Assert.Equal(ok, ThemeValidator.ValidateMediaName(media, type) is null);

        [Fact]
        public void UnsupportedSchemaVersionIsRejected()
        {
            var t = Custom();
            t.SchemaVersion = 2;
            Assert.NotEmpty(ThemeValidator.Validate(t));
        }

        [Fact]
        public void ContrastRatioBlackWhiteIs21()
        {
            Assert.Equal(21.0, ThemeValidator.ContrastRatio("#000000", "#FFFFFF"), 6);
            Assert.Equal(21.0, ThemeValidator.ContrastRatio("#FFFFFF", "#000000"), 6);
            Assert.Equal(1.0, ThemeValidator.ContrastRatio("#777777", "#777777"), 6);
        }

        [Fact]
        public void ContrastRatioKnownValue()
        {
            // #767676 on white is the well-known 4.54:1 AA boundary grey.
            Assert.Equal(4.54, ThemeValidator.ContrastRatio("#767676", "#FFFFFF"), 2);
        }
    }

    public class ThemePaletteTests
    {
        private static void AssertClose(string expectedHex, Rgb actual, int tolerance = 2)
        {
            var e = ColorMath.Parse(expectedHex);
            Assert.True(Math.Abs(e.R - actual.R) <= tolerance && Math.Abs(e.G - actual.G) <= tolerance && Math.Abs(e.B - actual.B) <= tolerance,
                $"expected ~{expectedHex}, got {actual.ToHex()}");
        }

        [Fact]
        public void ObsidianColoursDeriveToDarkXamlValues()
        {
            var map = ThemePalette.Derive(BuiltinThemes.Obsidian());

            AssertClose("#0E0E0E", map["ApplicationBackground"]);
            AssertClose("#101010", map["SolidBackgroundFillColorBase"]);
            AssertClose("#0B0B0B", map["SolidBackgroundFillColorSecondary"]);
            AssertClose("#252525", map["SolidBackgroundFillColorTertiary"]);
            AssertClose("#151515", map["CardBackgroundFillColorDefault"]);
            AssertClose("#1B1B1B", map["CardBackgroundFillColorSecondary"]);
            AssertClose("#151515", map["LayerFillColorDefault"]);
            AssertClose("#242424", map["CardStrokeColorDefault"]);
            AssertClose("#222222", map["DividerStrokeColorDefault"]);
            AssertClose("#303030", map["ControlStrokeColorDefault"]);
            AssertClose("#3E3E3E", map["ControlStrokeColorSecondary"]);
            AssertClose("#2A2A2A", map["ControlElevationBorder"]);
            AssertClose("#191919", map["ControlFillColorDefault"]);
            AssertClose("#1F1F1F", map["ControlFillColorSecondary"]);
            AssertClose("#252525", map["ControlFillColorTertiary"]);
            AssertClose("#121212", map["ControlFillColorInputActive"]);
            AssertClose("#F2F2F2", map["TextFillColorPrimary"]);
            AssertClose("#A0A0A0", map["TextFillColorSecondary"]);
            AssertClose("#767676", map["TextFillColorTertiary"]);
            AssertClose("#5E5E5E", map["TextFillColorDisabled"]);
            AssertClose("#686868", map["TextPlaceholderColor"]);
            AssertClose("#1A1A1A", map["NewTextEditorBackground"]);
        }

        [Fact]
        public void DarkAccentRampMatchesBuiltinMonochromeRamp()
        {
            var ramp = ThemePalette.AccentRamp(ColorMath.Parse("#D4D4D4"), ThemeBase.Dark);

            AssertClose("#D4D4D4", ramp.System);
            AssertClose("#DCDCDC", ramp.Primary);
            AssertClose("#E8E8E8", ramp.Secondary);
            AssertClose("#F4F4F4", ramp.Tertiary);
        }

        [Fact]
        public void LightRampGoesDarkerDarkRampGoesLighter()
        {
            var accent = ColorMath.Parse("#3B82F6");
            var dark = ThemePalette.AccentRamp(accent, ThemeBase.Dark);
            var light = ThemePalette.AccentRamp(accent, ThemeBase.Light);

            double L(Rgb c) => ColorMath.ToHsl(c).L;
            Assert.True(L(dark.Primary) > L(accent) && L(dark.Secondary) > L(dark.Primary) && L(dark.Tertiary) > L(dark.Secondary));
            Assert.True(L(light.Primary) < L(accent) && L(light.Secondary) < L(light.Primary) && L(light.Tertiary) < L(light.Secondary));
        }

        [Fact]
        public void StatusColoursMapToThemeColours()
        {
            var t = BuiltinThemes.Obsidian();
            var status = ThemePalette.StatusColors(t);

            Assert.Equal("#3FBF7F", status["ZeroStatusHealthyBrush"].ToHex());
            Assert.Equal("#D4A054", status["ZeroStatusWarningBrush"].ToHex());
            Assert.Equal("#D15B5B", status["ZeroStatusErrorBrush"].ToHex());
            Assert.Equal("#686868", status["ZeroStatusIdleBrush"].ToHex());
        }

        [Fact]
        public void GeneratedDictionaryDefinesTheSameKeysAsDarkXaml()
        {
            var dict = ThemePalette.BuildResources(BuiltinThemes.Obsidian());

            string[] required =
            {
                "ApplicationBackgroundColor", "ApplicationBackgroundBrush",
                "SolidBackgroundFillColorBaseBrush", "SolidBackgroundFillColorSecondaryBrush", "SolidBackgroundFillColorTertiaryBrush",
                "CardBackgroundFillColorDefaultBrush", "CardBackgroundFillColorSecondaryBrush", "LayerFillColorDefaultBrush", "LayerFillColorAltBrush",
                "CardStrokeColorDefaultBrush", "CardStrokeColorDefaultSolidBrush", "DividerStrokeColorDefaultBrush",
                "ControlStrokeColorDefaultBrush", "ControlStrokeColorSecondaryBrush", "SurfaceStrokeColorDefaultBrush",
                "ControlFillColorDefault", "ControlFillColorSecondary", "ControlFillColorTertiary", "ControlFillColorInputActive",
                "ControlFillColorDefaultBrush", "ControlFillColorSecondaryBrush", "ControlFillColorTertiaryBrush", "ControlFillColorInputActiveBrush",
                "ControlElevationBorderBrush",
                "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "TextFillColorDisabledBrush", "TextPlaceholderColorBrush",
                "NewTextEditorBackground", "NewTextEditorForeground", "NewTextEditorLink",
                "TextOnAccentFillColorPrimaryBrush"
            };

            foreach (var key in required)
                Assert.True(dict.Contains(key), $"missing {key}");
        }
    }

    public class ThemeManagerLogicTests
    {
        private static ThemeStore Store(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "zs-theme-mgr-" + Guid.NewGuid().ToString("N"));
            return new ThemeStore(dir, Path.Combine(dir, "Media"));
        }

        [Theory]
        [InlineData(LegacyTheme.Dark, "obsidian")]
        [InlineData(LegacyTheme.Light, "light")]
        [InlineData(LegacyTheme.Default, "system")]
        public void MigrationMapsEachLegacyTheme(LegacyTheme legacy, string expected)
        {
            var settings = new Settings { Theme = legacy };

            Assert.True(ThemeManager.EnsureMigrated(settings));
            Assert.Equal(expected, settings.InterfaceThemeId);
        }

        [Fact]
        public void MigrationDoesNotOverwriteAnExistingId()
        {
            var settings = new Settings { Theme = LegacyTheme.Dark, InterfaceThemeId = "custom:abc" };

            Assert.False(ThemeManager.EnsureMigrated(settings));
            Assert.Equal("custom:abc", settings.InterfaceThemeId);
        }

        [Fact]
        public void NewSettingsHaveNoInterfaceThemeId() => Assert.Null(new Settings().InterfaceThemeId);

        [Fact]
        public void SettingsThemeIsSyncedForEachBuiltinKind()
        {
            var store = Store(out _);
            var settings = new Settings { Theme = LegacyTheme.Default };

            Assert.Equal("obsidian", ThemeManager.ApplyToSettings(settings, "obsidian", store));
            Assert.Equal(LegacyTheme.Dark, settings.Theme);

            ThemeManager.ApplyToSettings(settings, "light", store);
            Assert.Equal(LegacyTheme.Light, settings.Theme);
            Assert.Equal("light", settings.InterfaceThemeId);

            ThemeManager.ApplyToSettings(settings, "system", store);
            Assert.Equal(LegacyTheme.Default, settings.Theme);
            Assert.Equal("system", settings.InterfaceThemeId);
        }

        [Fact]
        public void SettingsThemeFollowsCustomThemeBase()
        {
            var store = Store(out string dir);

            try
            {
                var dark = store.Create("obsidian", "Dark one");
                var light = store.Create("light", "Light one");
                var settings = new Settings();

                ThemeManager.ApplyToSettings(settings, "custom:" + light.Id, store);
                Assert.Equal(LegacyTheme.Light, settings.Theme);
                Assert.Equal("custom:" + light.Id, settings.InterfaceThemeId);

                ThemeManager.ApplyToSettings(settings, "custom:" + dark.Id, store);
                Assert.Equal(LegacyTheme.Dark, settings.Theme);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData("custom:doesnotexist")]
        [InlineData("custom:")]
        [InlineData("custom:../x")]
        [InlineData("nonsense")]
        [InlineData(null)]
        public void UnknownIdsFallBackToObsidian(string? id)
        {
            var store = Store(out _);
            var logs = new List<string>();

            var resolved = ThemeManager.Resolve(id, store, systemIsDark: false, logs.Add);
            Assert.Equal("obsidian", resolved.Id);
            Assert.NotEmpty(logs);

            var settings = new Settings { Theme = LegacyTheme.Light };
            Assert.Equal("obsidian", ThemeManager.ApplyToSettings(settings, id, store));
            Assert.Equal(LegacyTheme.Dark, settings.Theme);
        }

        [Fact]
        public void SystemResolvesByWindowsMode()
        {
            var store = Store(out _);

            Assert.Equal("obsidian", ThemeManager.Resolve("system", store, systemIsDark: true).Id);
            Assert.Equal("light", ThemeManager.Resolve("system", store, systemIsDark: false).Id);
        }

        [Fact]
        public void CustomThemeResolvesFromStore()
        {
            var store = Store(out string dir);

            try
            {
                var made = store.Create("light", "Paper");
                var resolved = ThemeManager.Resolve("custom:" + made.Id, store, true);

                Assert.Equal(made.Id, resolved.Id);
                Assert.Equal(ThemeKind.Custom, resolved.Kind);
                Assert.Equal("custom:" + made.Id, ThemeManager.IdFor(resolved));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
