using System.Buffers.Binary;
using System.Text;
using HexLinux.Input;

namespace HexLinux.Output;

/// <summary>
/// The parts of the kernel's uinput interface HexLinux uses, and the one
/// structure it hands over, encoded by hand.
///
/// <para>Every number here was checked by compiling a program against the
/// headers of the build machine (<c>linux/uinput.h</c>, <c>linux/input.h</c>):
/// <c>sizeof(struct uinput_setup) = 92</c>, <c>id</c> at 0 (bustype, vendor,
/// product, version as four 16-bit fields), <c>name</c> at 8 over 80 bytes,
/// <c>ff_effects_max</c> at 88; the ioctl requests below.</para>
///
/// <para>Encoded by hand rather than marshalled, like <see cref="InputEvent"/>:
/// the bytes can then be checked in a test, and a mistake shows there instead
/// of as a keyboard that silently never appears.</para>
/// </summary>
public static class UinputAbi
{
    public const string DevicePath = "/dev/uinput";

    public const uint SetEventBit = 0x40045564;
    public const uint SetKeyBit = 0x40045565;
    public const uint DeviceSetup = 0x405c5503;
    public const uint DeviceCreate = 0x5501;
    public const uint DeviceDestroy = 0x5502;

    /// <summary><c>BUS_VIRTUAL</c>: what the device honestly is.</summary>
    public const ushort BusVirtual = 0x06;

    public const int SetupSize = 92;
    private const int NameOffset = 8;
    private const int NameSize = 80;

    /// <summary>
    /// The keys the virtual keyboard declares, and so the only ones the kernel
    /// will let it send.
    ///
    /// <para>1 to 31 is what udev requires to class a device as a keyboard
    /// (every one of them must be present), and a device libinput does not
    /// take for a keyboard is ignored by the compositor. Left Shift, V and
    /// Insert complete the three paste shortcuts; Left Ctrl is already in the
    /// range. Nothing else: a mistake in a key sequence can then never press
    /// Power or Sleep.</para>
    /// </summary>
    public static IReadOnlyList<int> DeclaredKeys { get; } =
        [.. Enumerable.Range(1, 31), LinuxKeys.LeftShift, LinuxKeys.V, LinuxKeys.Insert];

    /// <summary>
    /// <c>struct uinput_setup</c> for a keyboard named <paramref name="name"/>.
    /// The name is cut to 79 bytes of UTF-8, the rest of its field left at
    /// zero, so the kernel always finds it terminated.
    /// </summary>
    public static byte[] BuildSetup(string name, ushort bus, ushort vendor, ushort product, ushort version)
    {
        ArgumentNullException.ThrowIfNull(name);

        byte[] setup = new byte[SetupSize];

        BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(0), bus);
        BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(2), vendor);
        BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(4), product);
        BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(6), version);

        byte[] encoded = Encoding.UTF8.GetBytes(name);
        int length = Math.Min(encoded.Length, NameSize - 1);

        // Never cut a multi-byte character in half.
        while (length > 0 && length < encoded.Length && (encoded[length] & 0xC0) == 0x80)
        {
            length--;
        }

        encoded.AsSpan(0, length).CopyTo(setup.AsSpan(NameOffset));

        // ff_effects_max, at 88, stays 0: no force feedback.
        return setup;
    }
}
