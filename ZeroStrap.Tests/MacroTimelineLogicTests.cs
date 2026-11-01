using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using ZeroStrap.Macros;
using ZeroStrap.UI.ViewModels.Shell;

using static ZeroStrap.Tests.MacroBuilders;

namespace ZeroStrap.Tests
{
    public class MacroTimelineLogicTests
    {
        private static MacroAction Delayed(MacroAction a, int delayMs)
        {
            a.DelayBeforeMs = delayMs;
            return a;
        }

        private static MacroAction Disabled(MacroAction a)
        {
            a.Enabled = false;
            return a;
        }

        private static TimelineItem Item(TimelineLayout layout, MacroAction a) => MacroTimelineLogic.Find(layout, a)!;

        // ---------------------------------------------------------------- layout

        [Fact]
        public void Layout_SequentialTimes_DelaysAndLanes()
        {
            var tap = TapKey(0x11, holdMs: 100, delayMs: 50);
            var down = KeyDown(0x11, delayMs: 20);
            var click = Click(MouseButton.Left, holdMs: 30);
            var move = new MacroAction { Kind = MacroActionKind.MouseMoveTo, X = 5, Y = 5, DelayBeforeMs = 10 };
            var cam = Delayed(Camera(300, 0, 800), 5);
            var wait = Wait(250);

            var layout = MacroTimelineLogic.Layout(new List<MacroAction> { tap, down, click, move, cam, wait });

            Assert.Equal((50L, 100L, TimelineLane.Keys), (Item(layout, tap).StartMs, Item(layout, tap).DurationMs, Item(layout, tap).Lane));
            Assert.Equal((170L, 0L, TimelineLane.Keys), (Item(layout, down).StartMs, Item(layout, down).DurationMs, Item(layout, down).Lane));
            Assert.Equal((170L, 30L, TimelineLane.Mouse), (Item(layout, click).StartMs, Item(layout, click).DurationMs, Item(layout, click).Lane));
            Assert.Equal((210L, 0L, TimelineLane.Mouse), (Item(layout, move).StartMs, Item(layout, move).DurationMs, Item(layout, move).Lane));
            Assert.Equal((215L, 800L, TimelineLane.Camera), (Item(layout, cam).StartMs, Item(layout, cam).DurationMs, Item(layout, cam).Lane));
            Assert.Equal((1015L, 250L, TimelineLane.Wait), (Item(layout, wait).StartMs, Item(layout, wait).DurationMs, Item(layout, wait).Lane));
            Assert.Equal(1265, layout.TotalMs);
            Assert.Equal(Enumerable.Range(0, 6), layout.Items.Select(i => i.Order));
        }

        [Fact]
        public void Layout_NestedGroups_ChildrenFirstIterationOnly_GroupSpansAllRepeats()
        {
            var a = TapKey(0x11, holdMs: 40, delayMs: 10);   // 50 per pass
            var inner = Group(2, Wait(25));                  // 50
            var outer = Delayed(Group(3, a, inner), 100);    // pass 100, ×3 = 300
            var after = Wait(5);

            var layout = MacroTimelineLogic.Layout(new List<MacroAction> { outer, after });

            var o = Item(layout, outer);
            Assert.Equal((100L, 300L, 3, 100L, TimelineLane.Groups), (o.StartMs, o.DurationMs, o.Iterations, o.IterationMs, o.Lane));
            Assert.Equal((110L, 1, (MacroAction?)outer), (Item(layout, a).StartMs, Item(layout, a).Depth, Item(layout, a).Parent));

            var i = Item(layout, inner);
            Assert.Equal((150L, 50L, 2, 25L, 1), (i.StartMs, i.DurationMs, i.Iterations, i.IterationMs, i.Depth));
            Assert.Equal((150L, 2), (Item(layout, inner.Actions[0]).StartMs, Item(layout, inner.Actions[0]).Depth));

            Assert.Equal(400, Item(layout, after).StartMs);
            Assert.Equal(405, layout.TotalMs);
            Assert.Equal(MacroStudioLogic.Flatten(new[] { outer, after }).Select(f => f.Action), layout.Items.Select(x => x.Action));
        }

