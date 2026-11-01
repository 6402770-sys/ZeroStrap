using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ZeroStrap.Mods;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.UI.Utility
{
    /// <summary>
    /// Runs Mods Studio engine calls off the UI thread, one at a time, and keeps the small summary (package count,
    /// unresolved conflicts) that Overview, Configure and the sidebar show. Call from the UI thread.
    /// </summary>
    public static class ModsStudioService
    {
        private const string LOG_IDENT = "ModsStudioService";

        private static readonly SemaphoreSlim Gate = new(1, 1);

        public static bool HasSummary { get; private set; }

        public static int PackageCount { get; private set; }

        public static int UnresolvedCount { get; private set; }

        /// <summary>Raised on the UI thread whenever the summary changed.</summary>
        public static event Action? SummaryChanged;

        public static async Task<T> RunAsync<T>(Func<T> work)
        {
            await Gate.WaitAsync();

            try
            {
                return await Task.Run(work);
            }
            finally
            {
                Gate.Release();
            }
        }

        public static async Task<ModStudioSnapshot> GetStateAsync()
        {
            var snapshot = await RunAsync(() => ModStudio.Default.GetState());
            Publish(snapshot);
            return snapshot;
        }

        /// <summary>Refreshes the summary; never throws.</summary>
        public static async Task RefreshSummaryAsync()
        {
            try
            {
                await GetStateAsync();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        public static void Publish(ModStudioSnapshot snapshot)
        {
            int packages = snapshot.Packages.Count;
            int unresolved = ModsStudioLogic.UnresolvedCount(snapshot.Conflicts);

            if (HasSummary && packages == PackageCount && unresolved == UnresolvedCount)
                return;

            HasSummary = true;
            PackageCount = packages;
            UnresolvedCount = unresolved;
            SummaryChanged?.Invoke();
        }

        public static string InstalledText(int count) => count == 0 ? "None installed" : $"{count} installed";

        public static string ConflictsText(int count) => $"{count} conflict{(count == 1 ? "" : "s")}";
    }
}
