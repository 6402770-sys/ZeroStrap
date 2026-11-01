using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Text;

using Xunit;

using ZeroStrap.Utility;

namespace ZeroStrap.Tests
{
    public class SafeArchiveExtractorTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "zs-archive-plan-root");

        private static ArchiveEntryInfo File_(string name, long length = 100, long compressed = 80) => new(name, length, compressed);

        private static ExtractionPlan Plan(ArchiveLimits? limits, params ArchiveEntryInfo[] entries) =>
            SafeArchiveExtractor.Plan(entries, Root, limits ?? new ArchiveLimits());

        [Theory]
        [InlineData("../evil.txt")]
        [InlineData("a/../../evil.txt")]
        [InlineData("a\\..\\..\\evil.txt")]
        [InlineData("..")]
        [InlineData("/etc/passwd")]
        [InlineData("\\windows\\system32\\x.dll")]
        [InlineData("\\\\server\\share\\x.txt")]
        [InlineData("C:/Windows/x.txt")]
        [InlineData("C:\\Windows\\x.txt")]
        [InlineData("C:x.txt")]
        [InlineData("file.txt:evil")]
        [InlineData("file.txt::$DATA")]
        [InlineData("con.txt")]
        [InlineData("dir/NUL")]
        [InlineData("dir/aux.dat")]
        [InlineData("dir/name.")]
        [InlineData("dir/bad|name.txt")]
        [InlineData("")]
        public void RejectsUnsafeNames(string name)
        {
            var plan = Plan(null, File_(name));

            Assert.Empty(plan.Accepted);
            Assert.Single(plan.Rejected);
            Assert.False(plan.IsRefused);
        }

        [Fact]
        public void AcceptsNormalNestedFiles()
        {
            var plan = Plan(null,
                File_("theme.json"),
                File_("media/bg.png"),
                File_("a\\b\\c.txt"),
                new ArchiveEntryInfo("media/", 0, 0, IsDirectory: true));

            Assert.Empty(plan.Rejected);
            Assert.Equal(4, plan.Accepted.Count);

            var nested = plan.Accepted.Single(a => a.RelativePath == "a/b/c.txt");
            Assert.Equal(Path.Combine(Root, "a", "b", "c.txt"), nested.DestinationPath);
            Assert.All(plan.Accepted, a => Assert.StartsWith(Root, a.DestinationPath));
        }

        [Fact]
        public void NormalisesDotSegmentsAndDoubleSlashes()
        {
            var plan = Plan(null, File_("./a//b/./c.txt"));

            Assert.Equal("a/b/c.txt", Assert.Single(plan.Accepted).RelativePath);
        }

        [Fact]
        public void TooManyEntriesRefusesTheWholeArchive()
        {
            var limits = new ArchiveLimits { MaxEntries = 3 };
            var plan = Plan(limits, File_("a.txt"), File_("b.txt"), File_("c.txt"), File_("d.txt"));

            Assert.True(plan.IsRefused);
            Assert.Empty(plan.Accepted);
        }

        [Fact]
        public void CompressionRatioBombIsRejectedPerEntry()
        {
            var plan = Plan(null,
                File_("bomb.bin", length: 10L * 1024 * 1024, compressed: 10 * 1024),
                File_("ok.txt"));

            Assert.Equal("ok.txt", Assert.Single(plan.Accepted).RelativePath);
            Assert.Contains("ratio", Assert.Single(plan.Rejected).Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ZeroCompressedSizeWithLargeLengthIsABomb()
        {
            var plan = Plan(null, File_("x.bin", length: 5L * 1024 * 1024, compressed: 0));

            Assert.Single(plan.Rejected);
        }

        [Fact]
        public void TinyHighlyCompressibleEntriesAreExemptFromTheRatioCheck()
        {
            var plan = Plan(null, File_("zeros.txt", length: 4096, compressed: 8));

            Assert.Single(plan.Accepted);
        }

        [Fact]
        public void OversizedEntryIsRejectedPerEntry()
        {
            var limits = new ArchiveLimits { MaxEntryBytes = 1000 };
            var plan = Plan(limits, File_("big.bin", length: 5000, compressed: 4000), File_("small.bin", length: 500, compressed: 400));

            Assert.Equal("small.bin", Assert.Single(plan.Accepted).RelativePath);
            Assert.Single(plan.Rejected);
        }

        [Fact]
        public void TotalSizeOverTheLimitRefusesTheArchive()
        {
            var limits = new ArchiveLimits { MaxTotalBytes = 1000, MaxEntryBytes = 800 };
            var plan = Plan(limits, File_("a.bin", 600, 500), File_("b.bin", 600, 500));

            Assert.True(plan.IsRefused);
            Assert.Empty(plan.Accepted);
        }

        [Fact]
        public void ExtensionAllowlistIsCaseInsensitiveAndAppliesToFilesOnly()
        {
            var limits = new ArchiveLimits { AllowedExtensions = new[] { ".png", ".json" } };
            var plan = Plan(limits,
                File_("a.PNG"),
                File_("b.json"),
                File_("c.exe"),
                File_("noext"),
                new ArchiveEntryInfo("folder/", 0, 0, IsDirectory: true));

            Assert.Equal(3, plan.Accepted.Count);
            Assert.Equal(new[] { "c.exe", "noext" }, plan.Rejected.Select(r => r.FullName).OrderBy(n => n));
        }

        [Fact]
        public void DuplicatePathsAfterNormalisationAreRejected()
        {
            var plan = Plan(null, File_("a/b.txt"), File_("A\\B.txt"));

            Assert.Single(plan.Accepted);
            Assert.Single(plan.Rejected);
        }

        // ------------------------------------------------------------ IO

        private static MemoryStream BuildZip(params (string name, string content)[] files)
        {
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in files)
                {
                    var entry = zip.CreateEntry(name);
                    using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    w.Write(content);
                }
            }

            ms.Position = 0;
            return ms;
        }

        [Fact]
        public void ExtractWritesAcceptedFilesAndSkipsTraversal()
        {
            string dest = Path.Combine(Path.GetTempPath(), "zs-extract-" + Guid.NewGuid().ToString("N"));
            string outside = Path.Combine(Path.GetTempPath(), "zs-extract-evil.txt");

            try
            {
                using var zip = BuildZip(("ok/a.txt", "hello"), ("../zs-extract-evil.txt", "bad"), ("deep/er/b.txt", "world"));
                var result = SafeArchiveExtractor.Extract(zip, dest, new ArchiveLimits());

                Assert.True(result.Succeeded);
                Assert.Equal(2, result.Extracted.Count);
                Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "ok", "a.txt")));
                Assert.Equal("world", File.ReadAllText(Path.Combine(dest, "deep", "er", "b.txt")));
                Assert.Single(result.Plan.Rejected);
                Assert.False(File.Exists(outside));
            }
            finally
            {
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                if (File.Exists(outside)) File.Delete(outside);
            }
        }

        [Fact]
        public void ExtractReportsNonZipInput()
        {
            string dest = Path.Combine(Path.GetTempPath(), "zs-extract-" + Guid.NewGuid().ToString("N"));
            using var notZip = new MemoryStream(Encoding.ASCII.GetBytes("this is not a zip file at all"));

            var result = SafeArchiveExtractor.Extract(notZip, dest, new ArchiveLimits());

            Assert.False(result.Succeeded);
            Assert.False(Directory.Exists(dest));
        }

        [Theory]
        [InlineData(unchecked((int)0xA1FF0000), true)]   // Unix symlink (0120777)
        [InlineData(unchecked((int)0x81A40000), false)]  // Unix regular file (0100644)
        [InlineData(0x400, true)]                        // Windows reparse point
        [InlineData(0x20, false)]                        // Windows archive attribute
        [InlineData(0, false)]
        public void LinkAttributesAreRecognised(int attributes, bool isLink)
        {
            Assert.Equal(isLink, SafeArchiveExtractor.IsLinkAttributes(attributes));
        }

        [Fact]
        public void LinksAreRejectedOnlyWhenRequested()
        {
            var link = new ArchiveEntryInfo("a/link", 10, 10, false, true);

            Assert.Single(Plan(new ArchiveLimits { RejectLinks = true }, link).Rejected);
            Assert.Single(Plan(new ArchiveLimits(), link).Accepted);
        }

        [Fact]
        public void ExactlyTheRatioLimitIsAcceptedAndOneOverIsRejected()
        {
            var at = Plan(null, File_("a.bin", length: 2_000_000, compressed: 20_000));
            var over = Plan(null, File_("b.bin", length: 2_020_000, compressed: 20_000));

            Assert.Single(at.Accepted);
            Assert.Single(over.Rejected);
        }

        [Fact]
        public void PathMapperCanRemapRejectIgnoreAndSkip()
        {
            var limits = new ArchiveLimits
            {
                PathMapper = (path, isDirectory) =>
                {
                    if (isDirectory) return ArchivePathDecision.Skip();
                    if (path.StartsWith("junk/")) return ArchivePathDecision.Ignore();
                    if (path.StartsWith("bad/")) return ArchivePathDecision.Reject("nope");
                    return ArchivePathDecision.Accept(path.Replace("wrap/", ""));
                }
            };

            var plan = Plan(limits,
                new ArchiveEntryInfo("wrap/", 0, 0, true),
                File_("wrap/a.txt"), File_("junk/x.txt"), File_("bad/y.txt"));

            Assert.Equal("a.txt", Assert.Single(plan.Accepted).RelativePath);
            Assert.Equal("junk/x.txt", Assert.Single(plan.Ignored));
            Assert.Equal("nope", Assert.Single(plan.Rejected).Reason);
        }

        [Fact]
        public void PathMapperOutputIsStillConfinedToTheDestination()
        {
            var limits = new ArchiveLimits { PathMapper = (_, _) => ArchivePathDecision.Accept("../escape.txt") };

            var plan = Plan(limits, File_("a.txt"));

            Assert.Empty(plan.Accepted);
            Assert.Single(plan.Rejected);
        }
    }
}
