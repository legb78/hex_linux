namespace HexLinux.Input;

/// <summary>
/// Translates the key names from settings.json into Linux input key codes,
/// the <c>KEY_*</c> values of <c>linux/input-event-codes.h</c>.
///
/// <para>These are the codes the kernel reports in <c>/dev/input/event*</c>:
/// physical key positions, before any keyboard layout is applied. That is
/// exactly what a hold-to-dictate shortcut is made of — modifiers and keys
/// that type nothing — and it means the shortcut behaves the same on an AZERTY
/// and a QWERTY keyboard.</para>
///
/// <para>Generic names — "Ctrl", "Super" — each stand for two physical keys.
/// The kernel never reports a generic code: it always sends the left or the
/// right key. This class bridges the two, by accepting either.</para>
///
/// <para><b>Which keys, and why fewer than HexWin.</b> HexWin accepts any key
/// since its pull request #68, because Windows withholds the keys of the
/// shortcut from the application. Linux does not (see
/// <see cref="ChordDetector"/>): a letter held as the shortcut would type
/// itself, then repeat, for the whole dictation. So only the keys whose
/// press reaching the desktop costs little are named here — the modifiers,
/// the function keys F1 to F24 and Pause. The others are refused with their
/// reason (<see cref="RefusedKeys"/>).</para>
///
/// <para>Every value below was checked against the kernel headers of the build
/// machine (<c>/usr/include/linux/input-event-codes.h</c>).</para>
/// </summary>
public static class LinuxKeys
{
    public const int LeftCtrl = 29;
    public const int LeftShift = 42;
    public const int RightShift = 54;
    public const int LeftAlt = 56;
    public const int RightCtrl = 97;
    public const int RightAlt = 100;
    public const int LeftSuper = 125;
    public const int RightSuper = 126;

    /// <summary>F1 to F10 are consecutive, 59 to 68.</summary>
    public const int F1 = 59;
    public const int F10 = 68;
    public const int F11 = 87;
    public const int F12 = 88;

    /// <summary>First function key that no ordinary keyboard carries.</summary>
    public const int F13 = 183;

    /// <summary>Last of the twelve consecutive codes F13 through F24.</summary>
    public const int F24 = 194;

    /// <summary><c>KEY_PAUSE</c>: types nothing and toggles nothing.</summary>
    public const int Pause = 119;

    // Keys the virtual keyboard presses, never part of a shortcut.
    public const int Backspace = 14;
    public const int V = 47;
    public const int Insert = 110;

    private static readonly Dictionary<string, int[]> ByName = BuildNames();

    private static Dictionary<string, int[]> BuildNames()
    {
        var names = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ctrl"] = [LeftCtrl, RightCtrl],
            ["LeftCtrl"] = [LeftCtrl],
            ["RightCtrl"] = [RightCtrl],

            ["Alt"] = [LeftAlt, RightAlt],
            ["LeftAlt"] = [LeftAlt],
            ["RightAlt"] = [RightAlt],

            ["Shift"] = [LeftShift, RightShift],
            ["LeftShift"] = [LeftShift],
            ["RightShift"] = [RightShift],

            ["Super"] = [LeftSuper, RightSuper],
            ["LeftSuper"] = [LeftSuper],
            ["RightSuper"] = [RightSuper],

            ["Pause"] = [Pause],
        };

        for (int number = 1; number <= 24; number++)
        {
            names[$"F{number}"] = [FunctionKey(number)];
        }

        return names;
    }

    /// <summary>The code of F<paramref name="number"/>: three runs in the kernel's table.</summary>
    private static int FunctionKey(int number) => number switch
    {
        <= 10 => F1 + (number - 1),
        11 => F11,
        12 => F12,
        _ => F13 + (number - 13),
    };

    /// <summary>
    /// The eight modifier keys. Held while a keystroke is sent, any of them
    /// changes what the keystroke means — the reason a paste waits for them to
    /// be let go, and an erase too.
    /// </summary>
    public static IReadOnlySet<int> Modifiers { get; } = new HashSet<int>(
        [LeftCtrl, RightCtrl, LeftShift, RightShift, LeftAlt, RightAlt, LeftSuper, RightSuper]);

    /// <summary>
    /// Every code a shortcut can be made of. <c>--watch-hotkey</c> listens to
    /// any device able to send one of them, so that a key can be identified
    /// before it is written into settings.json.
    /// </summary>
    public static IReadOnlySet<int> ShortcutCodes { get; } = new HashSet<int>(ByName.Values.SelectMany(codes => codes));

    /// <summary>
    /// Codes accepted for a key name. A generic name yields two: "Ctrl" is
    /// satisfied by the left key as much as by the right one.
    /// </summary>
    public static int[] Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return ByName.TryGetValue(name.Trim(), out int[]? codes)
            ? codes
            : throw new ArgumentException($"Unknown key: {name}", nameof(name));
    }

    /// <summary>
    /// The name a key is written with in settings.json, whatever case it was
    /// given in — "rightctrl" becomes "RightCtrl", "f5" becomes "F5" — or
    /// null when it is not a key a shortcut can use.
    /// </summary>
    public static string? Canonical(string? name)
    {
        string? trimmed = name?.Trim();

        return ByName.Keys.FirstOrDefault(known => string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The settings.json name of one physical key, or <c>null</c> when that key
    /// cannot be part of a shortcut.
    ///
    /// <para>The reverse of <see cref="Resolve"/>, and deliberately the
    /// <i>sided</i> name: the kernel only ever reports the left or the right
    /// key, so a key seen by <c>--watch-hotkey</c> is named with its side.
    /// Letters, digits and every other key get no name at all, so the
    /// diagnostic can never display what is being typed.</para>
    /// </summary>
    public static string? NameOf(int code) => code switch
    {
        LeftCtrl => "LeftCtrl",
        RightCtrl => "RightCtrl",
        LeftAlt => "LeftAlt",
        RightAlt => "RightAlt",
        LeftShift => "LeftShift",
        RightShift => "RightShift",
        LeftSuper => "LeftSuper",
        RightSuper => "RightSuper",
        Pause => "Pause",
        >= F1 and <= F10 => $"F{1 + (code - F1)}",
        F11 => "F11",
        F12 => "F12",
        >= F13 and <= F24 => $"F{13 + (code - F13)}",
        _ => null,
    };
}
