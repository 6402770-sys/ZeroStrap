using System.Collections.Generic;
using System.Linq;
using ZeroStrap.Enums;
using ZeroStrap.Models.APIs.GitHub;
using ZeroStrap.Utility;
using Xunit;

namespace ZeroStrap.Tests
{
    public class UpdateCheckerTests
    {
        private static GithubRelease Release(string tag, bool prerelease = false, bool draft = false, params string[] assetNames)
        {
            return new GithubRelease
            {
                TagName = tag,
                Prerelease = prerelease,
                Draft = draft,
                Assets = assetNames.Select(Asset).ToList()
            };
        }

        private static GithubReleaseAsset Asset(string name)
        {
            return new GithubReleaseAsset { Name = name, BrowserDownloadUrl = "https://example.invalid/" + name, Size = 1234 };
        }

        [Theory]
        [InlineData("3.0.0", "v3.0.0", UpdateState.UpToDate)]
        [InlineData("3.0.0", "v3.0.1", UpdateState.UpdateAvailable)]
        [InlineData("3.0.0", "v2.9.1", UpdateState.UpToDate)]
        [InlineData("3.0.0", "v3.0.0-obsidian.1", UpdateState.UpToDate)]
        [InlineData("3.0.0", "v3.1.0-obsidian", UpdateState.UpdateAvailable)]
        [InlineData("3.0.0.0", "v3.0.0", UpdateState.UpToDate)]
        [InlineData("3.0.0", "v3.0.0.0", UpdateState.UpToDate)]
        [InlineData("3.0.0", "3.0.0+abc123", UpdateState.UpToDate)]
        [InlineData("3.0.0", "v3.0.0.1", UpdateState.UpdateAvailable)]
        public void Evaluate_ComparesNumericVersions(string local, string tag, UpdateState expected)
        {
            Assert.Equal(expected, UpdateChecker.Evaluate(local, Release(tag, assetNames: "ZeroStrap.exe")).State);
        }

        [Fact]
        public void Evaluate_NullRelease_CheckFailed()
        {
            Assert.Equal(UpdateState.CheckFailed, UpdateChecker.Evaluate("3.0.0", null).State);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("latest")]
        [InlineData("vabc")]
        [InlineData("v")]
        public void Evaluate_BadTag_CheckFailed(string tag)
        {
            Assert.Equal(UpdateState.CheckFailed, UpdateChecker.Evaluate("3.0.0", Release(tag)).State);
        }

        [Theory]
        [InlineData("")]
        [InlineData("garbage")]
        [InlineData("x.y.z")]
        public void Evaluate_BadLocalVersion_UnknownVersion(string local)
        {
            Assert.Equal(UpdateState.UnknownVersion, UpdateChecker.Evaluate(local, Release("v3.0.1")).State);
        }

        [Fact]
        public void Evaluate_NewerPrerelease_UpToDate()
        {
            Assert.Equal(UpdateState.UpToDate, UpdateChecker.Evaluate("3.0.0", Release("v3.1.0", prerelease: true, assetNames: "ZeroStrap.exe")).State);
        }

        [Fact]
        public void Evaluate_NewerDraft_UpToDate()
        {
            Assert.Equal(UpdateState.UpToDate, UpdateChecker.Evaluate("3.0.0", Release("v3.1.0", draft: true, assetNames: "ZeroStrap.exe")).State);
        }

        [Fact]
        public void Evaluate_UpdateAvailable_ReportsTagAndAsset()
        {
            var result = UpdateChecker.Evaluate("3.0.0", Release("v3.1.0", assetNames: "ZeroStrap.exe"));

            Assert.Equal(UpdateState.UpdateAvailable, result.State);
            Assert.Equal("v3.1.0", result.LatestTag);
            Assert.Equal("ZeroStrap.exe", result.Asset?.Name);
        }

        [Fact]
        public void Evaluate_UpdateAvailable_NoUsableAsset_NullAsset()
        {
            var result = UpdateChecker.Evaluate("3.0.0", Release("v3.1.0", assetNames: "notes.txt"));

            Assert.Equal(UpdateState.UpdateAvailable, result.State);
            Assert.Null(result.Asset);
        }

        [Fact]
        public void Evaluate_NullAssets_UpdateAvailableWithNullAsset()
        {
            var release = Release("v3.1.0");
            release.Assets = null;

            var result = UpdateChecker.Evaluate("3.0.0", release);

            Assert.Equal(UpdateState.UpdateAvailable, result.State);
            Assert.Null(result.Asset);
        }

        [Fact]
        public void SelectAsset_PrefersExeOverZip()
        {
            var assets = new[] { Asset("ZeroStrap-3.1.0-win-x64.zip"), Asset("ZeroStrap.exe") };

            Assert.Equal("ZeroStrap.exe", UpdateChecker.SelectAsset(assets)?.Name);
        }

