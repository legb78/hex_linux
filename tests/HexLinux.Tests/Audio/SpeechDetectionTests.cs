using HexLinux.Audio;
using Xunit;

namespace HexLinux.Tests.Audio;

/// <summary>
/// The detector pool and the Silero model checks. Two recordings can overlap
/// for the length of one read on Linux — the one being abandoned still reads
/// its last block while the next has started — and Silero's native object
/// must never be fed by both.
/// </summary>
public class SpeechDetectionTests
{
    private sealed class CountingDetector : ISpeechDetector, IDisposable
    {
        public int Resets { get; private set; }

        public bool Disposed { get; private set; }

        public bool IsSpeech(ReadOnlySpan<byte> pcm) => true;

        public void Reset() => Resets++;

        public void Dispose() => Disposed = true;
    }

    // --- DetectorPool -----------------------------------------------------------------

    [Fact]
    public void The_first_detector_made_at_start_serves_the_first_recording()
    {
        // Loading Silero takes a moment: the daemon makes one when it starts,
        // to find a broken model then rather than mid-dictation.
        var first = new CountingDetector();
        int made = 0;
        using var pool = new DetectorPool(() => { made++; return new CountingDetector(); }, first);

        Assert.Same(first, pool.Rent());
        Assert.Equal(0, made);
    }

    [Fact]
    public void A_detector_given_back_is_reset_and_serves_the_next_recording()
    {
        // The next dictation must not start with the last one's state.
        var first = new CountingDetector();
        using var pool = new DetectorPool(() => new CountingDetector(), first);

        pool.Return(pool.Rent());

        Assert.Equal(1, first.Resets);
        Assert.Same(first, pool.Rent());
    }

    [Fact]
    public void Two_overlapping_recordings_never_share_a_detector()
    {
        // The abandoned recording still holds its detector: the new one gets
        // its own, and the extra one is disposed of when given back.
        using var pool = new DetectorPool(() => new CountingDetector(), new CountingDetector());

        var old = (CountingDetector)pool.Rent();
        var fresh = (CountingDetector)pool.Rent();

        Assert.NotSame(old, fresh);

        pool.Return(old);
        pool.Return(fresh);

        Assert.False(old.Disposed);
        Assert.True(fresh.Disposed);
        Assert.Same(old, pool.Rent());
    }

    [Fact]
    public void Disposing_the_pool_disposes_the_kept_detector_and_any_given_back_later()
    {
        // The daemon stops while a recording is still being abandoned: its
        // detector goes when that recording ends, not before.
        var pool = new DetectorPool(() => new CountingDetector(), new CountingDetector());
        var rented = (CountingDetector)pool.Rent();
        var kept = new CountingDetector();
        pool.Return(kept);

        pool.Dispose();
        pool.Dispose();

        Assert.True(kept.Disposed);
        Assert.False(rented.Disposed);
        pool.Return(rented);
        Assert.True(rented.Disposed);
        Assert.Throws<ObjectDisposedException>(() => pool.Rent());
    }

    [Fact]
    public void The_level_pool_makes_level_detectors()
    {
        using DetectorPool pool = DetectorPool.Level();

        Assert.IsType<LevelSpeechDetector>(pool.Rent());
    }

    [Fact]
    public void A_pool_needs_a_factory_and_a_detector_to_take_back()
    {
        Assert.Throws<ArgumentNullException>(() => new DetectorPool(null!));
        using DetectorPool pool = DetectorPool.Level();
        Assert.Throws<ArgumentNullException>(() => pool.Return(null!));
    }

    // --- SileroModel ------------------------------------------------------------------

    [Fact]
    public void The_detector_sits_next_to_the_engine_folder()
    {
        // Where scripts/get-model.sh puts it, as HexWin's get-model.ps1 does.
        Assert.Equal(
            "/home/ana/.local/share/hexlinux/models/silero_vad.onnx",
            SileroModel.PathFor("/home/ana/.local/share/hexlinux/models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"));
    }

    [Fact]
    public void A_trailing_slash_does_not_move_it_inside_the_engine_folder()
    {
        Assert.Equal("/opt/models/silero_vad.onnx", SileroModel.PathFor("/opt/models/parakeet/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void An_empty_model_folder_is_refused(string folder)
    {
        Assert.Throws<ArgumentException>(() => SileroModel.PathFor(folder));
    }

    [Fact]
    public void An_absent_file_says_so()
    {
        // The daemon then finds the pauses by the sound level, and logs why.
        Assert.Equal("absent", SileroModel.Problem(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(300_000)]
    [InlineData(700_000)]
    public void A_file_of_another_size_is_refused_before_the_native_library_sees_it(int size)
    {
        // Measured: a truncated or foreign file makes ONNX Runtime throw a
        // C++ exception nobody catches, and the daemon aborts at every start.
        Assert.Equal(
            $"not the file get-model.sh installs ({size} bytes, 643854 expected)",
            SileroModel.Problem(new byte[size]));
    }

    [Fact]
    public void A_file_of_the_right_size_but_other_content_is_refused()
    {
        // A copy damaged in place keeps its size: only the hash tells.
        Assert.Equal(
            "not the file get-model.sh installs (its SHA-256 differs)",
            SileroModel.Problem(new byte[SileroModel.ExpectedBytes]));
    }

    [Fact]
    public void The_daemon_checks_the_very_values_get_model_pins()
    {
        // The script downloads and checks the file, the daemon checks it again
        // before loading it: if the two disagreed, a fresh install would be
        // refused, or a file the script never checked accepted.
        string script = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "get-model.sh"));

        Assert.Contains($"\nvad_size={SileroModel.ExpectedBytes}\n", script, StringComparison.Ordinal);
        Assert.Contains($"\nvad_sha256={SileroModel.ExpectedSha256}\n", script, StringComparison.Ordinal);
        Assert.Contains($"\nvad_name=\"{SileroModel.FileName}\"\n", script, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HexLinux.sln")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("The tests do not run from a clone of the repository.");
    }
}
