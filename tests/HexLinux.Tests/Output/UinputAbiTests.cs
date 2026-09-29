using System.Buffers.Binary;
using System.Text;
using HexLinux.Configuration;
using HexLinux.Input;
using HexLinux.Output;
using Xunit;

namespace HexLinux.Tests.Output;

/// <summary>
/// The bytes and numbers handed to the kernel for the virtual keyboard. None
/// of this can be checked under WSL, whose kernel has no uinput: a wrong
/// ioctl number or offset would show only on a real desktop, as a keyboard
/// that never appears and pastes that never happen. The values below are
/// the ones the brief records from compiling against linux/uinput.h.
/// </summary>
public class UinputAbiTests
{
    private const int NameOffset = 8;
    private const int NameSize = 80;
    private const int FfEffectsMaxOffset = 88;

    /// <summary>The kernel's _IOW(type, nr, size) and _IO(type, nr).</summary>
    private static uint Iow(char type, uint nr, uint size) => (1u << 30) | (size << 16) | ((uint)type << 8) | nr;

    private static uint Io(char type, uint nr) => ((uint)type << 8) | nr;

    private static string NameIn(byte[] setup)
    {
        ReadOnlySpan<byte> field = setup.AsSpan(NameOffset, NameSize);
        int end = field.IndexOf((byte)0);

        Assert.True(end >= 0, "the name must be NUL-terminated inside its field");

        // Throws on a sequence cut in the middle of a character.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(field[..end]);
    }

    [Fact]
    public void The_ioctl_requests_are_the_kernel_ones()
    {
        Assert.Equal(0x40045564u, UinputAbi.SetEventBit);
        Assert.Equal(0x40045565u, UinputAbi.SetKeyBit);
        Assert.Equal(0x405c5503u, UinputAbi.DeviceSetup);
        Assert.Equal(0x5501u, UinputAbi.DeviceCreate);
        Assert.Equal(0x5502u, UinputAbi.DeviceDestroy);
    }

    [Fact]
    public void The_ioctl_requests_match_their_definitions_in_uinput_h()
    {
        // UI_SET_EVBIT = _IOW('U', 100, int), UI_SET_KEYBIT = _IOW('U', 101, int),
        // UI_DEV_SETUP = _IOW('U', 3, struct uinput_setup): the size of the
        // structure is part of the request, so the two must agree.
        Assert.Equal(UinputAbi.SetEventBit, Iow('U', 100, sizeof(int)));
        Assert.Equal(UinputAbi.SetKeyBit, Iow('U', 101, sizeof(int)));
        Assert.Equal(UinputAbi.DeviceSetup, Iow('U', 3, (uint)UinputAbi.SetupSize));
        Assert.Equal(UinputAbi.DeviceCreate, Io('U', 1));
        Assert.Equal(UinputAbi.DeviceDestroy, Io('U', 2));
    }

    [Fact]
    public void The_device_is_declared_as_virtual_on_dev_uinput()
    {
        // BUS_VIRTUAL: what the device honestly is.
        Assert.Equal("/dev/uinput", UinputAbi.DevicePath);
        Assert.Equal((ushort)0x06, UinputAbi.BusVirtual);
        Assert.Equal(92, UinputAbi.SetupSize);
    }

    // --- Declared keys (E10) ------------------------------------------------------

    [Fact]
    public void The_declared_keys_are_exactly_what_udev_needs_plus_the_paste_keys()
    {
        // E10: 1-31 so that udev classes the device as a keyboard, Left Shift,
        // V and Insert for the three shortcuts. Nothing else.
        int[] expected = [.. Enumerable.Range(1, 31), 42, 47, 110];

        Assert.Equal(expected, UinputAbi.DeclaredKeys);
        Assert.Equal(UinputAbi.DeclaredKeys.Count, UinputAbi.DeclaredKeys.Distinct().Count());
    }

    [Fact]
    public void Every_key_udev_requires_of_a_keyboard_is_declared()
    {
        // udev tags a device ID_INPUT_KEYBOARD only when every code from 1 to
        // 31 is present; libinput, and so the compositor, ignores a device
        // without that tag, and the paste would go nowhere.
        Assert.All(Enumerable.Range(1, 31), key => Assert.Contains(key, UinputAbi.DeclaredKeys));
        Assert.Contains(LinuxKeys.LeftCtrl, UinputAbi.DeclaredKeys);
    }

