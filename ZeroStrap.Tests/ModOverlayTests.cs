using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Xunit;

using ZeroStrap.Mods;

namespace ZeroStrap.Tests
{
    public sealed class ModOverlayTests : IDisposable
    {
        private const string Jump = @"content\sounds\action_jump.mp3";
        private const string Swim = @"content\sounds\action_swim.mp3";
        private const string Sky = @"content\sky\sky512_bk.tex";

        private readonly ModStudioFixture _fx = new();

        public void Dispose() => _fx.Dispose();

        private static string Stamp(string s) => s;

        [Fact]
        public void EnabledPackageIsCopiedAndIndexed()
        {
            var pkg = _fx.MakePackage("Sounds", new Dictionary<string, string> { [Jump] = "jump-A", [Swim] = "swim-A" });

            var rebuilt = _fx.Studio.Rebuild();

            Assert.Equal(2, rebuilt.Result.Copied);
            Assert.Equal("jump-A", _fx.ReadMod(Jump));
            Assert.Equal("swim-A", _fx.ReadMod(Swim));

            var index = _fx.Studio.IndexStore.Load();
            Assert.Equal(pkg.Id, index.Files[Jump].Owner);
            Assert.Equal(ModStudioFixture.Sha(Encoding.UTF8.GetBytes("jump-A")), index.Files[Jump].Sha256);
        }

        [Fact]
        public void SecondRebuildIsANoOp()
        {
            _fx.MakePackage("Sounds", new Dictionary<string, string> { [Jump] = "jump-A" });
            _fx.Studio.Rebuild();

            var again = _fx.Studio.Rebuild();

            Assert.True(again.Plan.IsNoOp);
            Assert.Equal(0, again.Result.Copied + again.Result.Deleted + again.Result.Displaced);
        }

        [Fact]
        public void DisablingDeletesOwnedFilesAndPrunesEmptyFolders()
        {
            var pkg = _fx.MakePackage("Sky", new Dictionary<string, string> { [Sky] = "sky" });
            _fx.WriteMod(@"ClientSettings\ClientAppSettings.json", "{\"flag\":1}");
            _fx.Studio.Rebuild();
            Assert.True(_fx.ModExists(Sky));

            _fx.Studio.SetEnabled(pkg.Id, false);
            var rebuilt = _fx.Studio.Rebuild();

            Assert.Equal(1, rebuilt.Result.Deleted);
            Assert.False(_fx.ModExists(Sky));
            Assert.False(Directory.Exists(_fx.ModPath("content")));
            Assert.True(Directory.Exists(_fx.Modifications));
            Assert.Equal("{\"flag\":1}", _fx.ReadMod(@"ClientSettings\ClientAppSettings.json"));
            Assert.Empty(_fx.Studio.IndexStore.Load().Files);
        }

        [Fact]
        public void ReEnablingRestoresTheFiles()
        {
            var pkg = _fx.MakePackage("Sky", new Dictionary<string, string> { [Sky] = "sky" });
            _fx.Studio.Rebuild();
            _fx.Studio.SetEnabled(pkg.Id, false);
            _fx.Studio.Rebuild();

            _fx.Studio.SetEnabled(pkg.Id, true);
            _fx.Studio.Rebuild();

            Assert.Equal("sky", _fx.ReadMod(Sky));
        }

        [Fact]
        public void HandEditedFileIsDisplacedNotLostWhenDisabling()
        {
            var pkg = _fx.MakePackage("Sounds", new Dictionary<string, string> { [Jump] = "jump-A" });
            _fx.Studio.Rebuild();
            _fx.WriteMod(Jump, "my own edit");

            _fx.Studio.SetEnabled(pkg.Id, false);
            var rebuilt = _fx.Studio.Rebuild();

            Assert.Equal(1, rebuilt.Result.Displaced);
            Assert.False(_fx.ModExists(Jump));

            string displacedRoot = Path.Combine(_fx.StudioDir, "Backups", "displaced");
            var saved = Directory.EnumerateFiles(displacedRoot, "action_jump.mp3", SearchOption.AllDirectories).Single();
            Assert.Equal("my own edit", File.ReadAllText(saved));
            Assert.EndsWith(Jump, saved);
        }

