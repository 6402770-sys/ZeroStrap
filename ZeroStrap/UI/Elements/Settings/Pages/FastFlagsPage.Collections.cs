using System.Windows;
using System.Windows.Controls;

using Microsoft.Win32;

using ZeroStrap.FastFlagCollections;
using ZeroStrap.FastFlags;
using ZeroStrap.UI.Elements.Dialogs;

namespace ZeroStrap.UI.Elements.Settings.Pages
{
    /// <summary>Zero FastFlags: official and imported collections.</summary>
    public partial class FastFlagsPage
    {
        /// <summary>One card or row in the collection lists.</summary>
        public sealed class CollectionRow
        {
            public string Id { get; init; } = "";

            public string Name { get; init; } = "";

            public string Meta { get; init; } = "";

            public string StatusText { get; init; } = "";

            public CollectionStatus Status { get; init; }
        }

        /// <summary>Raised after a collection was applied or undone, so hosts can refresh counts.</summary>
        public event EventHandler? FlagsReplaced;

        private static readonly Dictionary<string, int> OfficialCounts = new();

        private readonly Dictionary<string, int> _importedCounts = new();

        private static FastFlagCollectionsState CollectionState => App.FastFlagCollections.Prop;

        private static FastFlagWorkspace Workspace => FastFlagWorkspace.Instance;

        private Window? OwnerWindow => Window.GetWindow(this);

        #region Display

        private static string Date(DateTime value) => value.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

        private static string StatusText(CollectionStatus status, LastAppliedCollection? last) => status switch
        {
            CollectionStatus.Applied => $"Applied {Date(last?.AppliedAt ?? DateTime.Now)}",
            CollectionStatus.ChangedSinceApplied => "Changed since applied",
            _ => "Not applied"
        };

        private static int OfficialCount(OfficialCollection collection)
        {
            if (!OfficialCounts.TryGetValue(collection.Id, out int count))
            {
                try
                {
                    count = collection.LoadTemplate().Count;
                }
                catch
                {
                    count = 0;
                }

                OfficialCounts[collection.Id] = count;
            }

            return count;
        }

        private int ImportedCount(ImportedCollection collection)
        {
            if (!_importedCounts.TryGetValue(collection.Id, out int count))
            {
                var parsed = CollectionPayload.Parse(collection.PayloadText);
                count = parsed.Success ? parsed.Entries.Count : -1;
                _importedCounts[collection.Id] = count;
            }

            return count;
        }

