using System.Globalization;
using System.Text.RegularExpressions;

namespace HexLinux.Transcription;

/// <summary>What a segment changes in the document.</summary>
/// <param name="Erase">Characters to delete first, backwards from the caret, one Backspace each.</param>
/// <param name="Text">Text to insert after that.</param>
public readonly record struct SegmentInsertion(int Erase, string Text);

/// <summary>
/// Turns the segments of one dictation into what goes into the document: the
/// edits the user spoke, and the stitching across pauses. Ported from HexWin;
/// what changed is said at the end.
///
/// <para><b>Spoken edits.</b> Hesitations are dropped and "efface ça" removes
/// the sentence before it, see <see cref="SpokenEdits"/>. When a dictation is
/// inserted sentence by sentence, that sentence is often already typed: the
/// user says it, pauses, then says "efface ça". The joiner keeps the text it
/// inserted during the dictation, and answers with the number of characters
/// to erase. Nothing typed before the dictation is ever touched.</para>
///
/// <para><b>Stitching.</b> The engine transcribes each segment on its own and
/// closes every one with a full stop and opens it with a capital: "Je voudrais
/// parler." then "Avec le client.". Once inserted, that full stop could only
/// be taken back by erasing. So it is <b>held</b>: each segment goes in
/// without its final full stop, and the next one decides. If it opens with a
/// word that carries a sentence on — "avec", "and", "que"... — the full stop
/// is dropped and that word lowered; otherwise the full stop goes in first.
/// The last segment keeps its punctuation.</para>
///
/// <para>Only words that almost never open a sentence count. Articles are left
/// out: "Le train était en retard." starts a sentence far more often than it
/// continues one. Question and exclamation marks are never held either: a
/// pause after them does end the sentence.</para>
///
/// <para><b>What differs from HexWin, and why.</b> Two things, both so that a
/// Backspace can never reach text the dictation did not type:</para>
/// <list type="bullet">
/// <item>The erase is counted in characters as the user sees them (grapheme
/// clusters), not in UTF-16 units. A Backspace removes one such character,
/// or only part of it in applications that erase a decomposed accent on its
/// own: this count can at worst leave something behind, never delete one
/// character more than the dictation wrote — as a surrogate pair counted
/// twice would.</item>
/// <item><see cref="Invalidate"/>: an insertion that did not arrive, or an
/// erase that cannot be sent, leaves the document different from what the
/// joiner believes. From then on it erases nothing: the commands are still
/// taken out of the text, the sentences they aim at stay where they are, and
/// what follows is joined after them.</item>
/// </list>
///
/// <para><see cref="PendingErase"/> tells, before a segment is handed over,
/// whether it will ask for Backspaces: the daemon must then wait for the
/// modifiers to be let go — with Ctrl held, a Backspace erases a word — and,
/// if they are not, invalidate first.</para>
///
/// <para>Pure logic, one instance per dictation.</para>
/// </summary>
public sealed partial class SegmentJoiner
{
    /// <summary>What this dictation has put in the document so far.</summary>
    private string _document = string.Empty;

    private string _held = string.Empty;

    private bool _trusted = true;

    /// <summary>
    /// False once <see cref="Invalidate"/> was called: erase commands are then
    /// still taken out of the text, but no Backspace is asked for.
    /// </summary>
    public bool CanErase => _trusted;

    /// <summary>
    /// The Backspaces <see cref="Next"/> would ask for with
    /// <paramref name="text"/>, without changing anything.
    /// </summary>
    public int PendingErase(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int earlier = SpokenEdits.Apply(text).EarlierErasures;

        return _trusted && earlier > 0 ? Length(_document[Kept(_document, earlier).Length..]) : 0;
    }

    /// <param name="text">The cleaned transcription of the segment.</param>
    /// <param name="isLast">
    /// No segment will follow: its full stop is inserted rather than held.
    /// </param>
    public SegmentInsertion Next(string text, bool isLast)
    {
        ArgumentNullException.ThrowIfNull(text);

        (string edited, int earlierErasures) = SpokenEdits.Apply(text);
        int erase = EraseSentences(earlierErasures);

        if (!SpokenEdits.ContainsMeaning().IsMatch(edited))
        {
            return new SegmentInsertion(erase, string.Empty);
        }

        string joined = _document.Length == 0 ? edited : Join(edited);
        _held = string.Empty;

        if (!isLast && joined.EndsWith('.') && !joined.EndsWith("..", StringComparison.Ordinal))
        {
            _held = ".";
            joined = joined[..^1];
        }

        _document += joined;

        return new SegmentInsertion(erase, joined);
    }

    /// <summary>
    /// The punctuation still held once the dictation is over — when the last
    /// pause was followed by no speech at all. Empty if there is none.
    /// </summary>
    public string Finish()
    {
        string held = _held;
        _document += held;
        _held = string.Empty;

        return held;
    }

    /// <summary>
    /// What was inserted is no longer known for sure, or may not be erased:
    /// an insertion failed or was refused (session locked), or a modifier
    /// stays held. Erasing by count would then take the wrong characters —
    /// possibly ones typed before the dictation, or whole words — so no
    /// Backspace is asked for any more in this dictation.
    /// </summary>
    public void Invalidate() => _trusted = false;

    /// <summary>
    /// Takes the last sentences back out of what this dictation inserted, and
    /// returns how many characters that removes. A held full stop was never
    /// inserted: it simply goes with its sentence. Once invalidated, nothing
    /// is taken out: the sentences stay, their full stop still held.
    /// </summary>
    private int EraseSentences(int count)
    {
        if (count == 0 || !_trusted)
        {
            return 0;
        }

        string kept = Kept(_document, count);
        string erased = _document[kept.Length..];
        _document = kept;
        _held = string.Empty;

        return Length(erased);
    }

    /// <summary><paramref name="document"/> without its last <paramref name="count"/> sentences.</summary>
    private static string Kept(string document, int count)
    {
        string kept = document;

        for (int i = 0; i < count && kept.Length > 0; i++)
        {
            kept = SpokenEdits.WithoutLastSentence(kept);
        }

        return kept;
    }

    /// <summary>Characters as the user sees them, one Backspace each.</summary>
    private static int Length(string text) => new StringInfo(text).LengthInTextElements;

    private string Join(string text)
    {
        if (!_held.Equals(".", StringComparison.Ordinal) || !ContinuesSentence(text))
        {
            return _held + " " + text;
        }

        return " " + char.ToLowerInvariant(text[0]) + text[1..];
    }

    private static bool ContinuesSentence(string text)
    {
        Match first = FirstWord().Match(text);

        return first.Success && ContinuationWords.Contains(first.Value.ToLowerInvariant());
    }

    /// <summary>
    /// Data, matched against what the engine writes: not to be translated.
    /// The elided "qu'" and "d'" are left out on purpose: "Qu'est-ce que" and
    /// "D'accord" open sentences all the time.
    /// </summary>
    private static readonly HashSet<string> ContinuationWords =
    [
        "et", "ou", "donc", "car", "que", "qui", "dont", "de", "du", "des",
        "à", "au", "aux", "pour", "par", "avec", "sans", "sur", "sous", "dans",
        "chez", "vers", "entre", "parce", "puisque", "lorsque", "afin",
        "and", "or", "because", "that", "which", "whom", "whose", "of", "to",
        "for", "with", "without", "from", "into", "than", "about", "by",
    ];

    [GeneratedRegex(@"^\p{L}+")]
    private static partial Regex FirstWord();
}
