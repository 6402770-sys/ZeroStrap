namespace ZeroStrap.FastFlags.Memory
{
    // Cheap probe to find what Roblox build Imtheo's offset dump currently
    // targets. Pulls just enough bytes of the hpp to extract the embedded
    // `ClientVersion = "version-xxx"` line — the dump has it near the top so
    // a ranged ~16 KB read is enough. Returns the full "version-<guid>" form
    // the way ZeroStrap's deployment URLs expect it (Roblox's CDN only serves
    // paths with the "version-" prefix; stripping it 403s).
    internal static class OffsetBuildProbe
    {
        private static readonly string[] Sources =
        {
            "https://offsets.imtheo.lol/FFlags.hpp",
            "https://dev.imtheo.lol/Offsets/FFlags.hpp",
            "https://offsets.ntgetwritewatch.workers.dev/FFlags.hpp",
            "https://raw.githubusercontent.com/4anti/Roblox-Fastflag-Manager/main/data/FFlags.hpp"
        };

        private static readonly Regex ClientVersionRx =
            new(@"ClientVersion\s*=\s*""version-([0-9a-fA-F]+)""", RegexOptions.Compiled);

        private static readonly Regex HeaderVersionRx =
            new(@"Roblox Version\s*:\s*version-([0-9a-fA-F]+)", RegexOptions.Compiled);

        /// <summary>
        /// Returns the full version string ("version-xxxxx") of the Roblox
        /// build the current Imtheo dump targets, or null if every source
        /// fails. The "version-" prefix is required by Roblox's CDN URLs.
        /// </summary>
        public static async Task<string?> ProbeAsync(CancellationToken token = default)
        {
            const string LOG = "OffsetBuildProbe::ProbeAsync";

            foreach (var url in Sources)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(TimeSpan.FromSeconds(8));

                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    // Both github raw and the imtheo cdn honour Range headers.
                    // 16 KB is enough: ClientVersion lives in the first ~1 KB.
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 16 * 1024);

                    using var resp = await App.HttpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                    if (!resp.IsSuccessStatusCode)
                        continue;

                    string head = await resp.Content.ReadAsStringAsync(cts.Token);

                    var m = ClientVersionRx.Match(head);
                    if (!m.Success)
                        m = HeaderVersionRx.Match(head);

                    if (m.Success)
                    {
                        string full = "version-" + m.Groups[1].Value.ToLowerInvariant();
                        App.Logger.WriteLine(LOG, $"{url} → {full}");
                        return full;
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG, $"{url}: {ex.Message}");
                }
            }

            App.Logger.WriteLine(LOG, "no source returned a build version");
            return null;
        }
    }
}
