namespace ZeroStrap.Macros
{
    /// <summary>Outcome of <see cref="MacroValidator.Process"/>: a normalised copy plus what was clamped and what was rejected.</summary>
    public sealed class MacroValidationResult
    {
        public Macro Macro { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
        public List<string> Errors { get; init; } = new();

        public bool IsValid => Errors.Count == 0;
    }

    /// <summary>
    /// Pure normalisation (clamp what can be clamped) and validation (reject what cannot) for macros. Used by the store on
    /// load and save and by import, so everything read from disk or from a shared .zeromacro goes through here first.
    /// </summary>
    public static class MacroValidator
    {
        public const int SchemaVersion = 1;
        public const int MaxNameLength = 80;
        public const double MinSpeed = 0.25;
        public const double MaxSpeed = 4.0;
        public const int MaxRepeatCount = 100000;
        public const int MaxGroupRepeat = 10000;
        public const int MaxTimeMs = 600000;
        public const int MaxScanCode = 0x7F;
        public const int MaxCameraDelta = 20000;
        public const int MaxCoordinate = 100000;
        public const int MaxGroupDepth = 4;
        public const int MaxNodes = 10000;

        private const int MaxMessages = 25;

        // \z, not $: "$" would also accept a trailing newline.
        private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant);

        // Ids become file names. "settings" would overwrite Macros\settings.json; the others are Windows device names.
        private static readonly HashSet<string> ReservedIds = new(StringComparer.Ordinal)
        {
            "settings", "con", "prn", "aux", "nul",
            "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
            "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
        };

        public static string NewId() => Guid.NewGuid().ToString("n");

        public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id) && !ReservedIds.Contains(id);

        /// <summary>Normalises, then validates the normalised copy.</summary>
        public static MacroValidationResult Process(Macro macro)
        {
            var (normalized, warnings) = Normalize(macro);

            return new MacroValidationResult
            {
                Macro = normalized,
                Warnings = warnings,
                Errors = Validate(normalized)
            };
        }

        /// <summary>Returns a normalised deep copy (the input is not modified) and a warning for each value that was changed.</summary>
        public static (Macro Macro, List<string> Warnings) Normalize(Macro macro)
        {
            var warnings = new Messages();

            var result = new Macro
            {
                SchemaVersion = macro.SchemaVersion,
                Id = macro.Id ?? "",
                Name = CleanName(macro.Name, warnings),
                Enabled = macro.Enabled,
                Hotkey = NormalizeHotkey(macro.Hotkey, warnings),
                FocusRule = macro.FocusRule,
                Speed = ClampSpeed(macro.Speed, warnings),
                Repeat = new MacroRepeat
                {
                    Mode = macro.Repeat?.Mode ?? MacroRepeatMode.Once,
                    Count = Clamp(macro.Repeat?.Count ?? 1, 1, MaxRepeatCount, "Repeat count", warnings)
                }
            };

            int nodes = 0;
            result.Actions = NormalizeList(macro.Actions, "", 1, warnings, ref nodes);
            return (result, warnings.ToList());
        }

        /// <summary>
        /// Returns the reasons the macro must be rejected (empty when valid). Expects <see cref="Normalize"/> to have run:
        /// out-of-range numbers are clamped there, not reported here.
        /// </summary>
        public static List<string> Validate(Macro macro)
        {
            var errors = new Messages();

            if (macro.SchemaVersion != SchemaVersion)
                errors.Add($"Unsupported schema version {macro.SchemaVersion}");

            if (!IsValidId(macro.Id))
                errors.Add("Invalid id (use 1-64 lowercase letters, digits or dashes, starting with a letter or digit)");

            if (string.IsNullOrWhiteSpace(macro.Name))
                errors.Add("Name is empty");

            if (macro.Hotkey is not null)
            {
                if (!Hotkey.TryParse(macro.Hotkey, out var hotkey))
                    errors.Add($"Invalid hotkey '{macro.Hotkey}'");
                else if (!hotkey.IsAllowedShortcut)
                    errors.Add($"Hotkey '{macro.Hotkey}' is a typing key; add Ctrl, Alt or Win");
            }

            if (!Enum.IsDefined(typeof(MacroFocusRule), macro.FocusRule))
                errors.Add("Unknown focus rule");

            if (macro.Repeat is null)
                errors.Add("Missing repeat settings");
            else if (!Enum.IsDefined(typeof(MacroRepeatMode), macro.Repeat.Mode))
                errors.Add("Unknown repeat mode");

            if (double.IsNaN(macro.Speed) || macro.Speed < MinSpeed || macro.Speed > MaxSpeed)
                errors.Add("Speed is out of range");

            int nodes = 0;
            ValidateList(macro.Actions, "", 1, errors, ref nodes);

            if (nodes > MaxNodes)
                errors.Add($"Too many actions (more than {MaxNodes})");

            return errors.ToList();
        }

        // Stops copying once the node cap is exceeded (Validate then rejects the macro), so a hostile file with millions of
        // actions is not duplicated in memory before being refused.
        private static List<MacroAction> NormalizeList(List<MacroAction>? source, string prefix, int groupDepth, Messages warnings, ref int nodes)
        {
            var list = new List<MacroAction>();

            if (source is null)
                return list;

            for (int i = 0; i < source.Count; i++)
            {
                if (nodes > MaxNodes)
                    break;

                if (source[i] is { } action)
                {
                    nodes++;
                    list.Add(NormalizeAction(action, prefix + (i + 1), groupDepth, warnings, ref nodes));
                }
            }

            return list;
        }

