namespace Bloxstrap.Models
{
    internal class WatcherData
    {
        public int ProcessId { get; set; }

        public string? LogFile { get; set; }

        public List<int>? AutoclosePids { get; set; }

        /// <summary>
        /// When non-null, the watcher runs a persistent memory FFlag watchdog
        /// against ProcessId: injector keeps re-asserting these values into
        /// Roblox's live memory until the process exits.
        /// </summary>
        public Dictionary<string, string>? MemoryFlags { get; set; }

        /// <summary>
        /// Roblox build GUID the memory watchdog expects. If the loaded offset
        /// dump targets a different build, the watchdog refuses to write.
        /// </summary>
        public string? MemoryFlagBuildVersion { get; set; }
    }
}
