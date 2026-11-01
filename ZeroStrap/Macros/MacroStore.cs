using ZeroStrap.Mods;

namespace ZeroStrap.Macros
{
    /// <summary>Outcome of <see cref="MacroStore.Import"/>. <see cref="Macro"/> is the saved copy on success.</summary>
    public sealed class MacroImportResult
    {
        public Macro? Macro { get; init; }
        public List<string> Errors { get; init; } = new();
        public List<string> Warnings { get; init; } = new();

        public bool Success => Macro is not null;
    }

    /// <summary>
    /// Macros\&lt;id&gt;.json files. Everything read goes through <see cref="MacroValidator"/>; a file that cannot be read or
    /// fails validation is moved aside (never silently overwritten) and skipped.
    /// </summary>
    public sealed class MacroStore
    {
        public const string MacroExtension = ".json";
        public const string ShareExtension = ".zeromacro";
        public const long MaxFileBytes = 2 * 1024 * 1024;

        private readonly string _directory;
        private readonly Action<string> _log;

        /// <summary>Test seam: throwing from here simulates a failure just before a macro file is swapped in.</summary>
        internal Action? FaultBeforeCommit { get; set; }

        public string Directory => _directory;

        public MacroStore(string directory, Action<string>? log = null)
        {
            _directory = directory;
            _log = log ?? (_ => { });
        }

        private string PathFor(string id) => Path.Combine(_directory, id + MacroExtension);

