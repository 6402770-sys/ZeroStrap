using System;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using ZeroStrap.Macros;

namespace ZeroStrap.Tests
{
    /// <summary>
    /// Ownership of the macros between the settings window and the background host. The named mutex behaves the same across
    /// threads as across processes, so the hand-over is tested in-process with a unique name per test.
    /// </summary>
    public sealed class MacroHostLockTests
    {
        private readonly string _name = "ZeroStrap-MacroHostTest-" + Guid.NewGuid().ToString("N");

        [Fact]
        public void OnlyOneOwnerAtATime()
        {
            using var first = MacroHostLock.Acquire(TimeSpan.Zero, requestRelease: false, _name);
            Assert.NotNull(first);

            Assert.Null(MacroHostLock.Acquire(TimeSpan.FromMilliseconds(100), requestRelease: false, _name));

            first!.Dispose();

            using var second = MacroHostLock.Acquire(TimeSpan.Zero, requestRelease: false, _name);
            Assert.NotNull(second);
        }

        [Fact]
        public void AWaitingHostGetsTheLockOnceTheWindowLetsGo()
        {
            var window = MacroHostLock.Acquire(TimeSpan.Zero, requestRelease: false, _name);
            Assert.NotNull(window);

            var host = MacroHostLock.AcquireAsync(TimeSpan.FromSeconds(5), requestRelease: false, _name);
            Thread.Sleep(100);
            Assert.False(host.IsCompleted);

            window!.Dispose();

            using var acquired = host.Result;
            Assert.NotNull(acquired);
        }

        [Fact]
        public void TheWindowAsksABackgroundHostToHandOver()
        {
            var host = MacroHostLock.Acquire(TimeSpan.Zero, requestRelease: false, _name);
            Assert.NotNull(host);

            using var request = MacroHostLock.OpenReleaseRequest(_name);
            request.Reset();

            // The background host: when asked, it releases (in the app it shuts its runtime down first).
            var listener = Task.Run(() =>
            {
                if (request.WaitOne(5000))
                    host!.Dispose();
            });

            using var window = MacroHostLock.Acquire(TimeSpan.FromSeconds(5), requestRelease: true, _name);

            Assert.NotNull(window);
            Assert.True(listener.Wait(5000));
        }

        [Fact]
        public void WithoutARequestTheHolderKeepsTheLock()
        {
            using var host = MacroHostLock.Acquire(TimeSpan.Zero, requestRelease: false, _name);

            Assert.Null(MacroHostLock.Acquire(TimeSpan.FromMilliseconds(200), requestRelease: true, _name)); // nobody listens: times out
            Assert.True(host!.IsAcquired);
        }

        [Fact]
        public void DisposeIsIdempotent()
        {
            var hostLock = MacroHostLock.Acquire(TimeSpan.Zero, requestRelease: false, _name);

            hostLock!.Dispose();
            hostLock.Dispose();

            Assert.False(hostLock.IsAcquired);
        }

        [Fact]
        public void TheHostStaysWhileRobloxRunsAndLeavesAfterItCloses()
        {
            var t = TimeSpan.Zero;
            var lifetime = new MacroHostLifetime(t);

            Assert.False(lifetime.ShouldExit(true, t));
            Assert.False(lifetime.ShouldExit(true, t += TimeSpan.FromHours(3)));
            Assert.False(lifetime.ShouldExit(false, t += TimeSpan.FromSeconds(5))); // a rejoin gap
            Assert.False(lifetime.ShouldExit(true, t += TimeSpan.FromSeconds(5)));
            Assert.False(lifetime.ShouldExit(false, t += TimeSpan.FromSeconds(1)));
            Assert.True(lifetime.ShouldExit(false, t + MacroHostLifetime.AbsenceGrace));
        }

        [Fact]
        public void TheHostWaitsForRobloxToStartButNotForever()
        {
            var lifetime = new MacroHostLifetime(TimeSpan.Zero);

            Assert.False(lifetime.ShouldExit(false, TimeSpan.FromSeconds(30)));
            Assert.True(lifetime.ShouldExit(false, MacroHostLifetime.StartupGrace));
        }
    }
}
