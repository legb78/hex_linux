using HexLinux.Output;
using Xunit;

namespace HexLinux.Tests.Output;

/// <summary>
/// Which format of the user's clipboard is saved before a paste and put back
/// after it. Picking the wrong one loses the copy: an image restored as its
/// file name, rich text restored as nothing. Both vocabularies are fed here,
/// the MIME types of <c>wl-paste --list-types</c> and the atoms of
/// <c>xclip -o -target TARGETS</c>, as the tools really print them.
/// </summary>
public class ClipboardFormatsTests
{
    [Fact]
    public void Utf8_text_is_preferred_over_every_other_shape_on_Wayland()
    {
        // What wl-copy offers for a copied line of text, and what a browser
        // offers for a selection of a page (HTML and text).
        string[] offered = ["text/html", "image/png", "text/plain", "text/plain;charset=utf-8", "TEXT", "STRING", "UTF8_STRING"];

        Assert.Equal("text/plain;charset=utf-8", ClipboardFormats.Preferred(offered));
    }

    [Fact]
    public void Utf8_text_is_preferred_under_X11()
    {
        // A typical xclip TARGETS answer from a GTK application.
        string[] offered = ["TIMESTAMP", "TARGETS", "MULTIPLE", "SAVE_TARGETS", "text/html", "STRING", "TEXT", "UTF8_STRING"];

        Assert.Equal("UTF8_STRING", ClipboardFormats.Preferred(offered));
    }

    [Theory]
    [InlineData("text/plain", "STRING", "text/plain")]
    [InlineData("STRING", "TEXT", "STRING")]
    [InlineData("TEXT", "image/png", "TEXT")]
    public void Text_formats_rank_from_the_most_to_the_least_precise(string better, string worse, string expected)
    {
        Assert.Equal(expected, ClipboardFormats.Preferred([worse, better]));
    }

    [Fact]
    public void An_image_is_kept_when_there_is_no_text()
    {
        // A screenshot copied to the clipboard: losing it to a dictation is
        // exactly what the snapshot exists to prevent.
        Assert.Equal("image/png", ClipboardFormats.Preferred(["TARGETS", "image/jpeg", "image/png", "text/uri-list"]));
    }

    [Fact]
    public void A_file_list_is_kept_when_there_is_neither_text_nor_a_png()
    {
        // Files copied in a file manager, to be pasted into another folder.
        Assert.Equal("text/uri-list", ClipboardFormats.Preferred(["x-special/gnome-copied-files", "text/uri-list"]));
    }

    [Fact]
    public void Any_other_MIME_type_is_better_restored_than_lost()
    {
        // An application's own format: a LibreOffice range, a GIMP layer.
        Assert.Equal(
            "application/x-openoffice-embed-source-xml",
            ClipboardFormats.Preferred(["TARGETS", "application/x-openoffice-embed-source-xml", "application/x-other"]));
    }

    [Fact]
    public void Names_are_matched_whatever_their_case_and_returned_as_offered()
    {
        // The name is passed back to the tool to read the content: it must be
        // the one the clipboard owner used, not ours.
        Assert.Equal("Text/Plain;Charset=UTF-8", ClipboardFormats.Preferred(["Text/Plain;Charset=UTF-8"]));
    }

    [Fact]
    public void Surrounding_spaces_are_ignored()
    {
        Assert.Equal("image/png", ClipboardFormats.Preferred(["  image/png  "]));
    }

    public static TheoryData<string[]> NothingWorthSaving => new()
    {
        Array.Empty<string>(),
        new[] { "TARGETS", "MULTIPLE", "TIMESTAMP", "SAVE_TARGETS", "DELETE", "INCR" },
        new[] { "", "   " },

        // Server-side resources, not content: their ids mean nothing once
        // the owner is gone.
        new[] { "PIXMAP", "BITMAP" },
    };

    [Theory]
    [MemberData(nameof(NothingWorthSaving))]
    public void Nothing_worth_saving_gives_null(string[] offered)
    {
        // An empty clipboard, or only X11 bookkeeping and atoms that name no
        // content HexLinux can put back: the clipboard is cleared after the
        // paste instead.
        Assert.Null(ClipboardFormats.Preferred(offered));
    }

    [Fact]
    public void Bookkeeping_targets_are_never_taken_for_content_even_with_a_slash_nearby()
    {
        Assert.Equal("text/html", ClipboardFormats.Preferred(["TARGETS", "INCR", "text/html"]));
    }

    [Fact]
    public void A_missing_list_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ClipboardFormats.Preferred(null!));
        Assert.Throws<ArgumentNullException>(() => ClipboardFormats.ParseList(null!));
    }

    // --- Parsing the tools' output --------------------------------------------------

    [Fact]
    public void The_list_is_one_format_per_line()
    {
        Assert.Equal(
            ["text/plain;charset=utf-8", "text/plain", "UTF8_STRING"],
            ClipboardFormats.ParseList("text/plain;charset=utf-8\ntext/plain\nUTF8_STRING\n"));
    }

    [Fact]
    public void Blank_lines_and_carriage_returns_are_dropped()
    {
        Assert.Equal(["TARGETS", "UTF8_STRING"], ClipboardFormats.ParseList("TARGETS\r\n\r\n  UTF8_STRING  \r\n"));
    }

    [Fact]
    public void An_empty_output_is_an_empty_list()
    {
        Assert.Empty(ClipboardFormats.ParseList(string.Empty));
    }

    [Fact]
    public void A_list_as_printed_feeds_the_choice()
    {
        // The chain Clipboard.Capture runs: list, parse, pick.
        IReadOnlyList<string> formats = ClipboardFormats.ParseList("TIMESTAMP\nTARGETS\nimage/png\ntext/uri-list\n");

        Assert.Equal("image/png", ClipboardFormats.Preferred(formats));
    }

    [Fact]
    public void The_snapshot_cap_is_32_MB()
    {
        // Above it the clipboard is cleared rather than held in memory for
        // every dictation — the log says so.
        Assert.Equal(32 * 1024 * 1024, ClipboardFormats.MaxSnapshotBytes);
    }
}
