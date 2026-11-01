namespace ZeroStrap.Macros
{
    public enum PlayOutcome { Completed, Cancelled, Faulted }

    /// <summary>Result of <see cref="MacroPlayer.Play"/>. <see cref="Error"/> is set for <see cref="PlayOutcome.Faulted"/>.</summary>
    public readonly record struct PlayResult(PlayOutcome Outcome, string? Error = null)
    {
        public static PlayResult Completed => new(PlayOutcome.Completed);
        public static PlayResult Cancelled => new(PlayOutcome.Cancelled);
        public static PlayResult Faulted(string error) => new(PlayOutcome.Faulted, error);
    }

    /// <summary>
    /// Plays a macro through an <see cref="IInputSink"/>, synchronously on the calling thread. Safety rule: every key or
    /// mouse button the player presses is tracked, and whatever is still held when playback ends (completion, cancel or any
    /// exception) is released, one input at a time so a failing release cannot skip the others.
    /// </summary>
    public sealed class MacroPlayer
    {
        private readonly record struct HeldInput(bool IsMouse, ushort Scan, bool Extended, MouseButton Button);

        private static readonly TimeSpan FocusPollInterval = TimeSpan.FromMilliseconds(50);
        private const int SendLockWaitMs = 250;
        private static readonly TimeSpan FocusSlice = TimeSpan.FromMilliseconds(10);

        private readonly IInputSink _sink;
        private readonly IDelay _delay;
        private readonly IFocusGate? _gate;
        private readonly Action<string> _log;

        // _sendLock makes "check for stop, record as held, send" and "send every Up" each one atomic step, so an Up sent from
        // another thread (emergency stop) can never be followed by a Down that was already on its way: that would leave the key stuck.
        // Lock order: _sendLock, then _heldLock.
        private readonly object _sendLock = new();
        private readonly object _heldLock = new();
        private readonly List<HeldInput> _held = new();

        // Inputs whose release was refused by the OS (a blocked Up), kept after the run so they can be retried.
        private readonly List<HeldInput> _pending = new();

        private CancellationToken _runToken;
        private volatile bool _stopRequested;

        private int _playing;

        // State of the run in progress (Play is not re-entrant, so plain fields are enough).
        private double _speed = 1.0;
        private MacroFocusRule _rule;
        private long _scheduledMs;
        private bool _paused;
        private bool _gateFaultLogged;

        /// <summary>Raised with true when playback pauses because the focus rule failed, false when it resumes or ends.</summary>
        public event Action<bool>? PausedChanged;

        public bool IsPlaying => Volatile.Read(ref _playing) == 1;

        public MacroPlayer(IInputSink sink, IDelay delay, IFocusGate? gate = null, Action<string>? log = null)
        {
            _sink = sink;
            _delay = delay;
            _gate = gate;
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// Runs the macro to completion, cancellation or failure. The macro is normalised and validated first; an invalid macro
        /// is not played. Everything still held is released before this returns.
        /// </summary>
        public PlayResult Play(Macro macro, CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _playing, 1, 0) != 0)
                return PlayResult.Faulted("A macro is already playing");

            var outcome = PlayResult.Faulted("Playback did not run");
            int releaseFailures = 0;

            lock (_sendLock)
            {
                _runToken = ct;
                _stopRequested = false;
                TryReleasePendingLocked(); // an earlier run's blocked Ups may go through now
            }

            try
            {
                outcome = RunGuarded(macro, ct);
            }
            finally
            {
                lock (_sendLock)
                {
                    releaseFailures = ReleaseAllHeldLocked();
                    MoveHeldToPendingLocked(); // anything that still would not release stays pending instead of being forgotten
                }

                EndPause();
                Volatile.Write(ref _playing, 0);
            }

            if (releaseFailures > 0 && outcome.Outcome != PlayOutcome.Faulted)
                outcome = PlayResult.Faulted($"Could not release {releaseFailures} held input(s)");

            return outcome;
        }

        /// <summary>
        /// Sends Up for everything currently held (newest first). Safe to call from another thread, e.g. an emergency stop;
        /// one failing release never skips the rest. Returns how many releases failed. If a macro is playing it is also told to
        /// stop: once this has run, the player can never press anything again for that run. Failed releases stay pending
        /// (see <see cref="RetryPendingReleases"/>).
        /// </summary>
        public int ReleaseHeld()
        {
            // The flag goes up before the lock is taken: Press checks it under the lock, so no press can follow this call even
            // if the lock below cannot be had.
            if (IsPlaying)
                _stopRequested = true;

            // A bounded wait, so an emergency stop can never hang behind a send that is stuck inside the OS.
            if (!Monitor.TryEnter(_sendLock, SendLockWaitMs))
            {
                Log("Could not release held input: another send is in progress");
                return 1;
            }

            try
            {
                int failures = ReleaseAllHeldLocked();

                if (!IsPlaying)
                    MoveHeldToPendingLocked();

                return failures;
            }
            finally
            {
                Monitor.Exit(_sendLock);
            }
        }

        /// <summary>How many inputs are still logically held because their Up was refused (typically by UIPI).</summary>
        public int PendingReleaseCount
        {
            get
            {
                // Bounded like ReleaseHeld: a send stuck inside the OS must not hang whoever asks.
                if (!Monitor.TryEnter(_sendLock, SendLockWaitMs))
                    return 0;

                try
                {
                    return _pending.Count;
                }
                finally
                {
                    Monitor.Exit(_sendLock);
                }
            }
        }

        /// <summary>Tries again to release inputs whose Up was refused. Does nothing while a macro is playing. Returns how many are still pending.</summary>
        public int RetryPendingReleases()
        {
            if (!Monitor.TryEnter(_sendLock, SendLockWaitMs))
                return 1;

            try
            {
                if (!IsPlaying)
                    TryReleasePendingLocked();

                return _pending.Count;
            }
            finally
            {
                Monitor.Exit(_sendLock);
            }
        }

        private PlayResult RunGuarded(Macro macro, CancellationToken ct)
        {
            try
            {
                var (macroCopy, _) = MacroValidator.Normalize(macro);
                var errors = MacroValidator.Validate(macroCopy);

                if (errors.Count > 0)
                    return PlayResult.Faulted("Invalid macro: " + errors[0]);

                _speed = macroCopy.Speed;
                _rule = macroCopy.FocusRule;
                _scheduledMs = 0;
                _paused = false;
                _gateFaultLogged = false;

                lock (_heldLock)
                    _held.Clear();

                long iterations = macroCopy.Repeat.Mode switch
                {
                    MacroRepeatMode.Count => macroCopy.Repeat.Count,
                    MacroRepeatMode.UntilStopped => long.MaxValue,
                    _ => 1
                };

                for (long i = 0; i < iterations; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    long before = _scheduledMs;
                    RunList(macroCopy.Actions, ct);

                    // Keeps an empty or instant macro from flooding input or spinning the CPU between iterations.
                    if (i + 1 < iterations)
                    {
                        long used = _scheduledMs - before;

                        if (used < MacroTiming.MinIterationMs)
                            WaitMs(MacroTiming.MinIterationMs - used, ct);
                    }
                }

                return PlayResult.Completed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _stopRequested)
            {
                return PlayResult.Cancelled;
            }
            catch (Exception ex)
            {
                Log($"Macro playback failed: {ex.Message}");
                return PlayResult.Faulted(ex.Message);
            }
        }

        private void RunList(List<MacroAction> actions, CancellationToken ct)
        {
            foreach (var action in actions)
            {
                ct.ThrowIfCancellationRequested();

                if (!action.Enabled)
                    continue;

                CheckFocus(ct);

                if (action.DelayBeforeMs > 0)
                    WaitMs(Scaled(action.DelayBeforeMs), ct);

                ct.ThrowIfCancellationRequested();
                RunAction(action, ct);
            }
        }

        private void RunAction(MacroAction a, CancellationToken ct)
        {
            switch (a.Kind)
            {
                case MacroActionKind.Key:
                    RunButtonLike(new HeldInput(false, a.ScanCode, a.Extended, default), (int)a.KeyMode, a.HoldMs, ct);
                    break;

                case MacroActionKind.Mouse:
                    // MacroButtonMode and MacroKeyMode share ordering: Down, Up, Click/Tap.
                    RunButtonLike(new HeldInput(true, 0, false, a.Button), (int)a.ButtonMode, a.HoldMs, ct);
                    break;

                case MacroActionKind.MouseMoveTo:
                    _sink.MoveAbsolute(a.X, a.Y);
                    break;

                case MacroActionKind.Camera:
                    RunCamera(a, ct);
                    break;

                case MacroActionKind.Wait:
                    WaitMs(Scaled(a.Ms), ct);
                    break;

                case MacroActionKind.Group:
                    RunGroup(a, ct);
                    break;
            }
        }

        // mode: 0 = down, 1 = up, 2 = tap/click
        private void RunButtonLike(HeldInput input, int mode, int holdMs, CancellationToken ct)
        {
            if (mode == 0)
            {
                Press(input);
            }
            else if (mode == 1)
            {
                Release(input);
            }
            else
            {
                Press(input);

                long hold = Scaled(holdMs);

                if (hold > 0)
                    WaitMs(hold, ct);

                Release(input);
            }
        }

        private void RunGroup(MacroAction group, CancellationToken ct)
        {
            if (!MacroTiming.HasEffect(group.Actions))
                return;

            int repeat = Math.Max(1, group.GroupRepeat);

            for (int r = 0; r < repeat; r++)
            {
                ct.ThrowIfCancellationRequested();

                long before = _scheduledMs;
                RunList(group.Actions, ct);

                if (r + 1 < repeat)
                {
                    long used = _scheduledMs - before;

                    if (used < MacroTiming.MinGroupIterationMs)
                        WaitMs(MacroTiming.MinGroupIterationMs - used, ct);
                }
            }
        }

        private void RunCamera(MacroAction a, CancellationToken ct)
        {
            long duration = Scaled(a.DurationMs);

            if (a.Dx == 0 && a.Dy == 0)
            {
                if (duration > 0)
                    WaitMs(duration, ct);

                return;
            }

            var rightButton = new HeldInput(true, 0, false, MouseButton.Right);
            bool pressedHere = false;

            // If an earlier action already holds the right button, leave it held afterwards.
            if (a.HoldRightButton && !IsHeld(rightButton))
            {
                Press(rightButton);
                pressedHere = true;
            }

            var steps = MacroTiming.CameraSteps(a.Dx, a.Dy, (int)Math.Min(int.MaxValue, duration), a.Easing);
            var start = _delay.Now;
            int i = 0;

            while (i < steps.Count)
            {
                ct.ThrowIfCancellationRequested();
                start += WaitUntil(start + TimeSpan.FromMilliseconds(steps[i].AtMs), ct);

                // Everything already due goes out in one SendInput so a late wake-up does not turn into a burst of calls.
                long elapsedMs = (long)Math.Floor((_delay.Now - start).TotalMilliseconds);
                int dx = 0, dy = 0;

                do
                {
                    dx += steps[i].Dx;
                    dy += steps[i].Dy;
                    i++;
                }
                while (i < steps.Count && steps[i].AtMs <= elapsedMs);

                _sink.MoveRelative(dx, dy);
            }

            // Trailing zero-delta steps are skipped, so wait out the remainder of the duration.
            start += WaitUntil(start + TimeSpan.FromMilliseconds(duration), ct);
            _scheduledMs += duration;

            if (pressedHere)
                Release(rightButton);
        }

        private long Scaled(long ms) => MacroTiming.Scaled(ms, _speed);

        private void WaitMs(long ms, CancellationToken ct)
        {
            _scheduledMs += ms;
            WaitUntil(_delay.Now + TimeSpan.FromMilliseconds(ms), ct);
        }

        private bool Gated => _gate is not null && _rule == MacroFocusRule.RobloxOnly;

        /// <summary>
        /// Waits until the deadline, checking the focus rule in every slice. Time spent paused for focus does not count
        /// towards the wait. Returns the total time lost to pauses so callers can shift their own reference times.
        /// </summary>
        private TimeSpan WaitUntil(TimeSpan deadline, CancellationToken ct)
        {
            var lost = TimeSpan.Zero;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lost += CheckFocus(ct);

                var remaining = deadline + lost - _delay.Now;

                if (remaining <= TimeSpan.Zero)
                    return lost;

                _delay.Wait(Gated && remaining > FocusSlice ? FocusSlice : remaining, ct);
            }
        }

        /// <summary>
        /// If the focus rule fails: releases everything held (remembering it), waits until focus returns, then presses it
        /// again. Returns how long playback was paused.
        /// </summary>
        private TimeSpan CheckFocus(CancellationToken ct)
        {
            if (!Gated || FocusAllowed())
                return TimeSpan.Zero;

            var started = _delay.Now;

            HeldInput[] remembered;
            lock (_heldLock)
                remembered = _held.ToArray();

            lock (_sendLock)
                ReleaseAllHeldLocked();

            _paused = true;
            RaisePaused(true);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                _delay.Wait(FocusPollInterval, ct);

                if (FocusAllowed())
                    break;
            }

            foreach (var input in remembered)
                Press(input);

            _paused = false;
            RaisePaused(false);
            return _delay.Now - started;
        }

        // A gate that throws counts as "not allowed": input is withheld rather than sent to an unknown window.
        private bool FocusAllowed()
        {
            try
            {
                return _gate!.IsAllowed();
            }
            catch (Exception ex)
            {
                if (!_gateFaultLogged)
                {
                    _gateFaultLogged = true;
                    Log($"Focus check failed: {ex.Message}");
                }

                return false;
            }
        }

        private void EndPause()
        {
            if (!_paused)
                return;

            _paused = false;
            RaisePaused(false);
        }

        private void RaisePaused(bool paused)
        {
            try { PausedChanged?.Invoke(paused); }
            catch (Exception ex) { Log($"PausedChanged handler failed: {ex.Message}"); }
        }

        // The input is recorded as held before it is sent: if the send throws part way, the release pass still sends an Up.
        // A stop or release requested from another thread wins: nothing is pressed after it.
        private void Press(HeldInput input)
        {
            lock (_sendLock)
            {
                _runToken.ThrowIfCancellationRequested();

                if (_stopRequested)
                    throw new OperationCanceledException();

                lock (_heldLock)
                {
                    if (!_held.Contains(input))
                        _held.Add(input);
                }

                if (input.IsMouse)
                    _sink.MouseDown(input.Button);
                else
                    _sink.KeyDown(input.Scan, input.Extended);
            }
        }

        // Dropped from the held set only after the Up was sent, so a failed Up is retried by the final release.
        private void Release(HeldInput input)
        {
            lock (_sendLock)
            {
                SendUp(input);

                lock (_heldLock)
                    _held.Remove(input);
            }
        }

        private void SendUp(HeldInput input)
        {
            if (input.IsMouse)
                _sink.MouseUp(input.Button);
            else
                _sink.KeyUp(input.Scan, input.Extended);
        }

        private bool IsHeld(HeldInput input)
        {
            lock (_heldLock)
                return _held.Contains(input);
        }

        // Caller holds _sendLock.
        private int ReleaseAllHeldLocked()
        {
            HeldInput[] snapshot;

            lock (_heldLock)
                snapshot = _held.ToArray();

            int failures = 0;

            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                try
                {
                    SendUp(snapshot[i]);

                    lock (_heldLock)
                        _held.Remove(snapshot[i]);
                }
                catch (Exception ex)
                {
                    failures++;
                    Log($"Could not release held input: {ex.Message}");
                }
            }

            return failures;
        }

        // Caller holds _sendLock.
        private void MoveHeldToPendingLocked()
        {
            lock (_heldLock)
            {
                foreach (var input in _held)
                {
                    if (!_pending.Contains(input))
                        _pending.Add(input);
                }

                _held.Clear();
            }
        }

        // Caller holds _sendLock.
        private void TryReleasePendingLocked()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                try
                {
                    SendUp(_pending[i]);
                    _pending.RemoveAt(i);
                }
                catch (Exception ex)
                {
                    Log($"Could not release held input: {ex.Message}");
                }
            }
        }

        private void Log(string message)
        {
            try { _log(message); }
            catch { /* logging must never break the release path */ }
        }
    }
}
