namespace ZeroStrap.Macros
{
    /// <summary>
    /// Shares one real <see cref="IInputSink"/> between macros that play at the same time. Every player gets its own client
    /// (an <see cref="IInputSink"/>), and a key or mouse button is owned by each client that pressed it and has not released it.
    /// The real sink only sees:
    /// <list type="bullet">
    /// <item>a Down when a client presses an input that no <i>other</i> client owns (so a lone macro sends exactly what it would
    /// send to the real sink, repeated presses included);</item>
    /// <item>an Up when the last owner releases it. A client releasing an input another client still owns sends nothing, so
    /// macro A stopping never lets go of the W that macro B is still holding.</item>
    /// </list>
    /// Ownership is a set per input: a client pressing the same key twice still frees it with one release (the player tracks
    /// held input the same way). Every real send happens under one lock, so an Up from one client and a Down from another can
    /// never reach the OS in the wrong order. The lock is only ever waited on for a bounded time, so a send stuck inside the OS
    /// cannot hang an emergency stop. An Up the OS refuses (UIPI) leaves the input "unreleased": <see cref="RetryPending"/> and
    /// <see cref="ReleaseAll"/> try it again, and a client pressing it again takes it over.
    /// </summary>
    public sealed class SharedInputArbiter
    {
        public readonly record struct InputId(bool IsMouse, ushort Scan, bool Extended, MouseButton Button)
        {
            public static InputId Key(ushort scan, bool extended) => new(false, scan, extended, default);

            public static InputId Mouse(MouseButton button) => new(true, 0, false, button);
        }

        /// <summary>How long a send waits for another client's send to finish before giving up (ms).</summary>
        internal const int LockWaitMs = 250;

        private readonly IInputSink _sink;
        private readonly Action<string> _log;
        private readonly object _lock = new();

        // Guarded by _lock. Insertion order is kept so releases go out newest first.
        private readonly List<InputId> _order = new();
        private readonly Dictionary<InputId, HashSet<int>> _owners = new();
        private readonly List<InputId> _unreleased = new();
        private int _nextClient;

        public SharedInputArbiter(IInputSink sink, Action<string>? log = null)
        {
            _sink = sink;
            _log = log ?? (_ => { });
        }

        /// <summary>A new client with its own ownership. Give one to each player.</summary>
        public Client CreateClient() => new(this, Interlocked.Increment(ref _nextClient));

        /// <summary>How many clients own <paramref name="input"/>.</summary>
        public int OwnerCount(InputId input)
        {
            lock (_lock)
                return _owners.TryGetValue(input, out var owners) ? owners.Count : 0;
        }

        /// <summary>How many inputs at least one client owns.</summary>
        public int HeldCount
        {
            get
            {
                lock (_lock)
                    return _owners.Count;
            }
        }

        /// <summary>Inputs no client owns any more whose Up the OS refused.</summary>
        public int PendingCount
        {
            get
            {
                if (!Monitor.TryEnter(_lock, LockWaitMs))
                    return 0;

                try
                {
                    return _unreleased.Count;
                }
                finally
                {
                    Monitor.Exit(_lock);
                }
            }
        }

        /// <summary>Tries the refused Ups again. Returns how many are still pending.</summary>
        public int RetryPending()
        {
            if (!Monitor.TryEnter(_lock, LockWaitMs))
                return 1;

            try
            {
                RetryPendingLocked();
                return _unreleased.Count;
            }
            finally
            {
                Monitor.Exit(_lock);
            }
        }

        /// <summary>
        /// Releases every input any client owns (newest first) and retries the pending ones; afterwards nobody owns anything.
        /// For Stop all, the emergency stop and shutdown, once the players were told to stop. One failure never skips the rest.
        /// Returns how many inputs are still not released.
        /// </summary>
        public int ReleaseAll()
        {
            if (!Monitor.TryEnter(_lock, LockWaitMs))
            {
                Log("Could not release held input: another send is in progress");
                return 1;
            }

            try
            {
                for (int i = _order.Count - 1; i >= 0; i--)
                    TrySendUpLocked(_order[i]);

                _order.Clear();
                _owners.Clear();
                RetryPendingLocked();
                return _unreleased.Count;
            }
            finally
            {
                Monitor.Exit(_lock);
            }
        }

        // ---- client operations ----

        private void Press(int client, InputId input)
        {
            Enter();

            try
            {
                if (!_owners.TryGetValue(input, out var owners))
                {
                    owners = new HashSet<int>();
                    _owners[input] = owners;
                    _order.Add(input);
                }

                bool others = owners.Count > (owners.Contains(client) ? 1 : 0);

                // Owned before it is sent: if the send throws part way, the client's release still sends an Up.
                owners.Add(client);
                _unreleased.Remove(input);

                if (!others)
                    SendDown(input);
            }
            finally
            {
                Monitor.Exit(_lock);
            }
        }

