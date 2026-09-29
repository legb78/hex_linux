using System.Diagnostics.CodeAnalysis;
using SherpaOnnx;

namespace HexLinux.Audio;

/// <summary>
/// Speech detection by Silero VAD, a 630 KB neural network run by sherpa-onnx,
/// the library that already runs the engine. Ported from HexWin.
///
/// <para>It recognises a voice, where the level threshold only measures
/// loudness: a fan, a keyboard or a busy street stay "silence", so the pauses
/// between sentences are found in a noisy room too, and a cough or a click too
/// short to be speech does not reopen a segment.</para>
///
/// <para>Only the detector's verdict is used, not the segments it assembles:
/// cutting stays the job of <see cref="SpeechSegmenter"/>, whose pause is the
/// one the user chose.</para>
///
/// <para>The classes and members used — <c>VadModelConfig</c> and its
/// <c>SileroVad</c> fields, <c>VoiceActivityDetector(config,
/// bufferSizeInSeconds)</c>, <c>AcceptWaveform</c>, <c>IsSpeechDetected</c>,
/// <c>Clear</c>, <c>Reset</c> — were read from the managed assembly of
/// org.k2fsa.sherpa.onnx 1.13.8, and the native functions behind them from the
/// linux-x64 runtime package (<c>nm -D libsherpa-onnx-c-api.so</c>).</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Shell around the native detector, covered by the integration tests; the cutting it feeds is tested with SpeechSegmenter.")]
public sealed class SileroSpeechDetector : ISpeechDetector, IDisposable
{
    private readonly VoiceActivityDetector _detector;

    /// <param name="modelFile">
    /// silero_vad.onnx, already checked by <see cref="SileroModel.Problem"/>:
    /// the native side reports nothing the managed side could catch — a
    /// missing file gives a detector that never hears speech, a damaged one
    /// aborts the process.
    /// </param>
    public SileroSpeechDetector(string modelFile)
    {
        var config = new VadModelConfig();

        config.SileroVad.Model = modelFile;
        config.SileroVad.Threshold = 0.5f;

        // The detector's own pause is kept short: the one that closes a
        // segment is measured by SpeechSegmenter, on top of this one.
        config.SileroVad.MinSilenceDuration = 0.1f;

        // Shorter than this is a click or a cough, not a word.
        config.SileroVad.MinSpeechDuration = 0.2f;

        config.SileroVad.WindowSize = 512;
        config.SampleRate = RecordingFormat.SampleRate;
        config.NumThreads = 1;
        config.Provider = "cpu";

        _detector = new VoiceActivityDetector(config, bufferSizeInSeconds: 30);
    }

    public bool IsSpeech(ReadOnlySpan<byte> pcm)
    {
        _detector.AcceptWaveform(PcmConverter.ToNormalizedSamples(pcm));
        bool detected = _detector.IsSpeechDetected();

        // The detector queues the segments it assembles; nobody reads them,
        // so they are dropped before they pile up.
        _detector.Clear();

        return detected;
    }

    public void Reset() => _detector.Reset();

    public void Dispose() => _detector.Dispose();
}
