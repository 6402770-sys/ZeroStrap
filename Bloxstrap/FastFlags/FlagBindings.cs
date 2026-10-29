namespace Bloxstrap.FastFlags
{
    /// <summary>
    /// A keyboard shortcut that cycles a flag through a list of values in the pending configuration.
    /// It edits ClientAppSettings.json like any other edit: it needs Save, and Roblox only reads the
    /// file when it starts, so a running Roblox session is never changed.
    /// </summary>
    public sealed class FlagBinding
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Normalised gesture, e.g. "Ctrl+Shift+F5". See <see cref="FlagBindingEngine.NormalizeGesture"/>.</summary>
        public string Gesture { get; set; } = "";

        public string FlagName { get; set; } = "";

        /// <summary>Values to cycle through. Empty for bool flags means True/False.</summary>
        public List<string> Values { get; set; } = new();

        /// <summary>Optionally remove the flag as one step of the cycle (restores Roblox's default).</summary>
        public bool IncludeUnset { get; set; }

        public bool Enabled { get; set; } = true;
    }

    public sealed record BindingConflict(string Gesture, IReadOnlyList<FlagBinding> Bindings);

    public static class FlagBindingEngine
    {
        private static readonly string[] ModifierOrder = { "Ctrl", "Alt", "Shift", "Win" };

        private static readonly Dictionary<string, string> ModifierAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = "Ctrl", ["control"] = "Ctrl",
            ["alt"] = "Alt",
            ["shift"] = "Shift",
            ["win"] = "Win", ["windows"] = "Win"
        };

        /// <summary>
        /// Normalises "shift+ctrl+k" to "Ctrl+Shift+K". Returns null when the gesture is unusable:
        /// no key, unknown parts, or a plain character key without Ctrl/Alt/Win (that would fire while typing).
        /// </summary>
        public static string? NormalizeGesture(string? gesture)
        {
            if (String.IsNullOrWhiteSpace(gesture))
                return null;

            var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var modifiers = new HashSet<string>();
            string? key = null;

            foreach (string part in parts)
            {
                if (ModifierAliases.TryGetValue(part, out string? modifier))
                {
                    modifiers.Add(modifier);
                    continue;
                }

                if (key is not null)
                    return null;

                key = NormalizeKey(part);

                if (key is null)
                    return null;
            }

            if (key is null)
                return null;

            bool functionKey = Regex.IsMatch(key, "^F([1-9]|1[0-9]|2[0-4])$");

            if (!functionKey && !modifiers.Contains("Ctrl") && !modifiers.Contains("Alt") && !modifiers.Contains("Win"))
                return null;

            return String.Join("+", ModifierOrder.Where(modifiers.Contains).Append(key));
        }

        private static string? NormalizeKey(string key)
        {
            if (key.Length == 1 && char.IsLetterOrDigit(key[0]) && key[0] < 128)
                return key.ToUpperInvariant();

            if (Regex.IsMatch(key, "^[Ff]([1-9]|1[0-9]|2[0-4])$"))
                return key.ToUpperInvariant();

            if (Regex.IsMatch(key, "^(D[0-9]|NumPad[0-9])$", RegexOptions.IgnoreCase))
                return char.ToUpperInvariant(key[0]) + key[1..];

            return null;
        }

        public static IReadOnlyList<string> EffectiveValues(FlagBinding binding)
        {
            var values = binding.Values.Where(x => !String.IsNullOrEmpty(x)).ToList();

            if (values.Count == 0 && FastFlagRules.GetValueType(binding.FlagName) == FlagValueType.Bool)
                values = new() { "True", "False" };

            return values;
        }

        /// <summary>Returns null when the binding is usable, otherwise why not.</summary>
        public static string? Validate(FlagBinding binding)
        {
            if (NormalizeGesture(binding.Gesture) is null)
                return "Use a function key (F1-F24) or a combination with Ctrl, Alt or Win.";

            var name = FastFlagRules.ValidateName(binding.FlagName);

            if (!name.IsValid)
                return name.Error;

            var values = EffectiveValues(binding);

            if (values.Count + (binding.IncludeUnset ? 1 : 0) < 2)
                return "A binding needs at least two states to switch between.";

            foreach (string value in values)
            {
                var result = FastFlagRules.Validate(binding.FlagName, value);

                if (!result.IsValid)
                    return result.Error;
            }

            return null;
        }

        /// <summary>The value after <paramref name="current"/> in the cycle; null means "remove the flag".</summary>
        public static string? NextValue(FlagBinding binding, string? current)
        {
            var states = EffectiveValues(binding).Select(x => (string?)x).ToList();

            if (binding.IncludeUnset)
                states.Add(null);

            if (states.Count == 0)
                return current;

            int index = states.FindIndex(x => String.Equals(x, current, StringComparison.OrdinalIgnoreCase));

            // unknown current value (edited by hand): start the cycle from its first state
            return index < 0 ? states[0] : states[(index + 1) % states.Count];
        }

        /// <summary>Enabled bindings that share a gesture. Only the first of each group would ever fire.</summary>
        public static IReadOnlyList<BindingConflict> FindConflicts(IEnumerable<FlagBinding> bindings) =>
            bindings
                .Where(x => x.Enabled)
                .GroupBy(x => NormalizeGesture(x.Gesture) ?? x.Gesture, StringComparer.Ordinal)
                .Where(x => x.Count() > 1)
                .Select(x => new BindingConflict(x.Key, x.ToList()))
                .ToList();

        public static FlagBinding? Find(IEnumerable<FlagBinding> bindings, string gesture) =>
            bindings.FirstOrDefault(x => x.Enabled && NormalizeGesture(x.Gesture) == gesture);
    }
}
