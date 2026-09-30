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
/// <para>A known list, not an exhaustive one, matched on the exact variant
/// name: a part of a name says nothing. Colemak-DH keeps V in place on
/// row-staggered keyboards (its "angle mod") and moves it only in its
/// ortholinear form; "neo" moves it, "neo_qwertz" does not; left-handed Dvorak
/// keeps it. AZERTY, QWERTZ and plain Colemak keep it too. Layouts that type
/// no Latin letters (Cyrillic, Greek…) are not judged here. Pure: the outputs
/// of <c>localectl status</c> and of
/// <c>gsettings get org.gnome.desktop.input-sources sources</c> are passed
/// in.</para>
/// </summary>
public static partial class KeyboardLayouts
{
    /// <summary>
    /// The variants whose key at the QWERTY V position types another Latin
    /// letter or a punctuation mark: by variant name when that holds for every
    /// layout that has the variant, as "layout+variant" when it does not (the
    /// Georgian "ergonomic" types no Latin letter) or when the name is a single
    /// letter. Read from xkb-data 2.41: level 1 of the key &lt;AB04&gt;,
    /// compiled with xkbcomp, for every layout and variant of evdev.lst.
    /// </summary>
    private static readonly HashSet<string> MovedVariants = new(StringComparer.OrdinalIgnoreCase)
    {
        // Dvorak, its programmer variant (dvp) and its national forms.
        "dvorak", "dvorak-intl", "dvorak-alt-intl", "dvorak-classic", "dvorak-mac", "dvorak-r", "dvorakukp",
        "dvorak_quotes", "dvorak_altquotes", "dvorak-ucw", "dvp", "fr-dvorak", "svdvorak", "us_dvorak",

        // Bépo and Ergo-L's ISO form (French), Colemak-DH ortholinear, Workman.
        "bepo", "bepo_latin9", "bepo_afnor", "ergol_iso", "colemak_dh_ortho", "workman", "workman-intl",

        // The German Neo 2 family.
        "neo", "bone", "koy", "adnw",

        // Turkish F and E, and the layouts built on Turkish F.
        "tr+f", "tr+e", "ku_f", "crh_f",

        // Portuguese and Brazilian Nativo, and single layouts elsewhere.
        "nativo", "nativo-us", "nativo-epo", "lv+ergonomic", "ratise", "ucw", "iipa",
    };

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

        return [.. layouts.Where(MovesV).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"fr+bepo" is looked up by its variant, "bepo", then whole. A layout without a variant keeps V.</summary>
    private static bool MovesV(string layout)
    {
        int plus = layout.IndexOf('+', StringComparison.Ordinal);

        return plus > 0 && (MovedVariants.Contains(layout[(plus + 1)..]) || MovedVariants.Contains(layout));
    }

    [GeneratedRegex(@"\(\s*'xkb'\s*,\s*'([^']+)'\s*\)")]
    private static partial Regex XkbSource();
}
