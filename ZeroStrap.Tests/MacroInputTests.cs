using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using Xunit;

using ZeroStrap.Macros;

namespace ZeroStrap.Tests
{
    public sealed class MacroInputTests
    {
        private readonly List<MacroNative.INPUT> _sent = new();
        private readonly List<string> _logs = new();
        private uint _returns = 1;

        private SendInputSink NewSink() => new(
            inputs =>
            {
                _sent.AddRange(inputs);
                return _returns;
            },
            () => (-1920, 0, 3840, 1080),
            _logs.Add);

        [Fact]
        public void InputStructHasThePlatformLayout()
        {
            Assert.Equal(IntPtr.Size == 8 ? 40 : 28, MacroNative.InputSize);
        }

        [Fact]
        public void KeysUseScanCodesWithTheRightFlags()
        {
            var sink = NewSink();

            sink.KeyDown(0x1E, false);
            sink.KeyUp(0x1E, false);
            sink.KeyDown(0x4D, true);
            sink.KeyUp(0x4D, true);

            Assert.All(_sent, i => Assert.Equal(MacroNative.INPUT_KEYBOARD, i.type));
            Assert.Equal(MacroNative.KEYEVENTF_SCANCODE, _sent[0].U.ki.dwFlags);
            Assert.Equal(MacroNative.KEYEVENTF_SCANCODE | MacroNative.KEYEVENTF_KEYUP, _sent[1].U.ki.dwFlags);
            Assert.Equal(MacroNative.KEYEVENTF_SCANCODE | MacroNative.KEYEVENTF_EXTENDEDKEY, _sent[2].U.ki.dwFlags);
            Assert.Equal(MacroNative.KEYEVENTF_SCANCODE | MacroNative.KEYEVENTF_EXTENDEDKEY | MacroNative.KEYEVENTF_KEYUP, _sent[3].U.ki.dwFlags);
            Assert.Equal(0x1E, _sent[0].U.ki.wScan);
            Assert.Equal(0, _sent[0].U.ki.wVk);
        }

        [Fact]
        public void EveryEventCarriesTheInjectionMarker()
        {
            var sink = NewSink();

            sink.KeyDown(0x1E, false);
            sink.MouseDown(MouseButton.Left);
            sink.MoveRelative(1, 2);
            sink.MoveAbsolute(0, 0);

            Assert.Equal((UIntPtr)0x5A534D43u, _sent[0].U.ki.dwExtraInfo);
            Assert.All(_sent.GetRange(1, 3), i => Assert.Equal((UIntPtr)0x5A534D43u, i.U.mi.dwExtraInfo));
        }

        [Theory]
        [InlineData(MouseButton.Left, true, 0x0002u, 0u)]
        [InlineData(MouseButton.Left, false, 0x0004u, 0u)]
        [InlineData(MouseButton.Right, true, 0x0008u, 0u)]
        [InlineData(MouseButton.Right, false, 0x0010u, 0u)]
        [InlineData(MouseButton.Middle, true, 0x0020u, 0u)]
        [InlineData(MouseButton.Middle, false, 0x0040u, 0u)]
        [InlineData(MouseButton.X1, true, 0x0080u, 1u)]
        [InlineData(MouseButton.X1, false, 0x0100u, 1u)]
        [InlineData(MouseButton.X2, true, 0x0080u, 2u)]
        [InlineData(MouseButton.X2, false, 0x0100u, 2u)]
        public void MouseButtonsMapToTheirEventFlags(MouseButton button, bool down, uint flags, uint data)
        {
            var sink = NewSink();

            if (down) sink.MouseDown(button); else sink.MouseUp(button);

            Assert.Equal(MacroNative.INPUT_MOUSE, _sent[0].type);
            Assert.Equal(flags, _sent[0].U.mi.dwFlags);
            Assert.Equal(data, _sent[0].U.mi.mouseData);
        }