        [Fact]
        public void Layout_DisabledActions_TakeNoTime_AndAreDrawnInactive()
        {
            var off = Disabled(Delayed(Wait(500), 200));
            var offGroup = Disabled(Group(2, TapKey(0x11, 100)));
            var empty = Delayed(Group(4, Disabled(Wait(100))), 30); // enabled but nothing to run: only its delay
            var on = Wait(10);

            var layout = MacroTimelineLogic.Layout(new List<MacroAction> { Wait(100), off, offGroup, empty, on });

            Assert.Equal((100L, 0L, false), (Item(layout, off).StartMs, Item(layout, off).DurationMs, Item(layout, off).Active));
            Assert.Equal((100L, 0L, false), (Item(layout, offGroup).StartMs, Item(layout, offGroup).DurationMs, Item(layout, offGroup).Active));
            Assert.False(Item(layout, offGroup.Actions[0]).Active);
            Assert.Equal((130L, 0L, false), (Item(layout, empty).StartMs, Item(layout, empty).DurationMs, Item(layout, empty).Active));
            Assert.Equal(130, Item(layout, on).StartMs);
            Assert.Equal(140, layout.TotalMs);
        }

        [Fact]
        public void Layout_GroupIterationFloor_MatchesPlayer()
        {
            var zero = Group(5, KeyDown(0x11), KeyUp(0x11)); // 0 ms per pass, padded to 1 ms between iterations
            var layout = MacroTimelineLogic.Layout(new List<MacroAction> { zero });

            Assert.Equal((4L, 1L), (Item(layout, zero).DurationMs, Item(layout, zero).IterationMs));
            Assert.Equal(4, layout.TotalMs);
        }

        public static IEnumerable<object[]> EstimateMacros()
        {
            yield return new object[] { Of(TapKey(0x11, 50, 10), Wait(300), Click(MouseButton.Right, 20)) };
            yield return new object[] { Of(Group(3, TapKey(0x11, 40, 10), Group(2, Wait(25), Disabled(Wait(999)))), Camera(100, 0, 800)) };
            yield return new object[] { Of(Disabled(Wait(500)), Group(10, KeyDown(0x11), KeyUp(0x11)), Delayed(Group(2, Disabled(Wait(5))), 70)) };
            yield return new object[] { Of(Group(2, Group(3, Group(4, TapKey(0x1E, 5, 3)))), Delayed(Camera(0, 0, 120), 40)) };
            yield return new object[] { Of() };
        }

        [Theory]
        [MemberData(nameof(EstimateMacros))]
        public void Layout_Total_EqualsEstimate(Macro macro)
        {
            Assert.Equal(MacroTiming.EstimateDurationMs(macro), MacroTimelineLogic.Layout(macro.Actions).TotalMs);
        }

        // ---------------------------------------------------------------- retime

        [Fact]
        public void Retime_SetsDelay_ClampsToPreviousEnd_AndRipples()
        {
            var first = Wait(100);
            var moved = Delayed(TapKey(0x11, 50), 20);
            var next = Wait(10);
            var root = new List<MacroAction> { first, moved, next };

            Assert.Equal(400, MacroTimelineLogic.Retime(root, moved, 400));
            Assert.Equal(300, moved.DelayBeforeMs);
            Assert.Equal(450, Item(MacroTimelineLogic.Layout(root), next).StartMs);

            Assert.Equal(100, MacroTimelineLogic.Retime(root, moved, 30));
            Assert.Equal(0, moved.DelayBeforeMs);

            long far = MacroTimelineLogic.Retime(root, moved, 10_000_000);
            Assert.Equal(100 + MacroValidator.MaxTimeMs, far);
            Assert.Equal(MacroValidator.MaxTimeMs, moved.DelayBeforeMs);
        }

