using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

using ZeroStrap.Enums;
using ZeroStrap.Models.Theming;
using ZeroStrap.UI.Theming;
using ZeroStrap.UI.Utility;

namespace ZeroStrap.Tests
{
    public class ThemeTokenMathTests
    {
        [Theory]
        [InlineData(14, 1.0, 14)]
        [InlineData(10.5, 1.0, 10.5)]
        [InlineData(14, 1.15, 16)]      // 16.1 -> 16
        [InlineData(10.5, 0.9, 9.5)]    // 9.45 -> 9.5
        [InlineData(11, 0.9, 10)]       // 9.9 -> 10
        [InlineData(13, 0.95, 12.5)]    // 12.35 -> 12.5
        [InlineData(12.5, 1.1, 14)]     // 13.75 -> 14
        [InlineData(38, 1.05, 40)]      // 39.9 -> 40
        public void FontScaleRoundsToHalfPixels(double size, double scale, double expected) =>
            Assert.Equal(expected, ThemeTokens.ScaleFont(size, scale));

        [Theory]
        [InlineData(0, 12, 0)]
        [InlineData(0, 4, 0)]
        [InlineData(6, 6, 6)]
        [InlineData(6, 12, 12)]
        [InlineData(6, 14, 14)]
        [InlineData(16, 12, 32)]
        [InlineData(16, 6, 16)]
        [InlineData(4, 5, 3.5)]         // 3.33 -> 3.5
        [InlineData(3, 7, 3.5)]         // 3.5
        [InlineData(-5, 12, 0)]         // never below zero
        public void RadiusFactorScalesEveryRadius(int radius, double baseRadius, double expected) =>
            Assert.Equal(expected, ThemeTokens.ScaleRadius(baseRadius, radius));

        [Fact]
        public void RadiusFactorIsRelativeToSix()
        {
            Assert.Equal(1.0, ThemeTokens.RadiusFactor(6));
            Assert.Equal(0.0, ThemeTokens.RadiusFactor(0));
            Assert.Equal(16.0 / 6.0, ThemeTokens.RadiusFactor(16), 10);
        }

        [Theory]
        [InlineData(ThemeDensity.Compact, 0.8)]
        [InlineData(ThemeDensity.Comfortable, 1.0)]
        [InlineData(ThemeDensity.Spacious, 1.2)]
        public void DensityFactors(ThemeDensity density, double expected) =>
            Assert.Equal(expected, ThemeTokens.DensityFactor(density));

        [Theory]
        [InlineData(40, 0.8, 32)]
        [InlineData(30, 0.8, 24)]
        [InlineData(28, 0.8, 22)]       // 22.4
        [InlineData(28, 1.2, 34)]       // 33.6
        [InlineData(30, 1.2, 36)]
        [InlineData(8, 0.8, 6)]         // 6.4
        [InlineData(0, 1.2, 0)]
        [InlineData(40, 1.0, 40)]
        public void SpacingScalesToWholePixels(double value, double factor, double expected) =>
            Assert.Equal(expected, ThemeTokens.ScaleSpacing(value, factor));

        [Theory]
        [InlineData(null, "Inter, Segoe UI")]
        [InlineData("", "Inter, Segoe UI")]
        [InlineData("   ", "Inter, Segoe UI")]
        [InlineData("Inter", "Inter, Segoe UI")]
        [InlineData("inter", "Inter, Segoe UI")]
        [InlineData("Inter, Segoe UI", "Inter, Segoe UI")]
        [InlineData("Georgia", "Georgia, Inter, Segoe UI")]
        [InlineData("  Cascadia Mono ", "Cascadia Mono, Inter, Segoe UI")]
        public void FamilyStringKeepsFallbacks(string? family, string expected) =>
            Assert.Equal(expected, ThemeTokens.BuildFamily(family));

