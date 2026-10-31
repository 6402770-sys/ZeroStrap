using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Text;

using Xunit;

using ZeroStrap.Models.Theming;
using ZeroStrap.UI.Theming;

namespace ZeroStrap.Tests
{
    public sealed class ThemeStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "zs-theme-store-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _logs = new();
        private readonly ThemeStore _store;

        public ThemeStoreTests()
        {
            _store = NewStore("main");
        }

        private ThemeStore NewStore(string name) =>
            new(Path.Combine(_root, name, "Themes"), Path.Combine(_root, name, "Themes", "Media"), _logs.Add);

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        [Fact]
        public void ListHasBuiltinsFirstThenCustomsByName()
        {
            _store.Create("obsidian", "Zebra");
            _store.Create("light", "alpha");
            _store.Create("obsidian", "Mid");

            var names = _store.List().Select(t => t.Name).ToArray();

            Assert.Equal(new[] { "Obsidian", "Light", "alpha", "Mid", "Zebra" }, names);
            Assert.Equal(2, _store.List().Count(t => t.Kind == ThemeKind.Builtin));
        }

        [Fact]
        public void CreateCopiesSourceAndSetsMetadata()
        {
            var made = _store.Create("light", "Paper");

            Assert.Equal(ThemeKind.Custom, made.Kind);
            Assert.Equal(ThemeBase.Light, made.Base);
            Assert.Equal("light", made.BasedOn);
            Assert.Equal("Paper", made.Name);
            Assert.Equal(BuiltinThemes.Light().Colors.Background, made.Colors.Background);
            Assert.True(File.Exists(Path.Combine(_store.ThemesDirectory, made.Id + ".json")));

            var loaded = _store.Get(made.Id);
            Assert.Equal(made.ToJson(), loaded!.ToJson());
        }

        [Fact]
        public void CreateFromCustomKeepsTheBuiltinAsBasedOn()
        {
            var first = _store.Create("obsidian", "First");
            var second = _store.Create(first.Id, "Second");

            Assert.Equal("obsidian", second.BasedOn);
        }

        [Fact]
        public void CreateFromMissingThemeThrows()
        {
            Assert.Throws<ThemeStoreException>(() => _store.Create("nope", "X"));
        }

        [Fact]
        public void NamesAreMadeUnique()
        {
            Assert.Equal("Obsidian 2", _store.Create("obsidian", "Obsidian").Name);
            Assert.Equal("obsidian 3", _store.Create("obsidian", "obsidian").Name);
            Assert.Equal("Mine", _store.UniqueName("Mine"));

            _store.Create("obsidian", "Mine");
            Assert.Equal("Mine 2", _store.UniqueName("Mine"));
        }

        [Fact]
        public void UniqueNameStaysWithinFortyCharacters()
        {
            string longName = new string('a', 40);
            _store.Create("obsidian", longName);

            string next = _store.UniqueName(longName);

            Assert.True(next.Length <= 40);
            Assert.NotEqual(longName, next);
            Assert.Null(ThemeValidator.ValidateName(next));
        }

        [Fact]
        public void DuplicateMakesAnIndependentCopy()
        {
            var original = _store.Create("obsidian", "Original");
            var copy = _store.Duplicate(original.Id);

            Assert.NotEqual(original.Id, copy.Id);
            Assert.Equal("Original copy", copy.Name);
            Assert.Equal(original.Colors.Accent, copy.Colors.Accent);

            copy.Colors.Accent = "#112233";
            _store.Update(copy);
            Assert.NotEqual("#112233", _store.Get(original.Id)!.Colors.Accent);
            Assert.Equal("Original copy 2", _store.Duplicate(original.Id).Name);
        }

        [Fact]
        public void RenameChangesNameAndRejectsBadOrTakenNames()
        {
            var a = _store.Create("obsidian", "A");
            _store.Create("obsidian", "B");

            Assert.Equal("Renamed", _store.Rename(a.Id, "Renamed").Name);
            Assert.Equal("Renamed", _store.Get(a.Id)!.Name);

            Assert.Throws<ThemeStoreException>(() => _store.Rename(a.Id, "b"));
            Assert.Throws<ThemeStoreException>(() => _store.Rename(a.Id, "bad/name"));
            Assert.Throws<ThemeStoreException>(() => _store.Rename(a.Id, ""));
            Assert.Throws<ThemeStoreException>(() => _store.Rename("obsidian", "Hacked"));

            // renaming to its own name is fine
            Assert.Equal("Renamed", _store.Rename(a.Id, "Renamed").Name);
        }