        /// <summary>Rebuilds both lists, the undo line and the preset migration button from the persisted state and the current flags.</summary>
        private void RefreshCollections()
        {
            // the XAML-created elements do not exist yet when this is reached from the constructor
            if (OfficialList is null)
                return;

            try
            {
                var state = CollectionState;
                var flags = Workspace.Store.Snapshot();

                OfficialList.ItemsSource = ZeroCollectionCatalog.All.Select(c =>
                {
                    var status = CollectionStatusEvaluator.Evaluate(state.LastApplied, c.Id, flags);
                    return new CollectionRow
                    {
                        Id = c.Id,
                        Name = c.Name,
                        Meta = $"{c.Author} · {ReplaceFlagsDialog.Plural(OfficialCount(c))}",
                        Status = status,
                        StatusText = StatusText(status, state.LastApplied)
                    };
                }).ToList();

                MyCollectionsList.ItemsSource = state.Collections.Select(c =>
                {
                    var status = CollectionStatusEvaluator.Evaluate(state.LastApplied, c.Id, flags);
                    int count = ImportedCount(c);
                    return new CollectionRow
                    {
                        Id = c.Id,
                        Name = c.Name,
                        Meta = $"Imported {Date(c.ImportedAt)} · " + (count < 0 ? "can't be read" : ReplaceFlagsDialog.Plural(count)),
                        Status = status,
                        StatusText = StatusText(status, state.LastApplied)
                    };
                }).ToList();

                MyCollectionsEmptyText.Visibility = state.Collections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                if (state.UndoSnapshot is not null)
                {
                    UndoText.Text = $"Applied {state.LastApplied?.Name ?? "a collection"} on {Date(state.LastApplied?.AppliedAt ?? DateTime.Now)}.";
                    UndoPanel.Visibility = Visibility.Visible;
                }
                else
                {
                    UndoPanel.Visibility = Visibility.Collapsed;
                }

                int presets = Workspace.State.Prop.Presets.Count;

                if (!state.PresetsMigrated && presets > 0)
                {
                    MigratePresetsButton.Content = $"Bring over {presets} {(presets == 1 ? "preset" : "presets")} from the old preset list";
                    MigratePresetsButton.Visibility = Visibility.Visible;
                }
                else
                {
                    MigratePresetsButton.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("FastFlagsPage::RefreshCollections", ex);
            }
        }

        private static string EscapeMarkdown(string text) =>
            Regex.Replace(text, @"([\\`*_{}\[\]<>#|])", "\\$1");

        private static string? IdOf(object sender) => (sender as FrameworkElement)?.Tag as string;

        private static void Notify(string message, MessageBoxImage icon = MessageBoxImage.Information) =>
            Frontend.ShowMessageBox(EscapeMarkdown(message), icon);

        private static IEnumerable<string> AllNames(string? exceptId = null) =>
            ZeroCollectionCatalog.All.Select(x => x.Name)
                .Concat(CollectionState.Collections.Where(x => x.Id != exceptId).Select(x => x.Name));

        #endregion

        #region Apply / undo

        private void CollectionApply_Click(object sender, RoutedEventArgs e)
        {
            if (IdOf(sender) is string id)
                ApplyCollection(id);
        }

        private void ApplyCollection(string id)
        {
            string name;
            IReadOnlyList<(string Key, string Value)> template;
            IReadOnlyList<CollectionParameter> parameters;

            if (ZeroCollectionCatalog.Find(id) is { } official)
            {
                name = official.Name;
                parameters = official.Parameters;

                try
                {
                    template = official.LoadTemplate();
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("FastFlagsPage::ApplyCollection", ex);
                    Notify($"{official.Name} couldn't be loaded: {ex.Message}", MessageBoxImage.Error);
                    return;
                }
            }
            else if (CollectionState.Collections.FirstOrDefault(x => x.Id == id) is { } imported)
            {
                name = imported.Name;
                parameters = Array.Empty<CollectionParameter>();

                var parsed = CollectionPayload.Parse(imported.PayloadText);

                if (!parsed.Success)
                {
                    Notify($"{imported.Name} can't be read: {parsed.Errors[0]}", MessageBoxImage.Error);
                    return;
                }

                template = parsed.Entries;
            }
            else
            {
                return;
            }

            var owner = OwnerWindow;

            // step 1: the logical processor count, only for collections that need it
            int? logicalProcessors = null;

            if (parameters.Any(x => x.Kind == CollectionParameterKind.LogicalProcessorsMinusOne))
            {
                logicalProcessors = LogicalProcessorsDialog.Ask(owner, parameters.First(x => x.Kind == CollectionParameterKind.LogicalProcessorsMinusOne).Key);

                if (logicalProcessors is null)
                    return;
            }

            IReadOnlyList<(string Key, string Value)> resolved;

            try
            {
                resolved = CollectionResolver.Resolve(template, parameters, logicalProcessors);
            }
            catch (CollectionResolveException ex)
            {
                Notify(ex.Message, MessageBoxImage.Warning);
                return;
            }

            // step 2: explicit confirmation of the destructive replace
            var store = Workspace.Store;
            bool touchesEngine = resolved.Any(x => ReplaceFlagsDialog.EngineFlags.Contains(x.Key));

            string? resolvedLine = null;

            foreach (var parameter in parameters)
            {
                var entry = resolved.FirstOrDefault(x => x.Key == parameter.Key);

                if (entry.Key is not null)
                    resolvedLine = $"{entry.Key} = {entry.Value}";
            }

            if (!ReplaceFlagsDialog.Ask(owner, store.Snapshot().Count, name, resolved.Count, touchesEngine, resolvedLine))
                return;

            // step 3: replace, save, remember
            IReadOnlyDictionary<string, string> before;

            try
            {
                before = CollectionApplier.Replace(store, resolved);
            }
            catch (CollectionResolveException ex)
            {
                Notify(ex.Message, MessageBoxImage.Error);
                return;
            }

            Workspace.NotifyFlagsChanged();
            App.FastFlags.Save();

            var state = CollectionState;
            state.UndoSnapshot = new Dictionary<string, string>(before, StringComparer.Ordinal);
            state.LastApplied = new LastAppliedCollection
            {
                CollectionId = id,
                Name = name,
                AppliedAt = DateTime.Now,
                PayloadHash = CollectionStatusEvaluator.ComputeHash(resolved)
            };
            App.FastFlagCollections.Save();

            Reload();
            FlagsReplaced?.Invoke(this, EventArgs.Empty);
        }

        private void CollectionUndo_Click(object sender, RoutedEventArgs e)
        {
            var state = CollectionState;

            if (state.UndoSnapshot is not { } snapshot)
                return;

            string name = state.LastApplied?.Name ?? "the collection";

            var answer = Frontend.ShowMessageBox(
                $"Restore the {ReplaceFlagsDialog.Plural(snapshot.Count)} you had before applying {EscapeMarkdown(name)}?",
                MessageBoxImage.Question,
                MessageBoxButton.YesNo);

            if (answer != MessageBoxResult.Yes)
                return;

            try
            {
                CollectionApplier.Restore(Workspace.Store, snapshot);
            }
            catch (CollectionResolveException ex)
            {
                Notify(ex.Message, MessageBoxImage.Error);
                return;
            }

            Workspace.NotifyFlagsChanged();
            App.FastFlags.Save();

            state.UndoSnapshot = null;
            state.LastApplied = null;
            App.FastFlagCollections.Save();

            Reload();
            FlagsReplaced?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        #region My collections

        private void CollectionImport_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(OwnerWindow) != true)
                return;

            var result = CollectionPayload.ReadFile(dialog.FileName);

            if (!result.Success || result.OriginalText is null)
            {
                var errors = result.Errors.Take(10).ToList();
                string more = result.Errors.Count > 10 ? $"\n\n...and {result.Errors.Count - 10} more." : "";

                // the message box renders Markdown: one paragraph per problem, and flag names are escaped so they show as written
                Notify($"This file can't be imported:\n\n{String.Join("\n\n", errors)}{more}", MessageBoxImage.Warning);
                return;
            }

            string fileName = Path.GetFileName(dialog.FileName);

            CollectionState.Collections.Add(new ImportedCollection
            {
                Name = CollectionNaming.Unique(Path.GetFileNameWithoutExtension(fileName), AllNames()),
                ImportedAt = DateTime.Now,
                SourceFileName = fileName,
                PayloadText = result.OriginalText
            });

            App.FastFlagCollections.Save();
            RefreshCollections();
        }

