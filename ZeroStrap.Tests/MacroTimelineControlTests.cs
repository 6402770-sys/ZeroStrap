using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Xunit;

using ZeroStrap.Macros;
using ZeroStrap.UI.Elements.Controls;

using static ZeroStrap.Tests.MacroBuilders;

namespace ZeroStrap.Tests
{
    /// <summary>Smoke tests of the timeline control's public API (rendering and commands) on an STA thread, no window.</summary>
    public class MacroTimelineControlTests
    {
        private static void OnSta(Action body)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            failure?.Throw();
        }

        private static void Render(FrameworkElement element)
        {
            element.Measure(new Size(800, 200));
            element.Arrange(new Rect(0, 0, 800, 200));
            element.UpdateLayout();

            var bitmap = new RenderTargetBitmap(800, 200, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
        }

        [Fact]
        public void Control_RendersAndRunsCommands()
        {
            OnSta(() =>
            {
                var a = TapKey(0x11, 120, 30);
                var b = Wait(400);
                var c = Click(MouseButton.Left, 40);
                var nested = Group(3, KeyDown(0x11), Camera(200, 0, 300), KeyUp(0x11));
                var off = Wait(100);
                off.Enabled = false;
                var root = new List<MacroAction> { a, b, c, nested, off };

                var timeline = new MacroTimeline { Actions = root, LabelProvider = x => x.Kind.ToString() };
                int edited = 0, selection = 0;
                timeline.ActionsEdited += (_, _) => edited++;
                timeline.SelectionChanged += (_, _) => selection++;

                Render(timeline);
                Assert.Equal(MacroTiming.EstimateDurationMs(Of(root.ToArray())), timeline.Layout.TotalMs);
                Assert.True(timeline.ViewportMs > 0);
                Assert.True(timeline.ExtentMs > timeline.Layout.TotalMs);

                timeline.SetSelection(new[] { a, b });
                Assert.Equal(0, selection);
                Assert.Equal(new[] { a, b }, timeline.SelectedActions);
                Assert.Same(b, timeline.PrimaryAction);
                Assert.True(timeline.CanGroupSelection);

                Assert.True(timeline.GroupSelection());
                Assert.Equal((1, 1), (edited, selection));
                var group = Assert.Single(timeline.SelectedActions);
                Assert.Equal(new[] { a, b }, group.Actions);

                Assert.True(timeline.DuplicateSelection());
                Assert.Equal(2, edited);
                Render(timeline);

                timeline.SelectAll();
                Assert.Equal(MacroStudioLogicCount(root), timeline.SelectedActions.Count);
                Assert.True(timeline.DeleteSelection());
                Assert.Empty(root);
                Assert.Empty(timeline.SelectedActions);

                timeline.ZoomIn();
                timeline.ZoomOut();
                timeline.ZoomToFit();
                timeline.Zoom = 1000;
                Assert.Equal(ZeroStrap.UI.ViewModels.Shell.MacroTimelineLogic.MaxPxPerMs, timeline.Zoom);
                Render(timeline);
            });
        }

        private static int MacroStudioLogicCount(List<MacroAction> root) =>
            ZeroStrap.UI.ViewModels.Shell.MacroStudioLogic.CountAll(root);

        [Fact]
        public void Control_OffsetIsClampedToScrollableRange()
        {
            OnSta(() =>
            {
                var root = new List<MacroAction> { Wait(60_000) };
                var timeline = new MacroTimeline { Actions = root };
                Render(timeline);

                timeline.HorizontalOffsetMs = 1e12;
                Assert.Equal(timeline.ScrollableMs, timeline.HorizontalOffsetMs);

                timeline.HorizontalOffsetMs = -5;
                Assert.Equal(0, timeline.HorizontalOffsetMs);

                timeline.ScrollIntoView(root[0]);
                Render(timeline);
            });
        }
    }
}
