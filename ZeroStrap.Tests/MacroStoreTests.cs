using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Xunit;

using ZeroStrap.Macros;

using static ZeroStrap.Tests.MacroBuilders;

namespace ZeroStrap.Tests
{
    public sealed class MacroStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "zs-macro-store-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _logs = new();
        private readonly MacroStore _store;

        public MacroStoreTests()
        {
            _store = new MacroStore(Path.Combine(_root, "Macros"), _logs.Add);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string Dir => _store.Directory;

        private static Macro Sample(string id, string name = "Sample")
        {
            var m = Of(TapKey(0x1E, holdMs: 30), Wait(100), Camera(40, -10, 200, holdRight: true));
            m.Id = id;
            m.Name = name;
            m.Enabled = true;
            m.Hotkey = "Ctrl+F7";
            m.Speed = 2;
            m.Repeat = new MacroRepeat { Mode = MacroRepeatMode.Count, Count = 3 };
            return m;
        }

        private string Write(string fileName, string contents)
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, fileName);
            File.WriteAllText(path, contents);
            return path;
        }

        [Fact]
        public void SaveThenLoadAllRoundTrips()
        {
            _store.Save(Sample("a"));
            _store.Save(Sample("b", "Another"));

            var loaded = _store.LoadAll();

            Assert.Equal(new[] { "b", "a" }, loaded.Select(m => m.Id)); // ordered by name: Another, Sample
            var a = loaded.Single(m => m.Id == "a");
            Assert.True(a.Enabled);
            Assert.Equal("Ctrl+F7", a.Hotkey);
            Assert.Equal(2, a.Speed);
            Assert.Equal(MacroRepeatMode.Count, a.Repeat.Mode);
            Assert.Equal(3, a.Actions.Count);
            Assert.Equal(0x1E, a.Actions[0].ScanCode);
            Assert.True(a.Actions[2].HoldRightButton);
        }

        [Fact]
        public void LoadAllOnAMissingDirectoryIsEmptyAndDoesNotCreateIt()
        {
            Assert.Empty(_store.LoadAll());
            Assert.False(Directory.Exists(Dir));
        }

        [Fact]
        public void SaveNormalisesAndReturnsTheSavedCopy()
        {
            var m = Sample("a");
            m.Speed = 99;
            m.Name = "  Padded  ";

            var saved = _store.Save(m);

            Assert.Equal(4.0, saved.Speed);
            Assert.Equal("Padded", _store.Load("a")!.Name);
            Assert.Equal(99, m.Speed);
        }

        [Fact]
        public void SaveRejectsAnInvalidMacroAndWritesNothing()
        {
            var bad = Sample("../evil");

            Assert.Throws<InvalidDataException>(() => _store.Save(bad));
            Assert.False(Directory.Exists(Dir) && Directory.EnumerateFileSystemEntries(Dir).Any());
            Assert.False(File.Exists(Path.Combine(_root, "evil.json")));
        }

        [Fact]
        public void SaveCannotOverwriteTheSettingsFile()
        {
            Assert.Throws<InvalidDataException>(() => _store.Save(Sample("settings")));
        }

        [Fact]
        public void FailedSaveLeavesTheOldFileIntact()
        {
            _store.Save(Sample("a", "Original"));
            _store.FaultBeforeCommit = () => throw new IOException("disk gone");

            Assert.Throws<IOException>(() => _store.Save(Sample("a", "Replacement")));

            _store.FaultBeforeCommit = null;
            Assert.Equal("Original", _store.Load("a")!.Name);
            Assert.DoesNotContain(Directory.GetFiles(Dir), f => f.Contains(".tmp-"));
        }

        [Fact]
        public void CorruptFileIsQuarantinedAndSkipped()
        {
            _store.Save(Sample("good"));
            string bad = Write("broken.json", "{ this is not json");

            var loaded = _store.LoadAll();

            Assert.Equal(new[] { "good" }, loaded.Select(m => m.Id));
            Assert.False(File.Exists(bad));
            Assert.Single(Directory.GetFiles(Dir, "broken.json.corrupt-*"));
            Assert.Contains(_logs, l => l.Contains("broken.json"));
        }

        [Fact]
        public void InvalidMacroContentIsQuarantinedToo()
        {
            var m = Sample("hostile");
            string json = ZeroStrap.Macros.MacroJson.Serialize(m).Replace("\"hostile\"", "\"../hostile\"");
            Write("hostile.json", json);

            Assert.Empty(_store.LoadAll());
            Assert.Single(Directory.GetFiles(Dir, "hostile.json.corrupt-*"));
        }

        [Fact]
        public void UnknownKindInAFileIsQuarantined()
        {
            Write("odd.json", "{\"id\":\"odd\",\"name\":\"Odd\",\"actions\":[{\"kind\":\"teleport\"}]}");

            Assert.Empty(_store.LoadAll());
            Assert.Single(Directory.GetFiles(Dir, "odd.json.corrupt-*"));
        }

        [Fact]
        public void FileNameThatDoesNotMatchTheIdIsSkippedButKept()
        {
            string json = ZeroStrap.Macros.MacroJson.Serialize(Sample("real-id"));
            string path = Write("other-name.json", json);

            Assert.Empty(_store.LoadAll());
            Assert.True(File.Exists(path));
            Assert.Contains(_logs, l => l.Contains("does not match"));
        }

        [Fact]
        public void OversizedFileOnLoadIsQuarantined()
        {
            Write("huge.json", new string(' ', 2 * 1024 * 1024 + 10));

            Assert.Empty(_store.LoadAll());
            Assert.Single(Directory.GetFiles(Dir, "huge.json.corrupt-*"));
        }

        [Fact]
        public void SettingsFileInTheSameFolderIsNotTreatedAsAMacro()
        {
            Write("settings.json", "{\"emergencyStop\":\"Ctrl+Shift+End\"}");
            _store.Save(Sample("a"));

            Assert.Single(_store.LoadAll());
            Assert.True(File.Exists(Path.Combine(Dir, "settings.json")));
            Assert.Empty(Directory.GetFiles(Dir, "*.corrupt-*"));
        }

        [Fact]
        public void LeftoverTempAndQuarantineFilesAreIgnored()
        {
            _store.Save(Sample("a"));
            Write("a.json.tmp-123", "junk");
            Write("old.json.corrupt-20240101", "junk");

            Assert.Single(_store.LoadAll());
        }

        [Fact]
        public void DeleteRemovesTheFile()
        {
            _store.Save(Sample("a"));

            Assert.True(_store.Delete("a"));
            Assert.False(_store.Delete("a"));
            Assert.False(_store.Delete("../a"));
            Assert.Empty(_store.LoadAll());
        }

        [Fact]
        public void DuplicateGetsNewIdUniqueNameAndIsNotArmed()
        {
            _store.Save(Sample("a", "Farm"));

            var copy = _store.Duplicate("a")!;
            var second = _store.Duplicate("a")!;

            Assert.NotEqual("a", copy.Id);
            Assert.True(MacroValidator.IsValidId(copy.Id));
            Assert.Equal("Farm (2)", copy.Name);
            Assert.Equal("Farm (3)", second.Name);
            Assert.False(copy.Enabled);
            Assert.Null(copy.Hotkey);
            Assert.Equal(3, copy.Actions.Count);
            Assert.Equal(3, _store.LoadAll().Count);
            Assert.Null(_store.Duplicate("missing"));
        }

        [Fact]
        public void ImportAssignsNewIdDisablesAndClearsTheHotkey()
        {
            string file = Path.Combine(_root, "shared.zeromacro");
            Directory.CreateDirectory(_root);
            File.WriteAllText(file, ZeroStrap.Macros.MacroJson.Serialize(Sample("their-id", "Shared")));

            var result = _store.Import(file);

            Assert.True(result.Success);
            var m = result.Macro!;
            Assert.NotEqual("their-id", m.Id);
            Assert.True(MacroValidator.IsValidId(m.Id));
            Assert.False(m.Enabled);
            Assert.Null(m.Hotkey);
            Assert.Equal("Shared", m.Name);
            Assert.Equal(3, m.Actions.Count);

            var onDisk = _store.Load(m.Id)!;
            Assert.False(onDisk.Enabled);
            Assert.Null(onDisk.Hotkey);
        }

        [Fact]
        public void ImportDeduplicatesTheName()
        {
            _store.Save(Sample("a", "Shared"));
            string file = Path.Combine(_root, "shared.zeromacro");
            File.WriteAllText(file, ZeroStrap.Macros.MacroJson.Serialize(Sample("x", "shared")));

            var first = _store.Import(file);
            var second = _store.Import(file);

            Assert.Equal("shared (2)", first.Macro!.Name);
            Assert.Equal("shared (3)", second.Macro!.Name);
        }

        [Fact]
        public void ImportIgnoresAHostileIdInTheFile()
        {
            string file = Path.Combine(_root, "evil.zeromacro");
            Directory.CreateDirectory(_root);
            File.WriteAllText(file, ZeroStrap.Macros.MacroJson.Serialize(Sample("../../evil")));

            var result = _store.Import(file);

            Assert.True(result.Success);
            Assert.True(MacroValidator.IsValidId(result.Macro!.Id));
            Assert.Single(Directory.GetFiles(Dir, "*.json"));
        }

        [Fact]
        public void ImportClampsAndReportsWarnings()
        {
            var m = Sample("x");
            m.Speed = 50;
            string file = Path.Combine(_root, "fast.zeromacro");
            Directory.CreateDirectory(_root);
            File.WriteAllText(file, ZeroStrap.Macros.MacroJson.Serialize(m));

            var result = _store.Import(file);

            Assert.True(result.Success);
            Assert.Equal(4.0, result.Macro!.Speed);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void ImportOverTwoMegabytesIsRejected()
        {
            string file = Path.Combine(_root, "big.zeromacro");
            Directory.CreateDirectory(_root);
            File.WriteAllText(file, new string(' ', 2 * 1024 * 1024 + 1));

            var result = _store.Import(file);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("2 MB"));
            Assert.False(Directory.Exists(Dir));
        }

        [Fact]
        public void ImportOfAnExactlyTwoMegabyteFileIsNotRejectedForSize()
        {
            var m = Sample("x");
            string json = ZeroStrap.Macros.MacroJson.Serialize(m);
            string padded = json + new string(' ', 2 * 1024 * 1024 - json.Length);
            string file = Path.Combine(_root, "edge.zeromacro");
            Directory.CreateDirectory(_root);
            File.WriteAllText(file, padded);
            Assert.Equal(2 * 1024 * 1024, new FileInfo(file).Length);

            Assert.True(_store.Import(file).Success);
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("")]
        [InlineData("null")]
        [InlineData("[]")]
        [InlineData("{\"actions\":[{\"kind\":\"bogus\"}]}")]
        [InlineData("{\"name\":\"\"}")]
        [InlineData("{\"name\":\"x\",\"actions\":[{\"kind\":\"key\",\"scanCode\":0}]}")]
        [InlineData("{\"name\":\"x\",\"actions\":[{\"kind\":\"key\",\"scanCode\":70000}]}")]
        public void ImportOfGarbageFailsWithoutThrowing(string content)
        {
            string file = Path.Combine(_root, "bad.zeromacro");
            Directory.CreateDirectory(_root);
            File.WriteAllText(file, content);

            var result = _store.Import(file);

            Assert.False(result.Success);
            Assert.NotEmpty(result.Errors);
            Assert.Empty(_store.LoadAll());
        }

        [Fact]
        public void ImportOfAMissingFileFails()
        {
            Assert.False(_store.Import(Path.Combine(_root, "nope.zeromacro")).Success);
        }

        [Fact]
        public void ExportThenImportRoundTrips()
        {
            _store.Save(Sample("a", "Farm"));
            string file = Path.Combine(_root, "out", "farm.zeromacro");

            Assert.True(_store.Export("a", file));
            Assert.False(_store.Export("missing", file + "2"));

            var other = new MacroStore(Path.Combine(_root, "Other"), _logs.Add);
            var imported = other.Import(file);

            Assert.True(imported.Success);
            Assert.Equal("Farm", imported.Macro!.Name);
            Assert.Equal(3, imported.Macro.Actions.Count);
            Assert.Equal(40, imported.Macro.Actions[2].Dx);
        }

        [Fact]
        public void UniqueNameStaysWithinTheLengthLimit()
        {
            string longName = new string('n', 80);

            string unique = MacroStore.UniqueName(longName, new[] { longName });

            Assert.Equal(80, unique.Length);
            Assert.EndsWith(" (2)", unique);
        }

        // ---- settings ----

        private MacroStudioSettingsStore SettingsStore() => new(Path.Combine(Dir, "settings.json"), _logs.Add);

        [Fact]
        public void SettingsDefaultsWhenMissing()
        {
            var s = SettingsStore().Load();

            Assert.Equal("Ctrl+Shift+End", s.EmergencyStop);
            Assert.Equal("F9", s.RecordStopKey);
            Assert.False(s.NoticeAcknowledged);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Ctrl")]
        [InlineData("garbage")]
        public void EmergencyStopFallsBackToTheDefaultWhenEmptyOrInvalid(string value)
        {
            Write("settings.json", $"{{\"emergencyStop\":\"{value}\",\"recordStopKey\":\"{value}\"}}");

            var s = SettingsStore().Load();

            Assert.Equal("Ctrl+Shift+End", s.EmergencyStop);
            Assert.Equal("F9", s.RecordStopKey);
        }

        [Fact]
        public void NullEmergencyStopFallsBackToTheDefault()
        {
            Write("settings.json", "{\"emergencyStop\":null,\"noticeAcknowledged\":true}");

            var s = SettingsStore().Load();

            Assert.Equal("Ctrl+Shift+End", s.EmergencyStop);
            Assert.True(s.NoticeAcknowledged);
        }

        [Fact]
        public void SettingsRoundTripAndAreCanonicalised()
        {
            var store = SettingsStore();
            store.Save(new MacroStudioSettings { EmergencyStop = "alt+shift+escape", RecordStopKey = "f10", NoticeAcknowledged = true });

            var s = store.Load();

            Assert.Equal("Alt+Shift+Escape", s.EmergencyStop);
            Assert.Equal("F10", s.RecordStopKey);
            Assert.True(s.NoticeAcknowledged);
        }

        [Fact]
        public void SavingAnEmptyEmergencyStopWritesTheDefault()
        {
            var store = SettingsStore();
            store.Save(new MacroStudioSettings { EmergencyStop = "" });

            Assert.Contains("Ctrl+Shift+End", File.ReadAllText(store.FilePath));
        }

        [Fact]
        public void CorruptSettingsAreQuarantinedAndDefaultsUsed()
        {
            string path = Write("settings.json", "}{");

            var s = SettingsStore().Load();

            Assert.Equal("Ctrl+Shift+End", s.EmergencyStop);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(Dir, "settings.json.corrupt-*"));
        }
    }
}
