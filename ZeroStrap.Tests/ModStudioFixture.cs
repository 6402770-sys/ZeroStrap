using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using ZeroStrap.Mods;

namespace ZeroStrap.Tests
{
    /// <summary>Temp-directory world for Mods Studio tests. Never touches the real Base folder.</summary>
    public sealed class ModStudioFixture : IDisposable
    {
        public const string CursorPath = @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png";
        public const string EmojiPath = @"content\fonts\TwemojiMozilla.ttf";

        public static readonly byte[] CursorBytes = Encoding.UTF8.GetBytes("preset-cursor-2006");
        public static readonly byte[] EmojiBytes = Encoding.UTF8.GetBytes("preset-emoji-font");

        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zs-mods-" + Guid.NewGuid().ToString("N"));
        public string Modifications => Path.Combine(Root, "Modifications");
        public string StudioDir => Path.Combine(Root, "Mods");
        public string VersionDir => Path.Combine(Root, "Versions", "version-test");
        public List<string> Logs { get; } = new();
        public ModStudio Studio { get; }
        public BuiltinModCatalog Catalog { get; }

        public ModStudioFixture()
        {
            Directory.CreateDirectory(Modifications);

            Catalog = new BuiltinModCatalog(
                Modifications,
                resource => resource switch
                {
                    "Cursor.From2006.ArrowCursor.png" => Md5(CursorBytes),
                    "Cursor.From2013.ArrowCursor.png" => Md5(Encoding.UTF8.GetBytes("preset-cursor-2013")),
                    _ => Md5(Encoding.UTF8.GetBytes("resource:" + resource))
                },
                new[] { Md5(EmojiBytes) },
                Logs.Add);

            Studio = new ModStudio(StudioDir, Modifications, Catalog, () => VersionDir, Logs.Add);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
            catch { }
        }

        public static string Md5(byte[] data)
        {
            using var md5 = MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(data)).ToLowerInvariant();
        }

        public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        public string ModPath(string relative) => Path.Combine(Modifications, relative);

        public void WriteMod(string relative, string content) => WriteMod(relative, Encoding.UTF8.GetBytes(content));

        public void WriteMod(string relative, byte[] content)
        {
            string full = ModPath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }

        public string ReadMod(string relative) => File.ReadAllText(ModPath(relative));

        public bool ModExists(string relative) => File.Exists(ModPath(relative));

        /// <summary>Creates and enables a package.</summary>
        public ModPackage MakePackage(string name, IDictionary<string, string> files, bool enable = true)
        {
            string staging = Path.Combine(Root, "staging-" + Guid.NewGuid().ToString("N"));

            foreach (var (rel, content) in files)
            {
                string full = Path.Combine(staging, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }

            var package = Studio.Packages.CreateFromStaging(new ModManifest { Name = name }, staging, moveFiles: true);

            if (enable)
                Studio.SetEnabled(package.Id, true);

            return package;
        }

        public Dictionary<string, byte[]> Snapshot(string directory)
        {
            var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(directory))
                return map;

            foreach (string f in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                map[Path.GetRelativePath(directory, f)] = File.ReadAllBytes(f);

            return map;
        }
    }
}
