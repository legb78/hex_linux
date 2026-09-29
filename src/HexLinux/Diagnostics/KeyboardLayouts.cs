using System.Text.RegularExpressions;

namespace HexLinux.Diagnostics;

/// <summary>
/// Finds keyboard layouts on which the key at the QWERTY "V" position types
/// another letter.
///
/// <para>It matters for one sender only: the virtual keyboard presses
/// physical keys, so its Ctrl+V is Ctrl+<i>whatever that position types</i>.
/// On Dvorak it is Ctrl+K — the browser's search bar — and the dictation is
/// lost. xdotool and wtype work by keysym and are not affected; neither is
/// Shift+Insert, the key Insert being the same everywhere.</para>
///
/// <para>A known list, not an exhaustive one: the families below are those
/// whose V moves (Dvorak and its programmer variant, Bépo, Colemak-DH,
/// Workman, and the German Neo family). AZERTY, QWERTZ and plain Colemak keep
/// V in place. Pure: the outputs of <c>localectl status</c> and of
/// <c>gsettings get org.gnome.desktop.input-sources sources</c> are passed
/// in.</para>
/// </summary>
public static partial class KeyboardLayouts
{
    private static readonly string[] MovedVariants = ["dvorak", "dvp", "bepo", "colemak_dh", "workman", "neo", "bone", "koy", "adnw"];

    /// <summary>
    /// Layouts named by <c>localectl status</c>: "X11 Layout: us,fr" paired
    /// with "X11 Variant: ,bepo" gives "us" and "fr+bepo".
    /// </summary>
    public static IReadOnlyList<string> FromLocalectl(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        string[] layouts = [];
        string[] variants = [];

        foreach (string line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("X11 Layout:", StringComparison.Ordinal))
            {
                layouts = line["X11 Layout:".Length..].Split(',', StringSplitOptions.TrimEntries);
            }
            else if (line.StartsWith("X11 Variant:", StringComparison.Ordinal))
            {
                variants = line["X11 Variant:".Length..].Split(',', StringSplitOptions.TrimEntries);
            }
        }

        return
        [
            .. layouts
                .Select((layout, index) => index < variants.Length && variants[index].Length > 0
                    ? layout + "+" + variants[index]
                    : layout)
                .Where(layout => layout.Length > 0),
        ];
    }

    /// <summary>
    /// Layouts named by GNOME's input sources: "[('xkb', 'fr+bepo'), ('xkb', 'us')]".
    /// Input methods (<c>'ibus'</c>) are not layouts and are skipped.
    /// </summary>
    public static IReadOnlyList<string> FromGnomeSources(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return [.. XkbSource().Matches(output).Select(match => match.Groups[1].Value)];
    }

    /// <summary>The layouts, among <paramref name="layouts"/>, where V is elsewhere.</summary>
    public static IReadOnlyList<string> MovingV(IEnumerable<string> layouts)
    {
        ArgumentNullException.ThrowIfNull(layouts);

        return
        [
            .. layouts
                .Where(layout => MovedVariants.Any(variant => layout.Contains(variant, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    [GeneratedRegex(@"\(\s*'xkb'\s*,\s*'([^']+)'\s*\)")]
    private static partial Regex XkbSource();
}
