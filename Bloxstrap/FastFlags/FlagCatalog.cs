namespace Bloxstrap.FastFlags
{
    public sealed record FlagCatalogEntry(string Name, string Origin, FlagValueType Type);

    public sealed record CatalogParseReport(int TotalLines, int ValidEntries, int InvalidLines, int Duplicates)
    {
        public double InvalidRatio => TotalLines == 0 ? 1 : (double)InvalidLines / TotalLines;
    }

    /// <summary>
    /// A list of known Fast Flag names. Data only: names, the client layer they come from and their
    /// value type. It never contains values, offsets or anything executable.
    ///
    /// Text format (one per line), as published by MaximumADHD/Roblox-Client-Tracker FVariables.txt:
    ///   [C++] DFFlagSomething
    ///   [Lua] FIntSomethingElse
    /// </summary>
    public sealed class FlagCatalog
    {
        // a downloaded catalog must look like a real catalog before it may replace the local one
        public const int MinimumEntries = 1000;

        public const double MaximumInvalidRatio = 0.05;

        private static readonly Regex LinePattern = new(@"^\[([A-Za-z+]{1,8})\]\s+([A-Za-z0-9_]+)$", RegexOptions.Compiled);

        public static FlagCatalog Empty { get; } = new(Array.Empty<FlagCatalogEntry>());

        public IReadOnlyList<FlagCatalogEntry> Entries { get; }

        public FlagCatalog(IReadOnlyList<FlagCatalogEntry> entries)
        {
            Entries = entries;
        }

        public static FlagCatalog Parse(string text, out CatalogParseReport report)
        {
            var entries = new List<FlagCatalogEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            int total = 0, invalid = 0, duplicates = 0;

            using var reader = new StringReader(text);

            for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
            {
                line = line.Trim();

                if (line.Length == 0)
                    continue;

                total++;

                var match = LinePattern.Match(line);

                if (!match.Success)
                {
                    invalid++;
                    continue;
                }

                string name = match.Groups[2].Value;

                if (!FastFlagRules.ValidateName(name).IsValid || FastFlagRules.GetValueType(name) is not FlagValueType type)
                {
                    invalid++;
                    continue;
                }

                if (!seen.Add(name))
                {
                    duplicates++;
                    continue;
                }

                entries.Add(new FlagCatalogEntry(name, match.Groups[1].Value, type));
            }

            entries.Sort((a, b) => String.CompareOrdinal(a.Name, b.Name));

            report = new CatalogParseReport(total, entries.Count, invalid, duplicates);

            return new FlagCatalog(entries);
        }

        /// <summary>
        /// Returns null when the report is acceptable, otherwise a human readable reason.
        /// </summary>
        public static string? CheckAcceptable(CatalogParseReport report)
        {
            if (report.ValidEntries < MinimumEntries)
                return $"Catalog only has {report.ValidEntries} valid entries (minimum {MinimumEntries}).";

            if (report.InvalidRatio > MaximumInvalidRatio)
                return $"Catalog has too many malformed lines ({report.InvalidLines} of {report.TotalLines}).";

            return null;
        }
    }
}
