using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Xunit;

using ZeroStrap.Models.Theming;
using ZeroStrap.UI.Theming;

namespace ZeroStrap.Tests
{
    public sealed class ThemeMediaTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "zs-theme-media-" + Guid.NewGuid().ToString("N"));

        public ThemeMediaTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        private string Write(string name, byte[] bytes)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        // ---------------------------------------------------------------- validation

        [Theory]
        [InlineData("clip.mp4", ThemeBackgroundType.Video)]
        [InlineData("CLIP.MP4", ThemeBackgroundType.Video)]
        [InlineData("a.png", ThemeBackgroundType.Image)]
        [InlineData("a.JPG", ThemeBackgroundType.Image)]
        [InlineData("a.jpeg", ThemeBackgroundType.Image)]
        public void AcceptsSupportedExtensions(string name, ThemeBackgroundType kind)
        {
            var check = ThemeMedia.Check(name, 1024);
            Assert.True(check.Ok);
            Assert.Equal(kind, check.Kind);
            Assert.Null(check.Warning);
        }

        [Theory]
        [InlineData("a.gif")]
        [InlineData("a.webm")]
        [InlineData("a.exe")]
        [InlineData("a")]
        [InlineData("a.mp4.exe")]
        public void RejectsOtherExtensions(string name)
        {
            var check = ThemeMedia.Check(name, 1024);
            Assert.False(check.Ok);
            Assert.NotNull(check.Error);
        }

        [Fact]
        public void RejectsMissingEmptyAndWrongKind()
        {
            Assert.False(ThemeMedia.Check(null, 10).Ok);
            Assert.False(ThemeMedia.Check("", 10).Ok);
            Assert.False(ThemeMedia.Check("a.png", null).Ok);
            Assert.False(ThemeMedia.Check("a.png", 0).Ok);
            Assert.False(ThemeMedia.Check("a.png", 10, ThemeBackgroundType.Video).Ok);
            Assert.False(ThemeMedia.Check("a.mp4", 10, ThemeBackgroundType.Image).Ok);
            Assert.True(ThemeMedia.Check("a.mp4", 10, ThemeBackgroundType.Video).Ok);
        }

        [Fact]
        public void SizeThresholds()
        {
            Assert.Null(ThemeMedia.Check("a.mp4", ThemeMedia.WarnBytes).Warning);

            var warn = ThemeMedia.Check("a.mp4", ThemeMedia.WarnBytes + 1);
            Assert.True(warn.Ok);
            Assert.NotNull(warn.Warning);

            Assert.True(ThemeMedia.Check("a.mp4", ThemeMedia.MaxBytes).Ok);

            var tooBig = ThemeMedia.Check("a.mp4", ThemeMedia.MaxBytes + 1);
            Assert.False(tooBig.Ok);
            Assert.Contains("500", tooBig.Error);
        }

        [Fact]
        public void ThresholdsMatchTheSpec()
        {
            Assert.Equal(200L * 1024 * 1024, ThemeMedia.WarnBytes);
            Assert.Equal(500L * 1024 * 1024, ThemeMedia.MaxBytes);
        }

        [Fact]
        public void CheckFileReadsTheLengthFromDisk()
        {
            Assert.True(ThemeMedia.CheckFile(Write("ok.png", new byte[] { 1, 2, 3 })).Ok);
            Assert.False(ThemeMedia.CheckFile(Write("empty.png", Array.Empty<byte>())).Ok);
            Assert.False(ThemeMedia.CheckFile(Path.Combine(_root, "missing.png")).Ok);
        }

        // ---------------------------------------------------------------- naming and dedupe

        [Fact]
        public void StoredNameIsShaPlusLowerExtension()
        {
            Assert.Equal("abc123.mp4", ThemeMedia.StoredName("ABC123", ".MP4"));
            Assert.Equal("abc123.poster.png", ThemeMedia.PosterName("abc123.mp4"));
            Assert.Null(ThemeValidator.ValidateMediaName(ThemeMedia.StoredName(new string('a', 64), ".mp4"), ThemeBackgroundType.Video));
        }

        [Fact]
        public void ImportCopiesAsShaNamedFileAndKeepsTheSource()
        {
            string source = Write("My Photo.PNG", new byte[] { 9, 8, 7, 6 });
            string media = Path.Combine(_root, "media");

            var result = ThemeMedia.Import(media, source);

            Assert.True(result.Added);
            Assert.Equal(ThemeMedia.HashFile(source) + ".png", result.Name);
            Assert.Matches("^[0-9a-f]{64}\\.png$", result.Name);
            Assert.True(File.Exists(source));
            Assert.Equal(new byte[] { 9, 8, 7, 6 }, File.ReadAllBytes(Path.Combine(media, result.Name)));
            Assert.Empty(Directory.GetFiles(media, "*.tmp"));
        }

        [Fact]
        public void ImportingTheSameContentTwiceDedupes()
        {
            string a = Write("one.jpg", new byte[] { 1, 1, 2, 3 });
            string b = Write("two.jpg", new byte[] { 1, 1, 2, 3 });
            string media = Path.Combine(_root, "media");

            var first = ThemeMedia.Import(media, a);
            var second = ThemeMedia.Import(media, b);

            Assert.True(first.Added);
            Assert.False(second.Added);
            Assert.Equal(first.Name, second.Name);
            Assert.Single(Directory.GetFiles(media));
        }

        [Fact]
        public void DifferentContentGetsDifferentNames()
        {
            string media = Path.Combine(_root, "media");
            var a = ThemeMedia.Import(media, Write("a.png", new byte[] { 1 }));
            var b = ThemeMedia.Import(media, Write("b.png", new byte[] { 2 }));

            Assert.NotEqual(a.Name, b.Name);
            Assert.Equal(2, Directory.GetFiles(media).Length);
        }

        [Fact]
        public void SameBytesWithDifferentExtensionAreSeparateFiles()
        {
            string media = Path.Combine(_root, "media");
            var a = ThemeMedia.Import(media, Write("a.png", new byte[] { 5, 5 }));
            var b = ThemeMedia.Import(media, Write("a.jpg", new byte[] { 5, 5 }));

            Assert.NotEqual(a.Name, b.Name);
        }

        // ---------------------------------------------------------------- reference counting

        private static ThemeDefinition ThemeUsing(string? media)
        {
            var t = BuiltinThemes.Obsidian();
            t.Background = media is null
                ? new ThemeBackground()
                : new ThemeBackground { Type = media.EndsWith(".mp4") ? ThemeBackgroundType.Video : ThemeBackgroundType.Image, Media = media };
            return t;
        }

        [Fact]
        public void ReferenceCounting()
        {
            var themes = new List<ThemeDefinition> { ThemeUsing("a.png"), ThemeUsing(null), ThemeUsing("B.png") };

            Assert.True(ThemeMedia.IsReferenced("a.png", themes));
            Assert.True(ThemeMedia.IsReferenced("A.PNG", themes));
            Assert.True(ThemeMedia.IsReferenced("b.png", themes));
            Assert.False(ThemeMedia.IsReferenced("c.png", themes));
            Assert.False(ThemeMedia.IsReferenced(null, themes));
            Assert.False(ThemeMedia.IsReferenced("", themes));
        }

        [Fact]
        public void VideosOwnAPosterImagesDoNot()
        {
            Assert.Equal(new[] { "x.mp4", "x.poster.png" }, ThemeMedia.FilesFor("x.mp4"));
            Assert.Equal(new[] { "x.png" }, ThemeMedia.FilesFor("x.png"));
        }

        // ---------------------------------------------------------------- store: deleting and releasing media

        private ThemeStore NewStore() =>
            new(Path.Combine(_root, "Themes"), Path.Combine(_root, "Themes", "Media"));

        private static string Sha(char c) => new string(c, 64);

        private string PutMedia(ThemeStore store, string name, bool poster = false)
        {
            Directory.CreateDirectory(store.MediaDirectory);
            File.WriteAllBytes(Path.Combine(store.MediaDirectory, name), new byte[] { 1, 2, 3 });
            if (poster)
                File.WriteAllBytes(Path.Combine(store.MediaDirectory, ThemeMedia.PosterName(name)), new byte[] { 4 });

            return name;
        }

        private ThemeDefinition CustomWith(ThemeStore store, string name, string? media)
        {
            var made = store.Create("obsidian", name);
            if (media is not null)
            {
                made.Background = new ThemeBackground
                {
                    Type = media.EndsWith(".mp4") ? ThemeBackgroundType.Video : ThemeBackgroundType.Image,
                    Media = media
                };
                store.Update(made);
            }

            return made;
        }

        [Fact]
        public void DeletingAThemeKeepsMediaAnotherThemeStillUses()
        {
            var store = NewStore();
            string media = PutMedia(store, Sha('a') + ".mp4", poster: true);
            var first = CustomWith(store, "First", media);
            var second = CustomWith(store, "Second", media);

            store.Delete(first.Id);

            Assert.True(File.Exists(Path.Combine(store.MediaDirectory, media)));
            Assert.True(File.Exists(Path.Combine(store.MediaDirectory, ThemeMedia.PosterName(media))));

            store.Delete(second.Id);

            Assert.False(File.Exists(Path.Combine(store.MediaDirectory, media)));
            Assert.False(File.Exists(Path.Combine(store.MediaDirectory, ThemeMedia.PosterName(media))));
        }

        [Fact]
        public void ReleaseMediaOnlyDeletesUnreferencedFiles()
        {
            var store = NewStore();
            string used = PutMedia(store, Sha('b') + ".png");
            string unused = PutMedia(store, Sha('c') + ".png");
            CustomWith(store, "User", used);

            store.ReleaseMedia(used);
            store.ReleaseMedia(unused);
            store.ReleaseMedia(null);
            store.ReleaseMedia("..\\..\\escape.png");

            Assert.True(File.Exists(Path.Combine(store.MediaDirectory, used)));
            Assert.False(File.Exists(Path.Combine(store.MediaDirectory, unused)));
        }

        [Fact]
        public void RestoreDefaultsReleasesTheOldBackgroundMedia()
        {
            var store = NewStore();
            string media = PutMedia(store, Sha('d') + ".mp4", poster: true);
            var theme = CustomWith(store, "Moody", media);

            var reset = store.RestoreDefaults(theme.Id);

            Assert.Equal(ThemeBackgroundType.None, reset.Background.Type);
            Assert.False(File.Exists(Path.Combine(store.MediaDirectory, media)));
            Assert.False(File.Exists(Path.Combine(store.MediaDirectory, ThemeMedia.PosterName(media))));
        }

        [Fact]
        public void PruneRemovesOnlyUnreferencedContentAddressedFiles()
        {
            var store = NewStore();
            string keep = PutMedia(store, Sha('e') + ".mp4", poster: true);
            string orphan = PutMedia(store, Sha('f') + ".png");
            PutMedia(store, "my-own-name.png");
            CustomWith(store, "Keeper", keep);

            int deleted = store.PruneOrphanedMedia();

            Assert.Equal(1, deleted);
            Assert.True(File.Exists(Path.Combine(store.MediaDirectory, keep)));
            Assert.True(File.Exists(Path.Combine(store.MediaDirectory, ThemeMedia.PosterName(keep))));
            Assert.False(File.Exists(Path.Combine(store.MediaDirectory, orphan)));
            Assert.True(File.Exists(Path.Combine(store.MediaDirectory, "my-own-name.png")));
        }

        [Fact]
        public void PruneDoesNothingWhileAThemeFileIsUnreadable()
        {
            var store = NewStore();
            string media = PutMedia(store, Sha('1') + ".png");
            CustomWith(store, "Fine", null);
            File.WriteAllText(Path.Combine(store.ThemesDirectory, "broken.json"), "{ not json");

            Assert.Equal(0, store.PruneOrphanedMedia());
            Assert.True(File.Exists(Path.Combine(store.MediaDirectory, media)));
        }

        // ---------------------------------------------------------------- change detection

        private static ThemeBackground Video(Action<ThemeBackground>? tweak = null)
        {
            var b = new ThemeBackground { Type = ThemeBackgroundType.Video, Media = "v.mp4", Opacity = 0.5, Blur = 0, Speed = 1 };
            tweak?.Invoke(b);
            return b;
        }

        [Fact]
        public void NoBackgroundOnEitherSideIsNoChange()
        {
            Assert.Equal(BackgroundChange.None, ThemeMedia.Compare(null, "#000000", null, "#111111"));
            Assert.Equal(BackgroundChange.None, ThemeMedia.Compare(new ThemeBackground(), "#000000", new ThemeBackground { Opacity = 0.2 }, "#000000"));
            // a type without a file is not a background
            Assert.Equal(BackgroundChange.None, ThemeMedia.Compare(null, "#000", new ThemeBackground { Type = ThemeBackgroundType.Video }, "#000"));
        }

        [Fact]
        public void IdenticalBackgroundIsNoChange()
        {
            Assert.Equal(BackgroundChange.None, ThemeMedia.Compare(Video(), "#0E0E0E", Video(), "#0e0e0e"));
        }

        [Fact]
        public void MediaTypeOrPresenceChangesRebuild()
        {
            Assert.Equal(BackgroundChange.Rebuild, ThemeMedia.Compare(null, "#000", Video(), "#000"));
            Assert.Equal(BackgroundChange.Rebuild, ThemeMedia.Compare(Video(), "#000", null, "#000"));
            Assert.Equal(BackgroundChange.Rebuild, ThemeMedia.Compare(Video(), "#000", Video(b => b.Media = "w.mp4"), "#000"));
            Assert.Equal(BackgroundChange.Rebuild, ThemeMedia.Compare(Video(b => { b.Type = ThemeBackgroundType.Image; b.Media = "p.png"; }), "#000",
                Video(), "#000"));
        }

        [Fact]
        public void DimBlurSpeedAndScrimColourOnlyUpdateParameters()
        {
            Assert.Equal(BackgroundChange.Parameters, ThemeMedia.Compare(Video(), "#000", Video(b => b.Opacity = 0.7), "#000"));
            Assert.Equal(BackgroundChange.Parameters, ThemeMedia.Compare(Video(), "#000", Video(b => b.Blur = 5), "#000"));
            Assert.Equal(BackgroundChange.Parameters, ThemeMedia.Compare(Video(), "#000", Video(b => b.Speed = 2), "#000"));
            Assert.Equal(BackgroundChange.Parameters, ThemeMedia.Compare(Video(), "#000", Video(), "#ffffff"));
        }

        [Fact]
        public void CrossingTheHalfResolutionBlurRebuilds()
        {
            Assert.Equal(BackgroundChange.Rebuild, ThemeMedia.Compare(Video(b => b.Blur = 7), "#000", Video(b => b.Blur = 8), "#000"));
            Assert.Equal(BackgroundChange.Rebuild, ThemeMedia.Compare(Video(b => b.Blur = 20), "#000", Video(b => b.Blur = 3), "#000"));
            Assert.Equal(BackgroundChange.Parameters, ThemeMedia.Compare(Video(b => b.Blur = 8), "#000", Video(b => b.Blur = 30), "#000"));
            Assert.True(ThemeMedia.IsHalfResolution(Video(b => b.Blur = ThemeMedia.HalfResolutionBlur)));
            Assert.False(ThemeMedia.IsHalfResolution(Video(b => b.Blur = ThemeMedia.HalfResolutionBlur - 1)));
        }

        // ---------------------------------------------------------------- notice board

        [Fact]
        public void NoticesAreShownOnce()
        {
            var board = new BackgroundNoticeBoard();
            string? posted = null;
            board.Posted += id => posted = id;

            Assert.Null(board.Take("t1"));

            board.Post("t1", BackgroundNoticeBoard.FailureMessage(ThemeBackgroundType.Video));

            Assert.Equal("t1", posted);
            Assert.True(board.Has("t1"));
            Assert.Null(board.Take("t2"));
            Assert.Equal("The background video couldn't be played. Choose another file.", board.Take("t1"));
            Assert.Null(board.Take("t1"));
            Assert.False(board.Has("t1"));
        }
    }
}