        [Fact]
        public void SelectAsset_ExeMatchIsCaseInsensitive()
        {
            Assert.Equal("zerostrap-3.1.0.EXE", UpdateChecker.SelectAsset(new[] { Asset("zerostrap-3.1.0.EXE") })?.Name);
        }

        [Fact]
        public void SelectAsset_ChoosesZipWhenNoExe()
        {
            var assets = new[] { Asset("checksums.txt"), Asset("ZeroStrap-3.0.0-obsidian-win-x64.zip") };

            Assert.Equal("ZeroStrap-3.0.0-obsidian-win-x64.zip", UpdateChecker.SelectAsset(assets)?.Name);
        }

        [Fact]
        public void SelectAsset_UnrelatedAssets_ReturnsNull()
        {
            var assets = new[] { Asset("source.zip"), Asset("Other.exe"), Asset("notes.txt") };

            Assert.Null(UpdateChecker.SelectAsset(assets));
        }

        [Fact]
        public void SelectAsset_Empty_ReturnsNull()
        {
            Assert.Null(UpdateChecker.SelectAsset(new List<GithubReleaseAsset>()));
            Assert.Null(UpdateChecker.SelectAsset(null));
        }

        private static ZipEntrySelection Select(params (string Name, long Length)[] entries)
        {
            return UpdateChecker.SelectZipEntry(entries.Select(x => new ZipEntryInfo(x.Name, x.Length)));
        }

        [Theory]
        [InlineData("ZeroStrap.exe")]
        [InlineData("zerostrap.EXE")]
        [InlineData("ZeroStrap-3.0.0/ZeroStrap.exe")]
        [InlineData("ZeroStrap-3.0.0\\ZeroStrap.exe")]
        public void SelectZipEntry_AcceptsRootOrOneFolder(string name)
        {
            var selection = Select(("readme.txt", 10), (name, 1000));

            Assert.Null(selection.Reason);
            Assert.Equal(name, selection.Entry?.FullName);
        }

        [Theory]
        [InlineData("../ZeroStrap.exe")]
        [InlineData("a/../../ZeroStrap.exe")]
        [InlineData("..\\ZeroStrap.exe")]
        [InlineData("/ZeroStrap.exe")]
        [InlineData("C:\\ZeroStrap.exe")]
        [InlineData("C:/x/ZeroStrap.exe")]
        public void SelectZipEntry_RejectsUnsafePaths(string name)
        {
            var selection = Select((name, 1000));

            Assert.Null(selection.Entry);
            Assert.NotNull(selection.Reason);
        }

        [Fact]
        public void SelectZipEntry_UnsafeCandidateRejectsWholeArchive()
        {
            var selection = Select(("ZeroStrap.exe", 1000), ("../ZeroStrap.exe", 1000));

            Assert.Null(selection.Entry);
            Assert.NotNull(selection.Reason);
        }

        [Fact]
        public void SelectZipEntry_NestedTwoDeep_NotFound()
        {
            var selection = Select(("a/b/ZeroStrap.exe", 1000));

            Assert.Null(selection.Entry);
            Assert.NotNull(selection.Reason);
        }

        [Fact]
        public void SelectZipEntry_Oversize_Rejected()
        {
            var selection = Select(("ZeroStrap.exe", UpdateChecker.MaxExtractedLength + 1));

            Assert.Null(selection.Entry);
            Assert.NotNull(selection.Reason);
        }

        [Fact]
        public void SelectZipEntry_ExactlyMaxLength_Accepted()
        {
            Assert.NotNull(Select(("ZeroStrap.exe", UpdateChecker.MaxExtractedLength)).Entry);
        }

        [Fact]
        public void SelectZipEntry_Missing_NotFound()
        {
            var selection = Select(("readme.txt", 10), ("ZeroStrap.dll", 10), ("dir/", 0), ("ZeroStrap.exe/", 0));

            Assert.Null(selection.Entry);
            Assert.NotNull(selection.Reason);
        }

        [Fact]
        public void SelectZipEntry_Ambiguous_Rejected()
        {
            var selection = Select(("ZeroStrap.exe", 1000), ("dir/ZeroStrap.exe", 1000));

            Assert.Null(selection.Entry);
            Assert.NotNull(selection.Reason);
        }

        [Theory]
        [InlineData("v3.0.1", "v3.0.1")]
        [InlineData("..", "latest")]
        [InlineData("", "latest")]
        [InlineData("v1/../../x", "v1_.._.._x")]
        [InlineData("a\\b:c", "a_b_c")]
        public void SanitizeForPath_IsSingleSafeSegment(string input, string expected)
        {
            Assert.Equal(expected, UpdateChecker.SanitizeForPath(input));
        }
    }
}
