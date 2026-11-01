using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ZeroStrap.Macros;

namespace ZeroStrap.Tests
{
    /// <summary>Logs every call as text ("kd:1E", "mu:Left", "rel:3,4") and tracks what would still be held down.</summary>
    internal sealed class RecordingSink : IInputSink
    {
        public List<string> Calls { get; } = new();
        public HashSet<string> Down { get; } = new();

        /// <summary>Throws (after logging the call) when this returns true for the call text.</summary>
        public Func<string, bool>? ThrowOn { get; set; }

        /// <summary>Runs after each call has been logged; lets a test re-enter or inspect state.</summary>
        public Action<string>? OnCall { get; set; }

        private void Record(string call, string? downKey, bool isDown)
        {
            // Playback runs on another thread in the runtime tests, so recording is serialised.
            lock (Calls)
            {
                Calls.Add(call);

                if (downKey is not null)
                {
                    if (isDown) Down.Add(downKey); else Down.Remove(downKey);
                }
            }

            OnCall?.Invoke(call);

            if (ThrowOn?.Invoke(call) == true)
                throw new InvalidOperationException("sink failure on " + call);
        }

        public void KeyDown(ushort scan, bool extended) => Record($"kd:{scan:X2}", $"k{scan:X2}{(extended ? "e" : "")}", true);
        public void KeyUp(ushort scan, bool extended) => Record($"ku:{scan:X2}", $"k{scan:X2}{(extended ? "e" : "")}", false);
        public void MouseDown(MouseButton button) => Record($"md:{button}", "m" + button, true);
        public void MouseUp(MouseButton button) => Record($"mu:{button}", "m" + button, false);
        public void MoveRelative(int dx, int dy) => Record($"rel:{dx},{dy}", null, false);
        public void MoveAbsolute(int x, int y) => Record($"abs:{x},{y}", null, false);

        public (int Dx, int Dy) RelativeTotal()
        {
            int dx = 0, dy = 0;

            foreach (string call in Calls.Where(c => c.StartsWith("rel:")))
            {
                var parts = call.Substring(4).Split(',');
                dx += int.Parse(parts[0]);
                dy += int.Parse(parts[1]);
            }

            return (dx, dy);
        }
    }

    /// <summary>Virtual clock: Wait advances time instantly and can cancel a token at a chosen virtual time.</summary>
    internal sealed class FakeDelay : IDelay
    {
        public TimeSpan Time { get; set; }
        public List<TimeSpan> Waits { get; } = new();

        public CancellationTokenSource? Cts { get; set; }
        public TimeSpan? CancelAt { get; set; }

        /// <summary>Called after the clock moved, with the new time.</summary>
        public Action<TimeSpan>? OnAdvance { get; set; }

        public TimeSpan Now => Time;

        public void Wait(TimeSpan duration, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Waits.Add(duration);

            var target = Time + duration;

            if (CancelAt is { } cancelAt && target >= cancelAt)
            {
                Time = cancelAt;
                Cts!.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            Time = target;
            OnAdvance?.Invoke(Time);
        }
    }

    internal sealed class FakeGate : IFocusGate
    {
        public Func<bool> Allowed { get; set; } = () => true;

        public bool IsAllowed() => Allowed();
    }

    internal static class MacroBuilders
    {
        public static Macro Of(params MacroAction[] actions) => new()
        {
            Id = "test",
            Name = "Test",
            FocusRule = MacroFocusRule.AnyWindow,
            Actions = actions.ToList()
        };

        public static MacroAction TapKey(ushort scan, int holdMs = 0, int delayMs = 0) =>
            new() { Kind = MacroActionKind.Key, ScanCode = scan, KeyMode = MacroKeyMode.Tap, HoldMs = holdMs, DelayBeforeMs = delayMs };

        public static MacroAction KeyDown(ushort scan, int delayMs = 0) =>
            new() { Kind = MacroActionKind.Key, ScanCode = scan, KeyMode = MacroKeyMode.Down, DelayBeforeMs = delayMs };

        public static MacroAction KeyUp(ushort scan, int delayMs = 0) =>
            new() { Kind = MacroActionKind.Key, ScanCode = scan, KeyMode = MacroKeyMode.Up, DelayBeforeMs = delayMs };

        public static MacroAction MouseDown(MouseButton b) =>
            new() { Kind = MacroActionKind.Mouse, Button = b, ButtonMode = MacroButtonMode.Down };

        public static MacroAction Click(MouseButton b, int holdMs = 0) =>
            new() { Kind = MacroActionKind.Mouse, Button = b, ButtonMode = MacroButtonMode.Click, HoldMs = holdMs };

        public static MacroAction Wait(int ms) => new() { Kind = MacroActionKind.Wait, Ms = ms };

        public static MacroAction Camera(int dx, int dy, int durationMs, bool holdRight = false, MacroEasing easing = MacroEasing.Linear) =>
            new() { Kind = MacroActionKind.Camera, Dx = dx, Dy = dy, DurationMs = durationMs, HoldRightButton = holdRight, Easing = easing };

        public static MacroAction Group(int repeat, params MacroAction[] children) =>
            new() { Kind = MacroActionKind.Group, GroupRepeat = repeat, Actions = children.ToList() };
    }
}