        private void Release(int client, InputId input)
        {
            Enter();

            try
            {
                if (_owners.TryGetValue(input, out var owners))
                {
                    owners.Remove(client);

                    if (owners.Count > 0)
                        return; // another macro still holds it

                    _owners.Remove(input);
                    _order.Remove(input);
                }

                // Nobody owns it (any more): the real Up goes out, as it would without sharing.
                try
                {
                    SendUp(input);
                    _unreleased.Remove(input);
                }
                catch
                {
                    if (!_unreleased.Contains(input))
                        _unreleased.Add(input);

                    throw;
                }
            }
            finally
            {
                Monitor.Exit(_lock);
            }
        }

        /// <summary>Drops everything <paramref name="client"/> still owns, sending Up for inputs nobody else owns. Never throws.</summary>
        private int ReleaseClient(int client)
        {
            if (!Monitor.TryEnter(_lock, LockWaitMs))
            {
                Log("Could not release a finished macro's input: another send is in progress");
                return 1;
            }

            try
            {
                int failures = 0;

                for (int i = _order.Count - 1; i >= 0; i--)
                {
                    var input = _order[i];
                    var owners = _owners[input];

                    if (!owners.Remove(client) || owners.Count > 0)
                        continue;

                    _owners.Remove(input);
                    _order.RemoveAt(i);

                    if (!TrySendUpLocked(input))
                        failures++;
                }

                return failures;
            }
            finally
            {
                Monitor.Exit(_lock);
            }
        }

        private bool Owns(int client, InputId input)
        {
            lock (_lock)
                return _owners.TryGetValue(input, out var owners) && owners.Contains(client);
        }

        // ---- helpers (caller holds _lock unless noted) ----

        private void Enter()
        {
            if (!Monitor.TryEnter(_lock, LockWaitMs))
                throw new TimeoutException("Another macro's input is stuck in Windows");
        }

        private bool TrySendUpLocked(InputId input)
        {
            try
            {
                SendUp(input);
                _unreleased.Remove(input);
                return true;
            }
            catch (Exception ex)
            {
                Log("Could not release held input: " + ex.Message);

                if (!_unreleased.Contains(input))
                    _unreleased.Add(input);

                return false;
            }
        }

        private void RetryPendingLocked()
        {
            for (int i = _unreleased.Count - 1; i >= 0; i--)
            {
                var input = _unreleased[i];

                if (_owners.ContainsKey(input))
                {
                    _unreleased.RemoveAt(i); // owned again: its owner's release covers it
                    continue;
                }

                try
                {
                    SendUp(input);
                    _unreleased.RemoveAt(i);
                }
                catch (Exception ex)
                {
                    Log("Could not release held input: " + ex.Message);
                }
            }
        }

        private void SendDown(InputId input)
        {
            if (input.IsMouse)
                _sink.MouseDown(input.Button);
            else
                _sink.KeyDown(input.Scan, input.Extended);
        }

        private void SendUp(InputId input)
        {
            if (input.IsMouse)
                _sink.MouseUp(input.Button);
            else
                _sink.KeyUp(input.Scan, input.Extended);
        }

        private void Log(string message)
        {
            try { _log(message); }
            catch { /* logging must never break the release path */ }
        }

        /// <summary>One player's view of the shared sink. Moves pass straight through; keys and buttons are shared as described above.</summary>
        public sealed class Client : IInputSink
        {
            private readonly SharedInputArbiter _owner;

            internal Client(SharedInputArbiter owner, int id)
            {
                _owner = owner;
                Id = id;
            }

            public int Id { get; }

            public void KeyDown(ushort scan, bool extended) => _owner.Press(Id, InputId.Key(scan, extended));

            public void KeyUp(ushort scan, bool extended) => _owner.Release(Id, InputId.Key(scan, extended));

            public void MouseDown(MouseButton button) => _owner.Press(Id, InputId.Mouse(button));

            public void MouseUp(MouseButton button) => _owner.Release(Id, InputId.Mouse(button));

            public void MoveRelative(int dx, int dy) => _owner._sink.MoveRelative(dx, dy);

            public void MoveAbsolute(int x, int y) => _owner._sink.MoveAbsolute(x, y);

            /// <summary>Whether this client currently owns <paramref name="input"/>.</summary>
            public bool Owns(InputId input) => _owner.Owns(Id, input);

            /// <summary>
            /// Gives up everything this client still owns (sending Up where it was the last owner). Called when its macro ends,
            /// whatever the reason, so a release that failed inside the player can never keep a key down. Never throws;
            /// returns how many releases failed (they stay pending in the arbiter).
            /// </summary>
            public int ReleaseAllOwned() => _owner.ReleaseClient(Id);
        }
    }
}
