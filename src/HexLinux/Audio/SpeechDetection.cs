using System.Security.Cryptography;

namespace HexLinux.Audio;

/// <summary>
/// Tells speech from silence, one block of captured samples at a time.
/// </summary>
public interface ISpeechDetector
{
    bool IsSpeech(ReadOnlySpan<byte> pcm);

    /// <summary>Forgets the previous recording before a new one starts.</summary>
    void Reset();
}

/// <summary>
/// The fallback detector: anything louder than the silence threshold counts
/// as speech. Enough in a quiet room; in a noisy one the noise never falls
/// below the threshold, no pause is ever seen, and the recording stays in one
/// piece.
/// </summary>
public sealed class LevelSpeechDetector : ISpeechDetector
{
    public bool IsSpeech(ReadOnlySpan<byte> pcm) => !AudioLevel.IsSilent(pcm);

    public void Reset()
    {
    }
}

/// <summary>
/// Where the Silero VAD model is looked for, and whether the file found there
/// may be handed to the native library.
///
/// <para>Next to the engine's own folder, in the models folder — where
/// <c>scripts/get-model.sh</c> puts it, as HexWin's <c>get-model.ps1</c>
/// does. Absent, the pauses are found by the sound level, as before the
/// detector existed.</para>
///
/// <para><b>Checked, byte for byte, before it is loaded.</b> Measured with
/// sherpa-onnx 1.13.8 on linux-x64: a missing file only logs "vad is
/// nullptr" at every call, but a file that is not a valid model — truncated,
/// or anything else — makes ONNX Runtime throw a C++ exception nobody
/// catches, and the whole process aborts (SIGABRT). A daemon started at login
/// would then die at every start. So only the very file get-model.sh installs
/// is accepted: its size and SHA-256 are the ones the script pins (a test
/// keeps both in step), and anything else leaves the pauses to the sound
/// level.</para>
/// </summary>
public static class SileroModel
{
    public const string FileName = "silero_vad.onnx";

    /// <summary>Size of the file get-model.sh installs (GitHub API, asr-models release).</summary>
    internal const long ExpectedBytes = 643_854;

    /// <summary>Its SHA-256, the asset digest GitHub publishes, recomputed from a download.</summary>
    internal const string ExpectedSha256 = "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6";

    /// <summary>The detector's file for the engine installed in <paramref name="modelDirectory"/>.</summary>
    public static string PathFor(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);

        string folder = Path.TrimEndingDirectorySeparator(modelDirectory);

        return Path.Combine(Path.GetDirectoryName(folder) ?? folder, FileName);
    }

    /// <summary>
    /// Why the file cannot be used, in words for the log; null when it can.
    /// </summary>
    /// <param name="content">The whole file, or null when there is no such file.</param>
    public static string? Problem(byte[]? content)
    {
        if (content is null)
        {
            return "absent";
        }

        if (content.Length != ExpectedBytes)
        {
            return $"not the file get-model.sh installs ({content.Length} bytes, {ExpectedBytes} expected)";
        }

        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), ExpectedSha256, StringComparison.Ordinal)
            ? null
            : "not the file get-model.sh installs (its SHA-256 differs)";
    }
}
