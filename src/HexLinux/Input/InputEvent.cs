using System.Buffers.Binary;

namespace HexLinux.Input;

/// <summary>What happened to a key, as the kernel reports it.</summary>
public enum KeyTransition
{
    Released = 0,
    Pressed = 1,

    /// <summary>The key is still held; the kernel repeats it at the typematic rate.</summary>
    Repeated = 2,
}

/// <summary>
/// One <c>struct input_event</c>, as read from <c>/dev/input/event*</c> or
/// written to <c>/dev/uinput</c>.
///
/// <para>The layout on a 64-bit kernel is a <c>struct timeval</c> — two
/// 64-bit fields — then a 16-bit type, a 16-bit code and a signed 32-bit
/// value: 24 bytes, little-endian on x86-64. The time is left out: nothing
/// here decides on it, and the kernel fills it in on the way out.</para>
///
/// <para>Parsed by hand rather than marshalled, so that a short read or a
/// garbled buffer is a <c>false</c>, not a corrupted struct — and so that the
/// parsing is testable against bytes written in a test.</para>
/// </summary>
public readonly record struct InputEvent(ushort Type, ushort Code, int Value)
{
    /// <summary>Size of <c>struct input_event</c> on a 64-bit kernel.</summary>
    public const int Size = 24;

    public const ushort Synchronization = 0x00;
    public const ushort Key = 0x01;

    /// <summary><c>SYN_REPORT</c>: closes a group of events.</summary>
    public const ushort SynReport = 0;

    /// <summary>
    /// <c>SYN_DROPPED</c>: the reader fell behind and the kernel threw events
    /// away — possibly a release. Everything up to the next
    /// <see cref="SynReport"/> is unreliable.
    /// </summary>
    public const ushort SynDropped = 3;

    private const int TypeOffset = 16;
    private const int CodeOffset = 18;
    private const int ValueOffset = 20;

    public bool IsKey => Type == Key;

    public bool IsSyncReport => Type == Synchronization && Code == SynReport;

    public bool IsSyncDropped => Type == Synchronization && Code == SynDropped;

    /// <summary>A key event, as a virtual keyboard writes it.</summary>
    public static InputEvent KeyEvent(int code, bool pressed) => new(Key, checked((ushort)code), pressed ? 1 : 0);

    /// <summary>The <c>SYN_REPORT</c> that makes the preceding events take effect.</summary>
    public static InputEvent Report() => new(Synchronization, SynReport, 0);

    public KeyTransition Transition => (KeyTransition)Value;

    /// <summary>Reads one event from the start of <paramref name="buffer"/>.</summary>
    public static bool TryRead(ReadOnlySpan<byte> buffer, out InputEvent inputEvent)
    {
        if (buffer.Length < Size)
        {
            inputEvent = default;
            return false;
        }

        inputEvent = new InputEvent(
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[TypeOffset..]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[CodeOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(buffer[ValueOffset..]));

        return true;
    }

    /// <summary>
    /// Writes the event into the first 24 bytes of <paramref name="target"/>,
    /// time zeroed: uinput stamps it itself.
    /// </summary>
    public void WriteTo(Span<byte> target)
    {
        if (target.Length < Size)
        {
            throw new ArgumentException($"An input event takes up {Size} bytes.", nameof(target));
        }

        target[..TypeOffset].Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(target[TypeOffset..], Type);
        BinaryPrimitives.WriteUInt16LittleEndian(target[CodeOffset..], Code);
        BinaryPrimitives.WriteInt32LittleEndian(target[ValueOffset..], Value);
    }
}
