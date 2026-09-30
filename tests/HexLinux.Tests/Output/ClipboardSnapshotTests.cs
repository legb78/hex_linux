using HexLinux.Output;
using Xunit;

namespace HexLinux.Tests.Output;

/// <summary>
/// What decides, after a paste, between putting the user's clipboard back and
/// clearing it. Only a snapshot with both a format and its bytes can be
/// restored; anything less must lead to a clear, so that the dictation never
/// stays in the clipboard where any program could read it later.
/// </summary>
public class ClipboardSnapshotTests
{
    [Fact]
    public void Nothing_has_no_content_so_the_clipboard_is_cleared()
    {
        // An empty clipboard, an unreadable format, or content past the cap.
        Assert.False(ClipboardSnapshot.Nothing.HasContent);
        Assert.Null(ClipboardSnapshot.Nothing.Format);
        Assert.Null(ClipboardSnapshot.Nothing.Data);
    }

    [Fact]
    public void A_format_with_its_bytes_can_be_restored()
    {
        var snapshot = new ClipboardSnapshot("image/png", [0x89, 0x50, 0x4E, 0x47]);

        Assert.True(snapshot.HasContent);
    }

    [Theory]
    [InlineData("UTF8_STRING", false)]
    [InlineData(null, true)]
    public void Half_a_snapshot_is_not_restorable(string? format, bool withData)
    {
        // A format without bytes would restore an empty clipboard under a
        // name that promises content; bytes without a format cannot be
        // written back at all.
        var snapshot = new ClipboardSnapshot(format, withData ? [1, 2, 3] : null);

        Assert.False(snapshot.HasContent);
    }
}
