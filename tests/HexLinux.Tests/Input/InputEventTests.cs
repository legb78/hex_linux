using System.Buffers.Binary;
using HexLinux.Input;
using Xunit;

namespace HexLinux.Tests.Input;

/// <summary>
/// <c>struct input_event</c> is read from every keyboard and written to
/// <c>/dev/uinput</c>. An offset off by two bytes reads every key as another
/// one, or sends a paste that types the wrong character. The layout checked
/// here is the one measured against the kernel headers: 24 bytes, type at 16,
/// code at 18, a signed value at 20, little-endian.
/// </summary>
public class InputEventTests
{
    // KEY_2 of linux/input-event-codes.h: the same number as SYN_DROPPED.
    private const ushort Key2 = 3;

    /// <summary>One event as the kernel lays it out, time filled with noise.</summary>
    private static byte[] Raw(ushort type, ushort code, int value)
    {
        byte[] buffer = new byte[InputEvent.Size];

        for (int i = 0; i < 16; i++)
        {
            buffer[i] = (byte)(0xA0 + i);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(16), type);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(18), code);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(20), value);

        return buffer;
    }

    [Fact]
    public void The_size_is_the_one_of_a_64_bit_kernel()
    {
        Assert.Equal(24, InputEvent.Size);
    }

    [Fact]
    public void Type_code_and_value_are_read_at_their_kernel_offsets()
    {
        // Right Ctrl pressed, the event the default shortcut starts on. The
        // timestamp in front must not leak into any field.
        Assert.True(InputEvent.TryRead(Raw(InputEvent.Key, LinuxKeys.RightCtrl, 1), out InputEvent inputEvent));

        Assert.Equal(InputEvent.Key, inputEvent.Type);
        Assert.Equal(LinuxKeys.RightCtrl, inputEvent.Code);
        Assert.Equal(1, inputEvent.Value);
        Assert.True(inputEvent.IsKey);
        Assert.Equal(KeyTransition.Pressed, inputEvent.Transition);
    }

    [Fact]
    public void The_value_is_signed()
    {
        // The field is a signed 32-bit value: a wheel (EV_REL, REL_WHEEL)
        // turned one notch back reads -1, not 4294967295.
        Assert.True(InputEvent.TryRead(Raw(2, 8, -1), out InputEvent inputEvent));

        Assert.Equal(-1, inputEvent.Value);
    }

    [Fact]
    public void Only_the_first_event_of_a_longer_buffer_is_read()
    {
        // A read returns several events at once; the reader walks them 24
        // bytes at a time.
        byte[] buffer =
        [
            .. Raw(InputEvent.Key, LinuxKeys.RightCtrl, 0),
            .. Raw(InputEvent.Synchronization, InputEvent.SynReport, 0),
        ];

        Assert.True(InputEvent.TryRead(buffer, out InputEvent first));
        Assert.True(InputEvent.TryRead(buffer.AsSpan(InputEvent.Size), out InputEvent second));

        Assert.Equal(new InputEvent(InputEvent.Key, LinuxKeys.RightCtrl, 0), first);
        Assert.True(second.IsSyncReport);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(23)]
    public void A_short_buffer_is_refused_rather_than_misread(int length)
    {
        // A truncated read must be a false, not an event made of garbage.
        byte[] buffer = new byte[length];
        Array.Fill(buffer, (byte)0xFF);

        Assert.False(InputEvent.TryRead(buffer, out InputEvent inputEvent));
        Assert.Equal(default(InputEvent), inputEvent);
    }

    [Theory]
    [InlineData(0, KeyTransition.Released)]
    [InlineData(1, KeyTransition.Pressed)]
    [InlineData(2, KeyTransition.Repeated)]
    public void The_value_of_a_key_event_is_its_transition(int value, KeyTransition expected)
    {
        Assert.Equal(expected, new InputEvent(InputEvent.Key, LinuxKeys.RightCtrl, value).Transition);
    }

    [Fact]
    public void A_drop_and_a_report_are_told_apart()
    {
        var dropped = new InputEvent(InputEvent.Synchronization, InputEvent.SynDropped, 0);
        InputEvent report = InputEvent.Report();

        Assert.True(dropped.IsSyncDropped);
        Assert.False(dropped.IsSyncReport);
        Assert.False(dropped.IsKey);

        Assert.True(report.IsSyncReport);
        Assert.False(report.IsSyncDropped);
        Assert.False(report.IsKey);
    }

    [Fact]
    public void The_key_2_is_not_a_drop()
    {
        // KEY_2 and SYN_DROPPED share the number 3: only the type tells them
        // apart. Taken for a drop, typing a 2 would make the tracker forget the
        // keyboard's held keys and cancel the dictation.
        var key = new InputEvent(InputEvent.Key, Key2, 1);

        Assert.False(key.IsSyncDropped);
        Assert.False(key.IsSyncReport);
        Assert.True(key.IsKey);
    }

    [Fact]
    public void KeyEvent_builds_what_a_virtual_keyboard_writes()
    {
        Assert.Equal(new InputEvent(InputEvent.Key, LinuxKeys.V, 1), InputEvent.KeyEvent(LinuxKeys.V, pressed: true));
        Assert.Equal(new InputEvent(InputEvent.Key, LinuxKeys.V, 0), InputEvent.KeyEvent(LinuxKeys.V, pressed: false));
        Assert.Equal(new InputEvent(InputEvent.Synchronization, InputEvent.SynReport, 0), InputEvent.Report());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65_583)]
    public void KeyEvent_refuses_a_code_that_does_not_fit(int code)
    {
        // Truncated silently, 65 583 would become 47, KEY_V: a key sent that
        // nobody asked for.
        Assert.Throws<OverflowException>(() => InputEvent.KeyEvent(code, pressed: true));
    }

    [Fact]
    public void WriteTo_lays_the_event_out_like_the_kernel_and_zeroes_the_time()
    {
        // uinput stamps the time itself; whatever the buffer held before must
        // not be sent as one.
        byte[] target = new byte[InputEvent.Size + 4];
        Array.Fill(target, (byte)0xEE);

        InputEvent.KeyEvent(LinuxKeys.Insert, pressed: true).WriteTo(target);

        Assert.All(target[..16], b => Assert.Equal(0, b));
        Assert.Equal(InputEvent.Key, BinaryPrimitives.ReadUInt16LittleEndian(target.AsSpan(16)));
        Assert.Equal(LinuxKeys.Insert, BinaryPrimitives.ReadUInt16LittleEndian(target.AsSpan(18)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(target.AsSpan(20)));

        // Only the 24 bytes of the event are written.
        Assert.All(target[InputEvent.Size..], b => Assert.Equal(0xEE, b));
    }

    [Fact]
    public void An_event_written_reads_back_the_same()
    {
        var original = new InputEvent(InputEvent.Key, LinuxKeys.F24, 2);
        byte[] buffer = new byte[InputEvent.Size];

        original.WriteTo(buffer);

        Assert.True(InputEvent.TryRead(buffer, out InputEvent read));
        Assert.Equal(original, read);
    }

    [Fact]
    public void WriteTo_refuses_a_buffer_too_small()
    {
        byte[] target = new byte[InputEvent.Size - 1];

        Assert.Throws<ArgumentException>(() => InputEvent.Report().WriteTo(target));
    }
}
