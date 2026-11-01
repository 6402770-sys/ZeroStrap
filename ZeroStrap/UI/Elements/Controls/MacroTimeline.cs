using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

using ZeroStrap.Macros;
using ZeroStrap.UI.ViewModels.Shell;

namespace ZeroStrap.UI.Elements.Controls
{
    /// <summary>
    /// Timeline view of a macro's actions: a fixed lane header, a time ruler and one row per <see cref="TimelineLane"/>, drawn
    /// in <see cref="OnRender"/>. Only items that intersect the visible time window are drawn and hit-tested.
    /// <para>
    /// The host sets <see cref="Actions"/> (the macro's root list, edited in place) and <see cref="LabelProvider"/>, puts a
    /// horizontal ScrollBar under the control (Maximum = <see cref="ScrollableMs"/>, ViewportSize = <see cref="ViewportMs"/>,
    /// Value = <see cref="HorizontalOffsetMs"/>, or listen to <see cref="ViewChanged"/>), and calls <see cref="Refresh"/>
    /// whenever the actions change elsewhere. Every edit the control makes goes through <see cref="MacroTimelineLogic"/> and is
    /// followed by <see cref="ActionsEdited"/>; drags edit a scratch copy until the mouse is released (Esc cancels).
    /// </para>
    /// </summary>
    public sealed class MacroTimeline : FrameworkElement
    {
        /// <summary>Width of the lane name column on the left.</summary>
        public const double LaneHeaderWidth = 72;

        public const double RulerHeight = 22;
        public const double LaneHeight = 28;

        private const double BlockInset = 4;
        private const double EdgeGrip = 5;
        private const double DimOpacity = 0.4;
        private const double ZoomStep = 1.5;

        private static readonly string[] LaneNames = { "Keys", "Mouse", "Camera", "Wait", "Groups" };
        private static readonly int LaneCount = LaneNames.Length;

        // ---------------------------------------------------------------- public dependency properties

        /// <summary>Zoom in px per ms (clamped to <see cref="MacroTimelineLogic.MinPxPerMs"/> … <see cref="MacroTimelineLogic.MaxPxPerMs"/>).</summary>
        public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
            nameof(Zoom), typeof(double), typeof(MacroTimeline),
            new FrameworkPropertyMetadata(MacroTimelineLogic.DefaultPxPerMs, (d, _) => ((MacroTimeline)d).UpdateView(),
                (_, v) => MacroTimelineLogic.ClampZoom((double)v)));