        [Fact]
        public void UpdateWritesAtomicallyAndLeavesNoTempFiles()
        {
            var made = _store.Create("obsidian", "Edit me");

            made.Colors.Accent = "#abcdef";
            made.Components.Radius = 12;
            _store.Update(made);

            var loaded = _store.Get(made.Id)!;
            Assert.Equal("#ABCDEF", loaded.Colors.Accent); // normalised
            Assert.Equal(12, loaded.Components.Radius);
            Assert.Empty(Directory.GetFiles(_store.ThemesDirectory, "*.tmp"));
        }

        [Fact]
        public void UpdateRejectsInvalidAndBuiltinAndMissing()
        {
            var made = _store.Create("obsidian", "Valid");

            var bad = made.Clone();
            bad.Colors.Border = "nope";
            Assert.Throws<ThemeStoreException>(() => _store.Update(bad));

            // the stored file is untouched
            Assert.Equal(made.Colors.Border, _store.Get(made.Id)!.Colors.Border);

            var builtin = BuiltinThemes.Obsidian();
            Assert.Throws<ThemeStoreException>(() => _store.Update(builtin));

            var ghost = made.Clone();
            ghost.Id = "ghost123";
            Assert.Throws<ThemeStoreException>(() => _store.Update(ghost));

            var traversal = made.Clone();
            traversal.Id = "../escape";
            Assert.Throws<ThemeStoreException>(() => _store.Update(traversal));
        }

        [Fact]
        public void DeleteRemovesCustomsOnlyAndRaisesEvent()
        {
            var made = _store.Create("obsidian", "Temp");
            string? deleted = null;
            _store.Deleted += id => deleted = id;

            Assert.Throws<ThemeStoreException>(() => _store.Delete("obsidian"));
            Assert.Throws<ThemeStoreException>(() => _store.Delete("light"));

            _store.Delete(made.Id);

            Assert.Equal(made.Id, deleted);
            Assert.Null(_store.Get(made.Id));
            Assert.DoesNotContain(_store.List(), t => t.Id == made.Id);
            Assert.Throws<ThemeStoreException>(() => _store.Delete(made.Id));
        }

        [Fact]
        public void CorruptAndInvalidFilesAreSkippedAndLogged()
        {
            var good = _store.Create("obsidian", "Good");
            string dir = _store.ThemesDirectory;

            File.WriteAllText(Path.Combine(dir, "corrupt.json"), "{ this is not json");
            File.WriteAllText(Path.Combine(dir, "empty.json"), "");

            var badColour = good.Clone();
            badColour.Id = "badcolour";
            badColour.Name = "Bad colour";
            badColour.Colors.Accent = "zzz";
            File.WriteAllText(Path.Combine(dir, "badcolour.json"), badColour.ToJson());

            var mismatch = good.Clone();
            mismatch.Name = "Mismatch";
            File.WriteAllText(Path.Combine(dir, "otherfile.json"), mismatch.ToJson());

            var claimsBuiltin = BuiltinThemes.Light();
            File.WriteAllText(Path.Combine(dir, "light.json"), claimsBuiltin.ToJson());

            var customs = _store.ListCustom();

            Assert.Equal(new[] { "Good" }, customs.Select(t => t.Name).ToArray());
            Assert.True(_logs.Count >= 5);
            Assert.Null(_store.Get("corrupt"));
        }

        [Fact]
        public void FilesAreAlwaysTreatedAsCustom()
        {
            var made = _store.Create("obsidian", "Sneaky");
            var tampered = made.Clone();
            tampered.Kind = ThemeKind.Builtin;
            File.WriteAllText(Path.Combine(_store.ThemesDirectory, made.Id + ".json"), tampered.ToJson());

            Assert.Equal(ThemeKind.Custom, _store.Get(made.Id)!.Kind);
        }