        [Theory]
        [InlineData(ThemeSurfaceStyle.Outlined, 1)]
        [InlineData(ThemeSurfaceStyle.Flat, 0)]
        public void SurfaceBorder(ThemeSurfaceStyle style, double expected) =>
            Assert.Equal(expected, ThemeTokens.SurfaceBorder(style));

        [Theory]
        [InlineData(400, 400)]
        [InlineData(50, 100)]
        [InlineData(1200, 900)]
        public void WeightsAreClamped(int weight, int expected) => Assert.Equal(expected, ThemeTokens.NormaliseWeight(weight));
    }

    public class ThemeTokenSetTests
    {
        [Theory]
        [InlineData("obsidian")]
        [InlineData("light")]
        public void BuiltInsReproduceTheDefaultTokens(string id)
        {
            var theme = id == "light" ? BuiltinThemes.Light() : BuiltinThemes.Obsidian();
            var tokens = ThemeTokens.Build(theme);

            foreach (var (key, size) in ThemeTokens.BaseFontSizes)
                Assert.Equal(size, (double)tokens[key]);

            foreach (var (key, radius) in ThemeTokens.BaseRadii)
                Assert.Equal(new CornerRadius(radius), (CornerRadius)tokens[key]);

            foreach (var (key, v) in ThemeTokens.BaseSpacing)
                Assert.Equal(new Thickness(v.L, v.T, v.R, v.B), (Thickness)tokens[key]);

            Assert.Equal(14.0, (double)tokens[ThemeTokens.ControlContentFontSizeKey]);
            Assert.Equal(new Thickness(1), (Thickness)tokens[ThemeTokens.SurfaceBorderKey]);
            Assert.Equal(FontWeights.Normal, (FontWeight)tokens[ThemeTokens.BodyWeightKey]);
            Assert.Equal(FontWeights.SemiBold, (FontWeight)tokens[ThemeTokens.HeadingWeightKey]);
            Assert.Equal("Inter, Segoe UI", ((FontFamily)tokens[ThemeTokens.FamilyKey]).Source);
        }

        private static ThemeDefinition Custom(Action<ThemeDefinition>? edit = null)
        {
            var t = BuiltinThemes.Obsidian();
            t.Id = "mine";
            t.Kind = ThemeKind.Custom;
            edit?.Invoke(t);
            return t;
        }

        [Fact]
        public void BuildAppliesScaleRadiusDensityAndStyle()
        {
            var tokens = ThemeTokens.Build(Custom(t =>
            {
                t.Typography.Scale = 1.15;
                t.Typography.Family = "Georgia";
                t.Typography.BodyWeight = 500;
                t.Typography.HeadingWeight = 700;
                t.Layout.Density = ThemeDensity.Compact;
                t.Components.Radius = 0;
                t.Components.SurfaceStyle = ThemeSurfaceStyle.Flat;
            }));

            Assert.Equal(16.0, (double)tokens["ZeroFontSizeBody"]);          // 16.1
            Assert.Equal(32.0, (double)tokens["ZeroFontSizeDisplay"]);       // 32.2
            Assert.Equal(16.0, (double)tokens[ThemeTokens.ControlContentFontSizeKey]);
            Assert.Equal(new CornerRadius(0), (CornerRadius)tokens["ControlCornerRadius"]);
            Assert.Equal(new CornerRadius(0), (CornerRadius)tokens["ZeroRadiusSurface"]);
            Assert.Equal(new Thickness(32, 24, 32, 32), (Thickness)tokens["ZeroPageMargin"]);
            Assert.Equal(new Thickness(0, 22, 0, 0), (Thickness)tokens["ZeroSectionMargin"]);
            Assert.Equal(new Thickness(0), (Thickness)tokens[ThemeTokens.SurfaceBorderKey]);
            Assert.Equal("Georgia, Inter, Segoe UI", ((FontFamily)tokens[ThemeTokens.FamilyKey]).Source);
            Assert.Equal(FontWeights.Medium, (FontWeight)tokens[ThemeTokens.BodyWeightKey]);
            Assert.Equal(FontWeights.Bold, (FontWeight)tokens[ThemeTokens.HeadingWeightKey]);
        }