        /// <summary>Loads every valid macro, ordered by name. Unreadable or invalid files are quarantined; mismatched file names are skipped.</summary>
        public List<Macro> LoadAll()
        {
            var macros = new List<Macro>();

            if (!System.IO.Directory.Exists(_directory))
                return macros;

            foreach (string file in System.IO.Directory.EnumerateFiles(_directory, "*" + MacroExtension))
            {
                if (!string.Equals(Path.GetExtension(file), MacroExtension, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(file), MacroStudioSettingsStore.FileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var macro = TryLoadFile(file);

                if (macro is not null)
                    macros.Add(macro);
            }

            return macros
                .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(m => m.Id, StringComparer.Ordinal)
                .ToList();
        }

        public Macro? Load(string id)
        {
            if (!MacroValidator.IsValidId(id) || !File.Exists(PathFor(id)))
                return null;

            return TryLoadFile(PathFor(id));
        }

        private Macro? TryLoadFile(string file)
        {
            string name = Path.GetFileName(file);

            try
            {
                var parsed = MacroJson.Deserialize<Macro>(ReadBounded(file)) ?? throw new JsonException("null document");

                // Written by a newer ZeroStrap: left untouched so it is not destroyed by an older version.
                if (parsed.SchemaVersion > MacroValidator.SchemaVersion)
                {
                    _log($"Skipped {name}: it uses schema version {parsed.SchemaVersion}, which this version of ZeroStrap does not understand");
                    return null;
                }

                var result = MacroValidator.Process(parsed);

                if (!result.IsValid)
                    throw new InvalidDataException(string.Join("; ", result.Errors.Take(3)));

                if (!string.Equals(Path.GetFileNameWithoutExtension(file), result.Macro.Id, StringComparison.Ordinal))
                {
                    _log($"Skipped {name}: file name does not match macro id '{result.Macro.Id}'");
                    return null;
                }

                foreach (string warning in result.Warnings.Take(3))
                    _log($"{name}: {warning}");

                return result.Macro;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                _log($"{name} is not a valid macro ({ex.Message})");
                ModFileUtil.QuarantineCorrupt(file, _log);
                return null;
            }
            catch (Exception ex)
            {
                // A sharing violation (antivirus, OneDrive) or similar is transient: the file may be perfectly good, so it stays put.
                _log($"Could not read {name} ({ex.Message}); leaving it in place");
                return null;
            }
        }

        /// <summary>Reads the file as text without ever holding more than <see cref="MaxFileBytes"/> + 1 bytes: a huge file is refused while reading, not after loading it.</summary>
        /// <exception cref="InvalidDataException">The file is larger than 2 MB.</exception>
        private static string ReadBounded(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;

            while (true)
            {
                int wanted = (int)Math.Min(chunk.Length, MaxFileBytes + 1 - total);
                int read = stream.Read(chunk, 0, wanted);

                if (read <= 0)
                    break;

                total += read;

                if (total > MaxFileBytes)
                    throw new InvalidDataException("File is larger than 2 MB");

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        /// <summary>Normalises, validates and atomically writes the macro. Returns the normalised copy that was saved.</summary>
        /// <exception cref="InvalidDataException">The macro failed validation; nothing is written.</exception>
        public Macro Save(Macro macro)
        {
            var result = MacroValidator.Process(macro);

            if (!result.IsValid)
                throw new InvalidDataException("Invalid macro: " + string.Join("; ", result.Errors.Take(3)));

            ModFileUtil.WriteAllTextAtomic(PathFor(result.Macro.Id), MacroJson.Serialize(result.Macro), FaultBeforeCommit);
            return result.Macro;
        }

        /// <summary>Deletes the macro file. Returns false when there was nothing to delete.</summary>
        public bool Delete(string id)
        {
            if (!MacroValidator.IsValidId(id))
                return false;

            string path = PathFor(id);

            if (!File.Exists(path))
                return false;

            ModFileUtil.ClearReadOnly(path);
            File.Delete(path);
            return true;
        }

        /// <summary>Copies a macro under a new id and a de-duplicated name. The copy is disabled and has no hotkey so it cannot clash with the original.</summary>
        public Macro? Duplicate(string id)
        {
            var source = Load(id);

            if (source is null)
                return null;

            return SaveAsNew(source);
        }

        /// <summary>
        /// Imports a .zeromacro (the same JSON). The file is untrusted: size-capped, parsed, normalised and validated; the
        /// macro gets a new id and a de-duplicated name and is saved disabled with no hotkey, so an imported macro is never armed automatically.
        /// </summary>
        public MacroImportResult Import(string path)
        {
            var failure = new MacroImportResult();

            try
            {
                if (!File.Exists(path))
                {
                    failure.Errors.Add("File not found");
                    return failure;
                }

                var parsed = MacroJson.Deserialize<Macro>(ReadBounded(path));

                if (parsed is null)
                {
                    failure.Errors.Add("File is empty");
                    return failure;
                }

                // An id, hotkey or enabled flag from a foreign file is never trusted: the id is replaced and the others cleared before validation.
                string? droppedHotkey = parsed.Hotkey;
                parsed.Id = MacroValidator.NewId();
                parsed.Enabled = false;
                parsed.Hotkey = null;
                var result = MacroValidator.Process(parsed);

                if (!result.IsValid)
                {
                    failure.Errors.AddRange(result.Errors);
                    failure.Warnings.AddRange(result.Warnings);
                    return failure;
                }

                var saved = SaveAsNew(result.Macro);

                if (!string.IsNullOrWhiteSpace(droppedHotkey))
                    result.Warnings.Add($"The shortcut {droppedHotkey} from the file was not kept, so it can't clash with yours. Set one in the macro's settings.");

                return new MacroImportResult { Macro = saved, Warnings = result.Warnings };
            }
            catch (Exception ex)
            {
                failure.Errors.Add(ex is JsonException ? "Not a valid macro file: " + ex.Message : ex.Message);
                return failure;
            }
        }

        /// <summary>Writes the macro's JSON to <paramref name="path"/> (conventionally a .zeromacro). Returns false when the id is unknown.</summary>
        public bool Export(string id, string path)
        {
            var macro = Load(id);

            if (macro is null)
                return false;

            ModFileUtil.WriteAllTextAtomic(path, MacroJson.Serialize(macro));
            return true;
        }

        private Macro SaveAsNew(Macro source)
        {
            var copy = source.Clone();
            copy.Id = MacroValidator.NewId();
            copy.Enabled = false;
            copy.Hotkey = null;
            copy.Name = UniqueName(copy.Name, LoadAll().Select(m => m.Name));

            return Save(copy);
        }

        /// <summary>"Name", then "Name (2)", "Name (3)" ... compared case-insensitively and kept within the name length limit.</summary>
        internal static string UniqueName(string name, IEnumerable<string> existing)
        {
            var taken = new HashSet<string>(existing, StringComparer.CurrentCultureIgnoreCase);

            if (!taken.Contains(name))
                return name;

            for (int n = 2; ; n++)
            {
                string suffix = $" ({n})";
                string baseName = name.Length + suffix.Length > MacroValidator.MaxNameLength
                    ? name.Substring(0, MacroValidator.MaxNameLength - suffix.Length).TrimEnd()
                    : name;

                string candidate = baseName + suffix;

                if (!taken.Contains(candidate))
                    return candidate;
            }
        }
    }

    /// <summary>Macro Studio preferences (Macros\settings.json).</summary>
    public sealed class MacroStudioSettings
    {
        public const string DefaultEmergencyStop = "Ctrl+Shift+End";
        public const string DefaultRecordStopKey = "F9";

        public string EmergencyStop { get; set; } = DefaultEmergencyStop;
        public string RecordStopKey { get; set; } = DefaultRecordStopKey;
        public bool NoticeAcknowledged { get; set; }
    }

    /// <summary>Reads and atomically writes Macros\settings.json. An unreadable file is moved aside and defaults are used.</summary>
    public sealed class MacroStudioSettingsStore
    {
        public const string FileName = "settings.json";

        private readonly string _path;
        private readonly Action<string> _log;

        public string FilePath => _path;

        public MacroStudioSettingsStore(string path, Action<string>? log = null)
        {
            _path = path;
            _log = log ?? (_ => { });
        }

        public MacroStudioSettings Load()
        {
            if (!File.Exists(_path))
                return new MacroStudioSettings();

            try
            {
                var settings = MacroJson.Deserialize<MacroStudioSettings>(File.ReadAllText(_path))
                    ?? throw new JsonException("null document");

                return Sanitize(settings);
            }
            catch (Exception ex)
            {
                _log($"{FileName} is unreadable ({ex.Message})");
                ModFileUtil.QuarantineCorrupt(_path, _log);
                return new MacroStudioSettings();
            }
        }

        public void Save(MacroStudioSettings settings)
        {
            ModFileUtil.WriteAllTextAtomic(_path, MacroJson.Serialize(Sanitize(settings)));
        }

        /// <summary>The emergency stop and record stop key can never be empty or unparsable: they fall back to the defaults.</summary>
        private MacroStudioSettings Sanitize(MacroStudioSettings settings) => new()
        {
            EmergencyStop = Canonical(settings.EmergencyStop, MacroStudioSettings.DefaultEmergencyStop, nameof(settings.EmergencyStop)),
            RecordStopKey = Canonical(settings.RecordStopKey, MacroStudioSettings.DefaultRecordStopKey, nameof(settings.RecordStopKey)),
            NoticeAcknowledged = settings.NoticeAcknowledged
        };

        private string Canonical(string? value, string fallback, string setting)
        {
            if (Hotkey.TryParseShortcut(value, out var parsed))
                return parsed.ToString();

            if (!string.IsNullOrWhiteSpace(value))
                _log($"{setting} '{value}' is not a usable shortcut (plain typing keys need Ctrl, Alt or Win); using {fallback}");

            return fallback;
        }
    }
}
