using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Xunit;

using ZeroStrap.Mods;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.Tests
{
    public class ModsStudioLogicTests
    {
        private const string P1 = @"content\textures\zs-test\a.png";
        private const string P2 = @"content\textures\zs-test\b.png";

        private static string Label(string id) => id switch
        {
            "p1" => "Alpha",
            "p2" => "Beta",
            "builtin:cursors" => "Classic cursors",
            _ => id
        };

        private static ModConflict Conflict(string path, string owner, ConflictStatus status, params string[] candidates) => new()
        {
            Path = path,
            Candidates = candidates,
            CurrentOwner = owner,
            Status = status
        };

        // ---------------------------------------------------------------- status pill

        [Theory]
        [InlineData(false, false, ModStatus.Off)]
        [InlineData(false, true, ModStatus.Off)]
        [InlineData(true, false, ModStatus.On)]
        [InlineData(true, true, ModStatus.Conflict)]
        public void StatusPillFollowsEnabledAndUnresolved(bool enabled, bool unresolved, ModStatus expected) =>
            Assert.Equal(expected, ModsStudioLogic.StatusFor(enabled, unresolved));

        [Fact]
        public void StatusTextMatchesTheDesign()
        {
            Assert.Equal("On", ModsStudioLogic.StatusText(ModStatus.On));
            Assert.Equal("Off", ModsStudioLogic.StatusText(ModStatus.Off));
            Assert.Equal("Conflict", ModsStudioLogic.StatusText(ModStatus.Conflict));
        }

        [Fact]
        public void OnlyUnresolvedConflictsMarkAPackage()
        {
            var conflicts = new[]
            {
                Conflict(P1, "p1", ConflictStatus.Resolved, "p1", "p2"),
                Conflict(P2, "builtin:cursors", ConflictStatus.Unresolved, "builtin:cursors", "p2")
            };

            var marked = ModsStudioLogic.PackagesWithUnresolvedConflicts(conflicts);

            Assert.Equal(new[] { "p2" }, marked.ToArray());
            Assert.Equal(1, ModsStudioLogic.UnresolvedCount(conflicts));
        }

        [Fact]
        public void ContestedBuiltinsListsPresetsInAnyConflict()
        {
            var conflicts = new[]
            {
                Conflict(P1, "p1", ConflictStatus.Resolved, "builtin:cursors", "p1"),
                Conflict(P2, "p1", ConflictStatus.Resolved, "p1", "p2")
            };

            Assert.Equal(new[] { "builtin:cursors" }, ModsStudioLogic.ContestedBuiltins(conflicts).ToArray());
        }

        // ---------------------------------------------------------------- texts

        [Fact]
        public void ListMetaSkipsAnEmptyAuthor()
        {
            Assert.Equal("Textures · Ana", ModsStudioLogic.ListMeta(ModCategory.Textures, " Ana "));
            Assert.Equal("Sounds", ModsStudioLogic.ListMeta(ModCategory.Sounds, null));
            Assert.Equal("Sounds", ModsStudioLogic.ListMeta(ModCategory.Sounds, "  "));
        }

        [Fact]
        public void DetailsMetaListsAuthorCategorySourceAndDate()
        {
            var manifest = new ModManifest
            {
                Author = "Ana",
                Category = ModCategory.Fonts,
                Source = ModSource.Legacy,
                InstalledAt = new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero)
            };

            string text = ModsStudioLogic.DetailsMeta(manifest, CultureInfo.InvariantCulture);

            Assert.StartsWith("Ana · Fonts · Legacy · Installed Mar ", text);
            Assert.EndsWith(", 2026", text);
        }

        [Fact]
        public void CompatibilityTextHandlesUnknown()
        {
            Assert.Equal("Unknown until Roblox is installed", ModsStudioLogic.CompatibilityText(null, 4));
            Assert.Equal("3 of 4 files found in the current Roblox build", ModsStudioLogic.CompatibilityText(3, 4));
            Assert.Equal("Files (4)", ModsStudioLogic.FilesHeader(4));
            Assert.Equal("Conflicts (2)", ModsStudioLogic.ConflictsLinkText(2));
        }

        [Theory]
        [InlineData(0, 0, 0, "Saved. Roblox picks this up on its next launch.")]
        [InlineData(1, 0, 0, "Updated 1 file. Roblox picks this up on its next launch.")]
        [InlineData(2, 1, 0, "Updated 3 files. Roblox picks this up on its next launch.")]
        [InlineData(0, 0, 2, "Updated 2 files. Roblox picks this up on its next launch.")]
        public void UpdatedMessageCountsChangedFiles(int copied, int deleted, int displaced, string expected) =>
            Assert.Equal(expected, ModsStudioLogic.UpdatedMessage(copied, deleted, displaced));

        [Fact]
        public void BuiltinLabelsMatchTheLibrary()
        {
            Assert.Equal("Classic cursors", ModsStudioLogic.BuiltinLabel("builtin:cursors"));
            Assert.Equal("Classic character sounds", ModsStudioLogic.BuiltinLabel("builtin:old-sounds"));
            Assert.Equal("Classic avatar background", ModsStudioLogic.BuiltinLabel("builtin:old-avatar-background"));
            Assert.Equal("Something", ModsStudioLogic.BuiltinLabel("builtin:new-one", "Something"));
            Assert.Equal("builtin:new-one", ModsStudioLogic.BuiltinLabel("builtin:new-one"));
        }

        [Fact]
        public void ConfirmationsUseTheDesignedWording()
        {
            Assert.Equal(
                "Remove Alpha? Its files are removed from Modifications, and Roblox's original files come back the next time you launch.",
                ModsStudioLogic.RemoveConfirmation("Alpha"));
            Assert.Equal("Classic cursors will be turned off so Alpha can use this file.", ModsStudioLogic.PresetOffConfirmation("Classic cursors", "Alpha"));
        }

        // ---------------------------------------------------------------- ordering

        private static readonly string[] Order = { "a", "b", "c", "d" };

        [Fact]
        public void MoveSwapsWithTheNextEnabledPackage()
        {
            var enabled = new HashSet<string> { "a", "b", "c", "d" };

            Assert.Equal(new[] { "b", "a", "c", "d" }, ModsStudioLogic.MoveAmongEnabled(Order, enabled, "a", +1));
            Assert.Equal(new[] { "a", "c", "b", "d" }, ModsStudioLogic.MoveAmongEnabled(Order, enabled, "c", -1));
        }

        [Fact]
        public void MoveSkipsDisabledPackagesAndLeavesThemInPlace()
        {
            var enabled = new HashSet<string> { "a", "c", "d" }; // b is off

            Assert.Equal(new[] { "c", "b", "a", "d" }, ModsStudioLogic.MoveAmongEnabled(Order, enabled, "a", +1));
            Assert.Equal(new[] { "c", "b", "a", "d" }, ModsStudioLogic.MoveAmongEnabled(Order, enabled, "c", -1));
        }

        [Fact]
        public void MoveAtTheEdgesOrOfUnknownOrDisabledPackagesDoesNothing()
        {
            var enabled = new HashSet<string> { "a", "c" };

            Assert.Null(ModsStudioLogic.MoveAmongEnabled(Order, enabled, "a", -1));
            Assert.Null(ModsStudioLogic.MoveAmongEnabled(Order, enabled, "c", +1));
            Assert.Null(ModsStudioLogic.MoveAmongEnabled(Order, enabled, "b", +1)); // disabled
            Assert.Null(ModsStudioLogic.MoveAmongEnabled(Order, enabled, "zzz", +1)); // unknown
            Assert.Null(ModsStudioLogic.MoveAmongEnabled(Order, enabled, "a", 0));
        }

        [Fact]
        public void MoveDoesNotChangeTheInput()
        {
            var enabled = new HashSet<string> { "a", "b", "c", "d" };
            var input = Order.ToList();

            ModsStudioLogic.MoveAmongEnabled(input, enabled, "a", +1);

            Assert.Equal(Order, input);
        }

        // ---------------------------------------------------------------- conflict rows

        [Fact]
        public void RowsAreSortedAndLabelled()
        {
            var rows = ModsStudioLogic.BuildConflictRows(
                new[]
                {
                    Conflict(P2, "p1", ConflictStatus.Resolved, "p1", "p2"),
                    Conflict(P1, "p1", ConflictStatus.Unresolved, "p1", "p2")
                },
                Array.Empty<OverlayBlock>(),
                Label);

            Assert.Equal(new[] { P1, P2 }, rows.Select(r => r.Path).ToArray());

            var first = rows[0];
            Assert.Equal(ConflictRowStatus.Unresolved, first.Status);
            Assert.Equal("Alpha", first.CurrentOwnerLabel);
            Assert.Equal(new[] { "Alpha", "Beta" }, first.Candidates.Select(c => c.Label).ToArray());
            Assert.All(first.Candidates, c => Assert.True(c.Selectable));
            Assert.Equal(new[] { true, false }, first.Candidates.Select(c => c.IsCurrent).ToArray());
            Assert.Equal(ConflictRowStatus.Resolved, rows[1].Status);
        }

        [Fact]
        public void BuiltinCandidatesUseTheirDisplayNameAndAreFlagged()
        {
            var rows = ModsStudioLogic.BuildConflictRows(
                new[] { Conflict(P1, "builtin:cursors", ConflictStatus.Unresolved, "builtin:cursors", "p1") },
                Array.Empty<OverlayBlock>(),
                Label);

            var row = Assert.Single(rows);
            Assert.Equal("Classic cursors", row.Candidates[0].Label);
            Assert.True(row.Candidates[0].IsBuiltin);
            Assert.False(row.Candidates[1].IsBuiltin);
            Assert.Equal(new[] { "p1" }, row.PackageIds.ToArray());
        }

        [Fact]
        public void AnUnmanagedBlockAddsAnInformationalCandidateThatCannotBePicked()
        {
            var rows = ModsStudioLogic.BuildConflictRows(
                new[] { Conflict(P1, "p1", ConflictStatus.Resolved, "p1", "p2") },
                new[] { new OverlayBlock { Path = P1.ToUpperInvariant(), DesiredOwner = "p1", Reason = OverlayBlockReason.UnmanagedFileExists } },
                Label);

            var row = Assert.Single(rows);
            Assert.Equal(ConflictRowStatus.Blocked, row.Status);
            Assert.Equal(ModsStudioLogic.UnmanagedLabel, row.CurrentOwnerLabel);

            var info = row.Candidates.Single(c => c.OwnerId == ModsStudioLogic.UnmanagedOwnerId);
            Assert.False(info.Selectable);
            Assert.True(info.IsCurrent);
            Assert.Equal("File already in Modifications", info.Label);
            Assert.All(row.Candidates, c => Assert.False(c.Selectable)); // nothing can be written while the file is in the way
        }

        [Fact]
        public void ABlockedPathWithoutAConflictStillGetsARow()
        {
            var rows = ModsStudioLogic.BuildConflictRows(
                Array.Empty<ModConflict>(),
                new[]
                {
                    new OverlayBlock { Path = P2, DesiredOwner = "p2", Reason = OverlayBlockReason.UnmanagedFileExists },
                    new OverlayBlock { Path = P1, DesiredOwner = "p1", Reason = OverlayBlockReason.SourceMissing } // not a conflict
                },
                Label);

            var row = Assert.Single(rows);
            Assert.Equal(P2, row.Path);
            Assert.Equal(ConflictRowStatus.Blocked, row.Status);
            Assert.Equal(new[] { "p2" }, row.PackageIds.ToArray());
            Assert.All(row.Candidates, c => Assert.False(c.Selectable));
        }

        [Fact]
        public void FilteringKeepsOnlyRowsOfThePackage()
        {
            var rows = ModsStudioLogic.BuildConflictRows(
                new[]
                {
                    Conflict(P1, "p1", ConflictStatus.Resolved, "p1", "p2"),
                    Conflict(P2, "p1", ConflictStatus.Resolved, "p1", "p3")
                },
                Array.Empty<OverlayBlock>(),
                Label);

            Assert.Equal(2, ModsStudioLogic.ForPackage(rows, null).Count);
            Assert.Equal(2, ModsStudioLogic.ConflictCountFor(rows, "p1"));
            Assert.Equal(new[] { P1 }, ModsStudioLogic.ForPackage(rows, "p2").Select(r => r.Path).ToArray());
            Assert.Equal(0, ModsStudioLogic.ConflictCountFor(rows, "nobody"));
        }

        [Fact]
        public void NoConflictsMeansNoRows() =>
            Assert.Empty(ModsStudioLogic.BuildConflictRows(Array.Empty<ModConflict>(), Array.Empty<OverlayBlock>(), Label));
    }
}
