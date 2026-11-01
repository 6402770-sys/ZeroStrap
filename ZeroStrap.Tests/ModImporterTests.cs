using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

using Xunit;

using ZeroStrap.Mods;
using ZeroStrap.Utility;

namespace ZeroStrap.Tests
{
    public class ModImporterTests : IDisposable
    {
        private readonly ModStudioFixture _fx = new();
        private readonly string _work;

        public ModImporterTests()
        {
            _work = Path.Combine(_fx.Root, "work");
            Directory.CreateDirectory(_work);
        }

        public void Dispose() => _fx.Dispose();

        // ---------------------------------------------------------------- helpers

        private sealed record Z(string Name, byte[]? Data = null, int? Attributes = null);

        private static Z F(string name, string content = "data") => new(name, Encoding.UTF8.GetBytes(content));

        private string MakeZip(string fileName, params Z[] entries)
        {
            string path = Path.Combine(_work, fileName);

            using (var stream = File.Create(path))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var e in entries)
                {
                    var entry = zip.CreateEntry(e.Name, CompressionLevel.Optimal);

                    if (e.Attributes is int attributes)
                        entry.ExternalAttributes = attributes;

                    if (e.Name.EndsWith('/'))
                        continue;

                    using var s = entry.Open();
                    var data = e.Data ?? Array.Empty<byte>();
                    s.Write(data, 0, data.Length);
                }
            }