        [Fact]
        public void MaximumRadiusAndSpaciousDensity()
        {
            var tokens = ThemeTokens.Build(Custom(t => { t.Components.Radius = 16; t.Layout.Density = ThemeDensity.Spacious; }));

            Assert.Equal(new CornerRadius(16), (CornerRadius)tokens["ControlCornerRadius"]);
            Assert.Equal(new CornerRadius(32), (CornerRadius)tokens["ZeroRadiusSurface"]);
            Assert.Equal(new Thickness(48, 36, 48, 48), (Thickness)tokens["ZeroPageMargin"]);
            Assert.Equal(new Thickness(0, 34, 0, 0), (Thickness)tokens["ZeroSectionMargin"]);
        }

        [Fact]
        public void OutOfRangeValuesAreClampedNotTrusted()
        {
            var tokens = ThemeTokens.Build(Custom(t => { t.Typography.Scale = 9; t.Components.Radius = 99; }));

            Assert.Equal(ThemeTokens.ScaleFont(14, ThemeValidator.MaxScale), (double)tokens["ZeroFontSizeBody"]);
            Assert.Equal(new CornerRadius(16), (CornerRadius)tokens["ControlCornerRadius"]);
        }

        [Fact]
        public void SameDetectsEqualAndDifferentSets()
        {
            var a = ThemeTokens.Build(Custom());
            var b = ThemeTokens.Build(Custom());
            var c = ThemeTokens.Build(Custom(t => t.Typography.Family = "Georgia"));

            Assert.True(ThemeTokens.Same(a, b));
            Assert.False(ThemeTokens.Same(a, c));
            Assert.False(ThemeTokens.Same(null, a));
        }

        // ---------- the C# defaults and the shipped ZeroBrand.xaml must agree

