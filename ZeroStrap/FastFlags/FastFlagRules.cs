namespace ZeroStrap.FastFlags
{
    public enum FlagValueType
    {
        Bool,
        Int,
        String,
        Log
    }

    public readonly record struct FlagValidationResult(bool IsValid, string? Error)
    {
        public static FlagValidationResult Ok => new(true, null);

        public static FlagValidationResult Fail(string error) => new(false, error);
    }

    /// <summary>
    /// Name/value rules for Fast Flags. These are the same rules the original editor enforced
    /// (FastFlagEditorPage.ValidateFlagEntry), moved here so every entry point shares them.
    /// They say nothing about whether Roblox honours a flag: see <see cref="RobloxFlagAllowlist"/>.
    /// </summary>
    public static class FastFlagRules
    {
        public const int MaxNameLength = 200;

        public const int MaxValueLength = 4096;

        // longest prefixes first so "DFFlag" is not read as "DF" + "Flag"
        public static readonly IReadOnlyList<string> ValidPrefixes = new[]
        {
            "DFString", "FString", "DFFlag", "SFFlag", "FFlag", "DFInt", "FInt", "DFLog", "FLog"
        };

        // values must match the entire string to avoid cases where half the string
        // matches but the filter would still be invalid
        private static readonly Regex BoolFilterPattern = new("^(?:true|false)(;[\\d]{1,})+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex IntFilterPattern = new("^([\\d]{1,})?(;[\\d]{1,})+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StringFilterPattern = new("^[^;]*(;[\\d]{1,})+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string? GetPrefix(string name) => ValidPrefixes.FirstOrDefault(x => name.StartsWith(x, StringComparison.Ordinal));

        public static FlagValueType? GetValueType(string name) => GetPrefix(name) switch
        {
            "FFlag" or "DFFlag" or "SFFlag" => FlagValueType.Bool,
            "FInt" or "DFInt" => FlagValueType.Int,
            "FString" or "DFString" => FlagValueType.String,
            "FLog" or "DFLog" => FlagValueType.Log,
            _ => null
        };

        public static bool IsFilterFlag(string name) => name.EndsWith("_PlaceFilter", StringComparison.Ordinal) || name.EndsWith("_DataCenterFilter", StringComparison.Ordinal);

        public static FlagValidationResult ValidateName(string? name)
        {
            if (String.IsNullOrWhiteSpace(name) || GetPrefix(name) is null)
                return FlagValidationResult.Fail(Format(Strings.Menu_FastFlagEditor_InvalidPrefix, name ?? ""));

            if (name.Length > MaxNameLength || !name.All(x => x is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'))
                return FlagValidationResult.Fail(Format(Strings.Menu_FastFlagEditor_InvalidCharacter, name));

            return FlagValidationResult.Ok;
        }

        public static FlagValidationResult Validate(string? name, string? value)
        {
            var nameResult = ValidateName(name);

            if (!nameResult.IsValid)
                return nameResult;

            if (value is null || value.Length > MaxValueLength)
                return FlagValidationResult.Fail(Format(Strings.Menu_FastFlagEditor_InvalidNumberValue, name!));

            string lowerValue = value.ToLowerInvariant();

            if (IsFilterFlag(name!))
                return ValidateFilter(name!, value) ? FlagValidationResult.Ok : FlagValidationResult.Fail(Format(Strings.Menu_FastFlagEditor_InvalidPlaceFilter, name!));

            switch (GetPrefix(name!))
            {
                // decimals are rejected on purpose: Roblox int flags are 32-bit integers
                case "FInt" or "DFInt" when !Int32.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _):
                    return FlagValidationResult.Fail(Format(Strings.Menu_FastFlagEditor_InvalidNumberValue, name!));

                case "FFlag" or "DFFlag" when lowerValue != "true" && lowerValue != "false":
                    return FlagValidationResult.Fail(Format(Strings.Menu_FastFlagEditor_InvalidBoolValue, name!));
            }

            return FlagValidationResult.Ok;
        }

        private static bool ValidateFilter(string name, string value) => GetValueType(name) switch
        {
            FlagValueType.Bool => BoolFilterPattern.IsMatch(value),
            FlagValueType.Int => IntFilterPattern.IsMatch(value),
            FlagValueType.String or FlagValueType.Log => StringFilterPattern.IsMatch(value),
            _ => false
        };

        private static string Format(string template, string name)
        {
            try
            {
                return String.Format(template, name);
            }
            catch (FormatException)
            {
                return template;
            }
        }
    }
}