        [Fact]
        public void HandEditedFileIsDisplacedBeforeBeingReplaced()
        {
            _fx.MakePackage("Sounds", new Dictionary<string, string> { [Jump] = "jump-A" });
            _fx.Studio.Rebuild();
            _fx.WriteMod(Jump, "my own edit");

            var rebuilt = _fx.Studio.Rebuild();

            Assert.Equal(1, rebuilt.Result.Displaced);
            Assert.Equal("jump-A", _fx.ReadMod(Jump));

            string displacedRoot = Path.Combine(_fx.StudioDir, "Backups", "displaced");
            var saved = Directory.EnumerateFiles(displacedRoot, "action_jump.mp3", SearchOption.AllDirectories).Single();
            Assert.Equal("my own edit", File.ReadAllText(saved));
        }

        [Fact]
        public void EditedFileThatAlreadyMatchesThePackageIsKept()
        {
            _fx.MakePackage("Sounds", new Dictionary<string, string> { [Jump] = "jump-A" });
            _fx.Studio.Rebuild();
            _fx.WriteMod(Jump, "temp");
            _fx.WriteMod(Jump, "jump-A");

            var rebuilt = _fx.Studio.Rebuild();

            Assert.Equal(0, rebuilt.Result.Displaced);
            Assert.True(rebuilt.Plan.IsNoOp);
        }

        [Fact]
        public void DeletedManagedFileIsRestored()
        {
            _fx.MakePackage("Sounds", new Dictionary<string, string> { [Jump] = "jump-A" });
            _fx.Studio.Rebuild();
            File.Delete(_fx.ModPath(Jump));

            var rebuilt = _fx.Studio.Rebuild();

            Assert.Equal(1, rebuilt.Result.Copied);
            Assert.Equal("jump-A", _fx.ReadMod(Jump));
        }

        [Fact]
        public void ReadOnlyManagedFileCanBeReplacedAndDeleted()
        {
            var pkg = _fx.MakePackage("Sounds", new Dictionary<string, string> { [Jump] = "jump-A" });
            _fx.Studio.Rebuild();
            File.SetAttributes(_fx.ModPath(Jump), FileAttributes.ReadOnly);

            _fx.Studio.SetEnabled(pkg.Id, false);
            _fx.Studio.Rebuild();

            Assert.False(_fx.ModExists(Jump));
        }

        [Fact]
        public void UnmanagedExistingFileIsNeverOverwritten()
        {
            _fx.WriteMod(Jump, "the user's own file");
            _fx.MakePackage("Sounds", new Dictionary<string, string> { [Jump] = "jump-A", [Swim] = "swim-A" });

            var rebuilt = _fx.Studio.Rebuild();

            Assert.Equal("the user's own file", _fx.ReadMod(Jump));
            Assert.Equal("swim-A", _fx.ReadMod(Swim));

            var blocked = Assert.Single(rebuilt.Result.Blocked);
            Assert.Equal(Jump, blocked.Path);
            Assert.Equal(OverlayBlockReason.UnmanagedFileExists, blocked.Reason);

            var index = _fx.Studio.IndexStore.Load();
            Assert.False(index.Files.ContainsKey(Jump));
            Assert.True(index.Files.ContainsKey(Swim));

            // and it stays that way on later runs, also when the package is disabled
            _fx.Studio.Rebuild();
            Assert.Equal("the user's own file", _fx.ReadMod(Jump));
        }

        [Fact]
        public void ClientSettingsIsNeverTouchedEvenWithATamperedIndex()
        {
            _fx.WriteMod(@"ClientSettings\ClientAppSettings.json", "{\"a\":1}");
            string sha = ModStudioFixture.Sha(Encoding.UTF8.GetBytes("{\"a\":1}"));

            _fx.Studio.IndexStore.Save(new OverlayIndex
            {
                Files =
                {
                    [@"ClientSettings\ClientAppSettings.json"] = new OverlayIndexEntry { Owner = "x", Sha256 = sha },
                    [@"..\evil.txt"] = new OverlayIndexEntry { Owner = "x", Sha256 = sha },
                    [@"content\..\..\evil2.txt"] = new OverlayIndexEntry { Owner = "x", Sha256 = sha }
                }
            });
            File.WriteAllText(Path.Combine(_fx.Root, "evil.txt"), "outside");

            _fx.Studio.Rebuild();

            Assert.Equal("{\"a\":1}", _fx.ReadMod(@"ClientSettings\ClientAppSettings.json"));
            Assert.Equal("outside", File.ReadAllText(Path.Combine(_fx.Root, "evil.txt")));
        }

