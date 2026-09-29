using HexLinux.Input;
using Xunit;

namespace HexLinux.Tests.Input;

/// <summary>
/// Which devices HexLinux listens to decides whether the shortcut works at
/// all, and whether a dictation reacts to its own paste. The sample below is
/// written in the kernel's format for <c>/proc/bus/input/devices</c>, one
/// block per device of an ordinary laptop: the power button and lid switch
/// (a <c>kbd</c> handler, but not keyboards anyone dictates with), the
/// built-in keyboard, a mouse, a macro pad sending F13 to F16, a remapper's
/// virtual keyboard (keyd), and the two injectors whose keystrokes must never
/// be read back. The bitmaps were computed bit by bit; each test recalls the
/// codes it relies on.
/// </summary>
public class InputDeviceCatalogTests
{
    private const string Sample = """
        I: Bus=0019 Vendor=0000 Product=0001 Version=0000
        N: Name="Power Button"
        P: Phys=PNP0C0C/button/input0
        S: Sysfs=/devices/LNXSYSTM:00/LNXPWRBN:00/input/input0
        U: Uniq=
        H: Handlers=kbd event0
        B: PROP=0
        B: EV=3
        B: KEY=10000000000000 0

        I: Bus=0019 Vendor=0000 Product=0005 Version=0000
        N: Name="Lid Switch"
        P: Phys=PNP0C0D/button/input0
        S: Sysfs=/devices/LNXSYSTM:00/LNXSYBUS:00/PNP0C0D:00/input/input1
        U: Uniq=
        H: Handlers=event1
        B: PROP=0
        B: EV=21
        B: SW=1

        I: Bus=0011 Vendor=0001 Product=0001 Version=ab41
        N: Name="AT Translated Set 2 keyboard"
        P: Phys=isa0060/serio0/input0
        S: Sysfs=/devices/platform/i8042/serio0/input/input3
        U: Uniq=
        H: Handlers=sysrq kbd leds event3
        B: PROP=0
        B: EV=120013
        B: KEY=402000000 3803078f800d001 feffffdfffefffff fffffffffffffffe
        B: MSC=10
        B: LED=7

        I: Bus=0003 Vendor=1234 Product=5678 Version=0111
        N: Name="USB Optical Mouse"
        P: Phys=usb-0000:00:14.0-2/input0
        S: Sysfs=/devices/pci0000:00/0000:00:14.0/usb1/1-2/1-2:1.0/input/input5
        U: Uniq=
        H: Handlers=mouse0 event5
        B: PROP=0
        B: EV=17
        B: KEY=1f0000 0 0 0 0
        B: REL=903
        B: MSC=10

        I: Bus=0003 Vendor=1234 Product=9abc Version=0110
        N: Name="Macro Keypad"
        P: Phys=usb-0000:00:14.0-3/input0
        S: Sysfs=/devices/pci0000:00/0000:00:14.0/usb1/1-3/1-3:1.0/input/input6
        U: Uniq=
        H: Handlers=sysrq kbd event6
        B: PROP=0
        B: EV=100013
        B: KEY=780000000000000 0 0
        B: MSC=10

        I: Bus=0006 Vendor=0fac Product=0ade Version=0001
        N: Name="keyd virtual keyboard"
        P: Phys=
        S: Sysfs=/devices/virtual/input/input7
        U: Uniq=
        H: Handlers=sysrq kbd leds event7
        B: PROP=0
        B: EV=120013
        B: KEY=402000000 3803078f800d001 feffffdfffefffff fffffffffffffffe
        B: MSC=10
        B: LED=7

        I: Bus=0006 Vendor=0000 Product=0000 Version=0001
        N: Name="hexlinux virtual keyboard"
        P: Phys=
        S: Sysfs=/devices/virtual/input/input8
        U: Uniq=
        H: Handlers=sysrq kbd event8
        B: PROP=0
        B: EV=3
        B: KEY=400000000000 8400fffffffe

        I: Bus=0006 Vendor=0000 Product=0000 Version=0000
        N: Name="ydotoold virtual device"
        P: Phys=
        S: Sysfs=/devices/virtual/input/input9
        U: Uniq=
        H: Handlers=sysrq kbd event9
        B: PROP=0
        B: EV=3
        B: KEY=402000000 3803078f800d001 feffffdfffefffff fffffffffffffffe

        """;

    private static IReadOnlyList<InputDevice> Devices() => InputDeviceCatalog.Parse(Sample);

    private static InputDevice Device(string name) => Devices().Single(device => device.Name == name);

    private static string[] KeyboardNames(params int[] codes) =>
        [.. InputDeviceCatalog.Keyboards(Devices(), codes.ToHashSet()).Select(device => device.Name)];

