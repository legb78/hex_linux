namespace HexLinux.Input;

/// <summary>
/// Turns the raw events of every keyboard into what they mean for the
/// shortcut, remembering which keys each keyboard holds.
///
/// <para><b>It lives on the daemon's loop, not in the reader threads.</b> Each
/// keyboard has a thread of its own that does nothing but read and post; the
/// decisions are all taken here, one event at a time, in the order they were
/// posted. Deciding in the reader threads, under a lock, would still let two
/// keyboards post their conclusions in the wrong order — a start decided on
/// one keyboard arriving after the stop decided on the other, and a recording
/// that never ends.</para>
///
/// <para><b>Keys are remembered per keyboard</b>, for the two cases where a
/// release never comes:</para>
/// <list type="bullet">
/// <item>a keyboard that disappears — unplugged, a Bluetooth keyboard
/// falling asleep — takes its held keys with it, and only those are forgotten:
/// a dictation held on another keyboard carries on;</item>
/// <item>a keyboard whose events the kernel dropped (<c>SYN_DROPPED</c>, the
/// reader fell behind) may have lost a release. Its events are ignored until
/// the next <c>SYN_REPORT</c>, then everything it held is forgotten.</item>
/// </list>
///
/// <para>Pure: devices are plain names, events plain values.</para>
/// </summary>
public sealed class HotkeyTracker
{
    private readonly Dictionary<string, HashSet<int>> _held = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resynchronising = new(StringComparer.Ordinal);

    public HotkeyTracker(ChordDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);

        Detector = detector;
    }

    public ChordDetector Detector { get; }

    /// <summary>One event read from <paramref name="device"/>.</summary>
    public ChordAction OnEvent(string device, InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (inputEvent.IsSyncDropped)
        {
            _resynchronising.Add(device);
            return ChordAction.None;
        }

        if (_resynchronising.Contains(device))
        {
            if (!inputEvent.IsSyncReport)
            {
                return ChordAction.None;
            }

            _resynchronising.Remove(device);
            return ForgetKeysOf(device);
        }

        if (!inputEvent.IsKey)
        {
            return ChordAction.None;
        }

        int code = inputEvent.Code;
        HashSet<int> held = HeldOn(device);

        switch (inputEvent.Transition)
        {
            case KeyTransition.Pressed:
                held.Add(code);
                return Detector.OnKeyDown(code);

            case KeyTransition.Repeated:
                // A repeat of a press that was never seen — the key was already
                // down when the keyboard was opened, or its state was just
                // forgotten — must not complete the shortcut: the user did not
                // press anything now.
                return held.Contains(code) ? Detector.OnKeyDown(code) : ChordAction.None;

            case KeyTransition.Released:
                held.Remove(code);
                return Detector.OnKeyUp(code);

            default:
                return ChordAction.None;
        }
    }

    /// <summary>The keyboard is gone: forget what it held.</summary>
    public ChordAction OnDeviceRemoved(string device)
    {
        ArgumentNullException.ThrowIfNull(device);

        _resynchronising.Remove(device);
        ChordAction action = ForgetKeysOf(device);
        _held.Remove(device);

        return action;
    }

    /// <summary>The modifiers held right now, on any keyboard.</summary>
    public IReadOnlySet<int> HeldModifiers()
    {
        HashSet<int> modifiers = [];

        foreach (HashSet<int> held in _held.Values)
        {
            modifiers.UnionWith(held.Where(LinuxKeys.Modifiers.Contains));
        }

        return modifiers;
    }

    private HashSet<int> HeldOn(string device)
    {
        if (!_held.TryGetValue(device, out HashSet<int>? held))
        {
            held = [];
            _held[device] = held;
        }

        return held;
    }

    private ChordAction ForgetKeysOf(string device)
    {
        if (!_held.TryGetValue(device, out HashSet<int>? held) || held.Count == 0)
        {
            return ChordAction.None;
        }

        int[] codes = [.. held];
        held.Clear();

        return Detector.Forget(codes);
    }
}
