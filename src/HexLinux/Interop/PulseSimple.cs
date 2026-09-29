using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace HexLinux.Interop;

/// <summary>
/// libpulse's "simple" API: one blocking stream, to record or to play.
///
/// <para><b>Why libpulse-simple.</b> It is the audio API every Linux desktop
/// answers: PulseAudio itself, PipeWire through its pipewire-pulse server —
/// the default on current Ubuntu and Fedora — and WSLg. It records straight
/// in the engine's format, 16 kHz mono signed 16-bit, the server doing any
/// resampling, and it comes with the <c>libpulse0</c> package desktops already
/// have.</para>
///
/// <para>Values checked by compiling against <c>/usr/include/pulse</c>
/// (libpulse-dev 16.1, Ubuntu 24.04), command in the report:
/// <c>PA_SAMPLE_S16LE=3</c>, <c>PA_STREAM_PLAYBACK=1</c>,
/// <c>PA_STREAM_RECORD=2</c>; <c>sizeof(pa_sample_spec)=12</c> (format at 0 as
/// a 4-byte enum, rate at 4, channels at 8); <c>sizeof(pa_buffer_attr)=20</c>
/// (maxlength, tlength, prebuf, minreq, fragsize: five 32-bit fields, where
/// <c>(uint32_t)-1</c> means "the server's default"). Both libraries export
/// the functions below (checked with <c>nm -D</c>).</para>
///
/// <para>A stream is not thread-safe: each is created, used and freed by one
/// thread only.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "P/Invoke declarations: need a sound server, verified by --record, --test-feedback and --doctor.")]
internal static partial class PulseSimple
{
    private const string SimpleLibrary = "libpulse-simple.so.0";
    private const string PulseLibrary = "libpulse.so.0";

    public const int SampleS16LittleEndian = 3;
    public const int StreamPlayback = 1;
    public const int StreamRecord = 2;

    /// <summary>"Whatever the server prefers", for a <see cref="BufferAttributes"/> field.</summary>
    public const uint Default = uint.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    public struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferAttributes
    {
        public uint MaxLength;
        public uint TargetLength;
        public uint PreBuffering;
        public uint MinimumRequest;
        public uint FragmentSize;
    }

    [LibraryImport(SimpleLibrary, EntryPoint = "pa_simple_new", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint New(
        string? server,
        string name,
        int direction,
        string? device,
        string streamName,
        in SampleSpec spec,
        nint channelMap,
        in BufferAttributes attributes,
        out int error);

    [LibraryImport(SimpleLibrary, EntryPoint = "pa_simple_read")]
    public static unsafe partial int Read(nint stream, byte* data, nuint bytes, out int error);

    [LibraryImport(SimpleLibrary, EntryPoint = "pa_simple_write")]
    public static unsafe partial int Write(nint stream, byte* data, nuint bytes, out int error);

    [LibraryImport(SimpleLibrary, EntryPoint = "pa_simple_drain")]
    public static partial int Drain(nint stream, out int error);

    [LibraryImport(SimpleLibrary, EntryPoint = "pa_simple_free")]
    public static partial void Free(nint stream);

    /// <summary>A pointer to a static string: never marshalled as <c>string</c>, which would free it.</summary>
    [LibraryImport(PulseLibrary, EntryPoint = "pa_strerror")]
    private static partial nint StrErrorPointer(int error);

    public static string Describe(int error) => Marshal.PtrToStringUTF8(StrErrorPointer(error)) ?? $"PulseAudio error {error}";

    /// <summary>
    /// Opens a mono signed 16-bit stream, or throws with the server's own
    /// words.
    /// </summary>
    /// <param name="direction">Recording or playback.</param>
    /// <param name="rate">Samples per second.</param>
    /// <param name="streamName">What the sound settings show for the stream.</param>
    /// <param name="fragmentBytes">
    /// Recording: how much one read waits for, which bounds how long a stop
    /// waits. Zero for the server's default.
    /// </param>
    /// <param name="targetBytes">
    /// Playback: how much the server buffers. A short cue asks for its own
    /// length, so it is heard at once rather than after a buffer meant for
    /// music. Zero for the server's default.
    /// </param>
    public static nint Open(int direction, uint rate, string streamName, uint fragmentBytes, uint targetBytes = 0)
    {
        var spec = new SampleSpec { Format = SampleS16LittleEndian, Rate = rate, Channels = 1 };
        var attributes = new BufferAttributes
        {
            MaxLength = Default,
            TargetLength = targetBytes == 0 ? Default : targetBytes,
            PreBuffering = Default,
            MinimumRequest = Default,
            FragmentSize = fragmentBytes == 0 ? Default : fragmentBytes,
        };

        nint stream = New(null, "HexLinux", direction, null, streamName, in spec, 0, in attributes, out int error);

        if (stream == 0)
        {
            throw new InvalidOperationException($"the sound server refused the stream: {Describe(error)}");
        }

        return stream;
    }
}