        [Fact]
        public void BuildPlanIgnoresReservedAndEscapingIndexEntries()
        {
            string sha = "aa";
            var index = new OverlayIndex
            {
                Files =
                {
                    [@"ClientSettings\ClientAppSettings.json"] = new OverlayIndexEntry { Owner = "x", Sha256 = sha },
                    [@"..\evil.txt"] = new OverlayIndexEntry { Owner = "x", Sha256 = sha },
                    [@"README.txt"] = new OverlayIndexEntry { Owner = "x", Sha256 = sha },
                    [Jump] = new OverlayIndexEntry { Owner = "x", Sha256 = sha }
                }
            };

            var plan = ModOverlayBuilder.BuildPlan(
                new Dictionary<string, string>(), (_, _) => null, (_, _) => "", _ => false,
                index, _ => sha, _ => true, 0);

            var action = Assert.Single(plan.Actions);
            Assert.Equal(Jump, action.Path);
            Assert.Equal(OverlayActionKind.Delete, action.Kind);
            Assert.DoesNotContain(plan.NewIndex.Files.Keys, k => k.Contains("ClientSettings") || k.Contains("evil"));
        }

        [Fact]
        public void RemovingOnePackageLeavesOtherPackagesFilesByteIdentical()
        {
            var a = _fx.MakePackage("A", new Dictionary<string, string> { [Jump] = "jump-A", [Sky] = "sky-A" });
            var b = _fx.MakePackage("B", new Dictionary<string, string> { [Swim] = "swim-B", [@"content\sounds\other.mp3"] = "other-B" });
            _fx.WriteMod(@"ClientSettings\ClientAppSettings.json", "{}");
            _fx.WriteMod(@"content\fonts\unmanaged.ttf", "unmanaged");
            _fx.Studio.Rebuild();

            var before = _fx.Snapshot(_fx.Modifications);

            _fx.Studio.RemovePackage(a.Id);

            var after = _fx.Snapshot(_fx.Modifications);
            Assert.Null(_fx.Studio.Packages.Get(a.Id));
            Assert.False(after.ContainsKey(Jump));
            Assert.False(after.ContainsKey(Sky));

            foreach (string path in new[] { Swim, @"content\sounds\other.mp3", @"ClientSettings\ClientAppSettings.json", @"content\fonts\unmanaged.ttf" })
                Assert.Equal(before[path], after[path]);

            Assert.Equal(4, after.Count);
            Assert.NotNull(_fx.Studio.Packages.Get(b.Id));
        }

        [Fact]
        public void ReorderingPrioritiesMovesTheFile()
        {
            var a = _fx.MakePackage("A", new Dictionary<string, string> { [Jump] = "jump-A" });
            _fx.Studio.Rebuild();

            var b = _fx.MakePackage("B", new Dictionary<string, string> { [Jump] = "jump-B" });
            _fx.Studio.Rebuild();
            Assert.Equal("jump-A", _fx.ReadMod(Jump)); // b was enabled later: lowest priority

            _fx.Studio.SetPriority(new[] { b.Id, a.Id });
            _fx.Studio.Rebuild(); // the user reordered: priority wins
            Assert.Equal("jump-B", _fx.ReadMod(Jump));

            var c = _fx.MakePackage("C", new Dictionary<string, string> { [Jump] = "jump-C" });
            _fx.Studio.SetPriority(new[] { c.Id, b.Id, a.Id });
            _fx.Studio.Rebuild();
            Assert.Equal("jump-C", _fx.ReadMod(Jump));
        }

