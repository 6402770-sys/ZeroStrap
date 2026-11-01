using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ZeroStrap.Macros;

namespace ZeroStrap.UI.ViewModels.Shell
{
    /// <summary>The row of the timeline an action is drawn in.</summary>
    public enum TimelineLane
    {
        Keys,
        Mouse,
        Camera,
        Wait,
        Groups
    }

    /// <summary>
    /// One block of the timeline: when the action starts (after its delay) and how long it runs, in authored ms (speed 1×).
    /// Items come in flattened (list) order, so <see cref="Order"/> matches <see cref="MacroStudioLogic.Flatten"/>.
    /// </summary>
    public sealed record TimelineItem(MacroAction Action, MacroAction? Parent, int Depth, long StartMs, long DurationMs, TimelineLane Lane)
    {
        /// <summary>Index in flattened order.</summary>
        public int Order { get; init; }

        /// <summary>False when the player skips the action (disabled itself, inside a skipped group, or an empty group).</summary>
        public bool Active { get; init; } = true;

        /// <summary>Groups: how many times the children run (≥ 1). 1 for everything else.</summary>
        public int Iterations { get; init; } = 1;

        /// <summary>
        /// Groups: the period of one iteration (the first pass padded to the player's minimum). Iteration k (0-based) starts at
        /// StartMs + k * IterationMs; the children are laid out for the first one only. Equals DurationMs for everything else.
        /// </summary>
        public long IterationMs { get; init; }

        public long EndMs => MacroTimelineLogic.SatAdd(StartMs, DurationMs);
    }

    /// <summary>Result of <see cref="MacroTimelineLogic.Layout"/>.</summary>
    public sealed record TimelineLayout(IReadOnlyList<TimelineItem> Items, long TotalMs)
    {
        public static readonly TimelineLayout Empty = new(Array.Empty<TimelineItem>(), 0);
    }

    /// <summary>
    /// Pure logic of the Macro Studio timeline (no WPF): laying out the sequential action model on a time axis, the edits the
    /// timeline makes (retime, resize, multi-select delete/duplicate/group) and the px↔ms maths of the control.
    /// Times follow <see cref="MacroPlayer"/> exactly at speed 1×, so the layout total equals
    /// <see cref="MacroTiming.EstimateDurationMs"/> of a macro that runs once.
    /// </summary>
    public static class MacroTimelineLogic
    {
        /// <summary>Drag and resize positions snap to this many ms (unless the user holds Alt).</summary>
        public const int SnapMs = 10;

        /// <summary>Narrowest block in px, so 0-ms actions (key down, move to, …) stay visible and clickable.</summary>
        public const double MinBlockPx = 6;

        /// <summary>Zoom range in px per ms (1 px = 1 s … 1 ms = 2 px).</summary>
        public const double MinPxPerMs = 0.001;
        public const double MaxPxPerMs = 2.0;

        /// <summary>Default zoom: 1 s = 200 px.</summary>
        public const double DefaultPxPerMs = 0.2;

        // ---------------------------------------------------------------- layout

        public static TimelineLane LaneOf(MacroAction a) => a.Kind switch
        {
            MacroActionKind.Key => TimelineLane.Keys,
            MacroActionKind.Mouse or MacroActionKind.MouseMoveTo => TimelineLane.Mouse,
            MacroActionKind.Camera => TimelineLane.Camera,
            MacroActionKind.Group => TimelineLane.Groups,
            _ => TimelineLane.Wait
        };

        /// <summary>How long the action itself runs once its delay is over (groups excluded: see <see cref="Layout"/>).</summary>
        public static long OwnDurationMs(MacroAction a) => a.Kind switch
        {
            MacroActionKind.Key => a.KeyMode == MacroKeyMode.Tap ? Math.Max(0, a.HoldMs) : 0,
            MacroActionKind.Mouse => a.ButtonMode == MacroButtonMode.Click ? Math.Max(0, a.HoldMs) : 0,
            MacroActionKind.Camera => Math.Max(0, a.DurationMs),
            MacroActionKind.Wait => Math.Max(0, a.Ms),
            _ => 0
        };

        /// <summary>
        /// Lays the actions out in time. Disabled actions (and everything the player skips) are still listed, with
        /// <see cref="TimelineItem.Active"/> false, at the time the player reaches them and with no duration — the player
        /// skips them, delay included. A group spans all its iterations; its children are laid out for the first one.
        /// </summary>
        public static TimelineLayout Layout(IReadOnlyList<MacroAction>? root)
        {
            if (root is null || root.Count == 0)
                return TimelineLayout.Empty;

            var items = new List<TimelineItem>();
            long total = LayoutList(root, null, 0, 0, true, items);
            return new TimelineLayout(items, total);
        }

