using System.IO;
using System.Security.Cryptography;
using System.Text;

using Xunit;

using ZeroStrap.FastFlagCollections;
using ZeroStrap.FastFlags;

namespace ZeroStrap.Tests
{
    public class CollectionPayloadTests
    {
        [Fact]
        public void Parse_ValidObject_ReturnsEntriesInOrder()
        {
            var result = CollectionPayload.Parse("{ \"B\": \"2\", \"A\": \"1\", \"C\": \"3\" }");

            Assert.True(result.Success);
            Assert.Equal(new[] { ("B", "2"), ("A", "1"), ("C", "3") }, result.Entries);
        }

        [Fact]
        public void Parse_NumbersKeepRawText_BooleansAreCapitalised()
        {
            var result = CollectionPayload.Parse("{ \"S\": \"666\", \"N\": 666, \"F\": 1.50, \"E\": 1e3, \"T\": true, \"X\": false }");

            Assert.True(result.Success);
            Assert.Equal(new[] { ("S", "666"), ("N", "666"), ("F", "1.50"), ("E", "1e3"), ("T", "True"), ("X", "False") }, result.Entries);
        }

        [Fact]
        public void Parse_StringValuesAreKeptAsIs()
        {
            var result = CollectionPayload.Parse("{ \"A\": \" spaced \", \"B\": \"\", \"C\": \"True\", \"D\": \"a\\nb\" }");

            Assert.True(result.Success);
            Assert.Equal(new[] { ("A", " spaced "), ("B", ""), ("C", "True"), ("D", "a\nb") }, result.Entries);
        }

        [Theory]
        [InlineData("[1, 2]")]
        [InlineData("\"text\"")]
        [InlineData("42")]
        public void Parse_NonObjectRoot_Fails(string json)
        {
            var result = CollectionPayload.Parse(json);

            Assert.False(result.Success);
            Assert.Empty(result.Entries);
        }

        [Theory]
        [InlineData("{ \"Flag\": { \"a\": 1 } }")]
        [InlineData("{ \"Flag\": null }")]
        [InlineData("{ \"Flag\": [1] }")]
        public void Parse_UnsupportedValue_NamesTheKey(string json)
        {
            var result = CollectionPayload.Parse(json);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("'Flag'"));
        }

