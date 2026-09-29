using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using HexLinux.Audio;
using SherpaOnnx;

namespace HexLinux.Transcription;

/// <summary>What a transcription produced, and at what cost.</summary>
/// <param name="Text">Cleaned text, ready to insert. Empty if nothing usable.</param>
/// <param name="Duration">Compute time, to measure the latency as felt.</param>
public readonly record struct TranscriptionResult(string Text, TimeSpan Duration);

/// <summary>
/// Local speech recognition by Parakeet TDT v3 (NVIDIA), run through
/// sherpa-onnx and ONNX Runtime — the engine Hex uses on macOS and HexWin on
/// Windows, with the same NuGet packages built for linux-x64.
///
/// <para>The choice comes down to its architecture: Parakeet is a
/// <i>transducer</i>, where Whisper is an autoregressive encoder-decoder
/// producing its text token by token. That sequential decoding imposes a fixed
/// cost per transcription, independent of the length of the recording, and so
/// especially punishing on short dictations, which are the normal use.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Covered by the integration tests, which load the native engine and are excluded from CI.")]
public sealed class ParakeetEngine : IDisposable
{
    /// <summary>
    /// Fewer samples than this — a tenth of a second — are not handed to the
    /// engine, and give an empty result. A tenth of a second holds no word;
    /// and on an input too short to make a single feature frame, ONNX Runtime
    /// aborts the whole process instead of failing the call (verified with 0
    /// and 1 sample: "Invalid input shape: {0,128}", SIGABRT; 160 samples
    /// passed). The daemon never sends that little, but <c>--transcribe</c>
    /// reads whatever file it is given.
    /// </summary>
    public const int MinimumSamples = RecordingFormat.SampleRate / 10;

    private const int FeatureDimension = 80;

    private readonly OfflineRecognizer _recognizer;
    private readonly bool _frenchSpacing;

    private ParakeetEngine(OfflineRecognizer recognizer, string provider, bool frenchSpacing)
    {
        _recognizer = recognizer;
        _frenchSpacing = frenchSpacing;
        LoadedRuntime = provider;
    }

    /// <summary>ONNX Runtime provider actually requested, for the log.</summary>
    public string LoadedRuntime { get; }

    /// <summary>
    /// Checks that the model is complete, without loading anything into
    /// memory: every file present, and none implausibly small.
    ///
    /// <para>Lets a missing or damaged model be reported when the daemon
    /// starts, including when loading itself is deferred to the first
    /// dictation: discovering the problem while the user is speaking would be
    /// the worst possible moment, their sentence being already lost by
    /// then.</para>
    /// </summary>
    /// <exception cref="FileNotFoundException">A model file is missing or too small.</exception>
    public static void Validate(string modelDirectory)
    {
        IReadOnlyList<string> problems = ModelFiles.Problems(
            modelDirectory,
            path => File.Exists(path) ? new FileInfo(path).Length : null);

        if (problems.Count > 0)
        {
            throw new FileNotFoundException(
                $"The model is incomplete: {string.Join("; ", problems)}. "
                + "Run get-model.sh (scripts/get-model.sh in a clone) to download it again.",
                Path.Combine(modelDirectory, ModelFiles.Encoder));
        }
    }

    /// <summary>
    /// Loads the model from its folder: encoder, decoder, joiner and
    /// vocabulary, hence a folder rather than a single file.
    /// </summary>
    /// <param name="modelDirectory">The model folder.</param>
    /// <param name="provider">ONNX Runtime provider; only "cpu" exists.</param>
    /// <param name="threads">Decoding threads.</param>
    /// <param name="frenchSpacing">The <c>frenchSpacing</c> setting, applied by the cleaner.</param>
    /// <exception cref="FileNotFoundException">A model file is missing or too small.</exception>
    public static ParakeetEngine Load(string modelDirectory, string provider, int threads, bool frenchSpacing = true)
    {
        Validate(modelDirectory);

        var config = new OfflineRecognizerConfig();

        config.FeatConfig.SampleRate = RecordingFormat.SampleRate;
        config.FeatConfig.FeatureDim = FeatureDimension;

        config.ModelConfig.Transducer.Encoder = Path.Combine(modelDirectory, ModelFiles.Encoder);
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelDirectory, ModelFiles.Decoder);
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelDirectory, ModelFiles.Joiner);
        config.ModelConfig.Tokens = Path.Combine(modelDirectory, ModelFiles.Tokens);
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.Provider = provider;
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Debug = 0;

        config.DecodingMethod = "greedy_search";

        return new ParakeetEngine(new OfflineRecognizer(config), provider, frenchSpacing);
    }

    /// <summary>
    /// Transcribes a 16 kHz mono WAV stream. No language needs to be given:
    /// Parakeet v3 works out on its own which of its 25 European languages is
    /// being spoken.
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAsync(Stream wav, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wav);

        if (wav.CanSeek)
        {
            wav.Position = 0;
        }

        using var buffer = new MemoryStream();
        await wav.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        float[] samples = PcmConverter.FromWav(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));

        // Decoding is synchronous and compute-hungry: it runs off the calling
        // thread, which is the daemon's loop.
        return await Task.Run(() => Decode(samples), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the engine on a very short silence, to absorb the cost of the
    /// first inference: allocating the ONNX buffers and picking the compute
    /// kernels. Otherwise the user's first dictation would pay for it.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        using var silence = new MemoryStream(WavFile.CreateSilence(TimeSpan.FromMilliseconds(200)));

        try
        {
            await TranscribeAsync(silence, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down during warm-up: no consequence.
        }
    }

    private TranscriptionResult Decode(float[] samples)
    {
        if (samples.Length < MinimumSamples)
        {
            return new TranscriptionResult(string.Empty, TimeSpan.Zero);
        }

        long startedAt = Stopwatch.GetTimestamp();

        using OfflineStream stream = _recognizer.CreateStream();
        stream.AcceptWaveform(RecordingFormat.SampleRate, samples);

        _recognizer.Decode(stream);

        return new TranscriptionResult(
            TranscriptCleaner.Clean(stream.Result.Text, _frenchSpacing),
            Stopwatch.GetElapsedTime(startedAt));
    }

    public void Dispose() => _recognizer.Dispose();
}