        private static MacroAction NormalizeAction(MacroAction a, string label, int groupDepth, Messages warnings, ref int nodes)
        {
            string who = "Action " + label + ": ";

            var r = new MacroAction
            {
                Kind = a.Kind,
                Enabled = a.Enabled,
                Note = a.Note,
                ScanCode = a.ScanCode,
                Extended = a.Extended,
                KeyMode = a.KeyMode,
                Button = a.Button,
                ButtonMode = a.ButtonMode,
                Easing = a.Easing,
                HoldRightButton = a.HoldRightButton,
                DelayBeforeMs = Clamp(a.DelayBeforeMs, 0, MaxTimeMs, who + "delay", warnings),
                HoldMs = Clamp(a.HoldMs, 0, MaxTimeMs, who + "hold time", warnings),
                Ms = Clamp(a.Ms, 0, MaxTimeMs, who + "wait", warnings),
                DurationMs = Clamp(a.DurationMs, 0, MaxTimeMs, who + "duration", warnings),
                Dx = Clamp(a.Dx, -MaxCameraDelta, MaxCameraDelta, who + "camera X", warnings),
                Dy = Clamp(a.Dy, -MaxCameraDelta, MaxCameraDelta, who + "camera Y", warnings),
                X = Clamp(a.X, -MaxCoordinate, MaxCoordinate, who + "X", warnings),
                Y = Clamp(a.Y, -MaxCoordinate, MaxCoordinate, who + "Y", warnings),
                GroupRepeat = Clamp(a.GroupRepeat, 1, MaxGroupRepeat, who + "group repeat", warnings)
            };

            if (a.Kind == MacroActionKind.Camera && (r.Dx != 0 || r.Dy != 0) && r.DurationMs < 1)
            {
                r.DurationMs = 1;
                warnings.Add(who + "camera duration raised to 1 ms");
            }

            if (a.Note is { Length: > 500 })
                r.Note = a.Note.Substring(0, 500);

            // Past the allowed depth the children are dropped so a hostile file cannot make this recurse deeply;
            // Validate still rejects the group itself.
            if (a.Kind == MacroActionKind.Group && groupDepth <= MaxGroupDepth)
                r.Actions = NormalizeList(a.Actions, label + ".", groupDepth + 1, warnings, ref nodes);

            return r;
        }

        private static void ValidateList(List<MacroAction>? list, string prefix, int groupDepth, Messages errors, ref int nodes)
        {
            if (list is null)
                return;

            for (int i = 0; i < list.Count; i++)
            {
                if (nodes > MaxNodes)
                    return;

                nodes++;
                var a = list[i];
                string who = "Action " + prefix + (i + 1) + ": ";

                if (a is null)
                {
                    errors.Add(who + "missing");
                    continue;
                }

                if (!Enum.IsDefined(typeof(MacroActionKind), a.Kind)
                    || !Enum.IsDefined(typeof(MacroKeyMode), a.KeyMode)
                    || !Enum.IsDefined(typeof(MouseButton), a.Button)
                    || !Enum.IsDefined(typeof(MacroButtonMode), a.ButtonMode)
                    || !Enum.IsDefined(typeof(MacroEasing), a.Easing))
                {
                    errors.Add(who + "unknown kind or mode");
                    continue;
                }

                if (a.Kind == MacroActionKind.Key && (a.ScanCode < 1 || a.ScanCode > MaxScanCode))
                    errors.Add(who + $"scan code {a.ScanCode} is out of range (1-{MaxScanCode})");

                if (a.Kind == MacroActionKind.Group)
                {
                    if (groupDepth > MaxGroupDepth)
                        errors.Add(who + $"groups are nested more than {MaxGroupDepth} deep");
                    else
                        ValidateList(a.Actions, prefix + (i + 1) + ".", groupDepth + 1, errors, ref nodes);
                }
            }
        }

        private static string CleanName(string? name, Messages warnings)
        {
            string cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();

            if (cleaned.Length > MaxNameLength)
            {
                cleaned = cleaned.Substring(0, MaxNameLength).TrimEnd();
                warnings.Add($"Name shortened to {MaxNameLength} characters");
            }

            return cleaned;
        }

        private static string? NormalizeHotkey(string? text, Messages warnings)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            // Rewrite to the canonical spelling; text that does not parse is left alone so Validate rejects it.
            if (!Hotkey.TryParse(text, out var parsed))
                return text.Trim();

            // A shortcut that is a plain typing key is dropped (not rejected) so a macro saved before this rule still loads.
            if (!parsed.IsAllowedShortcut)
            {
                warnings.Add($"Hotkey '{parsed}' was removed: typing keys need Ctrl, Alt or Win");
                return null;
            }

            return parsed.ToString();
        }

        private static double ClampSpeed(double speed, Messages warnings)
        {
            if (double.IsNaN(speed))
            {
                warnings.Add("Speed reset to 1");
                return 1.0;
            }

            double clamped = Math.Min(MaxSpeed, Math.Max(MinSpeed, speed));

            if (clamped != speed)
                warnings.Add($"Speed clamped to {clamped.ToString(CultureInfo.InvariantCulture)}");

            return clamped;
        }

        private static int Clamp(int value, int min, int max, string what, Messages warnings)
        {
            int clamped = Math.Min(max, Math.Max(min, value));

            if (clamped != value)
                warnings.Add($"{what} clamped to {clamped}");

            return clamped;
        }

        /// <summary>Message list that stops growing after a cap so a hostile file cannot produce thousands of lines.</summary>
        private sealed class Messages
        {
            private readonly List<string> _items = new();
            private int _dropped;

            public void Add(string message)
            {
                if (_items.Count < MaxMessages)
                    _items.Add(message);
                else
                    _dropped++;
            }

            public List<string> ToList()
            {
                var copy = new List<string>(_items);

                if (_dropped > 0)
                    copy.Add($"...and {_dropped} more");

                return copy;
            }
        }
    }
}
