namespace Bloxstrap.FastFlags
{
    public enum CatalogUpdateMode
    {
        Manual,
        Automatic
    }

    public enum CatalogUpdateOutcome
    {
        Updated,
        UpToDate,
        Failed,
        Cancelled,
        AlreadyRunning
    }

    public sealed record CatalogUpdateResult(CatalogUpdateOutcome Outcome, string Message, CatalogMetadata? Metadata = null);

    /// <summary>Stored next to the catalog file. Describes the last *valid* catalog and the last check.</summary>
    public sealed class CatalogMetadata
    {
        public string SourceUrl { get; set; } = "";

        public DateTime? FetchedAtUtc { get; set; }

        public DateTime? LastCheckedUtc { get; set; }

        public string? LastCheckResult { get; set; }

        public string? ETag { get; set; }

        public string? Sha256 { get; set; }

        public int EntryCount { get; set; }

        /// <summary>Short content hash, shown to the user as the catalog version.</summary>
        [JsonIgnore]
        public string Version => Sha256 is { Length: >= 8 } ? Sha256[..8] : "-";
    }

    /// <summary>
    /// Downloads the Fast Flag *name* catalog. Separate from ZeroBootstrap's own updater: it never
    /// touches the executable or the user's flag configuration, only the two catalog files.
    ///
    /// Safety: the response is size-capped, parsed as plain text (nothing is executed), validated by
    /// <see cref="FlagCatalog.CheckAcceptable"/>, and only then swapped in with an atomic file replace.
    /// Any failure keeps the previous valid catalog. Cancellation is honoured up to the swap.
    /// </summary>
    public sealed class FlagCatalogUpdater
    {
        public const string DefaultSourceUrl = "https://raw.githubusercontent.com/MaximumADHD/Roblox-Client-Tracker/roblox/FVariables.txt";

        public const string SourceDescription = "MaximumADHD/Roblox-Client-Tracker (FVariables.txt)";

        public const long MaxDownloadBytes = 8 * 1024 * 1024;

        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Automatic checks never run more often than this, whatever the configured interval.</summary>
        public static readonly TimeSpan MinimumAutoInterval = TimeSpan.FromHours(6);

        private readonly HttpClient _http;
        private readonly string _directory;
        private readonly Func<DateTime> _utcNow;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public string SourceUrl { get; }

        public TimeSpan Timeout { get; set; } = DefaultTimeout;

        public string CatalogPath => Path.Combine(_directory, "FVariables.txt");

        public string MetadataPath => Path.Combine(_directory, "catalog.json");

        public bool IsRunning => _gate.CurrentCount == 0;