        /// <summary>Lays out one list from <paramref name="startMs"/> and returns where it ends.</summary>
        private static long LayoutList(IReadOnlyList<MacroAction> list, MacroAction? parent, int depth, long startMs, bool active, List<TimelineItem> into)
        {
            long t = startMs;

            foreach (var a in list)
            {
                if (a is null)
                    continue;

                bool runs = active && a.Enabled;
                int slot = into.Count;

                if (!runs)
                {
                    into.Add(new TimelineItem(a, parent, depth, t, 0, LaneOf(a)) { Order = slot, Active = false });

                    if (a.Kind == MacroActionKind.Group && a.Actions is not null)
                        LayoutList(a.Actions, a, depth + 1, t, false, into);

                    continue;
                }

                long start = SatAdd(t, Math.Max(0, a.DelayBeforeMs));

                if (a.Kind == MacroActionKind.Group)
                {
                    var children = a.Actions ?? new List<MacroAction>();
                    bool effect = MacroTiming.HasEffect(children);
                    into.Add(null!); // placeholder: the group's item is known once its children are laid out

                    long pass = LayoutList(children, a, depth + 1, start, effect, into) - start;
                    int repeat = Math.Max(1, a.GroupRepeat);
                    long duration = effect ? Repeated(pass, repeat, MacroTiming.MinGroupIterationMs) : 0;
                    long iteration = effect ? Math.Max(pass, MacroTiming.MinGroupIterationMs) : 0;

                    into[slot] = new TimelineItem(a, parent, depth, start, duration, TimelineLane.Groups)
                    {
                        Order = slot,
                        Active = effect,
                        Iterations = effect ? repeat : 1,
                        IterationMs = iteration
                    };

                    t = SatAdd(start, duration);
                }
                else
                {
                    long duration = OwnDurationMs(a);
                    into.Add(new TimelineItem(a, parent, depth, start, duration, LaneOf(a)) { Order = slot, IterationMs = duration });
                    t = SatAdd(start, duration);
                }
            }

            return t;
        }

        /// <summary>The item of an action (by reference), or null.</summary>
        public static TimelineItem? Find(TimelineLayout layout, MacroAction action) =>
            layout.Items.FirstOrDefault(i => ReferenceEquals(i.Action, action));

        // ---------------------------------------------------------------- retime / resize

        /// <summary>
        /// Moves an action so it starts at <paramref name="newStartMs"/> by changing its <see cref="MacroAction.DelayBeforeMs"/>.
        /// It cannot start before the previous sibling ends (delay ≥ 0) nor wait longer than the validator allows; everything
        /// after it ripples (the model is sequential). Actions the player skips cannot be moved. Returns the start applied.
        /// </summary>
        public static long Retime(IReadOnlyList<MacroAction> root, MacroAction action, long newStartMs)
        {
            var item = Find(Layout(root), action);

            if (item is null)
                return 0;

            // Skipped actions are laid out without their delay, so there is nothing to move. (An enabled but empty group
            // is reached: the player still waits its delay, so it moves like any other action.)
            if (!IsReached(root, action))
                return item.StartMs;

            long baseMs = item.StartMs - Math.Max(0, action.DelayBeforeMs);
            long delay = Math.Clamp(newStartMs - baseMs, 0, MacroValidator.MaxTimeMs);
            action.DelayBeforeMs = (int)delay;
            return baseMs + delay;
        }

        /// <summary>True when the player gets to the action (it and all its ancestors are enabled and run).</summary>
        private static bool IsReached(IReadOnlyList<MacroAction> root, MacroAction action)
        {
            var path = new List<MacroAction>();

            if (!PathTo(root, action, path))
                return false;

            // Every ancestor must be enabled and have something to run; the action itself only needs to be enabled.
            for (int i = 0; i < path.Count - 1; i++)
            {
                if (!path[i].Enabled || !MacroTiming.HasEffect(path[i].Actions ?? new List<MacroAction>()))
                    return false;
            }

            return action.Enabled;
        }

        private static bool PathTo(IReadOnlyList<MacroAction> list, MacroAction target, List<MacroAction> path)
        {
            foreach (var a in list)
            {
                if (a is null)
                    continue;

                path.Add(a);

                if (ReferenceEquals(a, target))
                    return true;

                if (a.Kind == MacroActionKind.Group && a.Actions is not null && PathTo(a.Actions, target, path))
                    return true;

                path.RemoveAt(path.Count - 1);
            }

            return false;
        }