        [Fact]
        public void Retime_InsideGroup_UsesPreviousSiblingOrGroupStart()
        {
            var a = Wait(50);
            var b = Wait(10);
            var group = Delayed(Group(2, a, b), 100);
            var root = new List<MacroAction> { Wait(20), group };

            Assert.Equal(120, MacroTimelineLogic.Retime(root, a, 0));
            Assert.Equal(0, a.DelayBeforeMs);
            Assert.Equal(200, MacroTimelineLogic.Retime(root, b, 200));
            Assert.Equal(30, b.DelayBeforeMs);
            Assert.Equal(90, Item(MacroTimelineLogic.Layout(root), group).IterationMs);
        }

        [Fact]
        public void Retime_SkippedActions_AreLeftAlone()
        {
            var off = Disabled(Delayed(Wait(10), 5));
            var child = Delayed(Wait(10), 7);
            var offGroup = Disabled(Group(1, child));
            var root = new List<MacroAction> { Wait(100), off, offGroup };

            Assert.Equal(100, MacroTimelineLogic.Retime(root, off, 500));
            Assert.Equal(5, off.DelayBeforeMs);
            Assert.Equal(100, MacroTimelineLogic.Retime(root, child, 500));
            Assert.Equal(7, child.DelayBeforeMs);
        }

        // ---------------------------------------------------------------- resize

        [Fact]
        public void Resize_PerKind_AndClamps()
        {
            var tap = TapKey(0x11, 10);
            var click = Click(MouseButton.Left, 10);
            var cam = Camera(300, 0, 800);
            var wait = Wait(100);

            Assert.True(MacroTimelineLogic.Resize(tap, 250));
            Assert.Equal(250, tap.HoldMs);
            Assert.True(MacroTimelineLogic.Resize(click, -40));
            Assert.Equal(0, click.HoldMs);
            Assert.True(MacroTimelineLogic.Resize(cam, 400));
            Assert.Equal(400, cam.DurationMs);
            Assert.Equal(750, MacroStudioLogic.CameraSpeed(cam), 3);
            Assert.True(MacroTimelineLogic.Resize(wait, 99_999_999));
            Assert.Equal(MacroValidator.MaxTimeMs, wait.Ms);
        }

        [Fact]
        public void Resize_RejectsInstantAndDerivedKinds()
        {
            var down = KeyDown(0x11);
            var up = new MacroAction { Kind = MacroActionKind.Mouse, ButtonMode = MacroButtonMode.Up, HoldMs = 5 };
            var move = new MacroAction { Kind = MacroActionKind.MouseMoveTo };
            var group = Group(2, Wait(10));

            foreach (var a in new[] { down, up, move, group })
            {
                Assert.False(MacroTimelineLogic.CanResize(a));
                Assert.False(MacroTimelineLogic.Resize(a, 500));
            }

            Assert.Equal(5, up.HoldMs);
            Assert.Equal(10, group.Actions[0].Ms);
        }

        // ---------------------------------------------------------------- multi-select edits

        [Fact]
        public void DeleteMany_RemovesAcrossDepths_GroupTakesItsChildren()
        {
            var a = Wait(1);
            var child = Wait(2);
            var keep = Wait(3);
            var group = Group(2, child, keep);
            var nested = Wait(4);
            var g2 = Group(1, nested);
            var root = new List<MacroAction> { a, group, g2 };

            Assert.Equal(2, MacroTimelineLogic.DeleteMany(root, new[] { a, child }));
            Assert.Equal(new[] { group, g2 }, root);
            Assert.Equal(new[] { keep }, group.Actions);

            Assert.Equal(1, MacroTimelineLogic.DeleteMany(root, new[] { nested, g2 }));
            Assert.Equal(new[] { group }, root);
        }

        [Fact]
        public void DuplicateMany_InsertsAfterLastSelectedOfEachList()
        {
            var a = Wait(1);
            var b = Wait(2);
            var c = Wait(3);
            var x = Wait(10);
            var y = Wait(11);
            var group = Group(2, x, y);
            var root = new List<MacroAction> { a, b, group, c };

            var copies = MacroTimelineLogic.DuplicateMany(root, new[] { c, a, y });

            Assert.Equal(3, copies.Count);
            Assert.Equal(new[] { 1, 11, 3 }, copies.Select(k => k.Ms));
            Assert.Equal(new[] { 1, 2, 0, 3, 1, 3 }, root.Select(k => k.Ms));
            Assert.Same(copies[0], root[4]);
            Assert.Same(copies[2], root[5]);
            Assert.Equal(new[] { x, y, copies[1] }, group.Actions);
            Assert.All(copies, k => Assert.DoesNotContain(k, new[] { a, c, y }));
        }