        public FlagCatalogUpdater(HttpClient http, string directory, string? sourceUrl = null, Func<DateTime>? utcNow = null)
        {
            _http = http;
            _directory = directory;
            SourceUrl = sourceUrl ?? DefaultSourceUrl;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public static bool IsDue(CatalogMetadata? metadata, TimeSpan interval, DateTime utcNow)
        {
            if (interval < MinimumAutoInterval)
                interval = MinimumAutoInterval;

            return metadata?.LastCheckedUtc is not DateTime last || utcNow - last >= interval;
        }

        public CatalogMetadata? LoadMetadata()
        {
            try
            {
                if (!File.Exists(MetadataPath))
                    return null;

                return JsonSerializer.Deserialize<CatalogMetadata>(File.ReadAllText(MetadataPath));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Loads the local catalog. A missing or corrupt file yields an empty catalog, never an exception.</summary>
        public FlagCatalog LoadCatalog(out string? problem)
        {
            problem = null;

            try
            {
                if (!File.Exists(CatalogPath))
                    return FlagCatalog.Empty;

                var catalog = FlagCatalog.Parse(File.ReadAllText(CatalogPath), out var report);
                problem = FlagCatalog.CheckAcceptable(report);

                return problem is null ? catalog : FlagCatalog.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = ex.Message;
                return FlagCatalog.Empty;
            }
        }

        public async Task<CatalogUpdateResult> UpdateAsync(CancellationToken token = default)
        {
            // one operation at a time: a second request while one runs is rejected, not queued
            if (!await _gate.WaitAsync(0, CancellationToken.None))
                return new(CatalogUpdateOutcome.AlreadyRunning, Strings.FastFlags_CatalogUpdater_AlreadyRunning);

            try
            {
                return await UpdateCoreAsync(token);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<CatalogUpdateResult> UpdateCoreAsync(CancellationToken token)
        {
            var metadata = LoadMetadata() ?? new CatalogMetadata();

            // a damaged local copy must not be kept alive by "304 Not Modified"
            bool haveLocal = metadata.Sha256 is not null && LocalHash() == metadata.Sha256;

            string text;
            string? etag;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(Timeout);

                using var request = new HttpRequestMessage(HttpMethod.Get, SourceUrl);

                if (haveLocal && metadata.ETag is not null && metadata.SourceUrl == SourceUrl)
                    request.Headers.TryAddWithoutValidation("If-None-Match", metadata.ETag);

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                if (response.StatusCode == HttpStatusCode.NotModified)
                    return Record(metadata, CatalogUpdateOutcome.UpToDate, Strings.FastFlags_CatalogUpdater_UpToDate);

                if (!response.IsSuccessStatusCode)
                    return Record(metadata, CatalogUpdateOutcome.Failed, String.Format(Strings.FastFlags_CatalogUpdater_HttpError, (int)response.StatusCode, response.ReasonPhrase));

                if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                    return Record(metadata, CatalogUpdateOutcome.Failed, Strings.FastFlags_CatalogUpdater_TooLargeKept);

                etag = response.Headers.ETag?.ToString();
                text = await ReadCappedAsync(response.Content, timeout.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new(CatalogUpdateOutcome.Cancelled, Strings.FastFlags_CatalogUpdater_Cancelled, metadata);
            }
            catch (OperationCanceledException)
            {
                return Record(metadata, CatalogUpdateOutcome.Failed, String.Format(Strings.FastFlags_CatalogUpdater_TimedOut, Timeout.TotalSeconds));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                return Record(metadata, CatalogUpdateOutcome.Failed, String.Format(Strings.FastFlags_CatalogUpdater_NetworkError, ex.Message));
            }

            var catalog = FlagCatalog.Parse(text, out var report);
            string? problem = FlagCatalog.CheckAcceptable(report);

            if (problem is not null)
                return Record(metadata, CatalogUpdateOutcome.Failed, String.Format(Strings.FastFlags_CatalogUpdater_Invalid, problem));

            string sha = Hash(Encoding.UTF8.GetBytes(text));

            if (haveLocal && sha == metadata.Sha256)
            {
                metadata.ETag = etag;
                return Record(metadata, CatalogUpdateOutcome.UpToDate, Strings.FastFlags_CatalogUpdater_UpToDate);
            }

            // last point where cancelling is harmless
            if (token.IsCancellationRequested)
                return new(CatalogUpdateOutcome.Cancelled, Strings.FastFlags_CatalogUpdater_Cancelled, metadata);

            Directory.CreateDirectory(_directory);

            string temp = CatalogPath + ".tmp";
            File.WriteAllText(temp, text);

            if (File.Exists(CatalogPath))
                File.Replace(temp, CatalogPath, null);
            else
                File.Move(temp, CatalogPath);

            metadata.SourceUrl = SourceUrl;
            metadata.FetchedAtUtc = _utcNow();
            metadata.ETag = etag;
            metadata.Sha256 = sha;
            metadata.EntryCount = catalog.Entries.Count;

            return Record(metadata, CatalogUpdateOutcome.Updated, String.Format(Strings.FastFlags_CatalogUpdater_Updated, catalog.Entries.Count));
        }

        private static string Hash(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

        private string? LocalHash()
        {
            try
            {
                // hash the text the same way it was hashed when downloaded
                return File.Exists(CatalogPath) ? Hash(Encoding.UTF8.GetBytes(File.ReadAllText(CatalogPath))) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken token)
        {
            await using var stream = await content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();

            var chunk = new byte[81920];
            int read;

            while ((read = await stream.ReadAsync(chunk, token)) > 0)
            {
                if (buffer.Length + read > MaxDownloadBytes)
                    throw new InvalidDataException(Strings.FastFlags_CatalogUpdater_TooLarge);

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private CatalogUpdateResult Record(CatalogMetadata metadata, CatalogUpdateOutcome outcome, string message)
        {
            metadata.LastCheckedUtc = _utcNow();
            metadata.LastCheckResult = message;

            if (String.IsNullOrEmpty(metadata.SourceUrl))
                metadata.SourceUrl = SourceUrl;

            try
            {
                Directory.CreateDirectory(_directory);

                string temp = MetadataPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

                if (File.Exists(MetadataPath))
                    File.Replace(temp, MetadataPath, null);
                else
                    File.Move(temp, MetadataPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the catalog itself is already consistent; losing the "last checked" stamp is harmless
                message += String.Format(Strings.FastFlags_CatalogUpdater_MetadataNotSaved, ex.Message);
            }

            return new(outcome, message, metadata);
        }
    }
}