        [Fact]
        public void NewlyEnabledPackageWithHigherPriorityDoesNotStealThePath()
        {
            var a = _fx.MakePackage("A", new Dictionary<string, string> { [Jump] = "jump-A" });
            var b = _fx.MakePackage("B", new Dictionary<string, string> { [Jump] = "jump-B" }, enable: false);
            _fx.Studio.SetPriority(new[] { b.Id, a.Id });
            _fx.Studio.Rebuild();
            Assert.Equal("jump-A", _fx.ReadMod(Jump));

            _fx.Studio.SetEnabled(b.Id, true);
            var rebuilt = _fx.Studio.Rebuild();

            Assert.Equal("jump-A", _fx.ReadMod(Jump));
            var conflict = Assert.Single(rebuilt.Resolution.Conflicts);
            Assert.Equal(ConflictStatus.Unresolved, conflict.Status);
            Assert.Equal(a.Id, conflict.CurrentOwner);
            Assert.Equal(0, rebuilt.Result.Copied + rebuilt.Result.Displaced + rebuilt.Result.Deleted);

            // it stays unresolved on later runs
            _fx.Studio.Rebuild();
            Assert.Equal("jump-A", _fx.ReadMod(Jump));

            // until the user chooses
            _fx.Studio.ChooseOwner(Jump, b.Id);
            _fx.Studio.Rebuild();
            Assert.Equal("jump-B", _fx.ReadMod(Jump));
        }

        [Fact]
        public void ChoosingAnOwnerAppliesThatPackage()
        {
            var a = _fx.MakePackage("A", new Dictionary<string, string> { [Jump] = "jump-A" });
            _fx.Studio.Rebuild();
            var b = _fx.MakePackage("B", new Dictionary<string, string> { [Jump] = "jump-B" });
            _fx.Studio.Rebuild();
            Assert.Equal("jump-A", _fx.ReadMod(Jump)); // b enabled later, lowest priority

            var resolution = _fx.Studio.ChooseOwner(Jump, b.Id);
            Assert.Equal(b.Id, resolution.Owners[Jump]);
            _fx.Studio.Rebuild();

            Assert.Equal("jump-B", _fx.ReadMod(Jump));
            Assert.Equal(b.Id, _fx.Studio.IndexStore.Load().Files[Jump].Owner);

            // clearing the choice keeps B (existing owner is sticky)
            _fx.Studio.ChooseOwner(Jump, null);
            _fx.Studio.Rebuild();
            Assert.Equal("jump-B", _fx.ReadMod(Jump));

            Assert.Throws<ModStudioException>(() => _fx.Studio.ChooseOwner(Swim, a.Id));
        }

        [Fact]
        public void DisablingTheWinnerHandsTheFileToTheNextPackage()
        {
            var a = _fx.MakePackage("A", new Dictionary<string, string> { [Jump] = "jump-A" });
            var b = _fx.MakePackage("B", new Dictionary<string, string> { [Jump] = "jump-B" });
            _fx.Studio.Rebuild();
            Assert.Equal("jump-A", _fx.ReadMod(Jump));

            _fx.Studio.SetEnabled(a.Id, false);
            var rebuilt = _fx.Studio.Rebuild();

            Assert.Equal("jump-B", _fx.ReadMod(Jump));
            Assert.Equal(0, rebuilt.Result.Displaced);
            Assert.Equal(b.Id, _fx.Studio.IndexStore.Load().Files[Jump].Owner);
        }

        [Fact]
        public void ActivePresetFileIsNotOverwrittenUntilPresetIsSwitchedOff()
        {
            const string cursorPath = ModStudioFixture.CursorPath;
            _fx.WriteMod(cursorPath, ModStudioFixture.CursorBytes);

            var pkg = _fx.MakePackage("Cursor pack", new Dictionary<string, string> { [cursorPath] = "pack cursor" });

            var first = _fx.Studio.Rebuild();
            Assert.Equal(ModStudioFixture.CursorBytes, File.ReadAllBytes(_fx.ModPath(cursorPath)));
            Assert.Equal("builtin:cursors", first.Resolution.Owners[cursorPath]);
            Assert.Equal(ConflictStatus.Unresolved, Assert.Single(first.Resolution.Conflicts).Status);

            var resolution = _fx.Studio.ChooseOwner(cursorPath, pkg.Id);
            var action = Assert.Single(resolution.Actions);
            Assert.Equal("builtin:cursors", action.PresetId);

            // without permission to switch the preset off, nothing changes
            _fx.Studio.Rebuild();
            Assert.Equal(ModStudioFixture.CursorBytes, File.ReadAllBytes(_fx.ModPath(cursorPath)));

            var done = _fx.Studio.Rebuild(allowPresetDisable: true);
            Assert.Equal("pack cursor", _fx.ReadMod(cursorPath));
            Assert.Equal(1, done.Result.Copied);
            Assert.Empty(_fx.Catalog.GetActive());
        }

