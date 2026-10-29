namespace Bloxstrap.FastFlags.Memory
{
    // Attaches to a running Roblox PID, resolves live flag addresses via
    // OffsetLoader (base + RVA), and writes the user's enabled flag values
    // directly into process memory. Mirrors how Roblox FFlag Manager asserts
    // runtime flags beyond what ClientAppSettings.json can set at boot.
    internal sealed class MemoryFlagInjector
    {
        private const string LOG = "MemoryFlagInjector";

        private readonly int _pid;
        private readonly Dictionary<string, string> _flags;
        private readonly string? _expectedBuildVersion;
        private readonly CancellationTokenSource _cts = new();

        public MemoryFlagInjector(int pid, Dictionary<string, string> flags, string? expectedBuildVersion = null)
        {
            _pid = pid;
            _flags = flags;
            _expectedBuildVersion = expectedBuildVersion;
        }

        /// <summary>
        /// Runs the injection on a background thread. Returns immediately.
        /// </summary>
        /// <param name="watchdog">
        /// When true, keep re-asserting flag values every few seconds until the
        /// Roblox process exits — Roblox's runtime occasionally resets flags
        /// after boot, so a one-shot write isn't enough for all of them.
        /// </param>
        public void StartBackground(bool watchdog)
        {
            if (_flags.Count == 0)
                return;

            new Thread(() => Run(watchdog))
            {
                IsBackground = true,
                Name = "FFlagMemoryInjector"
            }.Start();
        }

        public void Stop() => _cts.Cancel();

        private void Run(bool watchdog)
        {
            try
            {
                using var proc = new ProcessMemoryWriter(_pid);
                if (!proc.Attach(TimeSpan.FromSeconds(60)))
                    return;

                var loader = new OffsetLoader();
                var offsets = loader.LoadAsync().GetAwaiter().GetResult();
                if (offsets is null)
                {
                    App.Logger.WriteLine(LOG, "no offsets available — skipping memory inject");
                    return;
                }

                // Build-version guard. The RVAs in the dump point to specific
                // bytes in a specific RobloxPlayerBeta.exe build. If the dump
                // targets a different build than the running Roblox, every
                // write lands on random addresses — most hit .rdata and get
                // rejected (STATUS_SECTION_PROTECTION), the rest silently
                // corrupt unrelated data. Refuse to write when builds differ.
                if (!string.IsNullOrEmpty(_expectedBuildVersion)
                    && !string.IsNullOrEmpty(loader.SourceBuildVersion)
                    && !BuildVersionsMatch(_expectedBuildVersion, loader.SourceBuildVersion))
                {
                    App.Logger.WriteLine(LOG,
                        $"build mismatch: offsets target '{loader.SourceBuildVersion}' but Roblox is '{_expectedBuildVersion}' — refusing to write (would corrupt memory)");
                    return;
                }

                int ok = 0, miss = 0, fail = 0;
                foreach (var (name, value) in _flags)
                {
                    if (!TryLookup(offsets, name, out var fo))
                    {
                        miss++;
                        continue;
                    }
                    ulong addr = proc.BaseAddress + fo.Rva;
                    if (WriteTyped(proc, fo, addr, value))
                        ok++;
                    else
                        fail++;
                }
                App.Logger.WriteLine(LOG,
                    $"first pass: {ok} written, {miss} not in offset map, {fail} failed (of {_flags.Count})");

                if (!watchdog)
                    return;

                Process? target;
                try { target = Process.GetProcessById(_pid); }
                catch { return; }

                while (!_cts.IsCancellationRequested && !target.HasExited)
                {
                    Thread.Sleep(5000);
                    int reAssert = 0;
                    foreach (var (name, value) in _flags)
                    {
                        if (!TryLookup(offsets, name, out var fo))
                            continue;
                        ulong addr = proc.BaseAddress + fo.Rva;
                        if (!NeedsRewrite(proc, fo, addr, value))
                            continue;
                        if (WriteTyped(proc, fo, addr, value))
                            reAssert++;
                    }
                    if (reAssert > 0)
                        App.Logger.WriteLine(LOG, $"watchdog re-asserted {reAssert} flags");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG, $"fatal: {ex}");
            }
        }

        private static bool NeedsRewrite(ProcessMemoryWriter proc, FlagOffset fo, ulong addr, string desired)
        {
            switch (fo.Type)
            {
                case "bool":
                {
                    Span<byte> b = stackalloc byte[1];
                    if (!proc.ReadBytes(addr, b)) return true;
                    return (b[0] != 0) != IsTrueish(desired);
                }
                case "int":
                case "log":
                {
                    Span<byte> b = stackalloc byte[4];
                    if (!proc.ReadBytes(addr, b)) return true;
                    if (!int.TryParse(desired, NumberStyles.Integer, CultureInfo.InvariantCulture, out int want))
                        return false;
                    return BitConverter.ToInt32(b) != want;
                }
                case "float":
                {
                    Span<byte> b = stackalloc byte[4];
                    if (!proc.ReadBytes(addr, b)) return true;
                    if (!float.TryParse(desired, NumberStyles.Float, CultureInfo.InvariantCulture, out float want))
                        return false;
                    return Math.Abs(BitConverter.ToSingle(b) - want) > 0.0001f;
                }
                default:
                    return false;
            }
        }

        private static bool WriteTyped(ProcessMemoryWriter proc, FlagOffset fo, ulong addr, string value)
        {
            switch (fo.Type)
            {
                case "bool":
                {
                    byte b = (byte)(IsTrueish(value) ? 1 : 0);
                    return proc.WriteBytes(addr, new[] { b });
                }
                case "int":
                case "log":
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                        return false;
                    Span<byte> buf = stackalloc byte[4];
                    BitConverter.TryWriteBytes(buf, i);
                    return proc.WriteBytes(addr, buf);
                }
                case "float":
                {
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                        return false;
                    Span<byte> buf = stackalloc byte[4];
                    BitConverter.TryWriteBytes(buf, f);
                    return proc.WriteBytes(addr, buf);
                }
                case "string":
                    return WriteStdString(proc, addr, value);
                default:
                    return false;
            }
        }

        // MSVC std::string on x64 is 32 bytes:
        //   +0  union { char sso[16]; char* heap; }   // active via capacity
        //   +16 size_t  size
        //   +24 size_t  capacity                       // 15 means SSO active
        //
        // Writing an SSO payload is safe. A payload > 15 bytes requires an
        // allocation inside the target process (and the current heap pointer
        // to be freed if one is live) — out of scope for a bootstrapper. Keep
        // long strings on the ClientAppSettings.json path.
        private static bool WriteStdString(ProcessMemoryWriter proc, ulong addr, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > 15)
            {
                App.Logger.WriteLine(LOG, $"skip string >15 bytes at 0x{addr:X}");
                return false;
            }

            Span<byte> buf = stackalloc byte[32];
            bytes.CopyTo(buf);
            buf[bytes.Length] = 0;
            BitConverter.TryWriteBytes(buf.Slice(16, 8), (long)bytes.Length);
            BitConverter.TryWriteBytes(buf.Slice(24, 8), 15L);
            return proc.WriteBytes(addr, buf);
        }

        private static bool IsTrueish(string s)
        {
            string t = s.Trim().ToLowerInvariant();
            return t == "true" || t == "1" || t == "yes" || t == "on";
        }

        // Imtheo's hpp dumps bare flag identifiers inside `namespace FFlags {}`,
        // without the user-facing prefix. User's ClientAppSettings uses the
        // full form ("FFlagHandleAltEnterFullscreenManually"). We strip the
        // prefix for the lookup and reuse it to type the value.
        private static readonly (string Prefix, string Type)[] Prefixes =
        {
            ("DFFlag", "bool"), ("SFFlag", "bool"), ("FFlag", "bool"),
            ("DFInt",  "int"),  ("SFInt",  "int"),  ("FInt",  "int"),
            ("DFLog",  "int"),  ("FLog",   "int"),
            ("DFFloat","float"),("FFloat", "float"),
            ("DFString","string"),("SFString","string"),("FString","string"),
        };

        private static bool TryLookup(
            Dictionary<string, FlagOffset> offsets,
            string userFlagName,
            out FlagOffset fo)
        {
            if (offsets.TryGetValue(userFlagName, out fo!))
                return true;

            foreach (var (prefix, type) in Prefixes)
            {
                if (!userFlagName.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                string bare = userFlagName.Substring(prefix.Length);
                if (!offsets.TryGetValue(bare, out var raw))
                    continue;
                fo = raw with { Type = type };
                return true;
            }

            fo = null!;
            return false;
        }

        // Bloxstrap tracks the version as a bare GUID ("9ac8595712b447a7"),
        // the hpp dumps use the full form ("version-9ac8595712b447a7").
        // Normalize both sides to the GUID tail for comparison.
        private static bool BuildVersionsMatch(string expected, string actual)
        {
            static string Norm(string v) =>
                v.StartsWith("version-", StringComparison.OrdinalIgnoreCase) ? v.Substring(8) : v;
            return string.Equals(Norm(expected), Norm(actual), StringComparison.OrdinalIgnoreCase);
        }
    }
}