    [Theory]
    [InlineData(116)] // KEY_POWER
    [InlineData(142)] // KEY_SLEEP
    [InlineData(LinuxKeys.LeftSuper)]
    [InlineData(LinuxKeys.LeftAlt)]
    [InlineData(57)] // KEY_SPACE
    public void Keys_that_could_do_harm_are_not_declared(int key)
    {
        // E10: a mistake in a key sequence must never power the machine off
        // or open a launcher, since the kernel drops undeclared keys.
        Assert.DoesNotContain(key, UinputAbi.DeclaredKeys);
    }

    [Fact]
    public void Enter_is_declared_only_because_udev_insists_and_no_sequence_uses_it()
    {
        // KEY_ENTER (28) sits inside the 1-31 range udev requires. Sent into
        // a terminal it would run whatever the dictation left on the line.
        const int enter = 28;
        IEnumerable<int> used = Enum.GetValues<PasteShortcut>().SelectMany(shortcut => KeySequences.KeysOf(shortcut));

        Assert.Contains(enter, UinputAbi.DeclaredKeys);
        Assert.DoesNotContain(enter, used);
    }

    // --- struct uinput_setup ------------------------------------------------------

    [Fact]
    public void The_setup_carries_the_ids_little_endian_then_the_name()
    {
        byte[] setup = UinputAbi.BuildSetup(InputDeviceCatalog.UinputDeviceName, UinputAbi.BusVirtual, 0x1234, 0xABCD, 1);

        Assert.Equal(UinputAbi.SetupSize, setup.Length);
        Assert.Equal(UinputAbi.BusVirtual, BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(0)));
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(2)));
        Assert.Equal(0xABCD, BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(6)));
        Assert.Equal(InputDeviceCatalog.UinputDeviceName, NameIn(setup));
    }

    [Fact]
    public void The_name_is_the_one_the_keyboard_reader_ignores()
    {
        // Were HexLinux to listen to its own virtual keyboard, the Ctrl+V of a
        // segment pasted while the shortcut is held would read as a foreign
        // key and cancel the rest of the dictation.
        byte[] setup = UinputAbi.BuildSetup(InputDeviceCatalog.UinputDeviceName, UinputAbi.BusVirtual, 0, 0, 1);

        Assert.Contains(NameIn(setup), InputDeviceCatalog.InjectorNames);
    }

    [Fact]
    public void Everything_after_the_name_is_zero_and_so_is_ff_effects_max()
    {
        // No force feedback, and no stray byte the kernel could read as part
        // of the name.
        byte[] setup = UinputAbi.BuildSetup("kbd", UinputAbi.BusVirtual, 0, 0, 1);

        Assert.All(setup.AsSpan(NameOffset + 3, NameSize - 3).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(setup.AsSpan(FfEffectsMaxOffset)));
    }

    [Fact]
    public void A_long_name_is_cut_to_79_bytes_so_the_kernel_finds_it_terminated()
    {
        byte[] setup = UinputAbi.BuildSetup(new string('k', 200), UinputAbi.BusVirtual, 0, 0, 1);

        Assert.Equal(new string('k', 79), NameIn(setup));
        Assert.Equal(0, setup[NameOffset + NameSize - 1]);
        Assert.Equal(UinputAbi.SetupSize, setup.Length);
    }

    [Fact]
    public void A_name_of_exactly_79_bytes_is_kept_whole()
    {
        string name = new('k', 79);

        Assert.Equal(name, NameIn(UinputAbi.BuildSetup(name, UinputAbi.BusVirtual, 0, 0, 1)));
    }

    [Theory]
    [InlineData("é", 39)]   // 2 bytes: 78 of the 79 fit
    [InlineData("€", 26)]   // 3 bytes: 78
    [InlineData("🎹", 19)]  // 4 bytes: 76
    public void A_long_name_is_never_cut_in_the_middle_of_a_character(string character, int expectedCount)
    {
        // udev and libinput read the name as UTF-8: half a character would
        // make it invalid, and some tools refuse the whole device then.
        string name = string.Concat(Enumerable.Repeat(character, 60));

        string kept = NameIn(UinputAbi.BuildSetup(name, UinputAbi.BusVirtual, 0, 0, 1));

        Assert.Equal(string.Concat(Enumerable.Repeat(character, expectedCount)), kept);
        Assert.True(Encoding.UTF8.GetByteCount(kept) <= 79);
    }

    [Fact]
    public void An_empty_name_leaves_the_field_zeroed()
    {
        byte[] setup = UinputAbi.BuildSetup(string.Empty, UinputAbi.BusVirtual, 0, 0, 1);

        Assert.Equal(string.Empty, NameIn(setup));
        Assert.All(setup.AsSpan(NameOffset, NameSize).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_missing_name_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => UinputAbi.BuildSetup(null!, UinputAbi.BusVirtual, 0, 0, 1));
    }
}