        [Fact]
        public void IndexWriteIsAtomicWhenTheSwapFails()
        {
            var store = _fx.Studio.IndexStore;
            var good = new OverlayIndex { Files = { [Jump] = new OverlayIndexEntry { Owner = "a", Sha256 = "aa" } } };
            store.Save(good);
            string before = File.ReadAllText(store.FilePath);

            store.FaultBeforeCommit = () => throw new IOException("simulated crash");
            var next = new OverlayIndex { Files = { [Swim] = new OverlayIndexEntry { Owner = "b", Sha256 = "bb" } } };

            Assert.Throws<IOException>(() => store.Save(next));
            store.FaultBeforeCommit = null;

            Assert.Equal(before, File.ReadAllText(store.FilePath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp-*"));
            Assert.True(store.Load().Files.ContainsKey(Jump));
        }

        [Fact]
        public void InterruptedBuildNeverLeavesAnUnownedFile()
        {
            var pkg = _fx.MakePackage("Sounds", new Dictionary<string, string> { [Jump] = "jump-A", [Swim] = "swim-A" });

            // the journal write (first Save) succeeds, the final one fails
            int saves = 0;
            _fx.Studio.IndexStore.FaultBeforeCommit = () =>
            {
                if (++saves == 2)
                    throw new IOException("simulated crash");
            };

            Assert.Throws<IOException>(() => _fx.Studio.Rebuild());
            _fx.Studio.IndexStore.FaultBeforeCommit = null;

            Assert.True(_fx.ModExists(Jump));
            var index = _fx.Studio.IndexStore.Load();
            Assert.Equal(pkg.Id, index.Files[Jump].Owner);
            Assert.Equal(pkg.Id, index.Files[Swim].Owner);

            var recovered = _fx.Studio.Rebuild();
            Assert.Empty(recovered.Result.Blocked);
            Assert.Equal(0, recovered.Result.Displaced);
            Assert.True(recovered.Plan.IsNoOp);
        }

        [Fact]
        public void CorruptIndexIsQuarantinedAndNothingIsDeleted()
        {
            var pkg = _fx.MakePackage("Sounds", new Dictionary<string, string> { [Jump] = "jump-A" });
            _fx.Studio.Rebuild();
            File.WriteAllText(_fx.Studio.IndexStore.FilePath, "{ not json");

            var rebuilt = _fx.Studio.Rebuild();

            Assert.Equal("jump-A", _fx.ReadMod(Jump));
            Assert.Single(rebuilt.Result.Blocked); // file is now unmanaged as far as the studio knows
            Assert.Single(Directory.GetFiles(_fx.StudioDir, "overlay-index.json.corrupt-*"));
        }

        [Fact]
        public void CorruptPackageIsSkippedNotThrown()
        {
            _fx.MakePackage("Good", new Dictionary<string, string> { [Jump] = "jump-A" });
            string bad = Path.Combine(_fx.StudioDir, "Packages", "bad");
            Directory.CreateDirectory(bad);
            File.WriteAllText(Path.Combine(bad, "manifest.json"), "{ nope");
            Directory.CreateDirectory(Path.Combine(_fx.StudioDir, "Packages", "mismatch"));
            File.WriteAllText(Path.Combine(_fx.StudioDir, "Packages", "mismatch", "manifest.json"), "{\"Id\":\"other\",\"Name\":\"x\"}");

            var list = _fx.Studio.Packages.List();

            Assert.Single(list);
            Assert.Contains(_fx.Logs, l => l.Contains("bad"));
        }

        [Fact]
        public void PackageStoreRejectsUnsafeStagingFilesAndCreatesNothing()
        {
            string staging = Path.Combine(_fx.Root, "staging");
            Directory.CreateDirectory(Path.Combine(staging, "content", "scripts"));
            File.WriteAllText(Path.Combine(staging, "content", "scripts", "x.lua"), "x");

            Assert.Throws<ModStudioException>(() => _fx.Studio.Packages.CreateFromStaging(new ModManifest { Name = "Bad" }, staging));
            Assert.Empty(_fx.Studio.Packages.List());
            Assert.True(File.Exists(Path.Combine(staging, "content", "scripts", "x.lua")));
        }

        [Fact]
        public void PackageStoreMovesFilesInAndMakesIdsUnique()
        {
            string staging = Path.Combine(_fx.Root, "stage-move");
            Directory.CreateDirectory(Path.Combine(staging, "content", "sounds"));
            File.WriteAllText(Path.Combine(staging, "content", "sounds", "a.mp3"), "a");

            var first = _fx.Studio.Packages.CreateFromStaging(new ModManifest { Name = "Same Name" }, staging, moveFiles: true);
            Assert.False(File.Exists(Path.Combine(staging, "content", "sounds", "a.mp3")));
            Assert.Equal(ModCategory.Sounds, first.Manifest.Category);

            Directory.CreateDirectory(Path.Combine(staging, "content", "sounds"));
            File.WriteAllText(Path.Combine(staging, "content", "sounds", "a.mp3"), "a");
            var second = _fx.Studio.Packages.CreateFromStaging(new ModManifest { Name = "Same Name" }, staging, moveFiles: false);

            Assert.Equal("same-name", first.Id);
            Assert.Equal("same-name-2", second.Id);
            Assert.True(File.Exists(Path.Combine(staging, "content", "sounds", "a.mp3")));
        }

        [Fact]
        public void PackageHashesAreCachedAndFollowChanges()
        {
            var pkg = _fx.MakePackage("Sounds", new Dictionary<string, string> { [Jump] = "jump-A" });

            string h1 = _fx.Studio.Packages.GetSha256(pkg, Jump);
            Assert.Equal(ModStudioFixture.Sha(Encoding.UTF8.GetBytes("jump-A")), h1);
            Assert.Equal(h1, _fx.Studio.Packages.GetSha256(pkg, Jump));

            File.WriteAllText(pkg.FullPathOf(Jump), "jump-BBB");
            Assert.NotEqual(h1, _fx.Studio.Packages.GetSha256(pkg, Jump));
        }

        [Fact]
        public void CompatibilityCountsPathsPresentInTheVersionFolder()
        {
            var pkg = _fx.MakePackage("Sounds", new Dictionary<string, string> { [Jump] = "j", [Swim] = "s", [Sky] = "k" });

            Assert.Null(_fx.Studio.CompatibilityFor(pkg)); // no version folder yet

            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_fx.VersionDir, Jump))!);
            File.WriteAllText(Path.Combine(_fx.VersionDir, Jump), "orig");

            Assert.Equal(1, _fx.Studio.CompatibilityFor(pkg));
        }

        [Fact]
        public void SnapshotListsPackagesWithPriorityAndEnabledState()
        {
            var a = _fx.MakePackage("A", new Dictionary<string, string> { [Jump] = "a" });
            var b = _fx.MakePackage("B", new Dictionary<string, string> { [Swim] = "b" }, enable: false);
            _fx.Studio.SetPriority(new[] { b.Id, a.Id });

            var snapshot = _fx.Studio.GetState();

            Assert.Equal(new[] { b.Id, a.Id }, snapshot.Packages.Select(p => p.Package.Id).ToArray());
            Assert.False(snapshot.Packages[0].Enabled);
            Assert.True(snapshot.Packages[1].Enabled);
        }

        [Fact]
        public void EnsureOverlayRunsMigrationThenBuilds()
        {
            _fx.WriteMod(Jump, "loose");
            _fx.Studio.EnsureOverlay();

            Assert.True(_fx.Studio.LoadState().Migrated);
            Assert.Equal("loose", _fx.ReadMod(Jump));

            _fx.Studio.EnsureOverlay(); // idempotent
            Assert.Equal("loose", _fx.ReadMod(Jump));
        }
    }
}