        [Fact]
        public void DuplicateMany_SelectedGroupCopiesChildrenOnce()
        {
            var x = Wait(10);
            var group = Group(2, x);
            var root = new List<MacroAction> { group };

            var copies = MacroTimelineLogic.DuplicateMany(root, new[] { group, x });

            Assert.Single(copies);
            Assert.Equal(2, root.Count);
            Assert.Single(group.Actions);
            Assert.NotSame(x, root[1].Actions[0]);
        }

        [Fact]
        public void GroupMany_WrapsInListOrder_AtFirstIndex_KeepsTiming()
        {
            var a = Wait(10);
            var b = Delayed(Wait(20), 5);
            var c = Wait(30);
            var d = Wait(40);
            var root = new List<MacroAction> { a, b, c, d };
            long before = MacroTimelineLogic.Layout(root).TotalMs;

            Assert.True(MacroTimelineLogic.CanGroup(root, new[] { d, b }));
            var group = MacroTimelineLogic.GroupMany(root, new[] { d, b });

            Assert.NotNull(group);
            Assert.Equal((MacroActionKind.Group, 1, 0), (group!.Kind, group.GroupRepeat, group.DelayBeforeMs));
            Assert.Equal(new[] { b, d }, group.Actions);
            Assert.Equal(new[] { a, group, c }, root);
            Assert.Equal(before, MacroTimelineLogic.Layout(root).TotalMs);
        }

        [Fact]
        public void GroupMany_Nested_AndRejectedAcrossParents()
        {
            var x = Wait(1);
            var y = Wait(2);
            var group = Group(2, x, y);
            var top = Wait(3);
            var root = new List<MacroAction> { group, top };

            Assert.False(MacroTimelineLogic.CanGroup(root, new[] { x, top }));
            Assert.False(MacroTimelineLogic.CanGroup(root, new[] { group, x }));
            Assert.False(MacroTimelineLogic.CanGroup(root, Array.Empty<MacroAction>()));
            Assert.Null(MacroTimelineLogic.GroupMany(root, new[] { x, top }));
            Assert.Equal(new[] { group, top }, root);

            var inner = MacroTimelineLogic.GroupMany(root, new[] { y });
            Assert.Equal(new[] { x, inner }, group.Actions);
            Assert.Equal(new[] { y }, inner!.Actions);
        }

        [Fact]
        public void CanGroup_RespectsValidatorNestingLimit()
        {
            // Four levels of groups is the most the validator accepts; wrapping the outermost one would make five.
            var deepest = Group(1, Wait(1));
            var chain = Group(1, Group(1, Group(1, deepest)));
            var root = new List<MacroAction> { chain, Wait(2) };

            Assert.True(MacroValidator.Process(Of(chain.Clone())).IsValid);
            Assert.False(MacroTimelineLogic.CanGroup(root, new[] { chain }));
            Assert.False(MacroTimelineLogic.CanGroup(root, new[] { deepest.Actions[0] }));
            Assert.True(MacroTimelineLogic.CanGroup(root, new[] { root[1] }));

            var three = Group(1, Group(1, Group(1, Wait(1))));
            var root2 = new List<MacroAction> { three };
            var wrapped = MacroTimelineLogic.GroupMany(root2, new[] { three });
            Assert.NotNull(wrapped);
            Assert.True(MacroValidator.Process(Of(root2.ToArray())).IsValid);
        }

        // ---------------------------------------------------------------- selection helpers

