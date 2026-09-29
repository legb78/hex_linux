using HexLinux.Audio;
using Xunit;

namespace HexLinux.Tests.Audio;

/// <summary>
/// A test that needs Silero's model next to the recognition model, where
/// <c>scripts/get-model.sh</c> puts it, and skips itself with a usable message
/// when it is absent.
/// </summary>
public sealed class SileroRequiredFactAttribute : FactAttribute
{
    public SileroRequiredFactAttribute()
    {
        if (ModelFile is null)
        {
            Skip = "Silero VAD model not present. Run scripts/get-model.sh to download it.";
        }
    }

    /// <summary>silero_vad.onnx beside the model the application would use, or null.</summary>
    public static string? ModelFile =>
        ModelRequiredFactAttribute.ModelDirectory is { } model
        && SileroModel.PathFor(model) is { } file
        && File.Exists(file)
        && SileroModel.Problem(File.ReadAllBytes(file)) is null
            ? file
            : null;
}

/// <summary>
/// These load sherpa-onnx's native voice detector for real, with the model
/// get-model.sh pins. They are the only check that the linux-x64 runtime
/// package carries the detector and that the managed calls reach it.
/// </summary>
[Trait("Category", "Integration")]
public class SileroSpeechDetectorIntegrationTests
{
    /// <summary>50 ms, one capture buffer as the recorder reads them.</summary>
    private static readonly int Chunk = (int)RecordingFormat.BytesFor(TimeSpan.FromMilliseconds(50));

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "bonjour-fr.wav");

    private static SileroSpeechDetector Load() =>
        new(SileroRequiredFactAttribute.ModelFile ?? throw new InvalidOperationException("Silero absent. Run: scripts/get-model.sh"));

    private static byte[] FixturePcm() => File.ReadAllBytes(FixturePath)[WavFile.HeaderSize..];

    private static IEnumerable<byte[]> Chunks(byte[] pcm)
    {
        for (int offset = 0; offset + Chunk <= pcm.Length; offset += Chunk)
        {
            yield return pcm[offset..(offset + Chunk)];
        }
    }

    [SileroRequiredFact]
    public void A_spoken_sentence_is_heard_as_speech()
    {
        using SileroSpeechDetector detector = Load();

        int speech = Chunks(FixturePcm()).Count(chunk => detector.IsSpeech(chunk));

        Assert.True(speech > 0, "Silero heard no speech in the French fixture");
    }

    [SileroRequiredFact]
    public void Digital_silence_is_never_speech()
    {
        using SileroSpeechDetector detector = Load();
        byte[] silence = new byte[Chunk];

        Assert.All(Enumerable.Range(0, 40), _ => Assert.False(detector.IsSpeech(silence)));
    }

    [SileroRequiredFact]
    public void Loud_noise_is_not_speech_where_the_level_threshold_hears_it()
    {
        // The reason for the detector: a fan or a street is loud enough for
        // the level threshold, so no pause was ever found. Seeded noise at
        // about a tenth of full scale, well above that threshold.
        using SileroSpeechDetector detector = Load();
        var random = new Random(20260927);
        var level = new LevelSpeechDetector();
        int heardByLevel = 0;
        int heardBySilero = 0;

        for (int i = 0; i < 40; i++)
        {
            byte[] noise = new byte[Chunk];

            for (int offset = 0; offset < noise.Length; offset += 2)
            {
                short sample = (short)random.Next(-3_000, 3_000);
                noise[offset] = (byte)sample;
                noise[offset + 1] = (byte)(sample >> 8);
            }

            heardByLevel += level.IsSpeech(noise) ? 1 : 0;
            heardBySilero += detector.IsSpeech(noise) ? 1 : 0;
        }

        Assert.Equal(40, heardByLevel);
        Assert.True(heardBySilero < 10, $"Silero took white noise for speech in {heardBySilero} of 40 blocks");
    }

    [SileroRequiredFact]
    public void A_pause_after_the_fixture_closes_a_segment()
    {
        // The whole chain the daemon runs with segmentation on: the fixture,
        // then a second of silence, cut at a 700 ms pause.
        using SileroSpeechDetector detector = Load();
        var segmenter = new SpeechSegmenter(TimeSpan.FromMilliseconds(700), detector);
        byte[]? closed = null;

        foreach (byte[] chunk in Chunks([.. FixturePcm(), .. new byte[RecordingFormat.BytesPerSecond]]))
        {
            closed ??= segmenter.Push(chunk);
        }

        Assert.NotNull(closed);
    }

    [SileroRequiredFact]
    public void Resetting_between_recordings_keeps_the_detector_usable()
    {
        using SileroSpeechDetector detector = Load();

        foreach (byte[] chunk in Chunks(FixturePcm()))
        {
            detector.IsSpeech(chunk);
        }

        detector.Reset();

        Assert.False(detector.IsSpeech(new byte[Chunk]));
    }
}
