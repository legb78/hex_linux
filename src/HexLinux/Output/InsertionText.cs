using System.Text.RegularExpressions;

namespace HexLinux.Output;

/// <summary>
/// The last pass over a transcription before it reaches another application.
///
/// <para><b>Control characters and bidirectional overrides are removed.</b>
/// What is inserted is typed or pasted into whatever has the focus — a
/// terminal, possibly one running a root shell. The cleaner already folds
/// every line break into a space, so no Enter can be sent; but an escape
/// character or a C1 control would still reach a terminal as the start of a
/// command sequence, and a right-to-left override would make the text read
/// differently from what it is. The pinned model produces none of these; the
/// check is there for the day a model does.</para>
///
/// <para>Pure, and applied to every insertion, whatever the engine.</para>
/// </summary>
public static partial class InsertionText
{
    /// <summary>
    /// What will actually be inserted for segment <paramref name="ordinal"/>
    /// of a dictation (1 for the first). A segment that follows another is
    /// separated from it by a space, as the engine would have put between two
    /// sentences. Empty when nothing printable is left.
    /// </summary>
    public static string ForSegment(string text, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(text);

        string safe = Sanitize(text);

        if (safe.Length == 0)
        {
            return string.Empty;
        }

        return ordinal > 1 ? " " + safe : safe;
    }

    /// <summary>Removes control characters and bidirectional controls.</summary>
    public static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Unsafe().Replace(text, string.Empty).Trim();
    }

    /// <summary>
    /// <c>\p{Cc}</c> is C0, DEL and C1 — escape and the line breaks among
    /// them; U+202A–U+202E are the embeddings and overrides, U+2066–U+2069
    /// the isolates.
    /// </summary>
    [GeneratedRegex(@"[\p{Cc}‪-‮⁦-⁩]")]
    private static partial Regex Unsafe();
}