            return path;
        }

        private static ArchiveLimits Limits(int? entries = null, long? entryBytes = null)
        {
            var d = ModImporter.DefaultLimits();

            return new ArchiveLimits
            {
                MaxTotalBytes = d.MaxTotalBytes,
                MaxEntries = entries ?? d.MaxEntries,
                MaxEntryBytes = entryBytes ?? d.MaxEntryBytes,
                MaxCompressionRatio = d.MaxCompressionRatio,
                MinBytesForRatioCheck = d.MinBytesForRatioCheck,
                RejectLinks = true
            };
        }

        private static ImportPreview Analyze(string path, ArchiveLimits? limits = null) => ModImporter.Analyze(path, limits);

        private string ZipOf(params string[] names) => MakeZip("z" + Guid.NewGuid().ToString("N") + ".zip", names.Select(n => F(n)).ToArray());

        private static void AssertRejected(ImportPreview preview, string entry, string? reasonContains = null)
        {
            var rejection = Assert.Single(preview.RejectedEntries, r => r.Path == entry);

            if (reasonContains is not null)
                Assert.Contains(reasonContains, rejection.Reason, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(preview.AcceptedFiles, f => f.Path.Replace('\\', '/').Equals(entry, StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> Accepted(ImportPreview preview) => preview.AcceptedFiles.Select(f => f.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        // ---------------------------------------------------------------- path safety

        [Theory]
        [InlineData("content/../../evil.png", "traversal")]
        [InlineData("../evil.png", "traversal")]
        [InlineData("/content/evil.png", "rooted")]
        [InlineData("\\content\\evil.png", "rooted")]
        [InlineData("C:/content/evil.png", "drive")]
        [InlineData("C:\\content\\evil.png", "drive")]
        [InlineData("content/evil.png:stream", "stream")]
        [InlineData("content/CON.png", "reserved")]
        [InlineData("content/aux", "reserved")]
        public void UnsafePathsAreRejectedAndTheRestStillImports(string bad, string reason)
        {
            var preview = Analyze(ZipOf("content/ok.png", bad));

            Assert.Null(preview.FatalReason);
            Assert.Equal(new[] { @"content\ok.png" }, Accepted(preview));
            AssertRejected(preview, bad, reason);
        }

        [Fact]
        public void ZipSymlinkEntriesAreRejected()
        {
            string zip = MakeZip("sym.zip",
                F("content/ok.png"),
                new Z("content/link.png", Encoding.UTF8.GetBytes("target"), unchecked((int)0xA1FF0000)));

            var preview = Analyze(zip);

            Assert.Equal(new[] { @"content\ok.png" }, Accepted(preview));
            AssertRejected(preview, "content/link.png", "link");
        }

        [Fact]
        public void ZipReparsePointEntriesAreRejected()
        {
            string zip = MakeZip("rp.zip", F("content/ok.png"), new Z("content/rp.png", Encoding.UTF8.GetBytes("x"), 0x400));

            var preview = Analyze(zip);

            Assert.Equal(new[] { @"content\ok.png" }, Accepted(preview));
            AssertRejected(preview, "content/rp.png", "link");
        }

        [Fact]
        public void HighCompressionRatioEntryIsRejected()
        {
            string zip = MakeZip("bomb.zip", F("content/ok.png"), new Z("content/bomb.png", new byte[4 * 1024 * 1024]));

            var preview = Analyze(zip);

            Assert.Equal(new[] { @"content\ok.png" }, Accepted(preview));
            AssertRejected(preview, "content/bomb.png", "ratio");
        }

        [Fact]
        public void TooManyEntriesRefusesTheWholeArchive()
        {
            string zip = ZipOf("content/a.png", "content/b.png", "content/c.png", "content/d.png", "content/e.png");

            var preview = Analyze(zip, Limits(entries: 4));

            Assert.NotNull(preview.FatalReason);
            Assert.False(preview.CanInstall);
            Assert.Empty(preview.AcceptedFiles);
        }

        [Fact]
        public void DefaultModLimitsMatchThePlan()
        {
            var limits = ModImporter.DefaultLimits();

            Assert.Equal(2L * 1024 * 1024 * 1024, limits.MaxTotalBytes);
            Assert.Equal(20_000, limits.MaxEntries);
            Assert.Equal(256L * 1024 * 1024, limits.MaxEntryBytes);
            Assert.Equal(100, limits.MaxCompressionRatio);
            Assert.True(limits.RejectLinks);
        }

        [Fact]
        public void OversizeFileIsRejectedButOthersImport()
        {
            string zip = MakeZip("big.zip", F("content/ok.png", "small"), new Z("content/big.png", Encoding.UTF8.GetBytes(new string('x', 500))));

            var preview = Analyze(zip, Limits(entryBytes: 100));

            Assert.Equal(new[] { @"content\ok.png" }, Accepted(preview));
            AssertRejected(preview, "content/big.png", "larger");
        }

        [Fact]
        public void BlockedExecutableTypesAreRejected()
        {
            var preview = Analyze(ZipOf("content/ok.png", "content/scripts/run.lua", "content/native/x.dll", "content/a.exe", "content/b.luau"));

            Assert.Equal(new[] { @"content\ok.png" }, Accepted(preview));
            AssertRejected(preview, "content/scripts/run.lua", "blocked");
            AssertRejected(preview, "content/native/x.dll", "blocked");
            AssertRejected(preview, "content/a.exe", "blocked");
            AssertRejected(preview, "content/b.luau", "blocked");
        }

        [Fact]
        public void ClientSettingsIsRejected()
        {
            var preview = Analyze(ZipOf("content/ok.png", "ClientSettings/ClientAppSettings.json"));

            Assert.Equal(new[] { @"content\ok.png" }, Accepted(preview));
            AssertRejected(preview, "ClientSettings/ClientAppSettings.json", "Fast Flags");
        }

        [Fact]
        public void LooseFileAtTheVersionRootIsRejected()
        {
            var preview = Analyze(ZipOf("content/ok.png", "stray.png"));

            Assert.Equal(new[] { @"content\ok.png" }, Accepted(preview));
            AssertRejected(preview, "stray.png", "top level");
        }

        [Fact]
        public void UnknownRootFolderIsRejected()
        {
            var preview = Analyze(ZipOf("content/ok.png", "other/x.png"));

            AssertRejected(preview, "other/x.png", "allowed root");
        }

        [Fact]
        public void PlaceFilesAreOnlyAllowedUnderPlacesOrModels()
        {
            var preview = Analyze(ZipOf(
                "ExtraContent/places/Lobby.rbxl", "ExtraContent/models/Thing.rbxm", "ExtraContent/places/sub/Deep.rbxl",
                "ExtraContent/textures/Bad.rbxl", "content/Bad.rbxm"));

            Assert.Equal(
                new[] { @"ExtraContent\models\Thing.rbxm", @"ExtraContent\places\Lobby.rbxl", @"ExtraContent\places\sub\Deep.rbxl" },
                Accepted(preview));
            AssertRejected(preview, "ExtraContent/textures/Bad.rbxl", "ExtraContent");
            AssertRejected(preview, "content/Bad.rbxm", "ExtraContent");
        }

        [Fact]
        public void JsonIsOnlyAllowedInFontFamilies()
        {
            var preview = Analyze(ZipOf("content/fonts/families/Mine.json", "content/other.json", "shaders/x.json"));

            Assert.Equal(new[] { @"content\fonts\families\Mine.json" }, Accepted(preview));
            AssertRejected(preview, "content/other.json", "families");
            AssertRejected(preview, "shaders/x.json", "families");
        }

        [Fact]
        public void AllowedFileTypesAreAccepted()
        {
            string[] ok =
            {
                "content/a.png", "content/a.JPG", "content/a.jpeg", "content/a.bmp", "content/a.tga", "content/a.dds",
                "content/a.ogg", "content/a.mp3", "content/a.wav", "content/a.flac",
                "content/a.ttf", "content/a.otf", "content/a.ttc", "content/a.ktx", "content/a.mesh"
            };

            var preview = Analyze(ZipOf(ok));

            Assert.Equal(ok.Length, preview.AcceptedFiles.Count);
            Assert.Empty(preview.RejectedEntries);
        }

        [Fact]
        public void UnlistedFileTypesAreRejectedWithAReason()
        {
            var preview = Analyze(ZipOf("content/ok.png", "content/notes.txt", "content/data.bin", "content/noext"));

            AssertRejected(preview, "content/notes.txt", ".txt");
            AssertRejected(preview, "content/data.bin", ".bin");
            AssertRejected(preview, "content/noext", "without a file type");
        }

        [Fact]
        public void CaseVariantsOfAFileCollapseToOneAndTheDuplicateIsRejected()
        {
            var preview = Analyze(ZipOf("content/a.png", "CONTENT/A.png"));

            Assert.Single(preview.AcceptedFiles);
            Assert.Single(preview.RejectedEntries);
        }

        [Fact]
        public void AFileAndAFolderWithTheSameNameCannotBothBeImported()
        {
            var preview = Analyze(ZipOf("content/textures/a.png", "content/textures/a.png/b.png"));

            Assert.Equal(new[] { @"content\textures\a.png\b.png" }, Accepted(preview));
            Assert.Single(preview.RejectedEntries);
        }

        // ---------------------------------------------------------------- layouts

        [Fact]
        public void DirectRootsLayout()
        {
            var preview = Analyze(ZipOf("content/textures/a.png", "ExtraContent/places/p.rbxl", "PlatformContent/pc/textures/b.png", "shaders/c.dds"));

            Assert.Equal(ImportLayout.DirectRoots, preview.Layout);
            Assert.Null(preview.WrapperFolder);
            Assert.Null(preview.FatalReason);
            Assert.Equal(4, preview.AcceptedFiles.Count);
        }

        [Fact]
        public void RootCasingIsNormalised()
        {
            var preview = Analyze(ZipOf("CONTENT/Textures/A.png", "extracontent/places/p.rbxl"));

            Assert.Equal(new[] { @"content\Textures\A.png", @"ExtraContent\places\p.rbxl" }, Accepted(preview));
        }

        [Fact]
        public void SingleWrapperFolderIsStripped()
        {
            var preview = Analyze(ZipOf("My Cool Mod/content/textures/a.png", "My Cool Mod/shaders/b.dds"));

            Assert.Equal(ImportLayout.WrappedRoots, preview.Layout);
            Assert.Equal("My Cool Mod", preview.WrapperFolder);
            Assert.Equal(new[] { @"content\textures\a.png", @"shaders\b.dds" }, Accepted(preview));
        }

        [Fact]
        public void ModificationsFolderLayout()
        {
            var preview = Analyze(ZipOf("Modifications/content/textures/a.png"));

            Assert.Equal(ImportLayout.ModificationsFolder, preview.Layout);
            Assert.Equal(new[] { @"content\textures\a.png" }, Accepted(preview));
        }

        [Fact]
        public void ModificationsFolderInsideAWrapper()
        {
            var preview = Analyze(ZipOf("Pack/Modifications/content/textures/a.png"));

            Assert.Equal(ImportLayout.ModificationsFolder, preview.Layout);
            Assert.Equal("Pack", preview.WrapperFolder);
            Assert.Equal(new[] { @"content\textures\a.png" }, Accepted(preview));
        }

        [Fact]
        public void ZeroStrapPackageLayoutReadsTheManifestAndNeverTrustsItsId()
        {
            string manifest = "{ \"id\": \"../../evil\", \"name\": \"Neon Cursors\", \"author\": \"Zed\", \"description\": \"Bright\", \"category\": \"Cursors\", \"version\": \"1.2\", \"schemaVersion\": 1 }";
            string zip = MakeZip("pkg.zip", F("manifest.json", manifest), F("files/content/textures/Cursors/a.png"), F("thumbnail.png"));

            var preview = Analyze(zip);

            Assert.Equal(ImportLayout.ZeroStrapPackage, preview.Layout);
            Assert.Null(preview.FatalReason);
            Assert.Equal("Neon Cursors", preview.SuggestedName);
            Assert.Equal("Zed", preview.Author);
            Assert.Equal("Bright", preview.Description);
            Assert.Equal("1.2", preview.Version);
            Assert.Equal(ModCategory.Cursors, preview.SuggestedCategory);
            Assert.Equal(new[] { @"content\textures\Cursors\a.png" }, Accepted(preview));
            Assert.Empty(preview.RejectedEntries);

            var installed = _fx.Studio.Import(preview, preview.SuggestedName, preview.SuggestedCategory).Package;

            Assert.Equal("neon-cursors", installed.Id);
            Assert.Equal("Zed", installed.Manifest.Author);
        }

        [Fact]
        public void ZeroStrapPackageInsideAWrapper()
        {
            string zip = MakeZip("pkgw.zip", F("W/manifest.json", "{\"name\":\"Wrapped\"}"), F("W/files/shaders/a.dds"));

            var preview = Analyze(zip);

            Assert.Equal(ImportLayout.ZeroStrapPackage, preview.Layout);
            Assert.Equal("Wrapped", preview.SuggestedName);
            Assert.Equal(new[] { @"shaders\a.dds" }, Accepted(preview));
        }

        [Fact]
        public void BrokenManifestRefusesThePackage()
        {
            var preview = Analyze(MakeZip("badm.zip", F("manifest.json", "{ not json"), F("files/content/a.png")));

            Assert.NotNull(preview.FatalReason);
            Assert.Contains("manifest.json", preview.FatalReason);
            Assert.False(preview.CanInstall);
        }

        [Fact]
        public void ManifestFromANewerSchemaIsRefused()
        {
            var preview = Analyze(MakeZip("newer.zip", F("manifest.json", "{\"name\":\"X\",\"schemaVersion\":9}"), F("files/content/a.png")));

            Assert.NotNull(preview.FatalReason);
            Assert.Contains("newer", preview.FatalReason);
        }

        [Fact]
        public void ManifestFieldsAreValidated()
        {
            string manifest = "{\"name\":\"" + new string('n', 200) + "\",\"category\":\"Bogus\",\"author\":5}";
            var preview = Analyze(MakeZip("fields.zip", F("manifest.json", manifest), F("files/content/textures/a.png")));

            Assert.Null(preview.FatalReason);
            Assert.Equal(80, preview.SuggestedName.Length);
            Assert.Null(preview.Author);
            Assert.Equal(ModCategory.Textures, preview.SuggestedCategory);
            Assert.True(preview.Warnings.Count >= 3);
        }

        [Fact]
        public void ManifestWithoutFilesIsRejectedWithAnExplanation()
        {
            var preview = Analyze(MakeZip("nofiles.zip", F("manifest.json", "{\"name\":\"X\"}"), F("readme.txt")));

            Assert.NotNull(preview.FatalReason);
            Assert.Equal(ImportLayout.Unrecognised, preview.Layout);
        }

        [Theory]
        [InlineData("random/stuff.png")]
        [InlineData("textures/a.png")]
        [InlineData("a/content/x.png", "b/content/y.png")] // two wrappers: no guessing
        [InlineData("outer/inner/content/x.png")]           // two levels of wrapper
        [InlineData("pkg/files/content/x.png")]             // package files without a manifest
        public void UnrecognisedShapesAreRefusedWithAnExplanation(params string[] names)
        {
            var preview = Analyze(ZipOf(names));

            Assert.Equal(ImportLayout.Unrecognised, preview.Layout);
            Assert.False(string.IsNullOrWhiteSpace(preview.FatalReason));
            Assert.Empty(preview.AcceptedFiles);
            Assert.False(preview.CanInstall);
        }

        [Fact]
        public void EmptyArchiveIsRefused()
        {
            var preview = Analyze(MakeZip("empty.zip"));

            Assert.NotNull(preview.FatalReason);
        }

        [Fact]
        public void NotAZipIsRefusedWithAPlainMessage()
        {
            string path = Path.Combine(_work, "fake.zip");
            File.WriteAllText(path, "this is not a zip");

            var preview = Analyze(path);

            Assert.Contains("not a valid zip", preview.FatalReason);
        }

        [Fact]
        public void MissingSourceIsRefused()
        {
            var preview = Analyze(Path.Combine(_work, "nope.zip"));

            Assert.NotNull(preview.FatalReason);
        }

        [Fact]
        public void JunkIsIgnoredNotRejected()
        {
            var preview = Analyze(ZipOf(
                "__MACOSX/._a.png", "__MACOSX/content/._b.png", ".DS_Store", "content/.DS_Store", "Thumbs.db", "content/Thumbs.db",
                "desktop.ini", "README.md", "LICENSE", "license.txt",
                "content/textures/a.png"));

            Assert.Null(preview.FatalReason);
            Assert.Equal(ImportLayout.DirectRoots, preview.Layout);
            Assert.Equal(new[] { @"content\textures\a.png" }, Accepted(preview));
            Assert.Empty(preview.RejectedEntries);
            Assert.Equal(10, preview.IgnoredEntries.Count);
        }

        [Fact]
        public void JunkAroundAWrapperDoesNotBreakWrapperDetection()
        {
            var preview = Analyze(ZipOf("__MACOSX/Mod/._x", ".DS_Store", "README.txt", "Mod/README.md", "Mod/content/a.png"));

            Assert.Equal(ImportLayout.WrappedRoots, preview.Layout);
            Assert.Equal(new[] { @"content\a.png" }, Accepted(preview));
            Assert.Empty(preview.RejectedEntries);
        }

        [Fact]
        public void DirectoryEntriesAreNotReportedAnywhere()
        {
            string zip = MakeZip("dirs.zip", new Z("content/"), new Z("content/textures/"), F("content/textures/a.png"));

            var preview = Analyze(zip);

            Assert.Equal(new[] { @"content\textures\a.png" }, Accepted(preview));
            Assert.Empty(preview.RejectedEntries);
            Assert.Empty(preview.IgnoredEntries);
        }

        [Fact]
        public void SuggestedNameComesFromTheArchiveAndIsTidied()
        {
            string zip = MakeZip("Cool_Sky__pack.zip", F("content/sky/a.png"));

            var preview = Analyze(zip);

            Assert.Equal("Cool Sky pack", preview.SuggestedName);
            Assert.Equal(ModCategory.Sky, preview.SuggestedCategory);
        }

        [Fact]
        public void AnalysingAZipWritesNothingToDisk()
        {
            string zip = ZipOf("content/a.png", "content/b.png");
            var before = Directory.GetFileSystemEntries(_fx.Root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList();

            Analyze(zip);

            var after = Directory.GetFileSystemEntries(_fx.Root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList();
            Assert.Equal(before, after);
            Assert.False(Directory.Exists(Path.Combine(Path.GetTempPath(), "zerostrap-import-plan")));
        }

        // ---------------------------------------------------------------- folders

        private string MakeFolder(string name, params string[] files)
        {
            string root = Path.Combine(_work, name);

            foreach (string f in files)
            {
                string full = Path.Combine(root, f.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "data-" + f);
            }

            Directory.CreateDirectory(root);
            return root;
        }

        [Fact]
        public void FolderImportDetectsLayoutAndAppliesTheSameRules()
        {
            string folder = MakeFolder("My_Folder_Mod", "Wrapper/content/textures/a.png", "Wrapper/content/bad.lua", "Wrapper/stray.png", "Wrapper/README.md");

            var preview = Analyze(folder);

            Assert.Equal(ImportSourceKind.Folder, preview.SourceKind);
            Assert.Equal(ImportLayout.WrappedRoots, preview.Layout);
            Assert.Equal("Wrapper", preview.WrapperFolder);
            Assert.Equal("My Folder Mod", preview.SuggestedName);
            Assert.Equal(new[] { @"content\textures\a.png" }, Accepted(preview));
            AssertRejected(preview, "Wrapper/content/bad.lua", "blocked");
            AssertRejected(preview, "Wrapper/stray.png", "top level");
            Assert.Equal(new[] { "Wrapper/README.md" }, preview.IgnoredEntries);
        }

        [Fact]
        public void FolderOverTheEntryLimitIsRefusedEarly()
        {
            string folder = MakeFolder("Many", "content/a.png", "content/b.png", "content/c.png", "content/d.png");

            var preview = Analyze(folder, Limits(entries: 3));

            Assert.NotNull(preview.FatalReason);
            Assert.False(preview.CanInstall);
        }

        [Fact]
        public void FolderPackageReadsItsManifest()
        {
            string folder = MakeFolder("pkgfolder", "files/content/fonts/a.ttf");
            File.WriteAllText(Path.Combine(folder, "manifest.json"), "{\"name\":\"Fonty\",\"category\":\"fonts\"}");

            var preview = Analyze(folder);

            Assert.Equal(ImportLayout.ZeroStrapPackage, preview.Layout);
            Assert.Equal("Fonty", preview.SuggestedName);
            Assert.Equal(ModCategory.Fonts, preview.SuggestedCategory);
        }

        [Fact]
        public void FolderJunctionsAreRejectedAndNeverFollowed()
        {
            string target = MakeFolder("junction-target", "content/textures/secret.png");
            string folder = MakeFolder("with-junction", "content/textures/ok.png");
            string link = Path.Combine(folder, "content", "linked");

            if (!TryCreateJunction(link, target))
                return; // junctions could not be created in this environment

            var preview = Analyze(folder);

            Assert.Equal(new[] { @"content\textures\ok.png" }, Accepted(preview));
            var rejection = Assert.Single(preview.RejectedEntries);
            Assert.Equal("content/linked", rejection.Path);
            Assert.Contains("link", rejection.Reason, StringComparison.OrdinalIgnoreCase);

            // deleting the junction must not touch the target
            Directory.Delete(link);
            Assert.True(File.Exists(Path.Combine(target, "content", "textures", "secret.png")));
        }

        [Fact]
        public void FolderSymbolicLinkFilesAreRejectedWhenTheyCanBeCreated()
        {
            string real = Path.Combine(_work, "real.png");
            File.WriteAllText(real, "real");
            string folder = MakeFolder("with-symlink", "content/ok.png");
            string link = Path.Combine(folder, "content", "link.png");

            try { File.CreateSymbolicLink(link, real); }
            catch (Exception) { return; } // needs privileges on Windows

            var preview = Analyze(folder);

            Assert.Equal(new[] { @"content\ok.png" }, Accepted(preview));
            AssertRejected(preview, "content/link.png", "link");
        }

        private static bool TryCreateJunction(string link, string target)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var p = Process.Start(psi)!;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- install

        private string StagingDir => Path.Combine(_fx.StudioDir, "Staging");

        private void AssertNoStagingLeft() =>
            Assert.True(!Directory.Exists(StagingDir) || !Directory.EnumerateFileSystemEntries(StagingDir).Any());

        [Fact]
        public void InstallCreatesAnEnabledPackageAndWritesTheOverlay()
        {
            string zip = MakeZip("install.zip", F("Mod/content/textures/Cursors/KeyboardMouse/ArrowCursor.png", "NEW"), F("Mod/shaders/s.dds", "SH"));
            var preview = _fx.Studio.AnalyzeImport(zip);

            var result = _fx.Studio.Import(preview, "My Mod", ModCategory.Cursors, "Me", "Desc", "2.0");

            var package = result.Package;
            Assert.Equal("my-mod", package.Id);
            Assert.Equal(ModSource.Imported, package.Manifest.Source);
            Assert.Equal(ModCategory.Cursors, package.Manifest.Category);
            Assert.Equal("Me", package.Manifest.Author);
            Assert.Equal("version-test", package.Manifest.RobloxVersionAtInstall);
            Assert.Equal(2, package.Files.Count);

            var status = Assert.Single(_fx.Studio.GetState().Packages);
            Assert.True(status.Enabled);

            Assert.Equal("NEW", _fx.ReadMod(@"content\textures\Cursors\KeyboardMouse\ArrowCursor.png"));
            Assert.Equal("SH", _fx.ReadMod(@"shaders\s.dds"));
            Assert.True(result.Rebuild.Result.Copied >= 2);
            AssertNoStagingLeft();
            Assert.True(File.Exists(zip), "the source archive must be left alone");
        }

        [Fact]
        public void InstallFromAFolderWorksAndLeavesTheFolderAlone()
        {
            string folder = MakeFolder("FolderMod", "content/textures/a.png", "content/bad.lua");
            var preview = _fx.Studio.AnalyzeImport(folder);

            var result = _fx.Studio.Import(preview, "Folder Mod", ModCategory.Textures);

            Assert.Equal(new[] { @"content\textures\a.png" }, result.Package.Files.Select(f => f.RelativePath).ToArray());
            Assert.Equal("data-content/textures/a.png", _fx.ReadMod(@"content\textures\a.png"));
            Assert.True(File.Exists(Path.Combine(folder, "content", "bad.lua")));
            Assert.True(File.Exists(Path.Combine(folder, "content", "textures", "a.png")));
            AssertNoStagingLeft();
        }

        [Fact]
        public void ImportedPackageGetsTheLowestPriority()
        {
            _fx.MakePackage("First", new Dictionary<string, string> { [@"content\textures\one.png"] = "1" });
            _fx.MakePackage("Second", new Dictionary<string, string> { [@"content\textures\two.png"] = "2" });

            var preview = _fx.Studio.AnalyzeImport(ZipOf("content/textures/three.png"));
            var id = _fx.Studio.Import(preview, "Third", ModCategory.Textures).Package.Id;

            var order = _fx.Studio.GetState().Packages.Select(p => p.Package.Id).ToList();
            Assert.Equal(id, order.Last());
            Assert.Equal(3, order.Count);
        }

        [Fact]
        public void ANewConflictNeverStealsAFileFromItsCurrentOwner()
        {
            const string shared = @"content\textures\shared.png";
            _fx.MakePackage("Owner", new Dictionary<string, string> { [shared] = "OWNER", [@"content\textures\own.png"] = "x" });
            _fx.Studio.Rebuild();
            Assert.Equal("OWNER", _fx.ReadMod(shared));

            string zip = MakeZip("conflict.zip", F("content/textures/shared.png", "INTRUDER"), F("content/textures/fresh.png", "FRESH"));
            var preview = _fx.Studio.AnalyzeImport(zip);

            var conflict = Assert.Single(preview.Conflicts);
            Assert.Equal(shared, conflict.Path, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(new[] { "Owner" }, conflict.PackageNames);

            _fx.Studio.Import(preview, "Intruder", ModCategory.Textures);

            Assert.Equal("OWNER", _fx.ReadMod(shared));
            Assert.Equal("FRESH", _fx.ReadMod(@"content\textures\fresh.png"));

            var state = _fx.Studio.GetState();
            Assert.Contains(state.Conflicts, c => c.Path.Equals(shared, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void AnalyzeImportReportsNoConflictsWithDisabledPackages()
        {
            _fx.MakePackage("Off", new Dictionary<string, string> { [@"content\textures\a.png"] = "x" }, enable: false);

            var preview = _fx.Studio.AnalyzeImport(ZipOf("content/textures/a.png"));

            Assert.Empty(preview.Conflicts);
        }

        [Fact]
        public void InstallRefusesAFatalPreview()
        {
            var preview = _fx.Studio.AnalyzeImport(ZipOf("random/x.png"));

            var ex = Assert.Throws<ModStudioException>(() => _fx.Studio.Import(preview, "X", ModCategory.Other));

            Assert.Equal(preview.FatalReason, ex.Message);
            Assert.Empty(_fx.Studio.Packages.List());
        }

        [Fact]
        public void InstallRefusesAPreviewWithNoAcceptedFiles()
        {
            var preview = _fx.Studio.AnalyzeImport(ZipOf("content/evil.lua"));

            Assert.Null(preview.FatalReason);
            Assert.False(preview.CanInstall);
            Assert.Throws<ModStudioException>(() => _fx.Studio.Import(preview, "X", ModCategory.Other));
            Assert.Empty(_fx.Studio.Packages.List());
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void InstallRequiresAName(string name)
        {
            var preview = _fx.Studio.AnalyzeImport(ZipOf("content/a.png"));

            var ex = Assert.Throws<ModStudioException>(() => _fx.Studio.Import(preview, name, ModCategory.Other));

            Assert.Contains("name", ex.Message);
        }

        [Fact]
        public void InstallCleansUpWhenTheSourceDisappears()
        {
            string zip = ZipOf("content/a.png");
            var preview = _fx.Studio.AnalyzeImport(zip);
            File.Delete(zip);

            Assert.Throws<ModStudioException>(() => _fx.Studio.Import(preview, "Gone", ModCategory.Other));

            Assert.Empty(_fx.Studio.Packages.List());
            AssertNoStagingLeft();
        }

        [Fact]
        public void InstallDetectsASourceThatChangedAfterAnalysis()
        {
            string zip = MakeZip("changing.zip", F("content/a.png"));
            var preview = _fx.Studio.AnalyzeImport(zip);

            MakeZip("changing.zip", F("content/a.png"), F("content/b.png"));

            Assert.Throws<ModStudioException>(() => _fx.Studio.Import(preview, "Changed", ModCategory.Other));
            Assert.Empty(_fx.Studio.Packages.List());
            AssertNoStagingLeft();
        }

        [Fact]
        public void ImportingTheSameModTwiceCreatesTwoPackages()
        {
            string zip = ZipOf("content/a.png");

            var first = _fx.Studio.Import(_fx.Studio.AnalyzeImport(zip), "Same", ModCategory.Other).Package;
            var second = _fx.Studio.Import(_fx.Studio.AnalyzeImport(zip), "Same", ModCategory.Other).Package;

            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(2, _fx.Studio.Packages.List().Count);
        }
    }
}