        [Fact]
        public void Range_And_Neighbour_UseFlattenedOrder()
        {
            var a = Wait(1);
            var x = Wait(2);
            var group = Group(2, x);
            var b = Wait(3);
            var root = new List<MacroAction> { a, group, b };

            Assert.Equal(new[] { group, x, b }, MacroTimelineLogic.Range(root, b, group));
            Assert.Equal(x, MacroTimelineLogic.Neighbour(root, group, 1));
            Assert.Equal(a, MacroTimelineLogic.Neighbour(root, a, -1));
            Assert.Equal(a, MacroTimelineLogic.Neighbour(root, null, 1));
            Assert.Equal(b, MacroTimelineLogic.Neighbour(root, null, -1));
            Assert.Null(MacroTimelineLogic.Neighbour(new List<MacroAction>(), null, 1));
        }

        // ---------------------------------------------------------------- px / ms

        [Theory]
        [InlineData(0.2, 0)]
        [InlineData(0.013, 1234.5)]
        [InlineData(2.0, 77)]
        public void PxMs_RoundTrip(double zoom, double offset)
        {
            foreach (double ms in new[] { 0.0, 10, 999.5, 123456 })
            {
                double px = MacroTimelineLogic.MsToPx(ms, zoom, offset);
                Assert.Equal(ms, MacroTimelineLogic.PxToMs(px, zoom, offset), 6);
            }
        }

        [Fact]
        public void Snap_And_BlockWidth()
        {
            Assert.Equal(120, MacroTimelineLogic.Snap(124.9));
            Assert.Equal(130, MacroTimelineLogic.Snap(125));
            Assert.Equal(125, MacroTimelineLogic.Snap(124.6, snap: false));
            Assert.Equal(0, MacroTimelineLogic.Snap(-30));
            Assert.Equal(MacroTimelineLogic.MinBlockPx, MacroTimelineLogic.BlockWidthPx(0, 0.2));
            Assert.Equal(200, MacroTimelineLogic.BlockWidthPx(1000, 0.2));
        }

        [Fact]
        public void ZoomAround_KeepsAnchorTime_AndClamps()
        {
            var (zoom, offset) = MacroTimelineLogic.ZoomAround(0.2, 500, 300, 2);
            Assert.Equal(0.4, zoom, 9);
            Assert.Equal(MacroTimelineLogic.PxToMs(300, 0.2, 500), MacroTimelineLogic.PxToMs(300, zoom, offset), 6);

            Assert.Equal(MacroTimelineLogic.MaxPxPerMs, MacroTimelineLogic.ZoomAround(1.5, 0, 0, 10).PxPerMs);
            Assert.Equal(MacroTimelineLogic.MinPxPerMs, MacroTimelineLogic.ZoomAround(0.002, 0, 0, 0.01).PxPerMs);
            Assert.Equal(0, MacroTimelineLogic.ZoomAround(0.2, 0, 100, 0.5).OffsetMs);
        }

        [Fact]
        public void Ticks_AdaptToZoom_AndReadAsSeconds()
        {
            Assert.Equal(500, MacroTimelineLogic.TickStepMs(0.2));   // 500 ms = 100 px
            Assert.Equal(50, MacroTimelineLogic.TickStepMs(2.0));
            Assert.Equal(100_000, MacroTimelineLogic.TickStepMs(0.001));
            Assert.Equal("0s", MacroTimelineLogic.FormatTick(0, 500));
            Assert.Equal("0.5s", MacroTimelineLogic.FormatTick(500, 500));
            Assert.Equal("1s", MacroTimelineLogic.FormatTick(1000, 500));
            Assert.Equal("1m 30s", MacroTimelineLogic.FormatTick(90_000, 10_000));
            Assert.Equal("2m", MacroTimelineLogic.FormatTick(120_000, 10_000));
        }

        [Fact]
        public void IsVisible_CountsMinimumBlockWidth()
        {
            var item = new TimelineItem(Wait(0), null, 0, 1000, 0, TimelineLane.Wait);

            Assert.True(MacroTimelineLogic.IsVisible(item, 0.2, 1010, 2000));   // 6 px marker = 30 ms
            Assert.False(MacroTimelineLogic.IsVisible(item, 0.2, 1040, 2000));
            Assert.False(MacroTimelineLogic.IsVisible(item, 0.2, 0, 999));
        }
    }
}