        /// <summary>Key tap, mouse click, camera and wait have a length the user can drag; the rest are instantaneous or derived.</summary>
        public static bool CanResize(MacroAction a) => a.Kind switch
        {
            MacroActionKind.Key => a.KeyMode == MacroKeyMode.Tap,
            MacroActionKind.Mouse => a.ButtonMode == MacroButtonMode.Click,
            MacroActionKind.Camera or MacroActionKind.Wait => true,
            _ => false
        };

        /// <summary>
        /// Sets the action's length (hold time, camera duration — its speed follows — or wait), clamped to 0 … the validator's
        /// maximum. Returns false (and changes nothing) when the action cannot be resized.
        /// </summary>
        public static bool Resize(MacroAction action, long newDurationMs)
        {
            if (!CanResize(action))
                return false;

            int ms = (int)Math.Clamp(newDurationMs, 0, MacroValidator.MaxTimeMs);

            switch (action.Kind)
            {
                case MacroActionKind.Key:
                case MacroActionKind.Mouse:
                    action.HoldMs = ms;
                    break;
                case MacroActionKind.Camera:
                    action.DurationMs = ms;
                    break;
                case MacroActionKind.Wait:
                    action.Ms = ms;
                    break;
            }

            return true;
        }

        // ---------------------------------------------------------------- selection helpers

        /// <summary>Every action in flattened order.</summary>
        public static List<MacroAction> All(IEnumerable<MacroAction> root) =>
            MacroStudioLogic.Flatten(root).Select(f => f.Action).ToList();

        /// <summary>The actions between <paramref name="from"/> and <paramref name="to"/> (inclusive, either order) in flattened order.</summary>
        public static List<MacroAction> Range(IEnumerable<MacroAction> root, MacroAction from, MacroAction to)
        {
            var all = All(root);
            int a = all.FindIndex(x => ReferenceEquals(x, from));
            int b = all.FindIndex(x => ReferenceEquals(x, to));

            if (a < 0 && b < 0)
                return new List<MacroAction>();

            if (a < 0)
                a = b;
            else if (b < 0)
                b = a;

            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            return all.GetRange(lo, hi - lo + 1);
        }

        /// <summary>The action <paramref name="delta"/> steps away in flattened order (clamped to the ends), or null when empty.</summary>
        public static MacroAction? Neighbour(IEnumerable<MacroAction> root, MacroAction? from, int delta)
        {
            var all = All(root);

            if (all.Count == 0)
                return null;

            int index = from is null ? -1 : all.FindIndex(x => ReferenceEquals(x, from));

            if (index < 0)
                return delta < 0 ? all[^1] : all[0];

            return all[Math.Clamp(index + delta, 0, all.Count - 1)];
        }

        /// <summary>
        /// The selected actions that are not inside another selected group, in flattened order (an operation on a group
        /// already covers its children).
        /// </summary>
        public static List<MacroAction> TopMost(IEnumerable<MacroAction> root, IEnumerable<MacroAction> selection)
        {
            var set = new HashSet<MacroAction>(selection, ReferenceEqualityComparer.Instance);
            var result = new List<MacroAction>();
            CollectTopMost(root, set, result);
            return result;
        }

        private static void CollectTopMost(IEnumerable<MacroAction> list, HashSet<MacroAction> set, List<MacroAction> into)
        {
            foreach (var a in list)
            {
                if (a is null)
                    continue;

                if (set.Contains(a))
                    into.Add(a);
                else if (a.Kind == MacroActionKind.Group && a.Actions is not null)
                    CollectTopMost(a.Actions, set, into);
            }
        }

        // ---------------------------------------------------------------- multi-select edits

        /// <summary>Removes the selected actions (from any depth). Returns how many were removed (children of a removed group not counted).</summary>
        public static int DeleteMany(List<MacroAction> root, IEnumerable<MacroAction> selection)
        {
            int removed = 0;

            foreach (var a in TopMost(root, selection))
            {
                if (MacroStudioLogic.TryFind(root, a, out var list, out int index))
                {
                    list.RemoveAt(index);
                    removed++;
                }
            }

            return removed;
        }

        /// <summary>False when duplicating would take the macro past the validator's action limit.</summary>
        public static bool CanDuplicate(List<MacroAction> root, IEnumerable<MacroAction> selection)
        {
            var top = TopMost(root, selection);
            return top.Count > 0 && MacroStudioLogic.CountAll(root) + MacroStudioLogic.CountAll(top) <= MacroValidator.MaxNodes;
        }

