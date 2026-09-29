using System.Buffers.Binary;
using System.Text;
using HexLinux.Audio;
using Xunit;

namespace HexLinux.Tests.Audio;

/// <summary>
/// Reading the header of a WAV file given to <c>--transcribe</c>. That mode is
/// the main troubleshooting tool: a file it misreads points the user at the
/// engine when the fault is the file. Before the header was read, a 44.1 kHz
/// stereo WAV gave gibberish, and a file too short for one sample aborted the
/// process inside ONNX Runtime (QA-03, RV-17).
/// </summary>
public class WavFormatTests
{
    [Fact]
    public void A_file_written_by_the_recorder_is_exactly_what_the_engine_takes()
    {
        // --record's output fed back to --transcribe: the round trip the
        // README tells the user to make.
        byte[] file = WavFile.Create(new byte[3200]);

        Assert.Null(WavFile.TryReadFormat(file, out WavFormat format));
        Assert.Null(format.Mismatch());
        Assert.Equal(WavFile.HeaderSize, format.DataOffset);
        Assert.Equal(3200, format.DataLength);
    }

    [Fact]
    public void A_stereo_CD_quality_file_is_named_for_what_it_is()
    {
        // What a phone or an editor exports by default: the user must learn
        // to convert it, not that the engine "heard nothing usable".
        byte[] file = Build(format: 1, channels: 2, rate: 44_100, bits: 16, data: new byte[400]);

        Assert.Null(WavFile.TryReadFormat(file, out WavFormat format));
        Assert.Equal("44100 Hz instead of 16000, 2 channels instead of 1", format.Mismatch());
    }

    [Fact]
    public void The_default_of_arecord_is_refused_for_its_rate_and_its_sample_size()
    {
        // arecord without options writes 8 kHz unsigned 8-bit.
        byte[] file = Build(format: 1, channels: 1, rate: 8_000, bits: 8, data: new byte[400]);

        Assert.Null(WavFile.TryReadFormat(file, out WavFormat format));
        Assert.Equal("8000 Hz instead of 16000, 8-bit samples instead of 16", format.Mismatch());
    }

    [Fact]
    public void Float_samples_are_refused()
    {
        // Format 3: IEEE floats, 32-bit — read as 16-bit integers they would
        // be noise.
        byte[] file = Build(format: 3, channels: 1, rate: 16_000, bits: 32, data: new byte[400]);

        Assert.Null(WavFile.TryReadFormat(file, out WavFormat format));
        Assert.Equal("format 3 instead of PCM (1), 32-bit samples instead of 16", format.Mismatch());
    }

    [Fact]
    public void The_extensible_form_of_PCM_is_accepted()
    {
        // Some tools write WAVE_FORMAT_EXTENSIBLE even for plain 16-bit mono.
        byte[] file = Build(format: WavFormat.Extensible, channels: 1, rate: 16_000, bits: 16, data: new byte[400]);

        Assert.Null(WavFile.TryReadFormat(file, out WavFormat format));
        Assert.Null(format.Mismatch());
    }

    [Fact]
    public void A_metadata_chunk_before_the_samples_is_stepped_over()
    {
        // A LIST chunk of odd size, padded to an even one: skipping 44 bytes
        // blindly would read the metadata as sound.
        byte[] list = Chunk("LIST", Encoding.ASCII.GetBytes("INFOISFT\x05\0\0\0Lavf\0"));
        byte[] file = Build(format: 1, channels: 1, rate: 16_000, bits: 16, data: new byte[320], before: list);

        Assert.Null(WavFile.TryReadFormat(file, out WavFormat format));
        Assert.Equal(file.Length - 320, format.DataOffset);
        Assert.Equal(320, format.DataLength);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a wav file\n")]
    [InlineData("RIFF\0\0\0\0AVI LIST")]
    public void Something_that_is_no_WAV_file_says_so(string content)
    {
        // The 15-byte text file of QA-03, which used to abort the process.
        Assert.Equal("not a WAV file (no RIFF/WAVE header)", WavFile.TryReadFormat(Encoding.ASCII.GetBytes(content), out _));
    }

    [Fact]
    public void A_header_cut_short_says_so()
    {
        byte[] whole = WavFile.Create(new byte[100]);

        Assert.Equal("the WAV header is cut short", WavFile.TryReadFormat(whole.AsSpan(0, 30), out _));
    }

    [Fact]
    public void A_file_with_a_format_and_no_samples_says_so()
    {
        byte[] whole = WavFile.Create([]);

        Assert.Equal("the WAV file has no samples", WavFile.TryReadFormat(whole.AsSpan(0, 36), out _));
    }

    [Fact]
    public void A_data_size_larger_than_the_file_counts_what_is_really_there()
    {
        // A recording interrupted before its header was finalised.
        byte[] file = WavFile.Create(new byte[100]);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), uint.MaxValue);

        Assert.Null(WavFile.TryReadFormat(file, out WavFormat format));
        Assert.Equal(100, format.DataLength);
    }

    [Fact]
    public void A_chunk_size_that_points_past_the_end_ends_the_walk_without_throwing()
    {
        // A damaged file: an enormous "fmt " size must not make the reader
        // overflow or loop.
        byte[] file = [.. "RIFF\0\0\0\0WAVEjunk"u8, 0xFF, 0xFF, 0xFF, 0xFF];

        Assert.Equal("the WAV file has no format", WavFile.TryReadFormat(file, out _));
    }

    [Fact]
    public void Samples_before_the_format_are_refused()
    {
        byte[] file = [.. "RIFF\0\0\0\0WAVE"u8, .. Chunk("data", new byte[4])];

        Assert.Equal("the WAV file has its samples before its format", WavFile.TryReadFormat(file, out _));
    }

    private static byte[] Build(int format, int channels, int rate, int bits, byte[] data, byte[]? before = null)
    {
        byte[] fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, (ushort)format);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), (uint)(rate * channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)(channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)bits);

        return [.. "RIFF\0\0\0\0WAVE"u8, .. Chunk("fmt ", fmt), .. before ?? Array.Empty<byte>(), .. Chunk("data", data)];
    }

    private static byte[] Chunk(string id, byte[] content)
    {
        byte[] chunk = new byte[8 + content.Length + (content.Length & 1)];
        Encoding.ASCII.GetBytes(id).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)content.Length);
        content.CopyTo(chunk, 8);
        return chunk;
    }
}
