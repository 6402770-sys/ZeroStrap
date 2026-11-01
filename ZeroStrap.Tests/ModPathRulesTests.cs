using System;
using System.Linq;

using Xunit;

using ZeroStrap.Mods;

namespace ZeroStrap.Tests
{
    public class ModPathRulesTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(@"content\..\ClientSettings\x.json")]
        [InlineData(@"..\content\a.png")]
        [InlineData(@"content\a\..\..\b.png")]
        [InlineData(@"\content\a.png")]
        [InlineData("/content/a.png")]
        [InlineData(@"C:\content\a.png")]
        [InlineData(@"C:content\a.png")]
        [InlineData(@"content\a.png:stream")]
        [InlineData(@"content\\a.png")]
        [InlineData(@"content\.\a.png")]
        [InlineData(@"ClientSettings\ClientAppSettings.json")]
        [InlineData(@"clientsettings\ClientAppSettings.json")]
        [InlineData("a.png")]
        [InlineData("content")]
        [InlineData(@"ssl\cacert.pem")]
        [InlineData(@"Libraries\x.png")]
        [InlineData(@"content\CON.png")]
        [InlineData(@"content\sounds\NUL")]
        [InlineData(@"content\a?.png")]
        [InlineData(@"content\bad.")]
        [InlineData(@"content\bad ")]
        public void RejectsUnsafePaths(string path)
        {
            Assert.False(ModPathRules.TryNormalize(path, out _, out string? error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Theory]
        [InlineData(".lua")]
        [InlineData(".LUAU")]
        [InlineData(".dll")]
        [InlineData(".exe")]
        [InlineData(".bat")]
        [InlineData(".cmd")]
        [InlineData(".ps1")]
        [InlineData(".js")]
        [InlineData(".vbs")]
        public void RejectsBlockedExtensions(string extension)
        {
            Assert.False(ModPathRules.IsValid(@"content\scripts\thing" + extension));
        }

        [Fact]
        public void RejectsOverlongPaths()
        {
            Assert.False(ModPathRules.IsValid(@"content\" + new string('a', 250) + ".png"));
        }

        [Theory]
        [InlineData("content/sounds/action_jump.mp3", @"content\sounds\action_jump.mp3")]
        [InlineData(@"CONTENT\Sounds\x.ogg", @"content\Sounds\x.ogg")]
        [InlineData(@"extracontent\places\Mobile.rbxl", @"ExtraContent\places\Mobile.rbxl")]
        [InlineData(@"PlatformContent\pc\textures\sky.png", @"PlatformContent\pc\textures\sky.png")]
        [InlineData(@"platformcontent\pc\fonts\a.ttf", @"PlatformContent\pc\fonts\a.ttf")]
        [InlineData(@"shaders\shaders_d3d11.pack", @"shaders\shaders_d3d11.pack")]
        [InlineData(@"content\textures\Cursors\KeyboardMouse\ArrowCursor.png", @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png")]
        [InlineData(@"content\fonts\families\Some Font.json", @"content\fonts\families\Some Font.json")]
        public void AcceptsAndNormalises(string input, string expected)
        {
            Assert.True(ModPathRules.TryNormalize(input, out string normalized, out string? error), error);
            Assert.Equal(expected, normalized);
        }

        [Fact]
        public void NormalizeThrowsOnInvalid()
        {
            Assert.Throws<ArgumentException>(() => ModPathRules.Normalize(@"..\x"));
        }

        [Fact]
        public void PathSetsAreCaseInsensitive()
        {
            var set = ModPathRules.NewPathSet();
            set.Add(@"content\Sounds\a.mp3");
            Assert.Contains(@"CONTENT\sounds\A.MP3", set);
        }

        [Theory]
        [InlineData(@"ClientSettings\ClientAppSettings.json", true)]
        [InlineData(@"clientsettings\x", true)]
        [InlineData("README.txt", true)]
        [InlineData("readme.TXT", true)]
        [InlineData(@"content\a.lock", true)]
        [InlineData("x.lock", true)]
        [InlineData(@"content\README.txt", false)]
        [InlineData(@"content\sounds\a.mp3", false)]
        public void ReservedModificationsPaths(string path, bool reserved)
        {
            Assert.Equal(reserved, ModPathRules.IsReservedModificationsPath(path));
        }

        [Fact]
        public void CombineConfinedRefusesEscape()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zs-confine");
            Assert.StartsWith(root, ModPathRules.CombineConfined(root, @"content\a.png"));
            Assert.Throws<ArgumentException>(() => ModPathRules.CombineConfined(root, @"..\evil.txt"));
            Assert.Throws<ArgumentException>(() => ModPathRules.CombineConfined(root, @"content\..\..\evil.txt"));
        }

        [Fact]
        public void CategoryGuessUsesMajority()
        {
            Assert.Equal(ModCategory.Sounds, ModPackageStore.GuessCategory(new[] { @"content\sounds\a.mp3", @"content\sounds\b.mp3", @"content\textures\x.png" }));
            Assert.Equal(ModCategory.Cursors, ModPackageStore.GuessCategory(new[] { @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png" }));
            Assert.Equal(ModCategory.Fonts, ModPackageStore.GuessCategory(new[] { @"content\fonts\x.ttf" }));
            Assert.Equal(ModCategory.Sky, ModPackageStore.GuessCategory(new[] { @"content\sky\a.tex" }));
            Assert.Equal(ModCategory.Places, ModPackageStore.GuessCategory(new[] { @"ExtraContent\places\a.rbxl" }));
            Assert.Equal(ModCategory.Textures, ModPackageStore.GuessCategory(new[] { @"PlatformContent\pc\textures\a.png" }));
            Assert.Equal(ModCategory.Other, ModPackageStore.GuessCategory(new[] { @"shaders\a.pack" }));
            Assert.Equal(ModCategory.Other, ModPackageStore.GuessCategory(Array.Empty<string>()));
        }

        [Fact]
        public void SlugifyProducesValidIds()
        {
            Assert.Equal("my-cool-mod", ModPackageStore.Slugify("  My  Cool_Mod!! "));
            Assert.Equal("mod", ModPackageStore.Slugify("!!!"));
            Assert.True(ModPackageStore.IsValidId(ModPackageStore.Slugify(new string('x', 200))));
            Assert.False(ModPackageStore.IsValidId(@"..\x"));
            Assert.False(ModPackageStore.IsValidId("Upper"));
        }
    }
}