        private void CollectionRename_Click(object sender, RoutedEventArgs e)
        {
            if (CollectionState.Collections.FirstOrDefault(x => x.Id == IdOf(sender)) is not { } collection)
                return;

            string initial = collection.Name;

            while (true)
            {
                string? entered = TextPromptDialog.Ask(OwnerWindow, "Rename collection", "Collection name", initial);

                if (entered is null)
                    return;

                string? problem = CollectionNaming.Validate(entered, AllNames(collection.Id));

                if (problem is null)
                {
                    collection.Name = entered.Trim();
                    break;
                }

                Notify(problem, MessageBoxImage.Warning);
                initial = entered;
            }

            // a renamed collection that was applied keeps its status; only the display name changes
            if (CollectionState.LastApplied?.CollectionId == collection.Id)
                CollectionState.LastApplied.Name = collection.Name;

            App.FastFlagCollections.Save();
            RefreshCollections();
        }

        private void CollectionExport_Click(object sender, RoutedEventArgs e)
        {
            if (CollectionState.Collections.FirstOrDefault(x => x.Id == IdOf(sender)) is not { } collection)
                return;

            string safeName = String.Concat(collection.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

            var dialog = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                FileName = safeName + ".json",
                AddExtension = true,
                DefaultExt = ".json"
            };

            if (dialog.ShowDialog(OwnerWindow) != true)
                return;

            try
            {
                // the stored text, unchanged
                File.WriteAllText(dialog.FileName, collection.PayloadText, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Notify($"The collection couldn't be exported: {ex.Message}", MessageBoxImage.Error);
            }
        }

        private void CollectionRemove_Click(object sender, RoutedEventArgs e)
        {
            if (CollectionState.Collections.FirstOrDefault(x => x.Id == IdOf(sender)) is not { } collection)
                return;

            var answer = Frontend.ShowMessageBox(
                $"Remove {EscapeMarkdown(collection.Name)} from My collections? Your original file isn't touched.",
                MessageBoxImage.Warning,
                MessageBoxButton.YesNo);

            if (answer != MessageBoxResult.Yes)
                return;

            CollectionState.Collections.Remove(collection);
            _importedCounts.Remove(collection.Id);
            App.FastFlagCollections.Save();
            RefreshCollections();
        }

        private void CollectionMigrate_Click(object sender, RoutedEventArgs e)
        {
            var presets = Workspace.State.Prop.Presets;
            var (converted, skipped) = PresetMigration.FromPresets(presets, AllNames(), DateTime.Now);

            // the old FastFlagWorkspace.json is only read, never written
            CollectionState.Collections.AddRange(converted);
            CollectionState.PresetsMigrated = true;
            App.FastFlagCollections.Save();
            RefreshCollections();

            if (skipped > 0)
                Notify($"{skipped} {(skipped == 1 ? "preset" : "presets")} without valid flags {(skipped == 1 ? "was" : "were")} skipped.");
        }

        #endregion
    }
}
