using HexLinux.Audio;
using HexLinux.Diagnostics;
using HexLinux.Transcription;
using Xunit;

namespace HexLinux.Tests.Transcription;

/// <summary>
/// These tests really do load ONNX Runtime and transcribe a real file. They
/// are excluded from CI — the model weighs 670 MB once extracted — but they
/// remain the only way to check that the linux-x64 native libraries load and
/// that the whole chain works.
///
///     scripts/get-model.sh
///     dotnet test --filter Category=Integration
/// </summary>
[Trait("Category", "Integration")]
public class ParakeetEngineIntegrationTests
{
    private static string ModelPath =>
        ModelRequiredFactAttribute.ModelDirectory
        ?? throw new InvalidOperationException("Model absent. Run: scripts/get-model.sh");

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "bonjour-fr.wav");

    private static ParakeetEngine Load(bool frenchSpacing = true) =>
        ParakeetEngine.Load(ModelPath, "cpu", 4, frenchSpacing);

    [ModelRequiredFact]
    public async Task A_French_recording_is_transcribed()
    {
        using ParakeetEngine engine = Load();

        await using FileStream wav = File.OpenRead(FixturePath);
        TranscriptionResult result = await engine.TranscribeAsync(wav);

        Assert.Contains("transcription", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [ModelRequiredFact]
    public async Task French_is_recognised_without_being_told()
    {
        // Parakeet v3 works out the language by itself among the 25 it covers:
        // the reason HexLinux has no "language" setting.
        using ParakeetEngine engine = Load();

        await using FileStream wav = File.OpenRead(FixturePath);
        TranscriptionResult result = await engine.TranscribeAsync(wav);

        Assert.Contains("Bonjour", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [ModelRequiredFact]
    public async Task A_short_dictation_stays_under_a_second()
    {
        // Same deliberately generous threshold as HexWin: it must not turn
        // flaky on a loaded machine, and still catch an outright regression
        // such as the native library falling back to a debug build.
        using ParakeetEngine engine = Load();
        await engine.WarmUpAsync();

        await using FileStream wav = File.OpenRead(FixturePath);
        TranscriptionResult result = await engine.TranscribeAsync(wav);

        Assert.True(
            result.Duration < TimeSpan.FromSeconds(1),
            $"Transcribed in {result.Duration.TotalSeconds:F2} s, expected under a second.");
    }

    [ModelRequiredFact]
    public async Task Warming_up_does_not_throw()
    {
        using ParakeetEngine engine = Load();

        await engine.WarmUpAsync();
    }

    [ModelRequiredFact]
    public async Task A_silent_recording_produces_no_text()
    {
        // A common case: the key is released before anything was said. Nothing
        // must be inserted.
        using ParakeetEngine engine = Load();

        using var silence = new MemoryStream(WavFile.CreateSilence(TimeSpan.FromSeconds(1)));
        TranscriptionResult result = await engine.TranscribeAsync(silence);

        Assert.Equal(string.Empty, result.Text);
    }

    [ModelRequiredFact]
    public async Task No_control_character_reaches_the_text()
    {
        // What the engine hands over is typed into the focused window, a
        // terminal included: no line feed, escape or bidi control may come
        // out of it, whatever the model's vocabulary holds.
        using ParakeetEngine engine = Load();

        await using FileStream wav = File.OpenRead(FixturePath);
        TranscriptionResult result = await engine.TranscribeAsync(wav);

        Assert.DoesNotContain(result.Text, character => char.IsControl(character));
    }

    [ModelRequiredFact]
    public async Task French_spacing_off_changes_nothing_but_the_space_before_punctuation()
    {
        // The frenchSpacing setting reaches the cleaner through the engine,
        // and turning it off (someone who dictates English only) must not
        // change a single word: only the space French puts before ? ! ; :
        string with = await TranscribeFixtureAsync(frenchSpacing: true);
        string without = await TranscribeFixtureAsync(frenchSpacing: false);

        Assert.DoesNotMatch(@"\s[?!;:»]", without);
        Assert.Equal(System.Text.RegularExpressions.Regex.Replace(with, @"\s([?!;:»])", "$1"), without);
    }

    private static async Task<string> TranscribeFixtureAsync(bool frenchSpacing)
    {
        using ParakeetEngine engine = Load(frenchSpacing);

        await using FileStream wav = File.OpenRead(FixturePath);
        TranscriptionResult result = await engine.TranscribeAsync(wav);

        return result.Text;
    }

    [ModelRequiredFact]
    public async Task The_host_loads_once_for_simultaneous_callers()
    {
        // A key press preloads the model while a second request for it
        // arrives: both must get the same engine, loaded once, or the
        // machine would hold two copies of 670 MB.
        using var host = new EngineHost(ModelPath, "cpu", 4, true, new IdlePolicy(TimeSpan.Zero), SessionLog.Silent);

        Task<ParakeetEngine> first = host.GetAsync();
        Task<ParakeetEngine> second = host.GetAsync();
        ParakeetEngine[] engines = await Task.WhenAll(first, second);

        Assert.Same(engines[0], engines[1]);
        Assert.True(host.IsLoaded);
    }

    [ModelRequiredFact]
    public async Task Audio_too_short_for_a_single_frame_gives_no_text_instead_of_aborting()
    {
        // QA-03: a WAV of 0 or 1 sample, or a file under 44 bytes, made ONNX
        // Runtime abort the whole process ("Invalid input shape: {0,128}",
        // SIGABRT). A failure here takes the test run down with it.
        using ParakeetEngine engine = Load();

        foreach (int samples in new[] { 0, 1, 159, ParakeetEngine.MinimumSamples - 1 })
        {
            using var wav = new MemoryStream(WavFile.Create(new byte[samples * RecordingFormat.BytesPerSample]));
            TranscriptionResult result = await engine.TranscribeAsync(wav);

            Assert.Equal(string.Empty, result.Text);
        }

        using var header = new MemoryStream(new byte[10]);
        Assert.Equal(string.Empty, (await engine.TranscribeAsync(header)).Text);
    }

    [ModelRequiredFact]
    public async Task Disposing_the_host_during_a_transcription_waits_for_it()
    {
        // QA-01 / RV-02: SIGTERM or "Quit" during a transcription freed the
        // recognizer under the running decode — a segmentation fault that
        // took the daemon down, reproduced with a minute of audio and a
        // disposal 200 ms in. A regression here crashes the test run.
        byte[] fixture = await File.ReadAllBytesAsync(FixturePath);
        byte[] pcm = fixture[WavFile.HeaderSize..];
        byte[] minute = WavFile.Create([.. Enumerable.Repeat(pcm, (int)Math.Ceiling(60.0 / RecordingFormat.DurationOf(pcm.Length).TotalSeconds)).SelectMany(bytes => bytes)]);

        var host = new EngineHost(ModelPath, "cpu", 4, true, new IdlePolicy(TimeSpan.Zero), SessionLog.Silent);
        await host.GetAsync();

        using var wav = new MemoryStream(minute);
        Task<TranscriptionResult> decoding = host.TranscribeAsync(wav);

        await Task.Delay(200);
        host.Dispose();

        // Dispose returned after the decode: its result is there, whole.
        Assert.True(decoding.IsCompleted, "the host was freed while the decode still ran");
        Assert.Contains("transcription", (await decoding).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_disposed_host_refuses_new_work_cleanly()
    {
        // A segment arriving after the stop: an exception the daemon's
        // catch-all logs, not a crash in native code. No model needed: the
        // refusal comes before any load.
        var host = new EngineHost("/nonexistent/hexlinux/model", "cpu", 4, true, new IdlePolicy(TimeSpan.Zero), SessionLog.Silent);
        host.Dispose();

        using var wav = new MemoryStream(WavFile.CreateSilence(TimeSpan.FromSeconds(1)));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.TranscribeAsync(wav));
        host.BeginLoad();
    }

    [Fact]
    public void A_missing_model_gives_an_actionable_message()
    {
        // The message is what the daemon logs and notifies: it has to name
        // the script that fixes it.
        FileNotFoundException error = Assert.Throws<FileNotFoundException>(
            () => ParakeetEngine.Load("/nonexistent/hexlinux/model", "cpu", 4));

        Assert.Contains("get-model.sh", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_model_is_refused_before_the_native_code_sees_it()
    {
        // An interrupted copy leaves files that exist but are cut short:
        // ONNX Runtime would fail inside native code, possibly taking the
        // daemon down. Validate names the file instead.
        string folder = Path.Combine(Path.GetTempPath(), "hexlinux-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            foreach ((string name, long _) in ModelFiles.Required)
            {
                File.WriteAllBytes(Path.Combine(folder, name), new byte[16]);
            }

            FileNotFoundException error = Assert.Throws<FileNotFoundException>(() => ParakeetEngine.Validate(folder));

            Assert.Contains(ModelFiles.Encoder, error.Message, StringComparison.Ordinal);
            Assert.Contains("too small", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