        /// <summary>Time (ms) at the left edge of the time area; clamped to 0 … <see cref="ScrollableMs"/>. Binds two-way.</summary>
        public static readonly DependencyProperty HorizontalOffsetMsProperty = DependencyProperty.Register(
            nameof(HorizontalOffsetMs), typeof(double), typeof(MacroTimeline),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, _) => ((MacroTimeline)d).OnOffsetChanged(), (d, v) => ((MacroTimeline)d).CoerceOffset((double)v)));

        private static readonly DependencyPropertyKey ExtentMsKey = DependencyProperty.RegisterReadOnly(
            nameof(ExtentMs), typeof(double), typeof(MacroTimeline), new PropertyMetadata(0.0));

        private static readonly DependencyPropertyKey ViewportMsKey = DependencyProperty.RegisterReadOnly(
            nameof(ViewportMs), typeof(double), typeof(MacroTimeline), new PropertyMetadata(0.0));

        private static readonly DependencyPropertyKey ScrollableMsKey = DependencyProperty.RegisterReadOnly(
            nameof(ScrollableMs), typeof(double), typeof(MacroTimeline), new PropertyMetadata(0.0));

        /// <summary>Scrollable length of the timeline (ms): the macro plus room after it.</summary>
        public static readonly DependencyProperty ExtentMsProperty = ExtentMsKey.DependencyProperty;

        /// <summary>Visible length (ms) at the current width and zoom.</summary>
        public static readonly DependencyProperty ViewportMsProperty = ViewportMsKey.DependencyProperty;

        /// <summary>Largest <see cref="HorizontalOffsetMs"/>: max(0, ExtentMs − ViewportMs).</summary>
        public static readonly DependencyProperty ScrollableMsProperty = ScrollableMsKey.DependencyProperty;

        // ---------------------------------------------------------------- theme resources (DynamicResource-style)

        private static readonly DependencyProperty KeysBrushProperty = BrushProperty("KeysBrush");
        private static readonly DependencyProperty MouseBrushProperty = BrushProperty("MouseBrush");
        private static readonly DependencyProperty CameraBrushProperty = BrushProperty("CameraBrush");
        private static readonly DependencyProperty WaitBrushProperty = BrushProperty("WaitBrush");
        private static readonly DependencyProperty GroupsBrushProperty = BrushProperty("GroupsBrush");
        private static readonly DependencyProperty TextBrushProperty = BrushProperty("TextBrush");
        private static readonly DependencyProperty MutedTextBrushProperty = BrushProperty("MutedTextBrush");
        private static readonly DependencyProperty BackgroundBrushProperty = BrushProperty("BackgroundBrush");
        private static readonly DependencyProperty HeaderBrushProperty = BrushProperty("HeaderBrush");
        private static readonly DependencyProperty DividerBrushProperty = BrushProperty("DividerBrush");

        private static readonly DependencyProperty AccentColorProperty = DependencyProperty.Register(
            "AccentColor", typeof(Color), typeof(MacroTimeline),
            new FrameworkPropertyMetadata(Color.FromRgb(0x5E, 0x9B, 0xD6), FrameworkPropertyMetadataOptions.AffectsRender));

        private static readonly DependencyProperty LabelFontSizeProperty = DependencyProperty.Register(
            "LabelFontSize", typeof(double), typeof(MacroTimeline),
            new FrameworkPropertyMetadata(11.0, FrameworkPropertyMetadataOptions.AffectsRender));

        private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
            name, typeof(Brush), typeof(MacroTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        // ---------------------------------------------------------------- state

        private enum DragMode { None, Pending, Move, Resize, Band }

        private List<MacroAction>? _actions;
        private TimelineLayout _layout = TimelineLayout.Empty;
        private Func<MacroAction, string>? _labelProvider;

        private readonly HashSet<MacroAction> _selected = new(ReferenceEqualityComparer.Instance);
        private MacroAction? _primary;

        private DragMode _mode;
        private bool _pendingResize;
        private Point _downPoint;
        private TimelineItem? _dragItem;
        private List<MacroAction>? _scratchRoot;   // deep copy edited while dragging
        private MacroAction? _scratchAction;
        private TimelineLayout? _preview;          // layout of _scratchRoot (same item order as _layout)
        private long _dragValue;                   // start (move) or duration (resize) being previewed
        private Rect _band;
        private List<MacroAction> _bandBase = new();
        private List<MacroAction> _selectionAtDown = new();

        private readonly Dictionary<Brush, Brush> _softBrushes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Brush, Brush> _hatchBrushes = new(ReferenceEqualityComparer.Instance);

        /// <summary>Raised when the user changes the selection (not by <see cref="SetSelection"/>).</summary>
        public event EventHandler? SelectionChanged;

        /// <summary>Raised after the control committed an edit to <see cref="Actions"/>; the host saves and refreshes other views.</summary>
        public event EventHandler? ActionsEdited;

        /// <summary>Raised when the zoom, scroll offset, extent or viewport changes.</summary>
        public event EventHandler? ViewChanged;

        public MacroTimeline()
        {
            Focusable = true;
            ClipToBounds = true;
            SnapsToDevicePixels = true;
            AutomationProperties.SetName(this, "Macro timeline");

            SetResourceReference(KeysBrushProperty, "ZeroStatusInfoBrush");
            SetResourceReference(MouseBrushProperty, "ZeroStatusHealthyBrush");
            SetResourceReference(CameraBrushProperty, "ZeroStatusWarningBrush");
            SetResourceReference(WaitBrushProperty, "ZeroStatusIdleBrush");
            SetResourceReference(GroupsBrushProperty, "TextFillColorSecondaryBrush");
            SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
            SetResourceReference(MutedTextBrushProperty, "TextFillColorTertiaryBrush");
            SetResourceReference(BackgroundBrushProperty, "ControlFillColorDefaultBrush");
            SetResourceReference(HeaderBrushProperty, "CardBackgroundFillColorSecondaryBrush");
            SetResourceReference(DividerBrushProperty, "DividerStrokeColorDefaultBrush");
            SetResourceReference(AccentColorProperty, "SystemAccentColorSecondary");
            SetResourceReference(LabelFontSizeProperty, "ZeroFontSize11");
        }

        // ---------------------------------------------------------------- public API

        /// <summary>The macro's root action list. The control edits it in place (only through <see cref="MacroTimelineLogic"/>).</summary>
        public List<MacroAction>? Actions
        {
            get => _actions;
            set
            {
                if (ReferenceEquals(_actions, value))
                {
                    Refresh();
                    return;
                }

                CancelDrag();
                _actions = value;
                _selected.Clear();
                _primary = null;
                Refresh();
                HorizontalOffsetMs = 0;
            }
        }

        /// <summary>Block label (e.g. <c>a => MacroStudioLogic.Summary(a, keyName)</c>). Defaults to <see cref="MacroStudioLogic.Summary"/>.</summary>
        public Func<MacroAction, string>? LabelProvider
        {
            get => _labelProvider;
            set
            {
                _labelProvider = value;
                InvalidateVisual();
            }
        }

        public double Zoom
        {
            get => (double)GetValue(ZoomProperty);
            set => SetValue(ZoomProperty, value);
        }

        public double HorizontalOffsetMs
        {
            get => (double)GetValue(HorizontalOffsetMsProperty);
            set => SetValue(HorizontalOffsetMsProperty, value);
        }

        public double ExtentMs => (double)GetValue(ExtentMsProperty);

        public double ViewportMs => (double)GetValue(ViewportMsProperty);

        public double ScrollableMs => (double)GetValue(ScrollableMsProperty);

        /// <summary>The current layout (authored times at 1× speed).</summary>
        public TimelineLayout Layout => _layout;

        /// <summary>Selected actions in flattened (list) order.</summary>
        public IReadOnlyList<MacroAction> SelectedActions =>
            _layout.Items.Select(i => i.Action).Where(_selected.Contains).ToList();

        /// <summary>The action last clicked or navigated to (the one an inspector edits); null when nothing is selected.</summary>
        public MacroAction? PrimaryAction => _primary;

        public bool CanDeleteSelection => _actions is not null && _selected.Count > 0;

        public bool CanDuplicateSelection => _actions is not null && _selected.Count > 0 && MacroTimelineLogic.CanDuplicate(_actions, _selected);

        public bool CanGroupSelection => _actions is not null && _selected.Count > 0 && MacroTimelineLogic.CanGroup(_actions, _selected);

        /// <summary>Re-lays out <see cref="Actions"/> after they changed elsewhere; drops selected actions that no longer exist.</summary>
        public void Refresh()
        {
            _layout = MacroTimelineLogic.Layout(_actions);

            if (_selected.Count > 0)
            {
                var present = new HashSet<MacroAction>(_layout.Items.Select(i => i.Action), ReferenceEqualityComparer.Instance);
                _selected.RemoveWhere(a => !present.Contains(a));

                if (_primary is not null && !present.Contains(_primary))
                    _primary = _selected.Count > 0 ? SelectedActions[^1] : null;
            }

            UpdateView();
            InvalidateVisual();
        }

        /// <summary>
        /// Selects the given actions (others are ignored) without raising <see cref="SelectionChanged"/>, so a host can mirror
        /// another view's selection. <paramref name="primary"/> defaults to the last given action.
        /// </summary>
        public void SetSelection(IEnumerable<MacroAction>? actions, MacroAction? primary = null)
        {
            var list = actions?.ToList() ?? new List<MacroAction>();
            ApplySelection(list, primary ?? list.LastOrDefault(), raise: false);
        }

        public void SelectAll()
        {
            var all = _layout.Items.Select(i => i.Action).ToList();
            ApplySelection(all, _primary ?? all.FirstOrDefault(), raise: true);
        }

        /// <summary>Deletes the selected actions (from any depth). Raises <see cref="ActionsEdited"/> and <see cref="SelectionChanged"/>.</summary>
        public bool DeleteSelection()
        {
            if (!CanDeleteSelection)
                return false;

            CancelDrag();
            MacroTimelineLogic.DeleteMany(_actions!, _selected.ToList());
            _selected.Clear();
            _primary = null;
            Committed(selectionChanged: true);
            return true;
        }

        /// <summary>Duplicates the selected actions and selects the copies.</summary>
        public bool DuplicateSelection()
        {
            if (!CanDuplicateSelection)
                return false;

            CancelDrag();
            var copies = MacroTimelineLogic.DuplicateMany(_actions!, _selected.ToList());

            if (copies.Count == 0)
                return false;

            Select(copies, copies[^1]);
            Committed(selectionChanged: true);
            return true;
        }

        /// <summary>Wraps the selected actions (all in one list) in a new ×1 group and selects it.</summary>
        public bool GroupSelection()
        {
            if (!CanGroupSelection)
                return false;

            CancelDrag();
            var group = MacroTimelineLogic.GroupMany(_actions!, _selected.ToList());

            if (group is null)
                return false;

            Select(new[] { group }, group);
            Committed(selectionChanged: true);
            return true;
        }

        public void ZoomIn() => ZoomBy(ZoomStep, TimeAreaWidth / 2);

        public void ZoomOut() => ZoomBy(1 / ZoomStep, TimeAreaWidth / 2);

        /// <summary>Zooms so the whole macro fits the visible width and scrolls to the start.</summary>
        public void ZoomToFit()
        {
            Zoom = MacroTimelineLogic.FitZoom(_layout.TotalMs, TimeAreaWidth);
            HorizontalOffsetMs = 0;
        }

        /// <summary>Scrolls horizontally so the action's start is visible.</summary>
        public void ScrollIntoView(MacroAction action)
        {
            var item = MacroTimelineLogic.Find(_layout, action);

            if (item is null)
                return;

            double viewport = ViewportMs;
            double end = item.StartMs + Math.Min(item.DurationMs, viewport * 0.8);

            if (item.StartMs < HorizontalOffsetMs || end > HorizontalOffsetMs + viewport)
                HorizontalOffsetMs = item.StartMs - viewport * 0.1;
        }

        // ---------------------------------------------------------------- view maths

        private double TimeAreaWidth => Math.Max(0, ActualWidth - LaneHeaderWidth);

        private double CoerceOffset(double value)
        {
            if (double.IsNaN(value))
                return 0;

            return Math.Clamp(value, 0, Math.Max(0, ScrollableMs));
        }

        private void UpdateView()
        {
            double viewport = TimeAreaWidth / Zoom;
            double extent = MacroTimelineLogic.ExtentMs(_layout.TotalMs);

            SetValue(ViewportMsKey, viewport);
            SetValue(ExtentMsKey, extent);
            SetValue(ScrollableMsKey, Math.Max(0, extent - viewport));
            CoerceValue(HorizontalOffsetMsProperty);

            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnOffsetChanged()
        {
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ZoomBy(double factor, double anchorPx)
        {
            var (zoom, offset) = MacroTimelineLogic.ZoomAround(Zoom, HorizontalOffsetMs, anchorPx, factor);
            Zoom = zoom;
            HorizontalOffsetMs = offset;
        }

        private double XOf(double ms) => LaneHeaderWidth + MacroTimelineLogic.MsToPx(ms, Zoom, HorizontalOffsetMs);

        private static double LaneTop(TimelineLane lane) => RulerHeight + (int)lane * LaneHeight;

        /// <summary>The block of an item (groups: the whole span, nested ones inset a little).</summary>
        private Rect BlockRect(TimelineItem item)
        {
            double z = Zoom;
            double x = XOf(item.StartMs);
            double w = MacroTimelineLogic.BlockWidthPx(item.DurationMs, z);
            double inset = BlockInset + (item.Lane == TimelineLane.Groups ? Math.Min(item.Depth * 3, 8) : 0);
            return new Rect(x, LaneTop(item.Lane) + inset, w, Math.Max(4, LaneHeight - 2 * inset));
        }

        /// <summary>Keeps far-off coordinates within a sane range so huge zooms stay precise.</summary>
        private Rect ClampToView(Rect r)
        {
            double left = Math.Max(r.Left, LaneHeaderWidth - 8);
            double right = Math.Min(r.Right, ActualWidth + 8);
            return right <= left ? Rect.Empty : new Rect(left, r.Top, right - left, r.Height);
        }

        private bool IsResizeEdge(TimelineItem item, Rect r, Point p)
        {
            if (!item.Active || !MacroTimelineLogic.CanResize(item.Action))
                return false;

            // Wide blocks grab inside their right edge; narrow ones (0 ms markers) just outside it.
            return r.Width >= 3 * EdgeGrip
                ? p.X >= r.Right - EdgeGrip && p.X <= r.Right + 1
                : p.X >= r.Right - 1 && p.X <= r.Right + EdgeGrip;
        }

        private (TimelineItem Item, bool Edge)? HitTest(Point p)
        {
            if (p.X < LaneHeaderWidth || p.Y < RulerHeight)
                return null;

            int laneIndex = (int)((p.Y - RulerHeight) / LaneHeight);

            if (laneIndex >= LaneCount)
                return null;

            double z = Zoom;
            double from = HorizontalOffsetMs, to = from + ViewportMs;
            var items = _layout.Items;

            for (int i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i];

                if ((int)item.Lane != laneIndex || !MacroTimelineLogic.IsVisible(item, z, from, to))
                    continue;

                var r = BlockRect(item);
                bool edge = IsResizeEdge(item, r, p);

                if (edge || r.Contains(p))
                    return (item, edge);
            }

            return null;
        }

        // ---------------------------------------------------------------- selection

        private void Select(IEnumerable<MacroAction> actions, MacroAction? primary)
        {
            _selected.Clear();

            foreach (var a in actions)
                _selected.Add(a);

            _primary = primary is not null && _selected.Contains(primary) ? primary : _selected.LastOrDefault();
            InvalidateVisual();
        }

        private void ApplySelection(IReadOnlyCollection<MacroAction> actions, MacroAction? primary, bool raise)
        {
            var present = new HashSet<MacroAction>(_layout.Items.Select(i => i.Action), ReferenceEqualityComparer.Instance);
            var wanted = actions.Where(present.Contains).ToList();
            bool changed = !_selected.SetEquals(wanted) || !ReferenceEquals(_primary, primary);

            Select(wanted, primary);

            if (raise && changed)
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseSelectionIfChangedSince(List<MacroAction> before, MacroAction? primaryBefore)
        {
            if (!_selected.SetEquals(before) || !ReferenceEquals(_primary, primaryBefore))
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void MoveSelection(int delta)
        {
            if (_actions is null)
                return;

            var next = MacroTimelineLogic.Neighbour(_actions, _primary, delta);

            if (next is null)
                return;

            ApplySelection(new[] { next }, next, raise: true);
            ScrollIntoView(next);
        }

        private void Committed(bool selectionChanged)
        {
            Refresh();
            ActionsEdited?.Invoke(this, EventArgs.Empty);

            if (selectionChanged)
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------------- mouse

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();

            if (_actions is null)
                return;

            var p = e.GetPosition(this);
            var hit = HitTest(p);
            var mods = Keyboard.Modifiers;
            bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);

            _downPoint = p;
            _selectionAtDown = _selected.ToList();
            var primaryBefore = _primary;

            if (hit is { } h)
            {
                var action = h.Item.Action;

                if (shift)
                {
                    var range = MacroTimelineLogic.Range(_actions, _primary ?? action, action);
                    var anchor = _primary ?? action;
                    Select(ctrl ? _selected.Concat(range).ToList() : range, anchor);
                    _primary = anchor;
                }
                else if (ctrl)
                {
                    if (!_selected.Remove(action))
                        _selected.Add(action);

                    _primary = _selected.Contains(action) ? action : _selected.LastOrDefault();
                    InvalidateVisual();
                }
                else
                {
                    if (!(_selected.Count == 1 && _selected.Contains(action)))
                        Select(new[] { action }, action);

                    _primary = action;
                    _mode = DragMode.Pending;
                    _pendingResize = h.Edge;
                    _dragItem = h.Item;
                    CaptureMouse();
                }

                RaiseSelectionIfChangedSince(_selectionAtDown, primaryBefore);
            }
            else if (p.X >= LaneHeaderWidth && p.Y >= RulerHeight)
            {
                _bandBase = ctrl ? _selected.ToList() : new List<MacroAction>();

                if (!ctrl)
                {
                    _selected.Clear();
                    _primary = null;
                }

                _band = new Rect(p, p);
                _mode = DragMode.Band;
                CaptureMouse();
                InvalidateVisual();
            }

            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);

            switch (_mode)
            {
                case DragMode.None:
                    Cursor = HitTest(p) is { Edge: true } ? Cursors.SizeWE : null;
                    return;

                case DragMode.Pending:
                    if (Math.Abs(p.X - _downPoint.X) < SystemParameters.MinimumHorizontalDragDistance)
                        return;

                    BeginEditDrag();
                    break;

                case DragMode.Band:
                    _band = new Rect(_downPoint, p);
                    UpdateBandSelection();
                    InvalidateVisual();
                    return;
            }

            if (_mode is DragMode.Move or DragMode.Resize)
                UpdateEditDrag(p);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            var mode = _mode;
            var primaryBefore = _primary;
            _mode = DragMode.None;

            if (mode is DragMode.Move or DragMode.Resize)
                CommitEditDrag(mode);

            ClearDragState();

            if (IsMouseCaptured)
                ReleaseMouseCapture();

            if (mode == DragMode.Band)
            {
                InvalidateVisual();
                RaiseSelectionIfChangedSince(_selectionAtDown, primaryBefore);
            }

            e.Handled = mode != DragMode.None;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);

            if (_mode != DragMode.None)
                CancelDrag();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            double notches = e.Delta / 120.0;

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                double anchor = Math.Max(0, e.GetPosition(this).X - LaneHeaderWidth);
                ZoomBy(Math.Pow(1.25, notches), anchor);
                e.Handled = true;
            }
            else if (ScrollableMs > 0)
            {
                // Plain wheel scrolls the time axis; with nothing to scroll the page gets it.
                HorizontalOffsetMs -= notches * ViewportMs * 0.15;
                e.Handled = true;
            }
        }

        private void BeginEditDrag()
        {
            if (_actions is null || _dragItem is null)
            {
                _mode = DragMode.None;
                return;
            }

            // Edit a deep copy while dragging: the copy flattens in the same order as the real list.
            _scratchRoot = _actions.Select(a => a.Clone()).ToList();
            _scratchAction = MacroStudioLogic.Flatten(_scratchRoot)[_dragItem.Order].Action;
            _mode = _pendingResize ? DragMode.Resize : DragMode.Move;
            _dragValue = _mode == DragMode.Resize ? _dragItem.DurationMs : _dragItem.StartMs;
            Cursor = _mode == DragMode.Resize ? Cursors.SizeWE : Cursors.SizeAll;
        }

        private void UpdateEditDrag(Point p)
        {
            if (_dragItem is null || _scratchRoot is null || _scratchAction is null)
                return;

            bool snap = !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            double deltaMs = (p.X - _downPoint.X) / Zoom;

            if (_mode == DragMode.Move)
                _dragValue = MacroTimelineLogic.Retime(_scratchRoot, _scratchAction, MacroTimelineLogic.Snap(_dragItem.StartMs + deltaMs, snap));
            else
            {
                long duration = MacroTimelineLogic.Snap(_dragItem.DurationMs + deltaMs, snap);
                MacroTimelineLogic.Resize(_scratchAction, duration);
                _dragValue = MacroTimelineLogic.OwnDurationMs(_scratchAction);
            }

            _preview = MacroTimelineLogic.Layout(_scratchRoot);
            InvalidateVisual();
        }

        private void CommitEditDrag(DragMode mode)
        {
            if (_actions is null || _dragItem is null)
                return;

            var action = _dragItem.Action;
            bool changed;

            if (mode == DragMode.Move)
            {
                int before = action.DelayBeforeMs;
                MacroTimelineLogic.Retime(_actions, action, _dragValue);
                changed = action.DelayBeforeMs != before;
            }
            else
            {
                long before = MacroTimelineLogic.OwnDurationMs(action);
                MacroTimelineLogic.Resize(action, _dragValue);
                changed = MacroTimelineLogic.OwnDurationMs(action) != before;
            }

            _preview = null;

            if (changed)
                Committed(selectionChanged: false);
            else
                InvalidateVisual();
        }

        private void UpdateBandSelection()
        {
            double z = Zoom;
            double from = HorizontalOffsetMs, to = from + ViewportMs;
            var hits = _layout.Items
                .Where(i => MacroTimelineLogic.IsVisible(i, z, from, to) && BlockRect(i).IntersectsWith(_band))
                .Select(i => i.Action);

            var chosen = _bandBase.Concat(hits).ToList();
            Select(chosen, chosen.LastOrDefault());
        }

        /// <summary>Abandons a drag in progress: the model was never touched, and a rubber band restores the old selection.</summary>
        private void CancelDrag()
        {
            if (_mode == DragMode.None)
                return;

            if (_mode == DragMode.Band)
                Select(_selectionAtDown, _selectionAtDown.LastOrDefault());

            _mode = DragMode.None;
            ClearDragState();

            if (IsMouseCaptured)
                ReleaseMouseCapture();

            InvalidateVisual();
        }

        private void ClearDragState()
        {
            _preview = null;
            _scratchRoot = null;
            _scratchAction = null;
            _dragItem = null;
            _pendingResize = false;
            _band = Rect.Empty;
            Cursor = null;
        }

        // ---------------------------------------------------------------- keyboard

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Handled)
                return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;
            bool ctrl = mods == ModifierKeys.Control;

            switch (key)
            {
                case Key.Escape when _mode != DragMode.None:
                    CancelDrag();
                    break;
                case Key.Delete when mods == ModifierKeys.None:
                    DeleteSelection();
                    break;
                case Key.D when ctrl:
                    DuplicateSelection();
                    break;
                case Key.G when ctrl:
                    GroupSelection();
                    break;
                case Key.A when ctrl:
                    SelectAll();
                    break;
                case Key.Left when mods == ModifierKeys.None:
                    MoveSelection(-1);
                    break;
                case Key.Right when mods == ModifierKeys.None:
                    MoveSelection(1);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        // ---------------------------------------------------------------- layout and rendering

        protected override Size MeasureOverride(Size availableSize) =>
            new(LaneHeaderWidth + 160, RulerHeight + LaneCount * LaneHeight + 1);

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);

            if (sizeInfo.WidthChanged)
                UpdateView();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;

            if (w <= 0 || h <= 0)
                return;

            var background = (Brush?)GetValue(BackgroundBrushProperty) ?? Brushes.Transparent;
            var header = (Brush?)GetValue(HeaderBrushProperty) ?? background;
            var divider = (Brush?)GetValue(DividerBrushProperty) ?? Brushes.DimGray;
            var text = (Brush?)GetValue(TextBrushProperty) ?? Brushes.White;
            var muted = (Brush?)GetValue(MutedTextBrushProperty) ?? Brushes.Gray;
            var accent = Frozen(new SolidColorBrush((Color)GetValue(AccentColorProperty)));

            var dividerPen = Frozen(new Pen(divider, 1));
            var lanesBottom = RulerHeight + LaneCount * LaneHeight;

            dc.DrawRectangle(background, null, new Rect(0, 0, w, h));

            // Lane separators.
            for (int i = 0; i <= LaneCount; i++)
            {
                double y = Snap(RulerHeight + i * LaneHeight);
                dc.DrawLine(dividerPen, new Point(LaneHeaderWidth, y), new Point(w, y));
            }

            var timeArea = new Rect(LaneHeaderWidth, 0, Math.Max(0, w - LaneHeaderWidth), h);
            dc.PushClip(new RectangleGeometry(timeArea));

            DrawRuler(dc, dividerPen, muted, lanesBottom);
            DrawItems(dc, text, accent);

            if (_mode == DragMode.Band && !_band.IsEmpty)
            {
                var fill = Frozen(new SolidColorBrush((Color)GetValue(AccentColorProperty)) { Opacity = 0.15 });
                dc.DrawRectangle(fill, Frozen(new Pen(accent, 1)), _band);
            }

            dc.Pop();

            // Lane header column.
            dc.DrawRectangle(header, null, new Rect(0, 0, LaneHeaderWidth, h));
            dc.DrawLine(dividerPen, new Point(Snap(LaneHeaderWidth), 0), new Point(Snap(LaneHeaderWidth), h));

            for (int i = 0; i < LaneCount; i++)
            {
                var label = Text(LaneNames[i], muted, LaneHeaderWidth - 16);
                dc.DrawText(label, new Point(10, RulerHeight + i * LaneHeight + (LaneHeight - label.Height) / 2));
            }
        }

        private void DrawRuler(DrawingContext dc, Pen dividerPen, Brush muted, double lanesBottom)
        {
            double z = Zoom, from = HorizontalOffsetMs, to = from + ViewportMs;
            long step = MacroTimelineLogic.TickStepMs(z);
            long first = (long)Math.Floor(from / step) * step;
            var gridPen = Frozen(new Pen(dividerPen.Brush, 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) });

            dc.DrawLine(dividerPen, new Point(LaneHeaderWidth, Snap(RulerHeight)), new Point(ActualWidth, Snap(RulerHeight)));

            for (long t = first; t <= to + step; t += step)
            {
                double x = Snap(XOf(t));
                dc.DrawLine(dividerPen, new Point(x, RulerHeight - 6), new Point(x, RulerHeight));
                dc.DrawLine(gridPen, new Point(x, RulerHeight), new Point(x, lanesBottom));

                double half = Snap(XOf(t + step / 2.0));
                dc.DrawLine(dividerPen, new Point(half, RulerHeight - 3), new Point(half, RulerHeight));

                var label = Text(MacroTimelineLogic.FormatTick(t, step), muted, Math.Max(1, step * z - 6));
                dc.DrawText(label, new Point(x + 3, Math.Max(0, (RulerHeight - 6 - label.Height) / 2)));
            }
        }

        private void DrawItems(DrawingContext dc, Brush text, Brush accent)
        {
            var layout = _preview ?? _layout;
            bool previewing = _preview is not null && _preview.Items.Count == _layout.Items.Count;
            double z = Zoom, from = HorizontalOffsetMs, to = from + ViewportMs;

            var laneBrushes = new[]
            {
                (Brush?)GetValue(KeysBrushProperty) ?? Brushes.SteelBlue,
                (Brush?)GetValue(MouseBrushProperty) ?? Brushes.SeaGreen,
                (Brush?)GetValue(CameraBrushProperty) ?? Brushes.Goldenrod,
                (Brush?)GetValue(WaitBrushProperty) ?? Brushes.Gray,
                (Brush?)GetValue(GroupsBrushProperty) ?? Brushes.LightGray
            };

            var lanePens = laneBrushes.Select(b => Frozen(new Pen(b, 1))).ToArray();
            var selectedPen = Frozen(new Pen(accent, 2));
            var dashedGroupPen = Frozen(new Pen(laneBrushes[(int)TimelineLane.Groups], 1) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) });

            for (int i = 0; i < layout.Items.Count; i++)
            {
                var item = layout.Items[i];

                if (!MacroTimelineLogic.IsVisible(item, z, from, to))
                    continue;

                // While previewing, the drawn item belongs to the scratch copy; identity comes from the real layout.
                var original = previewing ? _layout.Items[i].Action : item.Action;
                bool selected = _selected.Contains(original);
                int lane = (int)item.Lane;
                var rect = BlockRect(item);

                if (!item.Active)
                    dc.PushOpacity(DimOpacity);

                if (item.Lane == TimelineLane.Groups)
                    DrawGroup(dc, item, rect, laneBrushes[lane], lanePens[lane], dashedGroupPen, text, z, from, to);
                else
                    DrawBlock(dc, item, rect, laneBrushes[lane], lanePens[lane], text, z);

                if (selected)
                {
                    var outline = ClampToView(rect);

                    if (!outline.IsEmpty)
                    {
                        outline.Inflate(1, 1);
                        dc.DrawRoundedRectangle(null, selectedPen, outline, 4, 4);
                    }
                }

                if (!item.Active)
                    dc.Pop();
            }
        }

        private void DrawBlock(DrawingContext dc, TimelineItem item, Rect rect, Brush laneBrush, Pen lanePen, Brush text, double z)
        {
            var r = ClampToView(rect);

            if (r.IsEmpty)
                return;

            if (item.DurationMs * z < MacroTimelineLogic.MinBlockPx)
            {
                // Instant (or nearly) action: a solid narrow marker.
                dc.DrawRoundedRectangle(laneBrush, null, r, 2, 2);
                return;
            }

            dc.DrawRoundedRectangle(Soft(laneBrush), lanePen, r, 4, 4);
            DrawLabel(dc, Label(item.Action), text, r);
        }

        private void DrawGroup(DrawingContext dc, TimelineItem item, Rect rect, Brush laneBrush, Pen lanePen, Pen dashedPen,
            Brush text, double z, double from, double to)
        {
            // First iteration: an outline bracket labelled like the list row.
            long firstMs = item.Iterations > 1 ? item.IterationMs : item.DurationMs;
            var first = new Rect(rect.X, rect.Y, MacroTimelineLogic.BlockWidthPx(firstMs, z), rect.Height);
            var r = ClampToView(first);

            if (!r.IsEmpty)
            {
                dc.DrawRoundedRectangle(null, lanePen, r, 4, 4);
                DrawLabel(dc, Label(item.Action), text, r);
            }

            if (item.Iterations <= 1 || item.IterationMs <= 0)
                return;

            // Later iterations: a hatched ghost tail with a divider per iteration and "×N" at its start.
            double tailX = XOf(item.StartMs + item.IterationMs);
            var tail = ClampToView(new Rect(tailX, rect.Y, Math.Max(0, rect.Right - tailX), rect.Height));

            if (tail.IsEmpty)
                return;

            dc.DrawRoundedRectangle(Hatch(laneBrush), dashedPen, tail, 4, 4);

            if (item.IterationMs * z >= 6)
            {
                long kFrom = Math.Max(2, (long)Math.Floor((from - item.StartMs) / item.IterationMs));
                long kTo = Math.Min(item.Iterations - 1, (long)Math.Ceiling((to - item.StartMs) / item.IterationMs));

                for (long k = kFrom; k <= kTo; k++)
                {
                    double x = Snap(XOf(item.StartMs + k * (double)item.IterationMs));
                    dc.DrawLine(dashedPen, new Point(x, rect.Top + 3), new Point(x, rect.Bottom - 3));
                }
            }

            if (tailX >= LaneHeaderWidth - 8)
                DrawLabel(dc, "×" + item.Iterations.ToString(CultureInfo.InvariantCulture), text, tail);
        }

        private void DrawLabel(DrawingContext dc, string label, Brush brush, Rect r)
        {
            double room = r.Width - 10;

            if (room < 18 || string.IsNullOrEmpty(label))
                return;

            var formatted = Text(label, brush, room);
            dc.PushClip(new RectangleGeometry(r));
            dc.DrawText(formatted, new Point(r.X + 5, r.Y + (r.Height - formatted.Height) / 2));
            dc.Pop();
        }

        private string Label(MacroAction action)
        {
            try
            {
                return _labelProvider?.Invoke(action) ?? MacroStudioLogic.Summary(action);
            }
            catch
            {
                return MacroStudioLogic.Summary(action);
            }
        }

        private FormattedText Text(string s, Brush brush, double maxWidth)
        {
            var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double size = (double)GetValue(LabelFontSizeProperty);

            return new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size > 0 ? size : 11, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, maxWidth),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
        }

        /// <summary>The lane colour at low opacity, for block fills.</summary>
        private Brush Soft(Brush brush)
        {
            if (_softBrushes.TryGetValue(brush, out var soft))
                return soft;

            if (_softBrushes.Count > 32)
                _softBrushes.Clear();

            var copy = brush.CloneCurrentValue();
            copy.Opacity = 0.28;
            return _softBrushes[brush] = Frozen(copy);
        }

        /// <summary>Diagonal hatching in the lane colour, for repeated group iterations.</summary>
        private Brush Hatch(Brush brush)
        {
            if (_hatchBrushes.TryGetValue(brush, out var hatch))
                return hatch;

            if (_hatchBrushes.Count > 32)
                _hatchBrushes.Clear();

            var drawing = new GeometryDrawing(null, Frozen(new Pen(brush, 1)), Geometry.Parse("M0,6 L6,0 M-1,1 L1,-1 M5,7 L7,5"));
            var tile = new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 6, 6),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, 6, 6),
                ViewboxUnits = BrushMappingMode.Absolute,
                Opacity = 0.35
            };

            return _hatchBrushes[brush] = Frozen(tile);
        }

        private static T Frozen<T>(T freezable) where T : Freezable
        {
            if (freezable.CanFreeze)
                freezable.Freeze();

            return freezable;
        }

        private static double Snap(double v) => Math.Round(v) + 0.5;

        // ---------------------------------------------------------------- accessibility

        protected override AutomationPeer OnCreateAutomationPeer() => new MacroTimelineAutomationPeer(this);

        private sealed class MacroTimelineAutomationPeer : FrameworkElementAutomationPeer
        {
            public MacroTimelineAutomationPeer(MacroTimeline owner) : base(owner)
            {
            }

            protected override string GetClassNameCore() => nameof(MacroTimeline);

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

            protected override string GetItemStatusCore()
            {
                int count = ((MacroTimeline)Owner)._selected.Count;
                return count == 1 ? "1 action selected" : $"{count} actions selected";
            }
        }
    }
}
