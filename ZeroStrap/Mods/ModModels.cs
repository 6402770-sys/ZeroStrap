namespace ZeroStrap.Mods
{
    public enum ModCategory
    {
        Sounds,
        Textures,
        Fonts,
        Sky,
        Cursors,
        Places,
        Other
    }

    public enum ModSource
    {
        Imported,
        Legacy
    }

    /// <summary>Contents of Packages\&lt;id&gt;\manifest.json.</summary>
    public sealed class ModManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Author { get; set; }
        public string? Description { get; set; }

        /// <summary>Null on a draft: the store guesses it from the file paths when the package is created.</summary>
        public ModCategory? Category { get; set; }

        public string? Version { get; set; }
        public string? Thumbnail { get; set; }
        public ModSource Source { get; set; } = ModSource.Imported;
        public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
        public string? RobloxVersionAtInstall { get; set; }
    }

    public sealed record ModPackageFile(string RelativePath, long Size);

    /// <summary>An installed package: manifest plus the validated set of files it contains.</summary>
    public sealed class ModPackage
    {
        public ModManifest Manifest { get; init; } = new();
        public string Directory { get; init; } = "";
        public string FilesDirectory => Path.Combine(Directory, "files");
        public IReadOnlyList<ModPackageFile> Files { get; init; } = Array.Empty<ModPackageFile>();

        public string Id => Manifest.Id;
        public string Name => Manifest.Name;

        public string FullPathOf(string relativePath) => Path.Combine(FilesDirectory, relativePath);

        public HashSet<string> PathSet()
        {
            var set = ModPathRules.NewPathSet();
            foreach (var f in Files)
                set.Add(f.RelativePath);
            return set;
        }
    }

    /// <summary>Contents of Mods\studio.json.</summary>
    public sealed class ModStudioState
    {
        public int SchemaVersion { get; set; } = 1;
        public List<string> Enabled { get; set; } = new();

        /// <summary>Package ids, highest priority first.</summary>
        public List<string> Priority { get; set; } = new();

        /// <summary>Explicit per-path ownership choices: relative path to owner id (package id or builtin id).</summary>
        public Dictionary<string, string> FileChoices { get; set; } = ModPathRules.NewPathMap<string>();

        public bool Migrated { get; set; }
        public string? MigrationBackup { get; set; }

        /// <summary>Bumped by SetPriority so that the next rebuild lets priority override sticky ownership.</summary>
        public int PriorityRevision { get; set; }
    }

    public sealed class OverlayIndexEntry
    {
        public string Owner { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    /// <summary>Contents of Mods\overlay-index.json: which Modifications files the studio wrote.</summary>
    public sealed class OverlayIndex
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>The PriorityRevision this index was last built under.</summary>
        public int PriorityRevision { get; set; }

        public Dictionary<string, OverlayIndexEntry> Files { get; set; } = ModPathRules.NewPathMap<OverlayIndexEntry>();

        public OverlayIndex Clone()
        {
            var copy = new OverlayIndex { SchemaVersion = SchemaVersion, PriorityRevision = PriorityRevision };

            foreach (var (path, entry) in Files)
                copy.Files[path] = new OverlayIndexEntry { Owner = entry.Owner, Sha256 = entry.Sha256 };

            return copy;
        }
    }

    public sealed class ModStudioException : Exception
    {
        public ModStudioException(string message) : base(message) { }
        public ModStudioException(string message, Exception inner) : base(message, inner) { }
    }
}
