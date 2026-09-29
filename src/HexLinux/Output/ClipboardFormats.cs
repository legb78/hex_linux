namespace HexLinux.Output;

/// <summary>
/// Picks which of the formats on offer in the clipboard to save before a
/// paste, so that it can be put back afterwards.
///
/// <para>A clipboard owner offers the same content under several names — a
/// copied web page comes as HTML, as plain text, sometimes as an image —
/// and the tools can only read and write one at a time. One is kept: plain
/// text first, then an image, then a file list, which covers nearly every
/// use. The Windows version restores the same three shapes.</para>
///
/// <para>Wayland offers MIME types; X11 offers atoms such as
/// <c>UTF8_STRING</c> alongside MIME types, and bookkeeping targets that are
/// not content at all. Both vocabularies are handled here.</para>
/// </summary>
public static class ClipboardFormats
{
    /// <summary>
    /// Past this size the clipboard is not saved, and so not restored: it is
    /// cleared after the paste instead. A copied image or file list stays far
    /// below it; what exceeds it is an application's own format for something
    /// huge, and holding it in memory for every dictation would cost more than
    /// losing it — which the log then says.
    /// </summary>
    public const int MaxSnapshotBytes = 32 * 1024 * 1024;

    private static readonly string[] Preference =
    [
        "text/plain;charset=utf-8",
        "UTF8_STRING",
        "text/plain",
        "STRING",
        "TEXT",
        "image/png",
        "text/uri-list",
    ];

    /// <summary>X11 targets that describe the selection rather than hold it.</summary>
    private static readonly HashSet<string> Bookkeeping = new(StringComparer.Ordinal)
    {
        "TARGETS",
        "MULTIPLE",
        "TIMESTAMP",
        "SAVE_TARGETS",
        "DELETE",
        "INCR",
    };

    /// <summary>
    /// The format to save, or null when nothing on offer is worth it — an
    /// empty clipboard, or one holding only bookkeeping.
    /// </summary>
    public static string? Preferred(IEnumerable<string> offered)
    {
        ArgumentNullException.ThrowIfNull(offered);

        string[] formats =
        [
            .. offered
                .Select(format => format.Trim())
                .Where(format => format.Length > 0 && !Bookkeeping.Contains(format)),
        ];

        foreach (string wanted in Preference)
        {
            string? match = Array.Find(formats, format => string.Equals(format, wanted, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        // Anything else that names a MIME type: an application's own format
        // is still better restored than lost.
        return Array.Find(formats, format => format.Contains('/', StringComparison.Ordinal));
    }

    /// <summary>
    /// Splits what <c>wl-paste --list-types</c> or
    /// <c>xclip -o -t TARGETS</c> prints: one format per line.
    /// </summary>
    public static IReadOnlyList<string> ParseList(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
