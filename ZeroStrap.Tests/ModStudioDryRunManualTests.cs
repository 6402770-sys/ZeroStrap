using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Xunit;

using ZeroStrap.Mods;

namespace ZeroStrap.Tests
{
    /// <summary>
    /// Manual only: does nothing unless ZS_MODS_DRYRUN_DIR points at a scratch folder that contains a
    /// COPY of a Modifications folder at &lt;dir&gt;\real-copy\Modifications. Never point it at the real Base folder.
    /// Run with: dotnet test --filter "Category=Manual"
    /// </summary>
    [Trait("Category", "Manual")]
    public class ModStudioDryRunManualTests
    {
        [Fact]
        public void DryRunAgainstACopyOfRealModifications()
        {
            string? dir = Environment.GetEnvironmentVariable("ZS_MODS_DRYRUN_DIR");

            if (string.IsNullOrEmpty(dir))
                return;

            string copy = Path.Combine(dir, "real-copy", "Modifications");
            Assert.True(Directory.Exists(copy), "missing " + copy);
            Assert.DoesNotContain("AppData\\Local\\ZeroStrap", Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase);

            var report = new StringBuilder();
            var logs = new List<string>();

            // Scenario A: the copy exactly as it is on this machine
            string studioA = Path.Combine(dir, "studio-a");
            var catalogA = BuiltinModCatalog.ForDirectory(copy, logs.Add);
            var studioAEngine = new ModStudio(studioA, copy, catalogA, () => null, logs.Add);

            var plan = studioAEngine.Migrator.Classify(new OverlayIndex(), out var candidates);
            report.AppendLine("== Scenario A: real Modifications copy ==");
            report.AppendLine($"Files: {Directory.GetFiles(copy, "*", SearchOption.AllDirectories).Length}");
            AppendClassification(report, plan, candidates);

            var migrationA = studioAEngine.EnsureMigrated();
            report.AppendLine($"Migration status: {migrationA.Status}");
            report.AppendLine($"Packages created: {studioAEngine.Packages.List().Count}; backup zip: {migrationA.BackupPath ?? "none"}");
            var rebuildA = studioAEngine.Rebuild();
            report.AppendLine($"Rebuild no-op: {rebuildA.Plan.IsNoOp}");

            // Scenario B: the same copy plus preset files written from the app's REAL embedded resources and loose mods
            string copyB = Path.Combine(dir, "scenario-b", "Modifications");
            CopyDirectory(copy, copyB);

            WriteResource(copyB, @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png", "Cursor.From2006.ArrowCursor.png");
            WriteResource(copyB, @"content\textures\Cursors\KeyboardMouse\ArrowFarCursor.png", "Cursor.From2006.ArrowFarCursor.png");
            WriteResource(copyB, @"content\sounds\action_jump.mp3", "Sounds.OldJump.mp3");
            WriteResource(copyB, @"content\sounds\action_swim.mp3", "Sounds.Empty.mp3");
            WriteResource(copyB, @"ExtraContent\places\Mobile.rbxl", "OldAvatarBackground.rbxl");
            File.WriteAllText(Path.Combine(copyB, "README.txt"), "readme");
            Directory.CreateDirectory(Path.Combine(copyB, "content", "sky"));
            File.WriteAllText(Path.Combine(copyB, "content", "sky", "sky512_bk.tex"), "loose sky mod");
            Directory.CreateDirectory(Path.Combine(copyB, "content", "sounds"));
            File.WriteAllText(Path.Combine(copyB, "content", "sounds", "ouch.ogg"), "loose sound mod");

            var catalogB = BuiltinModCatalog.ForDirectory(copyB, logs.Add);
            var studioB = new ModStudio(Path.Combine(dir, "studio-b"), copyB, catalogB, () => null, logs.Add);
            var planB = studioB.Migrator.Classify(new OverlayIndex(), out var candidatesB);
            report.AppendLine();
            report.AppendLine("== Scenario B: copy + real preset resources + loose mods ==");
            AppendClassification(report, planB, candidatesB);
            report.AppendLine("Active presets: " + string.Join(", ", catalogB.GetActive().Select(a => a.Preset.Id)));

            var migrationB = studioB.EnsureMigrated();
            report.AppendLine($"Migration status: {migrationB.Status}; packaged: {string.Join(", ", migrationB.PackagedFiles)}");
            report.AppendLine($"Rebuild no-op: {studioB.Rebuild().Plan.IsNoOp}");

            File.WriteAllText(Path.Combine(dir, "dryrun-report.txt"), report.ToString());
            File.WriteAllLines(Path.Combine(dir, "dryrun-log.txt"), logs);

            Assert.Equal(MigrationStatus.Migrated, migrationB.Status);
            Assert.Equal(2, migrationB.PackagedFiles.Count);
            Assert.Equal(new[] { "builtin:cursors", "builtin:old-avatar-background", "builtin:old-sounds" }, catalogB.GetActive().Select(a => a.Preset.Id).OrderBy(i => i).ToArray());
        }

        private static void AppendClassification(StringBuilder report, MigrationResult plan, List<(string Source, string Relative)> candidates)
        {
            report.AppendLine($"Would package ({candidates.Count}): {string.Join(", ", candidates.Select(c => c.Relative))}");
            report.AppendLine($"Preset-owned ({plan.PresetFiles.Count}): {string.Join(", ", plan.PresetFiles)}");
            report.AppendLine($"Left unmanaged ({plan.UnmanagedFiles.Count}): {string.Join(", ", plan.UnmanagedFiles)}");
            report.AppendLine($"Skipped ({plan.SkippedFiles.Count}): {string.Join(", ", plan.SkippedFiles)}");
        }

        private static void WriteResource(string root, string relative, string resource)
        {
            string full = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            using var stream = Resource.GetStream(resource);
            using var file = File.Create(full);
            stream.CopyTo(file);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }
    }
}