        [Fact]
        public void RestoreDefaultsResetsValuesButKeepsIdentity()
        {
            var made = _store.Create("light", "Tweaked");
            made.Colors.Accent = "#FF0000";
            made.Colors.Background = "#123456";
            made.Typography.Scale = 1.1;
            made.Components.Radius = 14;
            _store.Update(made);

            var restored = _store.RestoreDefaults(made.Id);

            Assert.Equal(made.Id, restored.Id);
            Assert.Equal("Tweaked", restored.Name);
            Assert.Equal(ThemeKind.Custom, restored.Kind);
            Assert.Equal(BuiltinThemes.Light().Colors.Accent, restored.Colors.Accent);
            Assert.Equal(BuiltinThemes.Light().Colors.Background, restored.Colors.Background);
            Assert.Equal(1.0, restored.Typography.Scale);
            Assert.Equal(6, restored.Components.Radius);
            Assert.Equal("light", restored.BasedOn);
            Assert.Equal(restored.ToJson(), _store.Get(made.Id)!.ToJson());
        }

        [Fact]
        public void RestoreDefaultsFallsBackToTheBaseWhenBasedOnIsMissing()
        {
            var made = _store.Create("light", "Orphan");
            var edited = made.Clone();
            edited.BasedOn = "deletedparent";
            edited.Colors.Accent = "#FF0000";
            _store.Update(edited);

            var restored = _store.RestoreDefaults(made.Id);

            Assert.Equal(BuiltinThemes.Light().Colors.Accent, restored.Colors.Accent);
            Assert.Equal("light", restored.BasedOn);
            Assert.Throws<ThemeStoreException>(() => _store.RestoreDefaults("obsidian"));
        }

        // ------------------------------------------------------------ import / export

        [Fact]
        public void ExportImportRoundTripAssignsNewIdAndDedupesName()
        {
            var made = _store.Create("obsidian", "Travelling");
            made.Colors.Accent = "#336699";
            made.Layout.Density = ThemeDensity.Spacious;
            _store.Update(made);

            string file = Path.Combine(_root, "out", "travelling.zerotheme");
            _store.Export(made.Id, file);
            Assert.True(File.Exists(file));

            // same store: name is deduped, id is new
            var sameStore = _store.Import(file);
            Assert.NotEqual(made.Id, sameStore.Theme.Id);
            Assert.Equal("Travelling 2", sameStore.Theme.Name);
            Assert.Equal("#336699", sameStore.Theme.Colors.Accent);
            Assert.Equal(ThemeDensity.Spacious, sameStore.Theme.Layout.Density);
            Assert.Equal(ThemeKind.Custom, sameStore.Theme.Kind);

            // different store: name kept
            var other = NewStore("other");
            var imported = other.Import(file);
            Assert.Equal("Travelling", imported.Theme.Name);
            Assert.Equal("#336699", other.Get(imported.Theme.Id)!.Colors.Accent);
        }

        [Fact]
        public void ExportOfABuiltinImportsAsCustom()
        {
            string file = Path.Combine(_root, "builtin.zerotheme");
            _store.Export("obsidian", file);

            var imported = _store.Import(file);

            Assert.Equal(ThemeKind.Custom, imported.Theme.Kind);
            Assert.Equal("Obsidian 2", imported.Theme.Name);
            Assert.Equal("obsidian", imported.Theme.BasedOn);
        }

