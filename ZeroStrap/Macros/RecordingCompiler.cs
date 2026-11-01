namespace ZeroStrap.Macros
{
    public enum RawEventKind
    {
        KeyDown,
        KeyUp,
        MouseDown,
        MouseUp,
        MouseDelta,

        /// <summary>
        /// Marker from the recorder: the stop key or an ignored shortcut was pressed. <see cref="RawRecordedEvent.X"/> holds the
        /// <see cref="HotkeyModifiers"/> that were part of the combination; the compiler removes those modifier presses from the recording.
        /// </summary>
        HotkeyConsumed
    }

    /// <summary>
    /// One captured input event. <see cref="TimeMs"/> is a monotonic millisecond timestamp. For key events
    /// <see cref="Scan"/>/<see cref="Extended"/> identify the key; for mouse button events <see cref="Button"/> is the button and
    /// <see cref="X"/>/<see cref="Y"/> the absolute cursor position; for <see cref="RawEventKind.MouseDelta"/> they are the
    /// relative movement.
    /// </summary>
    public readonly record struct RawRecordedEvent(
        RawEventKind Kind,
        double TimeMs,
        ushort Scan = 0,
        bool Extended = false,
        MouseButton Button = MouseButton.Left,
        int X = 0,
        int Y = 0);

    /// <summary>Settings for one recording session, used by both the recorder (what to capture) and the compiler (how to turn it into actions).</summary>
    public sealed class RecorderOptions
    {
        /// <summary>Pressing this ends the recording. The key itself is never recorded. Defaults to F9.</summary>
        public Hotkey StopKey { get; init; } = new(HotkeyModifiers.None, 0x78);

        /// <summary>Also ends the recording and is never recorded (the emergency-stop combination).</summary>
        public Hotkey? EmergencyStop { get; init; }

        /// <summary>Shortcuts that are never recorded (macro activation hotkeys) but do not end the recording.</summary>
        public IReadOnlyList<Hotkey> IgnoredHotkeys { get; init; } = Array.Empty<Hotkey>();

        /// <summary>When true, a MouseMoveTo to the cursor position is recorded before each button press. Off by default (games use relative input).</summary>
        public bool RecordAbsoluteMoves { get; init; }
    }

    /// <summary>
    /// Turns raw captured events into macro actions. Pure: no clocks, no Win32. Key and button pairs that go down and up with
    /// nothing in between within <see cref="TapMaxGapMs"/> become Tap/Click; bursts of relative movement become Camera actions.
    /// </summary>
    public static class RecordingCompiler
    {
        /// <summary>A down followed directly by its up within this many ms becomes a Tap/Click.</summary>
        public const int TapMaxGapMs = 500;

        /// <summary>A gap longer than this between two movement events starts a new Camera action.</summary>
        public const int BurstGapMs = 50;

        /// <summary>
        /// Compilation stops early past this many emitted actions (the validator allows 10000 nodes in total); the headroom is
        /// for the Ups that release whatever is still held when the cut happens.
        /// </summary>
        public const int MaxActions = 9700;

        private const int MaxCameraDelta = 20000;
        private const int MaxDelayMs = 600000;
        private const int MaxPosition = 100000;

        private enum ItemKind { KeyDown, KeyUp, ButtonDown, ButtonUp, Camera }

        private sealed class Item
        {
            public ItemKind Kind;
            public long StartMs;
            public long EndMs;
            public ushort Scan;
            public bool Extended;
            public MouseButton Button;
            public int X;
            public int Y;
            public bool Synthetic;
        }

        public static List<MacroAction> Compile(IReadOnlyList<RawRecordedEvent> events, RecorderOptions options) =>
            Compile(events, options, out _);

        public static List<MacroAction> Compile(IReadOnlyList<RawRecordedEvent> events, RecorderOptions options, out List<string> warnings)
        {
            warnings = new List<string>();
            var items = BuildItems(events, warnings);
            var actions = new List<MacroAction>();
            long? previousEnd = null;
            (int X, int Y)? lastPosition = null;

            bool cut = false;

            for (int i = 0; i < items.Count; i++)
            {
                // The cap counts emitted actions (a recorded absolute position adds a MouseMoveTo per press), not items.
                if (actions.Count >= MaxActions)
                {
                    cut = true;
                    break;
                }

                var item = items[i];
                int delay = previousEnd is null ? 0 : (int)Math.Clamp(item.StartMs - previousEnd.Value, 0, MaxDelayMs);

                if (item.Kind == ItemKind.ButtonDown && options.RecordAbsoluteMoves && lastPosition != (item.X, item.Y))
                {
                    actions.Add(new MacroAction
                    {
                        Kind = MacroActionKind.MouseMoveTo,
                        X = Math.Clamp(item.X, -MaxPosition, MaxPosition),
                        Y = Math.Clamp(item.Y, -MaxPosition, MaxPosition),
                        DelayBeforeMs = delay
                    });
                    lastPosition = (item.X, item.Y);
                    delay = 0;
                }

                var next = i + 1 < items.Count ? items[i + 1] : null;

                switch (item.Kind)
                {
                    case ItemKind.KeyDown:
                    {
                        if (next is { Kind: ItemKind.KeyUp, Synthetic: false } up && up.Scan == item.Scan && up.Extended == item.Extended
                            && up.StartMs - item.StartMs <= TapMaxGapMs)
                        {
                            actions.Add(Key(item, MacroKeyMode.Tap, delay, (int)(up.StartMs - item.StartMs)));
                            previousEnd = up.StartMs;
                            i++;
                        }
                        else
                        {
                            actions.Add(Key(item, MacroKeyMode.Down, delay, 0));
                            previousEnd = item.StartMs;
                        }

                        break;
                    }
                    case ItemKind.KeyUp:
                        actions.Add(Key(item, MacroKeyMode.Up, delay, 0));
                        previousEnd = item.StartMs;
                        break;
                    case ItemKind.ButtonDown:
                    {
                        if (next is { Kind: ItemKind.ButtonUp, Synthetic: false } up && up.Button == item.Button
                            && up.StartMs - item.StartMs <= TapMaxGapMs)
                        {
                            actions.Add(Mouse(item, MacroButtonMode.Click, delay, (int)(up.StartMs - item.StartMs)));
                            previousEnd = up.StartMs;
                            i++;
                        }
                        else
                        {
                            actions.Add(Mouse(item, MacroButtonMode.Down, delay, 0));
                            previousEnd = item.StartMs;
                        }

                        break;
                    }
                    case ItemKind.ButtonUp:
                        actions.Add(Mouse(item, MacroButtonMode.Up, delay, 0));
                        previousEnd = item.StartMs;
                        break;
                    case ItemKind.Camera:
                        actions.Add(new MacroAction
                        {
                            Kind = MacroActionKind.Camera,
                            Dx = item.X,
                            Dy = item.Y,
                            DurationMs = (int)Math.Clamp(Math.Max(1, item.EndMs - item.StartMs), 1, MaxDelayMs),
                            Easing = MacroEasing.Linear,
                            HoldRightButton = false,
                            DelayBeforeMs = delay
                        });
                        previousEnd = item.EndMs;
                        break;
                }
            }

            if (cut)
            {
                if (!warnings.Any(w => w.Contains("too long")))
                    warnings.Add("The recording was too long and was cut off.");

                ReleaseStillHeld(actions);
            }

            return actions;
        }

        /// <summary>After a cut, appends an Up for every key or button the emitted actions leave held, so the macro never ends with held input.</summary>
        private static void ReleaseStillHeld(List<MacroAction> actions)
        {
            var keys = new List<(ushort Scan, bool Extended)>();
            var buttons = new List<MouseButton>();

            foreach (var a in actions)
            {
                if (a.Kind == MacroActionKind.Key)
                {
                    var key = (a.ScanCode, a.Extended);

                    if (a.KeyMode == MacroKeyMode.Down && !keys.Contains(key)) keys.Add(key);
                    else if (a.KeyMode == MacroKeyMode.Up) keys.Remove(key);
                }
                else if (a.Kind == MacroActionKind.Mouse)
                {
                    if (a.ButtonMode == MacroButtonMode.Down && !buttons.Contains(a.Button)) buttons.Add(a.Button);
                    else if (a.ButtonMode == MacroButtonMode.Up) buttons.Remove(a.Button);
                }
            }

            foreach (var (scan, extended) in keys)
                actions.Add(new MacroAction { Kind = MacroActionKind.Key, ScanCode = scan, Extended = extended, KeyMode = MacroKeyMode.Up });

            foreach (var button in buttons)
                actions.Add(new MacroAction { Kind = MacroActionKind.Mouse, Button = button, ButtonMode = MacroButtonMode.Up });
        }

        private static MacroAction Key(Item item, MacroKeyMode mode, int delay, int holdMs) => new()
        {
            Kind = MacroActionKind.Key,
            ScanCode = item.Scan,
            Extended = item.Extended,
            KeyMode = mode,
            HoldMs = holdMs,
            DelayBeforeMs = delay
        };

        private static MacroAction Mouse(Item item, MacroButtonMode mode, int delay, int holdMs) => new()
        {
            Kind = MacroActionKind.Mouse,
            Button = item.Button,
            ButtonMode = mode,
            HoldMs = holdMs,
            DelayBeforeMs = delay
        };

        /// <summary>Which modifier a scan code is (Ctrl 1D, Shift 2A/36, Alt 38, Win E0 5B/5C), or None.</summary>
        private static HotkeyModifiers ModifierOf(ushort scan, bool extended) => scan switch
        {
            0x1D => HotkeyModifiers.Ctrl,
            0x2A or 0x36 when !extended => HotkeyModifiers.Shift,
            0x38 => HotkeyModifiers.Alt,
            0x5B or 0x5C when extended => HotkeyModifiers.Win,
            _ => HotkeyModifiers.None
        };

        /// <summary>Filters raw events (auto-repeat, orphan ups, bad scan codes), groups movement into bursts and releases anything still held.</summary>
        private static List<Item> BuildItems(IReadOnlyList<RawRecordedEvent> events, List<string> warnings)
        {
            var items = new List<Item>();
            var downKeys = new List<(ushort Scan, bool Extended)>();
            var downButtons = new List<MouseButton>();

            // Movement burst being collected.
            bool inBurst = false;
            double burstFirst = 0, burstLast = 0;
            long burstDx = 0, burstDy = 0;

            // Modifier keys that were part of a stop/ignored shortcut: their press is taken back out of the recording, and
            // their auto-repeats and release are skipped until they come up.
            var consumedKeys = new List<(ushort Scan, bool Extended)>();

            void ConsumeModifiers(HotkeyModifiers modifiers)
            {
                if (modifiers == HotkeyModifiers.None)
                    return;

                foreach (var key in downKeys.ToArray())
                {
                    var flag = ModifierOf(key.Scan, key.Extended);

                    if (flag == HotkeyModifiers.None || (modifiers & flag) == 0)
                        continue;

                    // A held modifier may already have been used with other keys in between; only its own Down is removed,
                    // which is what makes the macro start with the modifier up.
                    int index = items.FindLastIndex(it => it.Kind == ItemKind.KeyDown && it.Scan == key.Scan && it.Extended == key.Extended);

                    if (index >= 0)
                        items.RemoveAt(index);

                    downKeys.Remove(key);
                    consumedKeys.Add(key);
                }
            }

            double clock = double.NegativeInfinity;
            long lastMs = 0;
            int badScan = 0;
            bool truncated = false;

            void FlushBurst()
            {
                if (!inBurst)
                    return;

                inBurst = false;

                // A burst that nets out to no movement does nothing, so it is dropped (the delay folds into the next action).
                if (burstDx == 0 && burstDy == 0)
                    return;

                items.Add(new Item
                {
                    Kind = ItemKind.Camera,
                    StartMs = (long)Math.Round(burstFirst),
                    EndMs = (long)Math.Round(burstLast),
                    X = (int)burstDx,
                    Y = (int)burstDy
                });
            }

            foreach (var ev in events)
            {
                if (items.Count >= MaxActions)
                {
                    truncated = true;
                    break;
                }

                double t = Math.Max(ev.TimeMs, clock);
                clock = t;
                long ms = (long)Math.Round(t);
                lastMs = ms;

                if (ev.Kind == RawEventKind.MouseDelta)
                {
                    if (ev.X == 0 && ev.Y == 0)
                        continue;

                    bool fits = inBurst
                        && t - burstLast <= BurstGapMs
                        && Math.Abs(burstDx + ev.X) <= MaxCameraDelta
                        && Math.Abs(burstDy + ev.Y) <= MaxCameraDelta;

                    if (!fits)
                    {
                        FlushBurst();
                        inBurst = true;
                        burstFirst = t;
                        burstDx = 0;
                        burstDy = 0;
                    }

                    burstLast = t;
                    burstDx += ev.X;
                    burstDy += ev.Y;
                    continue;
                }

                // Anything that is actually kept ends the burst, so movement never gets reordered around a key or button event.
                // Events that are dropped (auto-repeat, orphans, bad scan codes) must not split it: holding W while turning
                // the camera repeats the W key-down every ~30 ms.
                switch (ev.Kind)
                {
                    case RawEventKind.HotkeyConsumed:
                        ConsumeModifiers((HotkeyModifiers)ev.X);
                        continue;
                    case RawEventKind.KeyDown:
                    case RawEventKind.KeyUp:
                    {
                        if (ev.Scan == 0 || ev.Scan > 0x7F)
                        {
                            badScan++;
                            continue;
                        }

                        var key = (ev.Scan, ev.Extended);

                        if (ev.Kind == RawEventKind.KeyDown)
                        {
                            if (downKeys.Contains(key))
                                continue; // auto-repeat

                            if (consumedKeys.Contains(key))
                                continue; // auto-repeat of a modifier that belongs to a stop or ignored shortcut

                            FlushBurst();
                            downKeys.Add(key);
                            items.Add(new Item { Kind = ItemKind.KeyDown, StartMs = ms, EndMs = ms, Scan = ev.Scan, Extended = ev.Extended });
                        }
                        else
                        {
                            if (consumedKeys.Remove(key))
                                continue; // the release of a modifier that belonged to a stop or ignored shortcut

                            if (!downKeys.Contains(key))
                                continue; // up for a key pressed before recording began

                            FlushBurst();
                            downKeys.Remove(key);
                            items.Add(new Item { Kind = ItemKind.KeyUp, StartMs = ms, EndMs = ms, Scan = ev.Scan, Extended = ev.Extended });
                        }

                        break;
                    }
                    case RawEventKind.MouseDown:
                        if (downButtons.Contains(ev.Button))
                            continue;

                        FlushBurst();
                        downButtons.Add(ev.Button);
                        items.Add(new Item { Kind = ItemKind.ButtonDown, StartMs = ms, EndMs = ms, Button = ev.Button, X = ev.X, Y = ev.Y });
                        break;
                    case RawEventKind.MouseUp:
                        if (!downButtons.Contains(ev.Button))
                            continue;

                        FlushBurst();
                        downButtons.Remove(ev.Button);
                        items.Add(new Item { Kind = ItemKind.ButtonUp, StartMs = ms, EndMs = ms, Button = ev.Button });
                        break;
                }
            }

            FlushBurst();

            // Whatever is still down at the end gets an Up straight away, so the macro never ends with held input.
            // The Ups are marked synthetic so they are not folded into a Tap that would hide how long the key was really held.
            foreach (var (scan, extended) in downKeys)
                items.Add(new Item { Kind = ItemKind.KeyUp, StartMs = lastMs, EndMs = lastMs, Scan = scan, Extended = extended, Synthetic = true });

            foreach (var button in downButtons)
                items.Add(new Item { Kind = ItemKind.ButtonUp, StartMs = lastMs, EndMs = lastMs, Button = button, Synthetic = true });

            if (badScan > 0)
                warnings.Add($"{badScan} key event(s) were skipped because they have no supported scan code.");

            if (truncated)
                warnings.Add("The recording was too long and was cut off.");

            return items;
        }
    }
}
