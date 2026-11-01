using System.Collections.Generic;
using System.Linq;

using Xunit;

using ZeroStrap.Mods;

namespace ZeroStrap.Tests
{
    public class ModConflictResolverTests
    {
        private const string P = @"content\sounds\a.mp3";

        private static ResolverPackage Pkg(string id, params string[] paths) => new() { Id = id, Paths = paths };

        private static ResolverInput Input(
            ResolverPackage[] packages,
            string[]? priority = null,
            Dictionary<string, string>? choices = null,
            Dictionary<string, string>? builtins = null,
            Dictionary<string, string>? previous = null,
            bool priorityChanged = false) => new()
        {
            EnabledPackages = packages,
            Priority = priority ?? packages.Select(p => p.Id).ToArray(),
            FileChoices = choices ?? new(),
            ActiveBuiltinOwners = builtins ?? new(),
            PreviousOwners = previous ?? new(),
            PriorityChanged = priorityChanged
        };

        [Fact]
        public void UncontestedPathHasNoConflict()
        {
            var r = ModConflictResolver.Resolve(Input(new[] { Pkg("a", P), Pkg("b", @"content\sounds\b.mp3") }));

            Assert.Empty(r.Conflicts);
            Assert.Equal("a", r.Owners[P]);
            Assert.Equal("b", r.Owners[@"content\sounds\b.mp3"]);
        }

        [Fact]
        public void TwoPackagesResolveByPriority()
        {
            var r = ModConflictResolver.Resolve(Input(new[] { Pkg("a", P), Pkg("b", P) }, priority: new[] { "b", "a" }));

            Assert.Equal("b", r.Owners[P]);
            var c = Assert.Single(r.Conflicts);
            Assert.Equal(P, c.Path);
            Assert.Equal(new[] { "b", "a" }, c.Candidates);
            Assert.Equal("b", c.CurrentOwner);
            Assert.Equal(ConflictStatus.Resolved, c.Status);
        }

        [Fact]
        public void UnlistedPackagesRankBelowListedOnes()
        {
            var r = ModConflictResolver.Resolve(Input(new[] { Pkg("a", P), Pkg("z", P) }, priority: new[] { "z" }));
            Assert.Equal("z", r.Owners[P]);
        }

        [Fact]
        public void ExplicitChoiceBeatsPriority()
        {
            var r = ModConflictResolver.Resolve(Input(
                new[] { Pkg("a", P), Pkg("b", P) },
                priority: new[] { "b", "a" },
                choices: new() { [P] = "a" }));

            Assert.Equal("a", r.Owners[P]);
            Assert.Equal(ConflictStatus.Resolved, Assert.Single(r.Conflicts).Status);
        }

        [Fact]
        public void ChoiceOfNonCandidateIsIgnored()
        {
            var r = ModConflictResolver.Resolve(Input(
                new[] { Pkg("a", P), Pkg("b", P) },
                choices: new() { [P] = "ghost" }));

            Assert.Equal("a", r.Owners[P]);
        }

        [Fact]
        public void ChoiceIsMatchedCaseInsensitivelyByPath()
        {
            var r = ModConflictResolver.Resolve(Input(
                new[] { Pkg("a", P), Pkg("b", P) },
                choices: new Dictionary<string, string>(ModPathRules.PathComparer) { [P.ToUpperInvariant()] = "b" }));

            Assert.Equal("b", r.Owners[P]);
        }

        [Fact]
        public void NewPackageDoesNotStealFromExistingOwner()
        {
            // b is new and has the higher priority, a already owns the path
            var r = ModConflictResolver.Resolve(Input(
                new[] { Pkg("a", P), Pkg("b", P) },
                priority: new[] { "b", "a" },
                previous: new() { [P] = "a" }));

            Assert.Equal("a", r.Owners[P]);
            var c = Assert.Single(r.Conflicts);
            Assert.Equal(ConflictStatus.Unresolved, c.Status);
            Assert.Equal("a", c.CurrentOwner);
        }

        [Fact]
        public void ExistingOwnerThatAlsoWinsByPriorityIsResolved()
        {
            var r = ModConflictResolver.Resolve(Input(
                new[] { Pkg("a", P), Pkg("b", P) },
                priority: new[] { "a", "b" },
                previous: new() { [P] = "a" }));

            Assert.Equal(ConflictStatus.Resolved, Assert.Single(r.Conflicts).Status);
        }

        [Fact]
        public void ReorderingPrioritiesLetsPriorityWin()
        {
            var r = ModConflictResolver.Resolve(Input(
                new[] { Pkg("a", P), Pkg("b", P) },
                priority: new[] { "b", "a" },
                previous: new() { [P] = "a" },
                priorityChanged: true));

            Assert.Equal("b", r.Owners[P]);
            Assert.Equal(ConflictStatus.Resolved, Assert.Single(r.Conflicts).Status);
        }

        [Fact]
        public void DisablingTheWinnerHandsThePathToTheNextOwner()
        {
            // a owned the path last time but is no longer enabled
            var r = ModConflictResolver.Resolve(Input(
                new[] { Pkg("b", P), Pkg("c", P) },
                priority: new[] { "a", "b", "c" },
                previous: new() { [P] = "a" }));

            Assert.Equal("b", r.Owners[P]);
        }

        [Fact]
        public void DisablingTheOnlyOwnerLeavesNoOwner()
        {
            var r = ModConflictResolver.Resolve(Input(new ResolverPackage[0], previous: new() { [P] = "a" }));
            Assert.Empty(r.Owners);
        }

        [Fact]
        public void BuiltinPresetKeepsItsPathUntilChosenAway()
        {
            var r = ModConflictResolver.Resolve(Input(
                new[] { Pkg("a", P) },
                builtins: new() { [P] = "builtin:old-sounds" }));

            Assert.Equal("builtin:old-sounds", r.Owners[P]);
            var c = Assert.Single(r.Conflicts);
            Assert.Equal(ConflictStatus.Unresolved, c.Status);
            Assert.Equal(new[] { "builtin:old-sounds", "a" }, c.Candidates);
            Assert.Empty(r.Actions);
        }

        [Fact]
        public void ChoosingAPackageOverAPresetRequestsDisablingThePreset()
        {
            var r = ModConflictResolver.Resolve(Input(
                new[] { Pkg("a", P) },
                choices: new() { [P] = "a" },
                builtins: new() { [P] = "builtin:old-sounds" }));

            Assert.Equal("a", r.Owners[P]);
            Assert.Equal(ConflictStatus.Resolved, Assert.Single(r.Conflicts).Status);

            var action = Assert.Single(r.Actions);
            Assert.Equal(ResolverActionKind.DisablePreset, action.Kind);
            Assert.Equal("builtin:old-sounds", action.PresetId);
            Assert.Equal(new[] { P }, action.Paths);
        }

        [Fact]
        public void ChoosingThePresetKeepsItWithoutActions()
        {
            var r = ModConflictResolver.Resolve(Input(
                new[] { Pkg("a", P) },
                choices: new() { [P] = "builtin:old-sounds" },
                builtins: new() { [P] = "builtin:old-sounds" }));

            Assert.Equal("builtin:old-sounds", r.Owners[P]);
            Assert.Empty(r.Actions);
            Assert.Equal(ConflictStatus.Resolved, Assert.Single(r.Conflicts).Status);
        }

        [Fact]
        public void ChoiceOfInactivePresetIsIgnored()
        {
            var r = ModConflictResolver.Resolve(Input(
                new[] { Pkg("a", P) },
                choices: new() { [P] = "builtin:old-sounds" }));

            Assert.Equal("a", r.Owners[P]);
        }
    }
}