    /// <summary>Everything parsed about a device, for comparing two parses.</summary>
    private static string Summary(InputDevice device) =>
        $"{device.Name}|{device.Bus}|{device.EventNode}|{string.Join(",", device.KeyCodes.Order())}";

    // --- Parsing ----------------------------------------------------------------

    [Fact]
    public void Every_block_becomes_one_device_in_file_order()
    {
        Assert.Equal(
            [
                "Power Button", "Lid Switch", "AT Translated Set 2 keyboard", "USB Optical Mouse",
                "Macro Keypad", "keyd virtual keyboard", "hexlinux virtual keyboard", "ydotoold virtual device",
            ],
            Devices().Select(device => device.Name));
    }

    [Fact]
    public void The_name_bus_and_event_node_of_a_keyboard_are_read()
    {
        InputDevice keyboard = Device("AT Translated Set 2 keyboard");

        // Bus=0011 is hexadecimal: BUS_I8042, the laptop's own keyboard.
        Assert.Equal(0x11, keyboard.Bus);
        Assert.Equal("event3", keyboard.EventNode);
        Assert.Equal("/dev/input/event3", keyboard.DevicePath);
    }

    [Fact]
    public void The_key_bitmap_of_a_full_keyboard_holds_every_shortcut_modifier()
    {
        // Four 64-bit words, most significant first: codes 0-63 are in the
        // last word, 64-127 in the one before, and so on.
        IReadOnlySet<int> keys = Device("AT Translated Set 2 keyboard").KeyCodes;

        Assert.All([29, 42, 54, 56, 97, 100, 125, 126, 183], code => Assert.Contains(code, keys));
        Assert.DoesNotContain(194, keys); // KEY_F24
        Assert.DoesNotContain(272, keys); // BTN_LEFT
        Assert.Equal(144, keys.Count);
    }

    [Fact]
    public void A_bitmap_word_is_placed_by_its_position_from_the_end()
    {
        // "10000000000000 0": bit 52 of the second word from the end is code
        // 64 + 52 = 116, KEY_POWER. Counted from the start, it would read as
        // code 52, a key of the main block.
        Assert.Equal(new HashSet<int> { 116 }, Device("Power Button").KeyCodes.ToHashSet());

        // "1f0000 0 0 0 0": BTN_LEFT (0x110) to BTN_EXTRA (0x114).
        Assert.Equal(new HashSet<int> { 272, 273, 274, 275, 276 }, Device("USB Optical Mouse").KeyCodes.ToHashSet());

        // Programmed to send F13 to F16, codes 183 to 186.
        Assert.Equal(new HashSet<int> { 183, 184, 185, 186 }, Device("Macro Keypad").KeyCodes.ToHashSet());
    }

    [Fact]
    public void HexLinux_own_virtual_keyboard_is_read_with_the_keys_it_declares()
    {
        // KEY_ESC to KEY_S (1-31), LeftShift, V and Insert: what the uinput
        // sender declares to paste and type.
        HashSet<int> declared = [.. Enumerable.Range(1, 31), 42, 47, 110];

        Assert.Equal(declared, Device("hexlinux virtual keyboard").KeyCodes.ToHashSet());
        Assert.Equal(0x06, Device("hexlinux virtual keyboard").Bus);
    }

    [Fact]
    public void A_device_without_a_key_bitmap_has_no_keys()
    {
        // The lid switch reports a switch, not keys.
        Assert.Empty(Device("Lid Switch").KeyCodes);
    }

    [Fact]
    public void A_malformed_bitmap_gives_no_keys_rather_than_wrong_ones()
    {
        // One word that does not parse shifts every later position: better
        // not to listen to the device than to believe it has a Ctrl it lacks.
        InputDevice device = Assert.Single(InputDeviceCatalog.Parse(
            "N: Name=\"Odd\"\nH: Handlers=kbd event4\nB: KEY=402000000 zz feffffdfffefffff fffffffffffffffe\n"));

        Assert.Empty(device.KeyCodes);
    }

    [Theory]
    [InlineData("H: Handlers=sysrq kbd leds event3", "event3")]
    [InlineData("H: Handlers=event12 kbd", "event12")]
    [InlineData("H: Handlers=kbd js0", null)]
    [InlineData("H: Handlers=kbd event", null)]
    [InlineData("H: Handlers=kbd eventx1", null)]
    [InlineData("H: Handlers=", null)]
    [InlineData("H: Other=event3", null)]
    public void The_event_node_is_the_first_eventN_handler(string line, string? expected)
    {
        // Only /dev/input/eventN can be read; a joystick or legacy handler
        // alone gives nothing to open.
        InputDevice device = Assert.Single(InputDeviceCatalog.Parse("N: Name=\"Pad\"\n" + line + "\n"));

        Assert.Equal(expected, device.EventNode);
    }