        /// <summary>
        /// Inserts deep copies of the selected actions; the copies of each list go, in list order, right after the last
        /// selected action of that list. Returns the copies (in the order of their originals), empty when not allowed.
        /// </summary>
        public static List<MacroAction> DuplicateMany(List<MacroAction> root, IEnumerable<MacroAction> selection)
        {
            var copies = new List<MacroAction>();

            if (!CanDuplicate(root, selection))
                return copies;

            var top = TopMost(root, selection);
            var byList = new Dictionary<List<MacroAction>, List<int>>(ReferenceEqualityComparer.Instance);
            var order = new List<List<MacroAction>>();

            foreach (var a in top)
            {
                if (!MacroStudioLogic.TryFind(root, a, out var list, out int index))
                    continue;

                if (!byList.TryGetValue(list, out var indexes))
                {
                    byList[list] = indexes = new List<int>();
                    order.Add(list);
                }

                indexes.Add(index);
            }

            var copyOf = new Dictionary<MacroAction, MacroAction>(ReferenceEqualityComparer.Instance);

            foreach (var list in order)
            {
                var indexes = byList[list];
                indexes.Sort();
                var batch = indexes.Select(i => list[i]).ToList();
                var clones = batch.Select(a => a.Clone()).ToList();

                list.InsertRange(indexes[^1] + 1, clones);

                for (int i = 0; i < batch.Count; i++)
                    copyOf[batch[i]] = clones[i];
            }

            foreach (var a in top)
            {
                if (copyOf.TryGetValue(a, out var copy))
                    copies.Add(copy);
            }

            return copies;
        }

        /// <summary>
        /// True when the selection can be wrapped in a new group: everything is in the same list, and the extra level stays
        /// within the validator's nesting and action limits.
        /// </summary>
        public static bool CanGroup(List<MacroAction> root, IEnumerable<MacroAction> selection)
        {
            var chosen = selection.Distinct<MacroAction>(ReferenceEqualityComparer.Instance).ToList();

            if (chosen.Count == 0)
                return false;

            List<MacroAction>? parentList = null;

            foreach (var a in chosen)
            {
                if (!MacroStudioLogic.TryFind(root, a, out var list, out _))
                    return false;

                if (parentList is null)
                    parentList = list;
                else if (!ReferenceEquals(parentList, list))
                    return false;
            }

            var row = MacroStudioLogic.Flatten(root).First(f => ReferenceEquals(f.Action, chosen[0]));
            int height = chosen.Max(GroupHeight);

            // The validator counts the top level as group depth 1 and allows groups down to MaxGroupDepth.
            if (row.Depth + 1 + height > MacroValidator.MaxGroupDepth)
                return false;

            return MacroStudioLogic.CountAll(root) + 1 <= MacroValidator.MaxNodes;
        }

        /// <summary>How many levels of groups an action holds (0 for a plain action, 1 for a group of plain actions).</summary>
        private static int GroupHeight(MacroAction a) =>
            a.Kind != MacroActionKind.Group ? 0 : 1 + (a.Actions?.Where(c => c is not null).Select(GroupHeight).DefaultIfEmpty(0).Max() ?? 0);

        /// <summary>
        /// Moves the selected actions (same list, kept in list order) into a new group (×1, no delay) placed where the first
        /// one was. Timing is unchanged. Returns the group, or null when <see cref="CanGroup"/> is false.
        /// </summary>
        public static MacroAction? GroupMany(List<MacroAction> root, IEnumerable<MacroAction> selection)
        {
            var chosen = selection.Distinct<MacroAction>(ReferenceEqualityComparer.Instance).ToList();

            if (!CanGroup(root, chosen))
                return null;

            MacroStudioLogic.TryFind(root, chosen[0], out var list, out _);

            var indexes = chosen.Select(a => list.FindIndex(x => ReferenceEquals(x, a))).OrderBy(i => i).ToList();
            var group = new MacroAction
            {
                Kind = MacroActionKind.Group,
                GroupRepeat = 1,
                Actions = indexes.Select(i => list[i]).ToList()
            };

            for (int i = indexes.Count - 1; i >= 0; i--)
                list.RemoveAt(indexes[i]);

            list.Insert(indexes[0], group);
            return group;
        }

        // ---------------------------------------------------------------- px ↔ ms, zoom, snap

        /// <summary>X (px, from the left edge of the time area) of a time, at the given zoom and scroll offset.</summary>
        public static double MsToPx(double ms, double pxPerMs, double offsetMs) => (ms - offsetMs) * pxPerMs;

