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
/// <para>Every value below was checked by compiling against the kernel
/// headers of the build machine (<c>linux/input-event-codes.h</c>).</para>
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

    /// <summary>First function key that no ordinary keyboard carries.</summary>
    public const int F13 = 183;

    /// <summary>Last of the twelve consecutive codes F13 through F24.</summary>
    public const int F24 = 194;

    // Keys used to paste, never part of a shortcut.
    public const int V = 47;
    public const int Insert = 110;

    private static readonly Dictionary<string, int[]> ByName = new(StringComparer.OrdinalIgnoreCase)
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
    };

    /// <summary>
    /// The eight modifier keys. Held while a keystroke is sent, any of them
    /// changes what the keystroke means — the reason a paste waits for them to
    /// be let go.
    /// </summary>
    public static IReadOnlySet<int> Modifiers { get; } = new HashSet<int>(
        [LeftCtrl, RightCtrl, LeftShift, RightShift, LeftAlt, RightAlt, LeftSuper, RightSuper]);

    /// <summary>
    /// Every code a shortcut can be made of. <c>--watch-hotkey</c> listens to
    /// any device able to send one of them, so that a key can be identified
    /// before it is written into settings.json.
    /// </summary>
    public static IReadOnlySet<int> ShortcutCodes { get; } = new HashSet<int>(
        [.. Modifiers, .. Enumerable.Range(F13, F24 - F13 + 1)]);

    /// <summary>
    /// Codes accepted for a key name. A generic name yields two: "Ctrl" is
    /// satisfied by the left key as much as by the right one.
    /// </summary>
    public static int[] Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        string trimmed = name.Trim();

        if (ByName.TryGetValue(trimmed, out int[]? codes))
        {
            return codes;
        }

        // F13 through F24 are consecutive in the kernel's table.
        if (trimmed.Length >= 3
            && (trimmed[0] is 'F' or 'f')
            && int.TryParse(trimmed.AsSpan(1), out int number)
            && number is >= 13 and <= 24)
        {
            return [F13 + (number - 13)];
        }

        throw new ArgumentException($"Unknown key: {name}", nameof(name));
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
        >= F13 and <= F24 => $"F{13 + (code - F13)}",
        _ => null,
    };
}