    [Fact]
    public void Missing_or_odd_fields_fall_back_to_empty_values()
    {
        // Field order and presence vary between kernels: a block with only a
        // key bitmap still yields a device.
        InputDevice device = Assert.Single(InputDeviceCatalog.Parse("I: Vendor=0001\nN: Label=x\nB: EV=3\nB: KEY=200000000 0\n"));

        Assert.Equal(string.Empty, device.Name);
        Assert.Equal(0, device.Bus);
        Assert.Null(device.EventNode);
        Assert.Null(device.DevicePath);
        Assert.Equal(new HashSet<int> { 97 }, device.KeyCodes.ToHashSet());
    }

    [Fact]
    public void Windows_line_endings_are_read_the_same()
    {
        // A sample saved from a Windows editor, or pasted into a bug report.
        IReadOnlyList<InputDevice> devices = InputDeviceCatalog.Parse(Sample.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(Devices().Select(Summary), devices.Select(Summary));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n\n")]
    [InlineData("\n\n\n\n")]
    public void Blank_text_holds_no_device(string text)
    {
        Assert.Empty(InputDeviceCatalog.Parse(text));
    }

    [Fact]
    public void Extra_blank_lines_create_no_phantom_device()
    {
        string spaced = Sample.Replace("\n\n", "\n\n\n\n", StringComparison.Ordinal);

        Assert.Equal(8, InputDeviceCatalog.Parse(spaced).Count);
    }

    // --- Choosing the keyboards -------------------------------------------------

    [Fact]
    public void The_default_shortcut_listens_to_the_keyboards_able_to_send_Right_Ctrl()
    {
        // Right Ctrl is code 97. keyd grabs the real keyboard and re-emits
        // everything through its own: that one must be read, on the virtual
        // bus like the injectors — hence the exclusion by name, not by bus.
        Assert.Equal(
            ["AT Translated Set 2 keyboard", "keyd virtual keyboard"],
            KeyboardNames(LinuxKeys.RightCtrl));
    }

    [Fact]
    public void The_power_button_is_not_a_keyboard_despite_its_kbd_handler()
    {
        // "Has a kbd handler" would have picked it up, and the lid switch's
        // node too: neither can send a key of any shortcut.
        string[] names = KeyboardNames([.. LinuxKeys.ShortcutCodes]);

        Assert.DoesNotContain("Power Button", names);
        Assert.DoesNotContain("Lid Switch", names);
        Assert.DoesNotContain("USB Optical Mouse", names);
    }

    [Fact]
    public void A_macro_pad_is_kept_for_a_function_key_shortcut()
    {
        // "Looks like a full keyboard" would miss the pad someone bought
        // precisely to dictate with F13 (code 183).
        Assert.Equal(
            ["AT Translated Set 2 keyboard", "Macro Keypad", "keyd virtual keyboard"],
            KeyboardNames(LinuxKeys.F13));
        Assert.Equal(["Macro Keypad"], KeyboardNames(LinuxKeys.F13 + 3));
    }

    [Fact]
    public void The_injectors_are_never_listened_to_even_when_they_declare_the_shortcut()
    {
        // HexLinux's own keyboard declares LeftCtrl (29) to paste: read back,
        // the Ctrl+V of a segment pasted while Ctrl is held would cancel the
        // rest of the dictation. ydotoold re-emits a whole keyboard.
        string[] names = KeyboardNames(LinuxKeys.LeftCtrl, LinuxKeys.RightCtrl);

        Assert.DoesNotContain(InputDeviceCatalog.UinputDeviceName, names);
        Assert.DoesNotContain("ydotoold virtual device", names);
        Assert.Contains("keyd virtual keyboard", names);
    }

    [Fact]
    public void The_name_HexLinux_gives_its_keyboard_is_the_one_excluded()
    {
        Assert.Equal("hexlinux virtual keyboard", InputDeviceCatalog.UinputDeviceName);
        Assert.Contains(InputDeviceCatalog.UinputDeviceName, InputDeviceCatalog.InjectorNames);
    }

    [Fact]
    public void A_device_without_an_event_node_is_not_listened_to()
    {
        // Nothing to open under /dev/input.
        InputDevice keyboard = Device("AT Translated Set 2 keyboard") with { EventNode = null };

        Assert.Empty(InputDeviceCatalog.Keyboards([keyboard], new HashSet<int> { LinuxKeys.RightCtrl }));
    }

    [Fact]
    public void The_source_is_the_kernel_list_of_input_devices()
    {
        Assert.Equal("/proc/bus/input/devices", InputDeviceCatalog.SourcePath);
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => InputDeviceCatalog.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => InputDeviceCatalog.Keyboards(null!, new HashSet<int>()));
        Assert.Throws<ArgumentNullException>(() => InputDeviceCatalog.Keyboards(Devices(), null!));
    }
}