        private static string FindBrandXaml()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "ZeroStrap", "UI", "Style", "ZeroBrand.xaml");
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }

            throw new FileNotFoundException("ZeroBrand.xaml not found above " + AppContext.BaseDirectory);
        }

        [Fact]
        public void XamlDefaultsMatchTheTokenTable()
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace sys = "clr-namespace:System;assembly=mscorlib";

            var doc = XDocument.Load(FindBrandXaml());
            var keyed = doc.Root!.Elements().Where(e => e.Attribute(x + "Key") is not null).ToDictionary(e => (string)e.Attribute(x + "Key")!);

            var expected = ThemeTokens.Build(BuiltinThemes.Obsidian());
            expected.Remove(ThemeTokens.ControlContentFontSizeKey); // WPF-UI's key, not ours

            foreach (var (key, value) in expected)
            {
                Assert.True(keyed.TryGetValue(key, out var element), $"ZeroBrand.xaml is missing token {key}");
                string text = element!.Value.Trim();

                switch (value)
                {
                    case double d:
                        Assert.Equal(sys + "Double", element.Name);
                        Assert.Equal(d, double.Parse(text, CultureInfo.InvariantCulture));
                        break;
                    case CornerRadius r:
                        Assert.Equal(wpf + "CornerRadius", element.Name);
                        Assert.Equal(r, (CornerRadius)new CornerRadiusConverter().ConvertFromInvariantString(text)!);
                        break;
                    case Thickness th:
                        Assert.Equal(wpf + "Thickness", element.Name);
                        Assert.Equal(th, (Thickness)new ThicknessConverter().ConvertFromInvariantString(text)!);
                        break;
                    case FontWeight w:
                        Assert.Equal(w, (FontWeight)new FontWeightConverter().ConvertFromInvariantString(text)!);
                        break;
                    case FontFamily f:
                        Assert.Equal(f.Source, text);
                        break;
                    default:
                        Assert.Fail($"Unhandled token type for {key}");
                        break;
                }
            }

            // every themeable key in the XAML is driven by the table (the mono family is deliberately not themeable)
            foreach (var (key, element) in keyed)
            {
                bool token = element.Name == sys + "Double" || element.Name == wpf + "CornerRadius" || element.Name == wpf + "Thickness" ||
                             element.Name == wpf + "FontWeight" || (element.Name == wpf + "FontFamily" && key != "ZeroMonoFont");
                if (token)
                    Assert.True(expected.ContainsKey(key), $"ZeroBrand.xaml defines {key} but ThemeTokens does not drive it");
            }
        }

        [Fact]
        public void ThemeableTokensAreNeverStaticResources()
        {
            string uiRoot = Path.Combine(Path.GetDirectoryName(FindBrandXaml())!, "..");
            var keys = ThemeTokens.Build(BuiltinThemes.Obsidian()).Keys.ToList();
            var offenders = new List<string>();

            foreach (var file in Directory.EnumerateFiles(uiRoot, "*.xaml", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                foreach (var key in keys)
                {
                    if (text.Contains("{StaticResource " + key + "}", StringComparison.Ordinal))
                        offenders.Add(Path.GetFileName(file) + " -> " + key);
                }
            }

            Assert.True(offenders.Count == 0, "StaticResource would freeze a theme token: " + string.Join(", ", offenders));
        }
    }

    public class ThemeGuardTests
    {
        private static ThemeDefinition Custom(string text, string background)
        {
            var t = BuiltinThemes.Obsidian();
            t.Id = "mine";
            t.Kind = ThemeKind.Custom;
            t.Colors.TextPrimary = text;
            t.Colors.Background = background;
            return t;
        }

        [Fact]
        public void BuiltInsAreNeverBlocked()
        {
            Assert.False(ThemeGuard.IsUnreadable(BuiltinThemes.Obsidian()));
            Assert.False(ThemeGuard.IsUnreadable(BuiltinThemes.Light()));

            var faint = BuiltinThemes.Obsidian();
            faint.Colors.TextPrimary = faint.Colors.Background;
            Assert.False(ThemeGuard.IsUnreadable(faint)); // Kind is Builtin
        }

        [Fact]
        public void CustomThemeWithReadableTextIsApplied() =>
            Assert.False(ThemeGuard.IsUnreadable(Custom("#F2F2F2", "#0E0E0E")));

        [Theory]
        [InlineData("#0E0E0E", "#0E0E0E")]   // identical
        [InlineData("#AAAAAA", "#FFFFFF")]   // 2.3:1
        [InlineData("#999999", "#FFFFFF")]   // 2.8:1
        public void CustomThemeBelowThreeToOneIsBlocked(string text, string background) =>
            Assert.True(ThemeGuard.IsUnreadable(Custom(text, background)));

        [Fact]
        public void ThreeToOneIsTheBoundary()
        {
            Assert.False(ThemeGuard.IsUnreadable(Custom("#949494", "#FFFFFF"))); // 3.03:1
            Assert.True(ThemeGuard.IsUnreadable(Custom("#9A9A9A", "#FFFFFF")));  // 2.8x:1
        }

        [Fact]
        public void WarningCaptionText() =>
            Assert.Equal("Not applied: primary text is too low-contrast to read.", ThemeGuard.WarningText);
    }

    public class MotionRulesTests
    {
        [Theory]
        // reduce, follow, windowsOn, expected
        [InlineData(false, false, true, true)]
        [InlineData(false, false, false, true)]   // not following: Windows setting ignored
        [InlineData(false, true, true, true)]
        [InlineData(false, true, false, false)]   // following and Windows has animations off
        [InlineData(true, false, true, false)]
        [InlineData(true, false, false, false)]
        [InlineData(true, true, true, false)]
        [InlineData(true, true, false, false)]
        public void EnabledTruthTable(bool reduce, bool follow, bool windowsOn, bool expected) =>
            Assert.Equal(expected, MotionRules.IsEnabled(reduce, follow, windowsOn));

        [Fact]
        public void StandardIsUnchanged()
        {
            Assert.Equal(1.0, MotionRules.TravelFactor(MotionIntensity.Standard));
            Assert.Equal(1.0, MotionRules.DurationFactor(MotionIntensity.Standard));
            Assert.Equal(12.0, MotionRules.Travel(12, MotionIntensity.Standard));
            Assert.Equal(380, MotionRules.Duration(380, MotionIntensity.Standard));
        }

        [Fact]
        public void SubtleShortensTravelAndDuration()
        {
            Assert.Equal(0.6, MotionRules.TravelFactor(MotionIntensity.Subtle));
            Assert.Equal(0.85, MotionRules.DurationFactor(MotionIntensity.Subtle));
            Assert.Equal(7.2, MotionRules.Travel(12, MotionIntensity.Subtle), 10);
            Assert.Equal(16.8, MotionRules.Travel(28, MotionIntensity.Subtle), 10);
            Assert.Equal(323, MotionRules.Duration(380, MotionIntensity.Subtle));  // 322.99999 -> 323
            Assert.Equal(94, MotionRules.Duration(110, MotionIntensity.Subtle));   // 93.5 -> 94
            Assert.Equal(0, MotionRules.Duration(0, MotionIntensity.Subtle));
        }

        [Fact]
        public void SettingsDefaults()
        {
            var settings = new ZeroStrap.Models.Persistable.Settings();
            Assert.False(settings.FollowWindowsAnimations);
            Assert.Equal(MotionIntensity.Standard, settings.MotionIntensity);
            Assert.False(settings.ReduceMotion);
        }

        [Fact]
        public void IntensitySerialisesAsAName()
        {
            Assert.Equal("\"Subtle\"", System.Text.Json.JsonSerializer.Serialize(MotionIntensity.Subtle));
        }
    }

    public class ThemeNewFieldValidationTests
    {
        private static ThemeDefinition Custom()
        {
            var t = BuiltinThemes.Obsidian();
            t.Id = "mine";
            t.Kind = ThemeKind.Custom;
            t.Name = "Mine";
            return t;
        }

        [Theory]
        [InlineData(99)]
        [InlineData(901)]
        public void WeightsOutOfRange(int weight)
        {
            var body = Custom(); body.Typography.BodyWeight = weight;
            var heading = Custom(); heading.Typography.HeadingWeight = weight;
            Assert.NotEmpty(ThemeValidator.Validate(body));
            Assert.NotEmpty(ThemeValidator.Validate(heading));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyFamilyIsRejected(string family)
        {
            var t = Custom(); t.Typography.Family = family;
            Assert.NotEmpty(ThemeValidator.Validate(t));
        }

        [Fact]
        public void UnknownEnumValuesAreRejected()
        {
            var density = Custom(); density.Layout.Density = (ThemeDensity)42;
            var button = Custom(); button.Components.ButtonStyle = (ThemeButtonStyle)42;
            var surface = Custom(); surface.Components.SurfaceStyle = (ThemeSurfaceStyle)42;
            Assert.NotEmpty(ThemeValidator.Validate(density));
            Assert.NotEmpty(ThemeValidator.Validate(button));
            Assert.NotEmpty(ThemeValidator.Validate(surface));
        }

        [Fact]
        public void EveryEditorChoiceValidates()
        {
            foreach (var density in Enum.GetValues<ThemeDensity>())
            foreach (var button in Enum.GetValues<ThemeButtonStyle>())
            foreach (var surface in Enum.GetValues<ThemeSurfaceStyle>())
            foreach (var heading in new[] { 500, 600, 700 })
            foreach (var body in new[] { 400, 500 })
            {
                var t = Custom();
                t.Layout.Density = density;
                t.Components.ButtonStyle = button;
                t.Components.SurfaceStyle = surface;
                t.Typography.HeadingWeight = heading;
                t.Typography.BodyWeight = body;
                t.Typography.Scale = 1.05;
                Assert.Empty(ThemeValidator.Validate(t));
            }
        }
    }
}