        [Fact]
        public void Parse_DuplicateKey_Fails()
        {
            var result = CollectionPayload.Parse("{ \"A\": \"1\", \"A\": \"2\" }");

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("'A'") && e.Contains("more than once"));
        }

        [Theory]
        [InlineData("{ \"\": \"1\" }")]
        [InlineData("{ \"   \": \"1\" }")]
        public void Parse_EmptyKey_Fails(string json)
        {
            Assert.False(CollectionPayload.Parse(json).Success);
        }

        [Fact]
        public void Parse_KeyLength_LimitIs200()
        {
            Assert.True(CollectionPayload.Parse($"{{ \"{new string('a', 200)}\": \"1\" }}").Success);
            Assert.False(CollectionPayload.Parse($"{{ \"{new string('a', 201)}\": \"1\" }}").Success);
        }

        [Fact]
        public void Parse_EntryLimit_Is5000()
        {
            static string Build(int count) => "{" + String.Join(",", Enumerable.Range(0, count).Select(i => $"\"F{i}\":\"1\"")) + "}";

            Assert.True(CollectionPayload.Parse(Build(5000)).Success);

            var over = CollectionPayload.Parse(Build(5001));
            Assert.False(over.Success);
            Assert.Empty(over.Entries);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("{ \"A\": }")]
        [InlineData("{ \"A\": \"1\", }")]
        [InlineData("not json")]
        [InlineData("{}")]
        public void Parse_InvalidOrEmpty_Fails(string json)
        {
            Assert.False(CollectionPayload.Parse(json).Success);
        }

        [Fact]
        public void ReadFile_KeepsOriginalTextVerbatim()
        {
            string path = Path.Combine(Path.GetTempPath(), $"zs-collection-{Guid.NewGuid():N}.json");
            string text = "{\r\n    \"A\":   \"1\",\r\n\t\"B\": 2\r\n}\r\n";

            try
            {
                File.WriteAllText(path, text);
                var result = CollectionPayload.ReadFile(path);

                Assert.True(result.Success);
                Assert.Equal(text, result.OriginalText);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ReadFile_OverOneMegabyte_Fails()
        {
            string path = Path.Combine(Path.GetTempPath(), $"zs-collection-{Guid.NewGuid():N}.json");

            try
            {
                File.WriteAllText(path, "{ \"A\": \"" + new string('x', 1024 * 1024) + "\" }");
                var result = CollectionPayload.ReadFile(path);

                Assert.False(result.Success);
                Assert.Contains("1 MB", result.Errors[0]);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    public class CollectionResolverTests
    {
        private static readonly IReadOnlyList<CollectionParameter> Parameters = new[] { ZeroCollectionCatalog.RuntimeConcurrency };

        private static IReadOnlyList<(string Key, string Value)> Template() => new[]
        {
            ("DFIntRuntimeConcurrency", "Your Logical Processors - 1"),
            ("DFIntOther", "5")
        };

        [Fact]
        public void Resolve_16Processors_Gives15()
        {
            var resolved = CollectionResolver.Resolve(Template(), Parameters, 16);

            Assert.Equal(new[] { ("DFIntRuntimeConcurrency", "15"), ("DFIntOther", "5") }, resolved);
        }

        [Fact]
        public void Resolve_DoesNotMutateTheTemplate()
        {
            var template = Template();

            CollectionResolver.Resolve(template, Parameters, 8);

            Assert.Equal("Your Logical Processors - 1", template[0].Value);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(1)]
        [InlineData(0)]
        [InlineData(-4)]
        public void Resolve_MissingOrTooFewProcessors_Throws(int? processors)
        {
            Assert.Throws<CollectionResolveException>(() => CollectionResolver.Resolve(Template(), Parameters, processors));
        }

        [Fact]
        public void Resolve_NoParameters_NeedsNoProcessorCount()
        {
            var resolved = CollectionResolver.Resolve(new[] { ("A", "1") }, Array.Empty<CollectionParameter>(), null);

            Assert.Equal(new[] { ("A", "1") }, resolved);
        }

        [Fact]
        public void Resolve_PlaceholderUnderAnotherKey_Throws()
        {
            var entries = new[] { ("DFIntRuntimeConcurrency", "Your Logical Processors - 1"), ("Other", "Your Logical Processors - 1") };

            Assert.Throws<CollectionResolveException>(() => CollectionResolver.Resolve(entries, Parameters, 8));
        }

        [Fact]
        public void Resolve_OnlyReplacesExactPlaceholder()
        {
            var entries = new[] { ("DFIntRuntimeConcurrency", "7") };

            Assert.Equal(entries, CollectionResolver.Resolve(entries, Parameters, 16));
        }
    }

    public class CollectionApplierTests
    {
        private static DictionaryFlagStore Store(params (string, string)[] flags)
        {
            var store = new DictionaryFlagStore();

            foreach (var (key, value) in flags)
                store.Set(key, value);

            return store;
        }

        [Fact]
        public void Replace_RemovesExtraKeys_SetsExactValues()
        {
            var store = Store(("Old1", "1"), ("Keep", "old"), ("Old2", "2"));

            CollectionApplier.Replace(store, new[] { ("Keep", "new"), ("Added", "True") });

            Assert.Equal(2, store.Flags.Count);
            Assert.Equal("new", store.Flags["Keep"]);
            Assert.Equal("True", store.Flags["Added"]);
        }

        [Fact]
        public void Replace_ReturnsTheExactPreApplySnapshot()
        {
            var store = Store(("Old1", "1"), ("Keep", "old"));

            var snapshot = CollectionApplier.Replace(store, new[] { ("Keep", "new") });

            Assert.Equal(new Dictionary<string, string> { ["Old1"] = "1", ["Keep"] = "old" }, snapshot);
        }

        [Fact]
        public void Restore_ReturnsToTheSnapshot()
        {
            var store = Store(("Old1", "1"), ("Keep", "old"));
            var before = new Dictionary<string, string>(store.Flags);

            var snapshot = CollectionApplier.Replace(store, new[] { ("Keep", "new"), ("Added", "x") });
            CollectionApplier.Restore(store, snapshot);

            Assert.Equal(before, store.Flags);
        }

        [Fact]
        public void Replace_PlaceholderValue_IsRefusedWithoutTouchingTheStore()
        {
            var store = Store(("Old", "1"));

            Assert.Throws<CollectionResolveException>(() =>
                CollectionApplier.Replace(store, new[] { ("A", "1"), ("DFIntRuntimeConcurrency", "Your Logical Processors - 1") }));

            Assert.Equal(new Dictionary<string, string> { ["Old"] = "1" }, store.Flags);
        }

        [Fact]
        public void Replace_ToEmptyCollection_ClearsEverything()
        {
            var store = Store(("A", "1"));

            CollectionApplier.Replace(store, Array.Empty<(string, string)>());

            Assert.Empty(store.Flags);
        }
    }

    public class OfficialCollectionTests
    {
        private static byte[] Bytes(OfficialCollection collection)
        {
            using var stream = typeof(ZeroCollectionCatalog).Assembly.GetManifestResourceStream(collection.ResourceName);
            Assert.NotNull(stream);
            using var memory = new MemoryStream();
            stream!.CopyTo(memory);
            return memory.ToArray();
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        [Fact]
        public void PureFps_IsEmbeddedUnmodified_With58Flags()
        {
            var collection = ZeroCollectionCatalog.Find(ZeroCollectionCatalog.PureFpsId)!;

            Assert.Equal("e11ab2cdabba093ab75921af99a4ab65acbfb340943356bf8dcf9e67ef1dd629", Hash(Bytes(collection)));
            Assert.Equal(58, collection.LoadTemplate().Count);
        }

        [Fact]
        public void LowPing_IsEmbeddedUnmodified_With45Flags()
        {
            var collection = ZeroCollectionCatalog.Find(ZeroCollectionCatalog.LowPingId)!;

            Assert.Equal("fc3589b853caff7d9bd08c418794559fdaf65559e00a91d16f148ba9c9ef72f2", Hash(Bytes(collection)));
            Assert.Equal(45, collection.LoadTemplate().Count);
        }

        [Fact]
        public void PureFps_TemplateKeepsPlaceholder_AfterResolve()
        {
            var collection = ZeroCollectionCatalog.Find(ZeroCollectionCatalog.PureFpsId)!;
            var template = collection.LoadTemplate();

            var resolved = CollectionResolver.Resolve(template, collection.Parameters, 12);

            Assert.Equal("11", resolved.Single(x => x.Key == "DFIntRuntimeConcurrency").Value);
            Assert.Equal("Your Logical Processors - 1", template.Single(x => x.Key == "DFIntRuntimeConcurrency").Value);
            Assert.Equal("Your Logical Processors - 1", collection.LoadTemplate().Single(x => x.Key == "DFIntRuntimeConcurrency").Value);
            Assert.DoesNotContain(resolved, x => ZeroCollectionCatalog.IsPlaceholder(x.Value));
        }

        [Fact]
        public void PureFps_AppliedToStore_NeverWritesThePlaceholder()
        {
            var collection = ZeroCollectionCatalog.Find(ZeroCollectionCatalog.PureFpsId)!;
            var store = new DictionaryFlagStore();
            store.Set("Unrelated", "1");

            CollectionApplier.Replace(store, CollectionResolver.Resolve(collection.LoadTemplate(), collection.Parameters, 16));

            Assert.Equal(58, store.Flags.Count);
            Assert.Equal("15", store.Flags["DFIntRuntimeConcurrency"]);
            Assert.DoesNotContain(store.Flags.Values, ZeroCollectionCatalog.IsPlaceholder);
        }

        [Fact]
        public void LowPing_NeedsNoProcessorCount()
        {
            var collection = ZeroCollectionCatalog.Find(ZeroCollectionCatalog.LowPingId)!;

            Assert.False(collection.NeedsLogicalProcessors);
            Assert.True(ZeroCollectionCatalog.Find(ZeroCollectionCatalog.PureFpsId)!.NeedsLogicalProcessors);
        }
    }

    public class CollectionStateTests
    {
        [Fact]
        public void Hash_IsOrderIndependent()
        {
            var a = CollectionStatusEvaluator.ComputeHash(new[] { ("A", "1"), ("B", "2"), ("C", "3") });
            var b = CollectionStatusEvaluator.ComputeHash(new[] { ("C", "3"), ("A", "1"), ("B", "2") });

            Assert.Equal(a, b);
            Assert.Equal(64, a.Length);
        }

        [Fact]
        public void Hash_ChangesWithAValue()
        {
            Assert.NotEqual(
                CollectionStatusEvaluator.ComputeHash(new[] { ("A", "1") }),
                CollectionStatusEvaluator.ComputeHash(new[] { ("A", "2") }));
        }

        [Fact]
        public void Hash_MatchesTheDocumentedFormat()
        {
            string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("A=1\nB=2\n"))).ToLowerInvariant();

            Assert.Equal(expected, CollectionStatusEvaluator.ComputeHash(new[] { ("B", "2"), ("A", "1") }));
        }

        [Fact]
        public void Status_OnlyTheLastAppliedCollectionCanBeAppliedOrChanged()
        {
            var flags = new Dictionary<string, string> { ["A"] = "1" };
            var last = new LastAppliedCollection { CollectionId = "x", PayloadHash = CollectionStatusEvaluator.ComputeHash(flags) };

            Assert.Equal(CollectionStatus.Applied, CollectionStatusEvaluator.Evaluate(last, "x", flags));
            Assert.Equal(CollectionStatus.NotApplied, CollectionStatusEvaluator.Evaluate(last, "y", flags));
            Assert.Equal(CollectionStatus.NotApplied, CollectionStatusEvaluator.Evaluate(null, "x", flags));

            flags["A"] = "2";
            Assert.Equal(CollectionStatus.ChangedSinceApplied, CollectionStatusEvaluator.Evaluate(last, "x", flags));
            Assert.Equal(CollectionStatus.NotApplied, CollectionStatusEvaluator.Evaluate(last, "y", flags));
        }

        [Fact]
        public void Naming_ValidatesEmptyLongAndDuplicate()
        {
            var existing = new[] { "Pure FPS", "Mine" };

            Assert.NotNull(CollectionNaming.Validate("  ", existing));
            Assert.NotNull(CollectionNaming.Validate(new string('a', 61), existing));
            Assert.NotNull(CollectionNaming.Validate("mine", existing));
            Assert.Null(CollectionNaming.Validate(new string('a', 60), existing));
            Assert.Null(CollectionNaming.Validate("Other", existing));
        }

        [Fact]
        public void Naming_UniqueNumbersATakenName()
        {
            Assert.Equal("Mine (2)", CollectionNaming.Unique("Mine", new[] { "Mine" }));
            Assert.Equal("Mine (3)", CollectionNaming.Unique("Mine", new[] { "Mine", "Mine (2)" }));
            Assert.True(CollectionNaming.Unique(new string('a', 100), Array.Empty<string>()).Length <= 60);
        }

        [Fact]
        public void PresetMigration_ConvertsPresetsAndSkipsEmptyOnes()
        {
            var presets = new[]
            {
                new FlagPreset { Name = "Low", Flags = new Dictionary<string, string> { ["A"] = "1", ["B"] = "True" } },
                new FlagPreset { Name = "Empty" },
                new FlagPreset { Name = "Low", Flags = new Dictionary<string, string> { ["C"] = "3" } }
            };

            var (collections, skipped) = PresetMigration.FromPresets(presets, new[] { "Pure FPS" }, DateTime.Now);

            Assert.Equal(1, skipped);
            Assert.Equal(new[] { "Low", "Low (2)" }, collections.Select(x => x.Name));

            var parsed = CollectionPayload.Parse(collections[0].PayloadText);
            Assert.True(parsed.Success);
            Assert.Equal(new[] { ("A", "1"), ("B", "True") }, parsed.Entries);
        }

        [Theory]
        [InlineData(16, 16, 16, true)]
        [InlineData(16, 12, null, false)]
        [InlineData(0, 0, null, false)]
        public void Detector_OnlyTrustsAgreeingCounts(int environment, int native, int? expectedCount, bool expectedReliable)
        {
            var (count, reliable) = LogicalProcessorDetector.Decide(environment, native);

            Assert.Equal(expectedCount, count);
            Assert.Equal(expectedReliable, reliable);
        }
    }
}
