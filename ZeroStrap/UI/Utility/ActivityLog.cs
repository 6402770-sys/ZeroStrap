using System.IO;
using System.Text.Json;

namespace ZeroStrap.UI.Utility
{
    public enum ActivityType
    {
        Launch,
        Configuration,
        Theme,
        Update,
        Backup
    }

    public class ActivityEntry
    {
        public ActivityType Kind { get; set; }
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public DateTime Time { get; set; }
    }

    /// <summary>
    /// Small, presentation-only history for the Overview "Recent Activity" list. Stored in
    /// ActivityLog.json next to Settings.json; never read by the launcher core. Failures are swallowed.
    /// </summary>
    public static class ActivityLog
    {
        private const int MaxEntries = 30;
        private const string LOG_IDENT = "ActivityLog";

        private static List<ActivityEntry>? _entries;

        private static string FileLocation => Path.Combine(Paths.Base, "ActivityLog.json");

        public static IReadOnlyList<ActivityEntry> Entries => Load();

        public static void Record(ActivityType kind, string title, string detail = "")
        {
            try
            {
                var entries = Load();
                entries.Insert(0, new ActivityEntry { Kind = kind, Title = title, Detail = detail, Time = DateTime.Now });

                if (entries.Count > MaxEntries)
                    entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);

                File.WriteAllText(FileLocation, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        /// <summary>Records an "Application updated" entry once per new launcher version.</summary>
        public static void NoteVersion()
        {
            var entries = Load();
            var version = App.Version;

            if (entries.Any(e => e.Kind == ActivityType.Update && e.Detail == $"ZeroStrap v{version}"))
                return;

            Record(ActivityType.Update, entries.Count == 0 ? "ZeroStrap installed" : "Application updated", $"ZeroStrap v{version}");
        }

        public static string Relative(DateTime time)
        {
            var span = DateTime.Now - time;

            if (span.TotalMinutes < 1) return "Just now";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalDays < 1) return (int)span.TotalHours == 1 ? "1 hour ago" : $"{(int)span.TotalHours} hours ago";
            if (span.TotalDays < 2) return "1 day ago";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays} days ago";

            return time.ToString("MMM d");
        }

        private static List<ActivityEntry> Load()
        {
            if (_entries is not null)
                return _entries;

            try
            {
                if (File.Exists(FileLocation))
                    _entries = JsonSerializer.Deserialize<List<ActivityEntry>>(File.ReadAllText(FileLocation));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }

            return _entries ??= new();
        }
    }
}
