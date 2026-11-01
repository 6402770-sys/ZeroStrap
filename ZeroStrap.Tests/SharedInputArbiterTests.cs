using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using ZeroStrap.Macros;

using static ZeroStrap.Tests.MacroBuilders;

namespace ZeroStrap.Tests
{
    /// <summary>Shared key ownership between concurrently playing macros. The real sink is a <see cref="RecordingSink"/>.</summary>
    public sealed class SharedInputArbiterTests
    {
        private const ushort W = 0x11, A = 0x1E;

        private readonly RecordingSink _sink = new();
        private readonly SharedInputArbiter _arbiter;

        public SharedInputArbiterTests() => _arbiter = new SharedInputArbiter(_sink);

        [Fact]
        public void ALoneClientPassesEverythingThrough()
        {
            var a = _arbiter.CreateClient();

            a.KeyDown(W, false);
            a.KeyDown(W, false); // a repeated press still reaches the OS, exactly as without sharing
            a.KeyUp(W, false);
            a.KeyUp(A, false);   // an Up for something never pressed still goes out
            a.MouseDown(MouseButton.Left);
            a.MouseUp(MouseButton.Left);
            a.MoveRelative(3, 4);
            a.MoveAbsolute(5, 6);

            Assert.Equal(new[] { "kd:11", "kd:11", "ku:11", "ku:1E", "md:Left", "mu:Left", "rel:3,4", "abs:5,6" }, _sink.Calls);
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void StoppingOneMacroDoesNotReleaseAKeyAnotherStillHolds()
        {
            var a = _arbiter.CreateClient();
            var b = _arbiter.CreateClient();

            a.KeyDown(W, false);
            b.KeyDown(W, false);
            Assert.Equal(new[] { "kd:11" }, _sink.Calls); // the second hold is not sent again
            Assert.Equal(2, _arbiter.OwnerCount(SharedInputArbiter.InputId.Key(W, false)));

            a.KeyUp(W, false);
            Assert.Contains("k11", _sink.Down); // B still needs it
            Assert.DoesNotContain("ku:11", _sink.Calls);

            b.KeyUp(W, false);
            Assert.Empty(_sink.Down);
            Assert.Equal(new[] { "kd:11", "ku:11" }, _sink.Calls);
        }

        [Fact]
        public void ATapByOneMacroWhileAnotherHoldsTheKeyLeavesItHeld()
        {
            var a = _arbiter.CreateClient();
            var b = _arbiter.CreateClient();

            a.KeyDown(W, false);
            b.KeyDown(W, false);
            b.KeyUp(W, false);

            Assert.Contains("k11", _sink.Down);
            Assert.True(a.Owns(SharedInputArbiter.InputId.Key(W, false)));
            Assert.False(b.Owns(SharedInputArbiter.InputId.Key(W, false)));
        }

        [Fact]
        public void TheSameClientPressingTwiceIsFreedByOneRelease()
        {
            var a = _arbiter.CreateClient();
            var b = _arbiter.CreateClient();

            b.KeyDown(W, false);
            a.KeyDown(W, false);
            a.KeyDown(W, false);
            a.KeyUp(W, false);

            Assert.Equal(1, _arbiter.OwnerCount(SharedInputArbiter.InputId.Key(W, false)));

            b.KeyUp(W, false);
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void ExtendedAndPlainScanCodesAreDifferentInputs()
        {
            var a = _arbiter.CreateClient();
            var b = _arbiter.CreateClient();

            a.KeyDown(0x1D, false); // left Ctrl
            b.KeyDown(0x1D, true);  // right Ctrl
            a.KeyUp(0x1D, false);

            Assert.Equal(new[] { "kd:1D", "kd:1D", "ku:1D" }, _sink.Calls);
        }

        [Fact]
        public void MouseButtonsAreSharedToo()
        {
            var a = _arbiter.CreateClient();
            var b = _arbiter.CreateClient();

            a.MouseDown(MouseButton.Right);
            b.MouseDown(MouseButton.Right);
            a.MouseUp(MouseButton.Right);

            Assert.Contains("mRight", _sink.Down);

            b.MouseUp(MouseButton.Right);
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void ReleaseAllOwnedDropsOnlyThatClientsInput()
        {
            var a = _arbiter.CreateClient();
            var b = _arbiter.CreateClient();

            a.KeyDown(W, false);
            a.KeyDown(A, false);
            b.KeyDown(W, false);

            Assert.Equal(0, a.ReleaseAllOwned());

            Assert.Contains("k11", _sink.Down);       // B still holds W
            Assert.DoesNotContain("k1E", _sink.Down); // A was the only owner of A
            Assert.Equal(1, _arbiter.HeldCount);
        }

        [Fact]
        public void ReleaseAllLetsGoOfEverythingNewestFirst()
        {
            var a = _arbiter.CreateClient();
            var b = _arbiter.CreateClient();

            a.KeyDown(W, false);
            b.KeyDown(A, false);
            b.MouseDown(MouseButton.Left);

            Assert.Equal(0, _arbiter.ReleaseAll());

            Assert.Empty(_sink.Down);
            Assert.Equal(new[] { "mu:Left", "ku:1E", "ku:11" }, _sink.Calls.Skip(3).ToArray());
            Assert.Equal(0, _arbiter.HeldCount);

            // A client releasing afterwards only sends a harmless extra Up, and nothing is owned twice.
            a.KeyUp(W, false);
            Assert.Equal(0, _arbiter.HeldCount);
        }

        [Fact]
        public void ARefusedUpStaysPendingUntilARetryWorks()
        {
            var a = _arbiter.CreateClient();
            bool refuse = true;
            _sink.ThrowOn = call => refuse && call == "ku:11";

            a.KeyDown(W, false);
            Assert.ThrowsAny<Exception>(() => a.KeyUp(W, false));
            Assert.Equal(1, _arbiter.PendingCount);

            Assert.Equal(1, _arbiter.RetryPending());

            refuse = false;
            Assert.Equal(0, _arbiter.RetryPending());
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void APendingInputPressedAgainIsTakenOver()
        {
            var a = _arbiter.CreateClient();
            var b = _arbiter.CreateClient();
            bool refuse = true;
            _sink.ThrowOn = call => refuse && call == "ku:11";

            a.KeyDown(W, false);
            Assert.ThrowsAny<Exception>(() => a.KeyUp(W, false));

            b.KeyDown(W, false);
            Assert.Equal(0, _arbiter.PendingCount);

            refuse = false;
            b.KeyUp(W, false);
            Assert.Empty(_sink.Down);
        }

        [Fact]
        public void AFailedDownIsStillOwnedSoItsReleaseGoesOut()
        {
            var a = _arbiter.CreateClient();
            _sink.ThrowOn = call => call == "kd:11";

            Assert.ThrowsAny<Exception>(() => a.KeyDown(W, false));
            Assert.True(a.Owns(SharedInputArbiter.InputId.Key(W, false)));

            _sink.ThrowOn = null;
            a.KeyUp(W, false);
            Assert.Contains("ku:11", _sink.Calls);
        }

        [Fact]
        public void ConcurrentClientsNeverLeaveAKeyStuck()
        {
            const int clients = 8;
            var tasks = Enumerable.Range(0, clients).Select(_ => Task.Run(() =>
            {
                var c = _arbiter.CreateClient();

                for (int i = 0; i < 500; i++)
                {
                    c.KeyDown(W, false);
                    c.KeyDown(A, false);
                    c.KeyUp(W, false);
                    c.KeyUp(A, false);
                }
            })).ToArray();

            Task.WaitAll(tasks);

            Assert.Empty(_sink.Down);
            Assert.Equal(0, _arbiter.HeldCount);

            // The OS never saw two Downs in a row for W without an Up in between (or the reverse) from different clients.
            int depth = 0;

            foreach (string call in _sink.Calls.Where(c => c.EndsWith(":11")))
            {
                depth += call.StartsWith("kd") ? 1 : -1;
                Assert.InRange(depth, 0, 1);
            }
        }

        [Fact]
        public void TwoPlayersSharingAKeyThroughTheArbiterKeepItHeldUntilBothEnd()
        {
            var playerA = new MacroPlayer(_arbiter.CreateClient(), new FakeDelay());
            var holder = _arbiter.CreateClient(); // stands in for a macro that is still running and holds W

            holder.KeyDown(W, false);

            // A presses W and its run ends: the player's end-of-run release must not take W away from the other holder.
            Assert.Equal(PlayOutcome.Completed, playerA.Play(Of(KeyDown(W), Wait(1)), default).Outcome);
            Assert.Contains("k11", _sink.Down);
            Assert.Equal(new[] { "kd:11" }, _sink.Calls);

            holder.KeyUp(W, false);
            Assert.Empty(_sink.Down);
        }
    }
}
