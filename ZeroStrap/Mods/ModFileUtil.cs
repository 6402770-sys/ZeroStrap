using System.Security.Cryptography;

namespace ZeroStrap.Mods
{
    internal static class ModFileUtil
    {
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string Sha256OfFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        public static string Md5OfFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
            using var md5 = MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
        }

        public static void ClearReadOnly(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.IsReadOnly)
                    info.IsReadOnly = false;
            }
            catch { /* the following operation reports the real failure */ }
        }

        public static string NewStamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

        /// <summary>
        /// Writes the file via a temp file in the same directory so a reader (or a crash) never sees a partial file.
        /// <paramref name="faultBeforeCommit"/> exists for tests: throwing from it simulates a failure just before the swap.
        /// </summary>
        public static void WriteAllTextAtomic(string path, string contents, Action? faultBeforeCommit = null)
        {
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);

            string temp = Path.Combine(directory, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));

            try
            {
                File.WriteAllText(temp, contents, new UTF8Encoding(false));

                faultBeforeCommit?.Invoke();

                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        /// <summary>Moves a corrupt state file aside so it is never silently overwritten.</summary>
        public static void QuarantineCorrupt(string path, Action<string> log)
        {
            try
            {
                string target = path + ".corrupt-" + NewStamp();
                File.Move(path, target);
                log($"Moved unreadable {Path.GetFileName(path)} to {Path.GetFileName(target)}");
            }
            catch (Exception ex)
            {
                log($"Could not quarantine {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>Deletes empty directories from <paramref name="startDirectory"/> upwards, stopping at (and never removing) <paramref name="stopAt"/>.</summary>
        public static void PruneEmptyDirectories(string startDirectory, string stopAt)
        {
            string stop = Path.GetFullPath(stopAt).TrimEnd('\\', '/');
            string? current = Path.GetFullPath(startDirectory).TrimEnd('\\', '/');

            while (current is not null
                && current.Length > stop.Length
                && current.StartsWith(stop + "\\", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                        return;

                    Directory.Delete(current);
                }
                catch
                {
                    return;
                }

                current = Path.GetDirectoryName(current);
            }
        }

        /// <summary>
        /// Copies source into a temp file next to destination while hashing it (SHA-256, one pass).
        /// The caller commits with <see cref="CommitTemp"/> or discards with <see cref="DiscardTemp"/>.
        /// </summary>
        public static (string TempPath, string Sha256) CopyToTemp(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            string temp = destination + ".zs-tmp-" + Guid.NewGuid().ToString("N");

            try
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
                using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                var buffer = new byte[81920];
                int read;

                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }

                output.Flush(true);

                return (temp, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
            }
            catch
            {
                DiscardTemp(temp);
                throw;
            }
        }

        public static void CommitTemp(string temp, string destination)
        {
            ClearReadOnly(destination);
            File.Move(temp, destination, true);
        }

        public static void DiscardTemp(string temp)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
