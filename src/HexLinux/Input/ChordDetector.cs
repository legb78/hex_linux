namespace HexLinux.Input;

/// <summary>What the shortcut is asking the application to do.</summary>
public enum ChordAction
{
    None,

    /// <summary>Every key is down: start recording.</summary>
    Start,

    /// <summary>A key was released: transcribe what was said.</summary>
    Stop,

    /// <summary>A foreign key intervened: give up without transcribing.</summary>
    Cancel,
}

/// <summary>
/// State machine for the hold-to-dictate shortcut.
///
/// Entirely pure logic: it knows nothing of evdev or of the keyboard, it just
/// receives key codes and decides. That is what makes it testable, whereas the
/// device reader itself is not.
///
/// <para><b>What differs from the Windows version: nothing is swallowed.</b>
/// A Windows low-level hook can keep a key from the rest of the system; a
/// reader of <c>/dev/input</c> only observes. The one way to withhold a key on
/// Linux is to grab the whole device (<c>EVIOCGRAB</c>) and re-emit every
/// other keystroke through a virtual keyboard — which would put this process
/// on the path of everything the user types, for the sake of one key. So the
/// keys of the shortcut still reach the desktop, and the default shortcut is
/// chosen to be harmless when they do: see <c>settings.json</c>.</para>
///
/// <para>With no swallowing there is no balance to keep and no Start menu to
/// neutralise; what remains are the races that cost a dictation — keyboard
/// auto-repeat, keys released out of order, a second start while the first
/// dictation is still being transcribed, a key left held when its keyboard is
/// unplugged.</para>
/// </summary>
public sealed class ChordDetector
{
    /// <summary>
    /// Codes accepted for each key of the shortcut. One slot per requested
    /// key; "Ctrl" accepts the left key as well as the right one.
    /// </summary>
    private readonly int[][] _requirements;

    /// <summary>Code actually held that satisfies each slot, or 0.</summary>
    private readonly int[] _satisfiedBy;

    /// <summary>
    /// True from the moment a dictation is announced until every key of the
    /// shortcut has been let go.
    ///
    /// Without it, a shortcut completed again without having been fully
    /// released announces a second start. Releasing one key and pressing it
    /// back is enough: the other slots are still held, so the chord reads as
    /// complete once more. That is the ordinary way of letting go of a
    /// two-key shortcut, and the second start lands while the first dictation
    /// is still being transcribed.
    /// </summary>
    private bool _started;

    public ChordDetector(IEnumerable<string> keyNames)
    {
        ArgumentNullException.ThrowIfNull(keyNames);

        _requirements = [.. keyNames.Select(LinuxKeys.Resolve)];

        if (_requirements.Length == 0)
        {
            throw new ArgumentException("The shortcut must hold at least one key.", nameof(keyNames));
        }

        _satisfiedBy = new int[_requirements.Length];
    }

    /// <summary>True between the moment the shortcut completes and its release.</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// True while any key of the shortcut is still held — after a two-key
    /// shortcut has been half released, for instance. What lets an insertion
    /// wait for the whole shortcut to be let go.
    /// </summary>
    public bool IsAnyHeld => !NothingHeld();

    /// <summary>Every code that can take part in the shortcut.</summary>
    public IReadOnlySet<int> Codes => _requirements.SelectMany(codes => codes).ToHashSet();

    /// <summary>
    /// A key went down. Auto-repeat, which the kernel reports as a separate
    /// value, is to be passed here too: it is recognised and ignored.
    /// </summary>
    public ChordAction OnKeyDown(int code)
    {
        // Auto-repeat: holding a key sends key-downs in bursts. They must not
        // restart the recording.
        if (IsAlreadySatisfying(code))
        {
            return ChordAction.None;
        }

        int slot = FindFreeSlot(code);

        if (slot < 0)
        {
            // Key foreign to the shortcut. During a dictation it interrupts:
            // RightCtrl held and then C pressed is a copy, and the user was
            // not asking for a transcription.
            if (IsActive)
            {
                IsActive = false;
                return ChordAction.Cancel;
            }

            return ChordAction.None;
        }

        _satisfiedBy[slot] = code;

        // Complete again while _started still holds: the shortcut was only
        // half released. Nothing starts rather than opening a dictation over
        // the one being transcribed.
        if (!AllSatisfied() || IsActive || _started)
        {
            return ChordAction.None;
        }

        IsActive = true;
        _started = true;

        return ChordAction.Start;
    }

    public ChordAction OnKeyUp(int code)
    {
        int slot = FindSatisfiedSlot(code);

        if (slot < 0)
        {
            return ChordAction.None;
        }

        _satisfiedBy[slot] = 0;

        // The shortcut is armed again only once nothing is held any more. This
        // is the release that pairs with _started, and it must be checked on
        // every key-up, including those arriving after the transcription has
        // begun.
        if (NothingHeld())
        {
            _started = false;
        }

        if (!IsActive)
        {
            return ChordAction.None;
        }

        IsActive = false;
        return ChordAction.Stop;
    }

    /// <summary>
    /// Forgets any state in progress.
    ///
    /// <para>Indispensable when key-ups stop arriving. On Linux the kernel
    /// keeps reporting them across a locked screen, unlike Windows — but not
    /// across an unplugged keyboard: a key held on a device that vanishes is
    /// never released, and without a reset the shortcut would stop responding
    /// until the application restarts.</para>
    /// </summary>
    public ChordAction Reset()
    {
        bool wasActive = IsActive;

        Array.Clear(_satisfiedBy);
        IsActive = false;
        _started = false;

        return wasActive ? ChordAction.Cancel : ChordAction.None;
    }

    /// <summary>
    /// Forgets some keys without their release ever arriving: the ones held
    /// on a keyboard that has just disappeared, or whose events the kernel
    /// dropped.
    ///
    /// <para>Narrower than <see cref="Reset"/> on purpose. A Bluetooth
    /// keyboard falling asleep must not cancel a dictation held on the laptop's
    /// own keyboard; only when a forgotten key was part of the shortcut in
    /// progress is the dictation given up — nobody can tell whether it was
    /// released on purpose.</para>
    /// </summary>
    public ChordAction Forget(IEnumerable<int> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);

        bool forgotAny = false;

        foreach (int code in codes)
        {
            int slot = FindSatisfiedSlot(code);

            if (slot >= 0)
            {
                _satisfiedBy[slot] = 0;
                forgotAny = true;
            }
        }

        if (NothingHeld())
        {
            _started = false;
        }

        if (!forgotAny || !IsActive)
        {
            return ChordAction.None;
        }

        IsActive = false;
        return ChordAction.Cancel;
    }

    private bool IsAlreadySatisfying(int code) => Array.IndexOf(_satisfiedBy, code) >= 0;

    private int FindFreeSlot(int code)
    {
        for (int i = 0; i < _requirements.Length; i++)
        {
            if (_satisfiedBy[i] == 0 && Array.IndexOf(_requirements[i], code) >= 0)
            {
                return i;
            }
        }

        return -1;
    }

    private int FindSatisfiedSlot(int code) => Array.IndexOf(_satisfiedBy, code);

    private bool AllSatisfied() => Array.IndexOf(_satisfiedBy, 0) < 0;

    /// <summary>True when no key of the shortcut is held any more.</summary>
    private bool NothingHeld() => Array.TrueForAll(_satisfiedBy, key => key == 0);
}