        /// <summary>Time under an x position (px, from the left edge of the time area).</summary>
        public static double PxToMs(double px, double pxPerMs, double offsetMs) => px / pxPerMs + offsetMs;

        /// <summary>Rounds a time to whole ms, or to the <see cref="SnapMs"/> grid when snapping, never below 0.</summary>
        public static long Snap(double ms, bool snap = true)
        {
            if (double.IsNaN(ms) || ms <= 0)
                return 0;

            double rounded = snap ? Math.Round(ms / SnapMs, MidpointRounding.AwayFromZero) * SnapMs : Math.Round(ms, MidpointRounding.AwayFromZero);
            return rounded >= long.MaxValue ? long.MaxValue : (long)rounded;
        }

        /// <summary>Drawn width of a block: its duration at the zoom, but at least <see cref="MinBlockPx"/>.</summary>
        public static double BlockWidthPx(long durationMs, double pxPerMs) => Math.Max(MinBlockPx, durationMs * pxPerMs);

        public static double ClampZoom(double pxPerMs) =>
            double.IsNaN(pxPerMs) || pxPerMs <= 0 ? DefaultPxPerMs : Math.Clamp(pxPerMs, MinPxPerMs, MaxPxPerMs);

        /// <summary>
        /// Zooms by <paramref name="factor"/> keeping the time under <paramref name="anchorPx"/> where it is. Returns the new
        /// zoom and the new offset (not below 0).
        /// </summary>
        public static (double PxPerMs, double OffsetMs) ZoomAround(double pxPerMs, double offsetMs, double anchorPx, double factor)
        {
            double anchorMs = PxToMs(anchorPx, pxPerMs, offsetMs);
            double zoom = ClampZoom(pxPerMs * factor);
            double offset = Math.Max(0, anchorMs - anchorPx / zoom);
            return (zoom, offset);
        }

        /// <summary>Zoom that fits <paramref name="totalMs"/> (plus a little room) into <paramref name="viewportPx"/>.</summary>
        public static double FitZoom(long totalMs, double viewportPx)
        {
            if (viewportPx <= 0)
                return DefaultPxPerMs;

            double span = Math.Max(100, totalMs * 1.05);
            return ClampZoom(viewportPx / span);
        }

        /// <summary>Scrollable time range: the macro plus some room after it to drag into.</summary>
        public static long ExtentMs(long totalMs) => SatAdd(totalMs, Math.Max(1000, totalMs / 10));

        /// <summary>Tick spacing (1, 2 or 5 × 10^k ms) so labels are at least <paramref name="minPx"/> apart.</summary>
        public static long TickStepMs(double pxPerMs, double minPx = 64)
        {
            pxPerMs = ClampZoom(pxPerMs);
            long step = 1;

            while (step < long.MaxValue / 10)
            {
                foreach (long m in new long[] { 1, 2, 5 })
                {
                    if (step * m * pxPerMs >= minPx)
                        return step * m;
                }

                step *= 10;
            }

            return step;
        }

        /// <summary>Ruler label: "0s", "0.5s", "12s", "1m", "1m 30s", "250ms" (only when the ticks are finer than 10 ms).</summary>
        public static string FormatTick(long ms, long stepMs)
        {
            if (stepMs < 10 && ms % 1000 != 0)
                return ms.ToString(CultureInfo.InvariantCulture) + "ms";

            if (ms < 60_000)
                return (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + "s";

            long minutes = ms / 60_000;
            double seconds = (ms % 60_000) / 1000.0;
            return seconds == 0
                ? minutes.ToString(CultureInfo.InvariantCulture) + "m"
                : minutes.ToString(CultureInfo.InvariantCulture) + "m " + seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
        }

        /// <summary>True when [startMs, startMs + max(duration, minimum block)] touches the visible window.</summary>
        public static bool IsVisible(TimelineItem item, double pxPerMs, double windowStartMs, double windowEndMs)
        {
            double end = item.StartMs + BlockWidthPx(item.DurationMs, pxPerMs) / pxPerMs;
            return end >= windowStartMs && item.StartMs <= windowEndMs;
        }

        // ---------------------------------------------------------------- saturating maths (matches MacroTiming)

        private static long Repeated(long pass, long times, long floor)
        {
            long padded = Math.Max(pass, floor);
            return SatAdd(SatMul(padded, times - 1), pass);
        }

        internal static long SatAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

        private static long SatMul(long a, long b)
        {
            if (a == 0 || b == 0)
                return 0;

            return a > long.MaxValue / b ? long.MaxValue : a * b;
        }
    }
}
