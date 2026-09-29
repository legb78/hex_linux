namespace HexLinux.Input;

/// <summary>
/// The shortcut as a person reads it: "Right Ctrl", not "RightCtrl" — and what
/// choosing it costs.
///
/// <para>The names in settings.json are identifiers and stay that way in the
/// file. What the tray tooltip and the diagnostics show is what is printed on
/// the keyboard, with the side spelled out: telling the left Ctrl from the
/// right one is the whole point of a shortcut made of a single
/// modifier.</para>
///
/// <para>English only, like the rest of HexLinux's interface for now; the
/// Windows version, which has a French interface, keeps its labels in its
/// string tables.</para>
/// </summary>
public static class HotkeyText
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = "Ctrl",
        ["LeftCtrl"] = "Left Ctrl",
        ["RightCtrl"] = "Right Ctrl",
        ["Alt"] = "Alt",
        ["LeftAlt"] = "Left Alt",
        ["RightAlt"] = "Right Alt",
        ["Shift"] = "Shift",
        ["LeftShift"] = "Left Shift",
        ["RightShift"] = "Right Shift",
        ["Super"] = "Super",
        ["LeftSuper"] = "Left Super",
        ["RightSuper"] = "Right Super",
    };

    /// <summary>
    /// The keys whose presence during a paste turns Ctrl+V into another
    /// shortcut. Ctrl is not one of them: Ctrl held with Ctrl+V is still
    /// Ctrl+V.
    /// </summary>
    private static readonly HashSet<string> PasteAltering = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shift", "LeftShift", "RightShift", "Alt", "LeftAlt", "RightAlt", "Super", "LeftSuper", "RightSuper",
    };

    /// <summary>One key; a name that is not known is shown as it is.</summary>
    public static string Describe(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        string trimmed = key.Trim();

        return Labels.TryGetValue(trimmed, out string? label) ? label : trimmed;
    }

    /// <summary>The whole shortcut, keys joined the way a keyboard chord is written.</summary>
    public static string Describe(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        return string.Join(" + ", keys.Select(Describe));
    }

    /// <summary>
    /// What the shortcut costs the user, in plain words; empty when it costs
    /// nothing worth saying.
    ///
    /// <para>On Windows the keys of the shortcut were withheld from the
    /// desktop, so a lone modifier cost nothing. On Linux the desktop still
    /// sees every press (see <c>ChordDetector</c>): a lone Super opens the
    /// overview when released, a lone Alt reaches the menu bar, and a key
    /// also used for capitals or shortcuts starts a dictation whenever it is
    /// held a moment before the next key. Right Ctrl, the default, is the one
    /// modifier with none of these costs.</para>
    /// </summary>
    /// <param name="keys">The shortcut, as normalised by the settings.</param>
    /// <param name="segmentation">Whether segments are pasted while the shortcut is still held.</param>
    public static IReadOnlyList<string> Caveats(IReadOnlyList<string> keys, bool segmentation)
    {
        ArgumentNullException.ThrowIfNull(keys);

        List<string> caveats = [];

        if (keys.Count == 1)
        {
            string key = keys[0].Trim();
            string label = Describe(key);

            string? caveat = key.ToUpperInvariant() switch
            {
                "SHIFT" or "LEFTSHIFT" or "RIGHTSHIFT" =>
                    $"{label} is also used for capitals: holding it a moment before a letter starts a dictation, which the letter then cancels.",
                "CTRL" or "LEFTCTRL" =>
                    $"{label} is also used for shortcuts such as Ctrl+C: holding it a moment before the letter starts a dictation, which the letter then cancels.",
                "ALT" or "LEFTALT" =>
                    $"{label} still reaches the desktop: pressed and released alone, it moves the focus to the menu bar of many applications.",
                "RIGHTALT" =>
                    "Right Alt is AltGr on many layouts (French, German…): characters such as @ or € are typed with it, and each one cancels the dictation.",
                "SUPER" or "LEFTSUPER" or "RIGHTSUPER" =>
                    $"{label} still reaches the desktop: released alone, it opens the overview or the application menu on most desktops.",
                _ => null,
            };

            if (caveat is not null)
            {
                caveats.Add(caveat);
            }
        }

        if (segmentation && keys.Any(PasteAltering.Contains))
        {
            caveats.Add(
                "With \"segmentation\" on, segments are pasted while the shortcut is still held, and Shift, Alt or "
                + "Super held during a paste turn Ctrl+V into another shortcut. Turn segmentation off or use Right Ctrl.");
        }

        return caveats;
    }
}
