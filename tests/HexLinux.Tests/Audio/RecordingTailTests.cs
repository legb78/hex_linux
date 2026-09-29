using HexLinux.Audio;
using Xunit;

namespace HexLinux.Tests.Audio;

/// <summary>
/// People release the key on the last syllable: the recording must go on a
/// little past the release, and take in what the sound server still holds.
/// </summary>
public class RecordingTailTests
{
    [Fact]
    public void An_unknown_latency_still_gets_the_tail()
    {
        // pa_simple_get_latency failed: the 100 ms past the release are still
        // read, which is what HexWin keeps listening for.
        Assert.Equal(RecordingFormat.BytesFor(TimeSpan.FromMilliseconds(100)), RecordingTail.BytesAfterStop(RecordingTail.UnknownLatency));
    }

    [Fact]
    public void The_server_backlog_is_read_on_top_of_the_tail()
    {
        // 40 ms of audio were still in the server when the key went up: they
        // hold the end of the last word.
        Assert.Equal(RecordingFormat.BytesFor(TimeSpan.FromMilliseconds(140)), RecordingTail.BytesAfterStop(40_000));
    }

    [Fact]
    public void A_zero_latency_is_the_tail_alone()
    {
        Assert.Equal(RecordingFormat.BytesFor(RecordingTail.Tail), RecordingTail.BytesAfterStop(0));
    }

    [Theory]
    [InlineData(500_000UL)]
    [InlineData(2_700_000UL)]
    [InlineData(ulong.MaxValue - 1)]
    public void A_long_backlog_is_capped(ulong latency)
    {
        // A server that reports seconds of backlog must not keep the stop
        // waiting that long: 500 ms at most, HexWin's flush timeout.
        Assert.Equal(RecordingFormat.BytesFor(TimeSpan.FromMilliseconds(600)), RecordingTail.BytesAfterStop(latency));
    }

    [Fact]
    public void The_tail_is_a_whole_number_of_samples()
    {
        // Half a 16-bit sample would shift the rest of the stream by a byte.
        Assert.Equal(0, RecordingTail.BytesAfterStop(12_345) % RecordingFormat.BytesPerSample);
    }

    [Fact]
    public void The_tail_is_HexWins()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(100), RecordingTail.Tail);
        Assert.Equal(TimeSpan.FromMilliseconds(500), RecordingTail.MaximumBacklog);
    }
}