        [Fact]
        public void RelativeMoveUsesTheMoveFlagOnly()
        {
            NewSink().MoveRelative(-7, 12);

            Assert.Equal(MacroNative.MOUSEEVENTF_MOVE, _sent[0].U.mi.dwFlags);
            Assert.Equal(-7, _sent[0].U.mi.dx);
            Assert.Equal(12, _sent[0].U.mi.dy);
        }

        [Fact]
        public void AbsoluteMoveIsNormalisedOverTheVirtualDesktop()
        {
            var sink = NewSink();

            sink.MoveAbsolute(-1920, 0);
            sink.MoveAbsolute(1919, 1079);

            uint flags = MacroNative.MOUSEEVENTF_MOVE | MacroNative.MOUSEEVENTF_ABSOLUTE | MacroNative.MOUSEEVENTF_VIRTUALDESK;
            Assert.Equal(flags, _sent[0].U.mi.dwFlags);
            Assert.Equal((0, 0), (_sent[0].U.mi.dx, _sent[0].U.mi.dy));
            Assert.Equal((65535, 65535), (_sent[1].U.mi.dx, _sent[1].U.mi.dy));
        }

        [Theory]
        [InlineData(0, 0, 1920, 0)]
        [InlineData(1919, 0, 1920, 65535)]
        [InlineData(-5000, 0, 1920, 0)]
        [InlineData(99999, 0, 1920, 65535)]
        [InlineData(100, 0, 1, 0)]
        [InlineData(100, 0, 0, 0)]
        public void NormalizeAbsoluteClampsToTheRange(int value, int origin, int extent, int expected)
        {
            Assert.Equal(expected, MacroNative.NormalizeAbsolute(value, origin, extent));
        }

        [Fact]
        public void ShortSendIsReportedOnceAndFlagged()
        {
            var sink = NewSink();
            _returns = 0;

            Assert.Throws<InputBlockedException>(() => sink.KeyDown(0x1E, false));
            Assert.Throws<InputBlockedException>(() => sink.KeyUp(0x1E, false));

            Assert.True(sink.LastSendBlocked);
            Assert.Single(_logs);

            _returns = 1;
            sink.KeyDown(0x1E, false);
            Assert.False(sink.LastSendBlocked);
        }

        [Fact]
        public void PrecisionDelayWaitsAtLeastTheRequestedTime()
        {
            using var delay = new PrecisionDelay();
            var watch = Stopwatch.StartNew();
            var before = delay.Now;

            delay.Wait(TimeSpan.FromMilliseconds(60), CancellationToken.None);

            watch.Stop();
            Assert.InRange(watch.Elapsed.TotalMilliseconds, 59, 250);
            Assert.True(delay.Now - before >= TimeSpan.FromMilliseconds(59));
        }

        [Fact]
        public void PrecisionDelayThrowsWhenAlreadyCancelledOrCancelledDuringTheWait()
        {
            using var delay = new PrecisionDelay();
            using var cts = new CancellationTokenSource();

            cts.CancelAfter(30);
            var watch = Stopwatch.StartNew();

            Assert.Throws<OperationCanceledException>(() => delay.Wait(TimeSpan.FromSeconds(5), cts.Token));
            Assert.True(watch.ElapsedMilliseconds < 200);
            Assert.Throws<OperationCanceledException>(() => delay.Wait(TimeSpan.FromMilliseconds(1), cts.Token));
        }

        [Fact]
        public void PrecisionDelayZeroWaitReturnsImmediatelyAndDisposeIsRepeatable()
        {
            var delay = new PrecisionDelay();

            delay.Wait(TimeSpan.Zero, CancellationToken.None);
            delay.Dispose();
            delay.Dispose();

            // After disposal it still waits (via Thread.Sleep) rather than failing.
            var watch = Stopwatch.StartNew();
            delay.Wait(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            Assert.True(watch.ElapsedMilliseconds >= 15);
        }
    }
}
