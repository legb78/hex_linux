using System.Globalization;

namespace HexLinux.Input;

/// <summary>One input device, as described in <c>/proc/bus/input/devices</c>.</summary>
/// <param name="Name">The name the driver gives it.</param>
/// <param name="Bus">The bus type: 0x03 USB, 0x11 i8042, 0x06 virtual…</param>
/// <param name="EventNode">Its node under <c>/dev/input</c>, "event3", or null.</param>
/// <param name="KeyCodes">Every key code it can report.</param>
public sealed record InputDevice(string Name, int Bus, string? EventNode, IReadOnlySet<int> KeyCodes)
{
    public string? DevicePath => EventNode is null ? null : "/dev/input/" + EventNode;
}

/// <summary>
/// Reads <c>/proc/bus/input/devices</c>, the kernel's list of every input
/// device and the keys each one can produce, and picks the ones worth
/// listening to.
///
/// <para>Worth listening to means: able to send at least one key of the
/// shortcut. Anything looser picks up the wrong devices. "Has a kbd handler"
/// also matches the power button, the lid switch and the laptop's hotkeys;
/// "looks like a full keyboard" misses the macro pad someone bought precisely
/// to dictate with F13.</para>
///
/// <para>Pure parsing, tested against sample text: the file needs no
/// permission to read, but a test cannot choose which devices a machine
/// has.</para>
/// </summary>
public static class InputDeviceCatalog
{
    public const string SourcePath = "/proc/bus/input/devices";

    /// <summary>
    /// Devices that re-emit keystrokes some program injected. Listening to
    /// them would make a dictation react to its own paste: the Ctrl+V of a
    /// segment inserted while the shortcut is held reads as a foreign key and
    /// cancels the rest. Matched by name — the bus type would also exclude
    /// remappers such as keyd, whose virtual keyboard carries everything the
    /// user really types.
    /// </summary>
    public static readonly IReadOnlySet<string> InjectorNames = new HashSet<string>(StringComparer.Ordinal)
    {
        UinputDeviceName,
        "ydotoold virtual device",
    };

    /// <summary>The name HexLinux gives its own virtual keyboard.</summary>
    public const string UinputDeviceName = "hexlinux virtual keyboard";

    private const int BitsPerWord = 64;

    public static IReadOnlyList<InputDevice> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<InputDevice> devices = [];

        foreach (string block in text.Replace("\r", "", StringComparison.Ordinal).Split("\n\n"))
        {
            if (ParseBlock(block) is { } device)
            {
                devices.Add(device);
            }
        }

        return devices;
    }

    /// <summary>
    /// The devices able to report at least one of <paramref name="codes"/>,
    /// injectors excepted.
    /// </summary>
    public static IReadOnlyList<InputDevice> Keyboards(IEnumerable<InputDevice> devices, IReadOnlySet<int> codes)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(codes);

        return
        [
            .. devices.Where(device =>
                device.EventNode is not null
                && !InjectorNames.Contains(device.Name)
                && device.KeyCodes.Overlaps(codes)),
        ];
    }

    private static InputDevice? ParseBlock(string block)
    {
        string? name = null;
        int bus = 0;
        string? eventNode = null;
        HashSet<int> keys = [];
        bool any = false;

        foreach (string raw in block.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length < 3 || line[1] != ':')
            {
                continue;
            }

            any = true;
            string content = line[2..].Trim();

            switch (line[0])
            {
                case 'I':
                    bus = ReadHexField(content, "Bus=");
                    break;
                case 'N':
                    name = ReadName(content);
                    break;
                case 'H':
                    eventNode = ReadEventNode(content);
                    break;
                case 'B' when content.StartsWith("KEY=", StringComparison.Ordinal):
                    keys = ReadBitmap(content["KEY=".Length..]);
                    break;
                default:
                    break;
            }
        }

        return any ? new InputDevice(name ?? string.Empty, bus, eventNode, keys) : null;
    }

    /// <summary>N: Name="AT Translated Set 2 keyboard"</summary>
    private static string ReadName(string content)
    {
        const string prefix = "Name=";

        if (!content.StartsWith(prefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return content[prefix.Length..].Trim().Trim('"');
    }

    /// <summary>I: Bus=0011 Vendor=0001 Product=0001 Version=ab41</summary>
    private static int ReadHexField(string content, string field)
    {
        foreach (string part in content.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(field, StringComparison.Ordinal)
                && int.TryParse(part.AsSpan(field.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
        }

        return 0;
    }

    /// <summary>H: Handlers=sysrq kbd event3 leds</summary>
    private static string? ReadEventNode(string content)
    {
        const string prefix = "Handlers=";

        if (!content.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return content[prefix.Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(handler => handler.StartsWith("event", StringComparison.Ordinal)
                && handler.Length > "event".Length
                && handler.AsSpan("event".Length).IndexOfAnyExceptInRange('0', '9') < 0);
    }

    /// <summary>
    /// B: KEY=402000000 3803078f800d001 feffffdfffefffff fffffffffffffffe
    ///
    /// <para>A bitmap printed as hexadecimal words, the most significant word
    /// first and leading zeros dropped. Each word is an unsigned long, so 64
    /// bits on the 64-bit kernels HexLinux runs on: the last word holds codes
    /// 0 to 63, the one before it 64 to 127, and so on.</para>
    /// </summary>
    private static HashSet<int> ReadBitmap(string words)
    {
        string[] parts = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        HashSet<int> codes = [];

        for (int index = 0; index < parts.Length; index++)
        {
            if (!ulong.TryParse(parts[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong word))
            {
                // A word that does not parse makes every later position
                // uncertain: better no keys than the wrong ones.
                return [];
            }

            int baseCode = (parts.Length - 1 - index) * BitsPerWord;

            for (int bit = 0; bit < BitsPerWord; bit++)
            {
                if ((word & (1UL << bit)) != 0)
                {
                    codes.Add(baseCode + bit);
                }
            }
        }

        return codes;
    }
}