        [Fact]
        public void MediaTravelsWithTheThemeAndIsRenamedOnImport()
        {
            var made = _store.Create("obsidian", "Pictured");
            Directory.CreateDirectory(_store.MediaDirectory);
            File.WriteAllBytes(Path.Combine(_store.MediaDirectory, "bg.png"), new byte[] { 1, 2, 3, 4 });
            made.Background = new ThemeBackground { Type = ThemeBackgroundType.Image, Media = "bg.png" };
            _store.Update(made);

            string file = Path.Combine(_root, "pictured.zerotheme");
            _store.Export(made.Id, file);

            using (var zip = ZipFile.OpenRead(file))
                Assert.Contains(zip.Entries, e => e.FullName == "media/bg.png");

            var other = NewStore("other");
            var imported = other.Import(file).Theme;

            Assert.Equal(ThemeBackgroundType.Image, imported.Background.Type);
            Assert.Equal(imported.Id + "-bg.png", imported.Background.Media);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(other.MediaDirectory, imported.Background.Media!)));

            // deleting the theme removes its media
            other.Delete(imported.Id);
            Assert.False(File.Exists(Path.Combine(other.MediaDirectory, imported.Id + "-bg.png")));
        }

        private string Zip(params (string name, byte[] data)[] files)
        {
            string path = Path.Combine(_root, "crafted-" + Guid.NewGuid().ToString("N") + ".zerotheme");
            Directory.CreateDirectory(_root);

            using var fs = File.Create(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            foreach (var (name, data) in files)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data, 0, data.Length);
            }

            return path;
        }

        private static byte[] Json(Action<ThemeDefinition>? edit = null)
        {
            var t = BuiltinThemes.Obsidian();
            t.Id = "whatever1";
            t.Kind = ThemeKind.Custom;
            t.Name = "Crafted";
            edit?.Invoke(t);
            return Encoding.UTF8.GetBytes(t.ToJson());
        }

        [Fact]
        public void ImportRejectsMissingOrInvalidThemeJson()
        {
            Assert.Throws<ThemeStoreException>(() => _store.Import(Zip(("readme.txt", new byte[] { 1 }))));
            Assert.Throws<ThemeStoreException>(() => _store.Import(Zip(("theme.json", Encoding.UTF8.GetBytes("{ nope")))));
            Assert.Throws<ThemeStoreException>(() => _store.Import(Zip(("theme.json", Json(t => t.Colors.Accent = "red")))));
            Assert.Throws<ThemeStoreException>(() => _store.Import(Zip(("theme.json", Json(t => t.Components.Radius = 99)))));
            Assert.Throws<ThemeStoreException>(() => _store.Import(Zip(("theme.json", Json(t => t.Name = "con")))));
            Assert.Throws<ThemeStoreException>(() => _store.Import(Zip(("theme.json", Json(t => t.SchemaVersion = 7)))));
            Assert.Throws<ThemeStoreException>(() => _store.Import(Path.Combine(_root, "does-not-exist.zerotheme")));
            Assert.Empty(_store.ListCustom());
        }

        [Fact]
        public void ImportRejectsDisallowedMediaTypes()
        {
            var file = Zip(
                ("theme.json", Json(t => t.Background = new ThemeBackground { Type = ThemeBackgroundType.Image, Media = "evil.exe" })),
                ("media/evil.exe", new byte[] { 0x4D, 0x5A }));

            Assert.Throws<ThemeStoreException>(() => _store.Import(file));
            Assert.Empty(_store.ListCustom());
            Assert.False(Directory.Exists(_store.MediaDirectory) && Directory.GetFiles(_store.MediaDirectory).Length > 0);
        }

        [Fact]
        public void ImportDropsMissingMediaWithAWarning()
        {
            var file = Zip(("theme.json", Json(t => t.Background = new ThemeBackground { Type = ThemeBackgroundType.Image, Media = "gone.png" })));

            var result = _store.Import(file);

            Assert.Equal(ThemeBackgroundType.None, result.Theme.Background.Type);
            Assert.Null(result.Theme.Background.Media);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void ImportIgnoresTraversalEntriesAndNeverWritesOutsideTheStore()
        {
            string escape = Path.Combine(_root, "escaped.json");
            var file = Zip(("theme.json", Json()), ("../../escaped.json", Encoding.UTF8.GetBytes("{}")));

            var result = _store.Import(file);

            Assert.False(File.Exists(escape));
            Assert.Contains(result.Warnings, w => w.Contains("escaped.json"));
            Assert.Single(_store.ListCustom());
        }

        [Fact]
        public void ImportForcesCustomKindAndIgnoresClaimedIdAndBasedOn()
        {
            var file = Zip(("theme.json", Json(t =>
            {
                t.Id = "obsidian";
                t.Kind = ThemeKind.Builtin;
                t.BasedOn = "../../x";
            })));

            var result = _store.Import(file).Theme;

            Assert.NotEqual("obsidian", result.Id);
            Assert.Equal(ThemeKind.Custom, result.Kind);
            Assert.Equal("obsidian", result.BasedOn);
        }
    }
}
