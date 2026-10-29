namespace Bloxstrap.FastFlags.Memory
{
    internal sealed record FlagOffset(string FullName, uint Rva, string Type);

    // Fetches FFlags.hpp from Imtheo's mirror chain and parses it into an
    // {flag-name -> (rva, type)} map. Same source list and acceptance rules
    // Roblox FFlag Manager uses, trimmed to what a bootstrapper needs.
    internal sealed class OffsetLoader
    {
        private static readonly string[] Sources =
        {
            "https://dev.imtheo.lol/Offsets/FFlags.hpp",
            "https://offsets.imtheo.lol/FFlags.hpp",
            "https://offsets.ntgetwritewatch.workers.dev/FFlags.hpp",
            "https://raw.githubusercontent.com/4anti/Roblox-Fastflag-Manager/main/data/FFlags.hpp",
            "https://raw.githubusercontent.com/souloveryall/offsets.hpp/main/Offsets.hpp",
            "https://raw.githubusercontent.com/NtReadVirtualMemory/Roblox-Offsets-Website/main/FFlags.hpp"
        };

        private const uint RvaMin = 0x100000;
        private const uint RvaMax = 0x10000000;
        private const int MinValidFlags = 500;
        private const long MaxBodySize = 5 * 1024 * 1024;

        private static readonly Regex Uintptr =
            new(@"(?:inline\s+constexpr\s+)?uintptr_t\s+([A-Za-z_][A-Za-z0-9_]{0,127})\s*=\s*0x([0-9a-fA-F]{1,8})",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex FlagListNs =
            new(@"namespace\s+FFlagList\s*\{[\s\S]{0,65536}?\}", RegexOptions.Compiled);

        private static readonly Regex ClientVersionRx =
            new(@"ClientVersion\s*=\s*""([^""]+)""", RegexOptions.Compiled);

        public string? SourceId { get; private set; }
        public string? SourceBuildVersion { get; private set; }

        public static string CachePath => Path.Combine(Paths.Base, "FFlags", "offsets_cache.json");

        public async Task<Dictionary<string, FlagOffset>?> LoadAsync()
        {
            const string LOG = "OffsetLoader::LoadAsync";

            foreach (var url in Sources)
            {
                try
                {
                    string? body = await FetchAsync(url);
                    if (body is null || body.Length < 1024)
                        continue;

                    var parsed = Parse(body);
                    if (parsed.Count < MinValidFlags)
                    {
                        App.Logger.WriteLine(LOG, $"{url}: only {parsed.Count} flags, rejecting");
                        continue;
                    }

                    SourceId = url;
                    SourceBuildVersion = ExtractClientVersion(body);
                    WriteCache(parsed);
                    App.Logger.WriteLine(LOG, $"loaded {parsed.Count} flags from {url} (build {SourceBuildVersion ?? "?"})");
                    return parsed;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG, $"{url}: {ex.Message}");
                }
            }

            var cached = LoadCache();
            if (cached is not null)
            {
                App.Logger.WriteLine(LOG, $"loaded {cached.Count} flags from disk cache");
                SourceId = "disk_cache";
                return cached;
            }

            var baseline = LoadBaseline();
            if (baseline is not null)
            {
                App.Logger.WriteLine(LOG, $"loaded {baseline.Count} flags from bundled baseline");
                SourceId = "bundled_baseline";
                return baseline;
            }

            App.Logger.WriteLine(LOG, "all offset sources failed");
            return null;
        }

        private Dictionary<string, FlagOffset>? LoadBaseline()
        {
            try
            {
                var asm = typeof(OffsetLoader).Assembly;
                string resourceName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("FFlags_baseline.hpp", StringComparison.OrdinalIgnoreCase))
                    ?? "";

                if (string.IsNullOrEmpty(resourceName))
                    return null;

                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream is null)
                    return null;

                using var reader = new StreamReader(stream, Encoding.ASCII);
                string body = reader.ReadToEnd();

                var parsed = Parse(body);
                SourceBuildVersion = ExtractClientVersion(body);
                return parsed.Count >= MinValidFlags ? parsed : null;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("OffsetLoader::LoadBaseline", ex.Message);
                return null;
            }
        }

        private static async Task<string?> FetchAsync(string url)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var req = await App.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!req.IsSuccessStatusCode)
                return null;

            using var stream = await req.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream, Encoding.ASCII);

            var sb = new StringBuilder(1 << 20);
            var buf = new char[131072];
            int n;
            while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                sb.Append(buf, 0, n);
                if (sb.Length > MaxBodySize)
                    return null;
            }
            return sb.ToString();
        }

        private static Dictionary<string, FlagOffset> Parse(string body)
        {
            // FFlagList namespace holds struct-member offsets, not flag RVAs. Strip
            // it so its uintptr entries don't poison the real flag map.
            string stripped = FlagListNs.Replace(body, "");

            var map = new Dictionary<string, FlagOffset>(16384, StringComparer.Ordinal);
            foreach (Match m in Uintptr.Matches(stripped))
            {
                string name = m.Groups[1].Value;
                if (!uint.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rva))
                    continue;
                if (rva < RvaMin || rva >= RvaMax)
                    continue;

                map[name] = new FlagOffset(name, rva, InferType(name));
            }
            return map;
        }

        private static string ExtractClientVersion(string body)
        {
            var m = ClientVersionRx.Match(body);
            return m.Success ? m.Groups[1].Value : "";
        }

        // Prefix-driven typing matches Roblox's own flag naming convention; a dumped
        // flag's prefix fixes its underlying storage type.
        private static string InferType(string name)
        {
            if (name.StartsWith("FFlag") || name.StartsWith("DFFlag") || name.StartsWith("SFFlag"))
                return "bool";
            if (name.StartsWith("FInt") || name.StartsWith("DFInt") || name.StartsWith("SFInt"))
                return "int";
            if (name.StartsWith("FLog") || name.StartsWith("DFLog"))
                return "int";
            if (name.StartsWith("FString") || name.StartsWith("DFString") || name.StartsWith("SFString"))
                return "string";
            if (name.StartsWith("FFloat") || name.StartsWith("DFFloat"))
                return "float";
            return "unknown";
        }

        private void WriteCache(Dictionary<string, FlagOffset> flags)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                var payload = new Dictionary<string, object?>
                {
                    ["schema_version"] = 1,
                    ["source"] = SourceId,
                    ["source_build_version"] = SourceBuildVersion,
                    ["generated_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["flags"] = flags.ToDictionary(
                        kv => kv.Key,
                        kv => (object)new Dictionary<string, object>
                        {
                            ["rva"] = $"0x{kv.Value.Rva:X}",
                            ["full_name"] = kv.Value.FullName,
                            ["type"] = kv.Value.Type
                        })
                };
                File.WriteAllText(CachePath, JsonSerializer.Serialize(payload));
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("OffsetLoader::WriteCache", ex.Message);
            }
        }

        private Dictionary<string, FlagOffset>? LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath))
                    return null;

                using var fs = File.OpenRead(CachePath);
                using var doc = JsonDocument.Parse(fs);
                var root = doc.RootElement;
                if (!root.TryGetProperty("flags", out var flagsEl))
                    return null;

                var map = new Dictionary<string, FlagOffset>(16384, StringComparer.Ordinal);
                foreach (var entry in flagsEl.EnumerateObject())
                {
                    string rvaStr = entry.Value.GetProperty("rva").GetString() ?? "";
                    if (!rvaStr.StartsWith("0x"))
                        continue;
                    if (!uint.TryParse(rvaStr.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rva))
                        continue;
                    if (rva < RvaMin || rva >= RvaMax)
                        continue;

                    string full = entry.Value.GetProperty("full_name").GetString() ?? entry.Name;
                    string type = entry.Value.GetProperty("type").GetString() ?? "unknown";
                    map[entry.Name] = new FlagOffset(full, rva, type);
                }
                if (root.TryGetProperty("source_build_version", out var sbv))
                    SourceBuildVersion = sbv.GetString();

                return map.Count >= MinValidFlags ? map : null;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("OffsetLoader::LoadCache", ex.Message);
                return null;
            }
        }
    }
}
