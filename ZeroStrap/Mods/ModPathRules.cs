namespace ZeroStrap.Mods
{
    /// <summary>
    /// Rules for the version-root-relative paths a mod package may contain. Pure, no I/O.
    /// A valid path is normalised to backslash form, with the first segment canonicalised to one of
    /// <see cref="AllowedRoots"/>. All comparisons are case-insensitive (use <see cref="PathComparer"/>).
    /// </summary>
    public static class ModPathRules
    {
        public static readonly IReadOnlyList<string> AllowedRoots = new[] { "content", "ExtraContent", "PlatformContent", "shaders" };

        public static readonly IReadOnlyList<string> BlockedExtensions = new[]
        {
            ".lua", ".luau", ".dll", ".exe", ".bat", ".cmd", ".ps1", ".js", ".vbs"
        };

        public const int MaxPathLength = 200;

        public static StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;

        public static HashSet<string> NewPathSet() => new(PathComparer);

        public static Dictionary<string, T> NewPathMap<T>() => new(PathComparer);

        /// <summary>Returns true and the normalised path when <paramref name="raw"/> is acceptable.</summary>
        public static bool TryNormalize(string? raw, out string normalized, out string? error)
        {
            normalized = "";
            error = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "empty path";
                return false;
            }

            string path = raw.Replace('/', '\\');

            if (path.Length > MaxPathLength)
            {
                error = "path too long";
                return false;
            }

            if (path.Contains(':'))
            {
                error = "drive letters and stream names are not allowed";
                return false;
            }

            if (path.StartsWith('\\'))
            {
                error = "rooted path";
                return false;
            }

            foreach (char c in path)
            {
                if (c < 32 || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                {
                    error = "illegal character";
                    return false;
                }
            }

            string[] segments = path.Split('\\');

            foreach (string segment in segments)
            {
                if (segment.Length == 0)
                {
                    error = "empty path segment";
                    return false;
                }

                if (segment == ".")
                {
                    error = "relative segment";
                    return false;
                }

                if (segment == "..")
                {
                    error = "parent directory reference";
                    return false;
                }

                if (PathValidator.IsFileNameValid(segment) != PathValidator.ValidationResult.Ok)
                {
                    error = $"invalid name '{segment}'";
                    return false;
                }
            }

            if (segments.Length < 2)
            {
                error = "files must be inside content, ExtraContent, PlatformContent or shaders";
                return false;
            }

            if (segments[0].Equals("ClientSettings", StringComparison.OrdinalIgnoreCase))
            {
                error = "ClientSettings is managed by Fast Flags";
                return false;
            }

            string? root = AllowedRoots.FirstOrDefault(r => r.Equals(segments[0], StringComparison.OrdinalIgnoreCase));

            if (root is null)
            {
                error = $"'{segments[0]}' is not an allowed root folder";
                return false;
            }

            segments[0] = root;

            string extension = Path.GetExtension(segments[^1]);

            if (BlockedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                error = $"blocked file type '{extension}'";
                return false;
            }

            normalized = string.Join('\\', segments);
            return true;
        }

        public static bool IsValid(string? raw) => TryNormalize(raw, out _, out _);

        /// <summary>Normalises or throws <see cref="ArgumentException"/>.</summary>
        public static string Normalize(string? raw)
        {
            if (!TryNormalize(raw, out string normalized, out string? error))
                throw new ArgumentException($"Invalid mod path '{raw}': {error}");

            return normalized;
        }

        /// <summary>True for files the studio must never manage inside the Modifications folder.</summary>
        public static bool IsReservedModificationsPath(string relativePath)
        {
            string path = relativePath.Replace('/', '\\').TrimStart('\\');

            if (path.StartsWith("ClientSettings\\", StringComparison.OrdinalIgnoreCase)
                || path.Equals("ClientSettings", StringComparison.OrdinalIgnoreCase))
                return true;

            if (path.Equals("README.txt", StringComparison.OrdinalIgnoreCase))
                return true;

            return path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Resolves <paramref name="relativePath"/> under <paramref name="root"/>, refusing anything that escapes it.</summary>
        public static string CombineConfined(string root, string relativePath)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
            string full = Path.GetFullPath(Path.Combine(fullRoot, relativePath));

            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Path escapes its root: {relativePath}");

            return full;
        }
    }
}
