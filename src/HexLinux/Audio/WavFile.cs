using System.Buffers.Binary;

namespace HexLinux.Audio;

/// <summary>What a WAV file says of its own samples, and where they are.</summary>
/// <param name="AudioFormat">1 for plain PCM, 0xFFFE for its extensible form, 3 for floats…</param>
/// <param name="Channels">1 for mono.</param>
/// <param name="SampleRate">Samples per second and per channel.</param>
/// <param name="BitsPerSample">16 for the format the engine takes.</param>
/// <param name="DataOffset">Where the samples start in the file.</param>
/// <param name="DataLength">How many bytes of samples the file really holds.</param>
public readonly record struct WavFormat(int AudioFormat, int Channels, int SampleRate, int BitsPerSample, int DataOffset, int DataLength)
{
    /// <summary>Plain PCM, as <c>arecord</c> and <c>--record</c> write it.</summary>
    public const int Pcm = 1;

    /// <summary>WAVE_FORMAT_EXTENSIBLE: what some tools write for PCM too, the real format then sits in a sub-header.</summary>
    public const int Extensible = 0xFFFE;

    /// <summary>
    /// Why these samples are not what the engine takes — 16 kHz, mono,
    /// 16-bit PCM — or null when they are.
    /// </summary>
    public string? Mismatch()
    {
        List<string> wrong = [];

        if (AudioFormat is not (Pcm or Extensible))
        {
            wrong.Add($"format {AudioFormat} instead of PCM (1)");
        }

        if (SampleRate != RecordingFormat.SampleRate)
        {
            wrong.Add($"{SampleRate} Hz instead of {RecordingFormat.SampleRate}");
        }

        if (Channels != RecordingFormat.Channels)
        {
            wrong.Add($"{Channels} channels instead of {RecordingFormat.Channels}");
        }

        if (BitsPerSample != RecordingFormat.BitsPerSample)
        {
            wrong.Add($"{BitsPerSample}-bit samples instead of {RecordingFormat.BitsPerSample}");
        }

        return wrong.Count == 0 ? null : string.Join(", ", wrong);
    }
}

/// <summary>
/// Writes the WAV header that precedes the raw samples.
///
/// WAV is the internal exchange format: what the recorder returns, what the
/// diagnostic mode writes to disk, and what the engine reads back. The header
/// is built by hand because library writers close the underlying stream,
/// whereas a recording lives in a MemoryStream that has to be read afterwards.
/// A wrong header raises nothing: the engine loads it and transcribes noise.
/// Hence the tests on the bytes produced.
/// </summary>
public static class WavFile
{
    /// <summary>Size of the canonical RIFF/WAVE header, in bytes.</summary>
    public const int HeaderSize = 44;

    /// <summary>
    /// Assembles a complete WAV file from raw samples in
    /// <see cref="RecordingFormat"/>.
    /// </summary>
    public static byte[] Create(ReadOnlySpan<byte> pcm)
    {
        byte[] file = new byte[HeaderSize + pcm.Length];

        WriteHeader(file, pcm.Length);
        pcm.CopyTo(file.AsSpan(HeaderSize));

        return file;
    }

    /// <summary>Entirely silent WAV file, useful for warming up.</summary>
    public static byte[] CreateSilence(TimeSpan duration) =>
        Create(new byte[RecordingFormat.BytesFor(duration)]);

    /// <summary>
    /// Writes the header into the first 44 bytes of <paramref name="target"/>.
    /// </summary>
    public static void WriteHeader(Span<byte> target, int pcmByteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pcmByteCount);

        if (target.Length < HeaderSize)
        {
            throw new ArgumentException(
                $"A WAV header takes up {HeaderSize} bytes.", nameof(target));
        }

        const int fmtChunkSize = 16;
        const short pcmFormat = 1;

        Write(target, 0, "RIFF"u8);
        WriteInt32(target, 4, 36 + pcmByteCount);          // total size minus 8
        Write(target, 8, "WAVE"u8);

        Write(target, 12, "fmt "u8);
        WriteInt32(target, 16, fmtChunkSize);
        WriteInt16(target, 20, pcmFormat);                 // uncompressed PCM
        WriteInt16(target, 22, RecordingFormat.Channels);
        WriteInt32(target, 24, RecordingFormat.SampleRate);
        WriteInt32(target, 28, RecordingFormat.BytesPerSecond);
        WriteInt16(target, 32, RecordingFormat.Channels * RecordingFormat.BytesPerSample);
        WriteInt16(target, 34, RecordingFormat.BitsPerSample);

        Write(target, 36, "data"u8);
        WriteInt32(target, 40, pcmByteCount);
    }

    /// <summary>
    /// Reads the header of a WAV file of any origin, or says why it is none.
    ///
    /// <para>For <c>--transcribe</c>, the diagnostic that must not mislead:
    /// the engine silently takes whatever bytes follow a 44-byte header as
    /// 16 kHz mono PCM, so a 44.1 kHz stereo file came out as gibberish, and
    /// a file too short to hold one sample aborted the process inside ONNX
    /// Runtime (both verified). The chunks are walked rather than assumed at
    /// fixed places: some tools put a <c>LIST</c> chunk of metadata before
    /// the samples.</para>
    /// </summary>
    /// <returns>Null when <paramref name="format"/> was read, else the reason.</returns>
    public static string? TryReadFormat(ReadOnlySpan<byte> file, out WavFormat format)
    {
        format = default;

        if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file[8..12].SequenceEqual("WAVE"u8))
        {
            return "not a WAV file (no RIFF/WAVE header)";
        }

        int? audioFormat = null;
        int channels = 0;
        int sampleRate = 0;
        int bits = 0;
        long offset = 12;

        // Each chunk: a four-letter id, a 32-bit little-endian size, the
        // content, and a padding byte when the size is odd.
        while (offset + 8 <= file.Length)
        {
            ReadOnlySpan<byte> id = file.Slice((int)offset, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice((int)offset + 4, 4));
            int content = (int)offset + 8;

            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || content + 16 > file.Length)
                {
                    return "the WAV header is cut short";
                }

                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(content, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(content + 2, 2));
                sampleRate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(content + 4, 4)), int.MaxValue);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(content + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (audioFormat is null)
                {
                    return "the WAV file has its samples before its format";
                }

                // A size larger than the file — a recording cut short, or a
                // stream written before its length was known — counts what
                // is really there.
                int length = (int)Math.Min(size, file.Length - content);

                format = new WavFormat(audioFormat.Value, channels, sampleRate, bits, content, length);
                return null;
            }

            offset = content + size + (size & 1);
        }

        return audioFormat is null ? "the WAV file has no format" : "the WAV file has no samples";
    }

    private static void Write(Span<byte> target, int offset, ReadOnlySpan<byte> value) =>
        value.CopyTo(target[offset..]);

    private static void WriteInt32(Span<byte> target, int offset, int value)
    {
        // WAV is little-endian, whatever the machine.
        target[offset] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)(value >> 16);
        target[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteInt16(Span<byte> target, int offset, int value)
    {
        target[offset] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
    }
}
