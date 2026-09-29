using HexLinux.Diagnostics;
using Xunit;

namespace HexLinux.Tests.Diagnostics;

/// <summary>
/// The layouts on which the virtual keyboard's Ctrl+V would press another
/// shortcut. It presses the key at the QWERTY V position: on Dvorak that is
/// Ctrl+K, on Turkish F Ctrl+C — the dictation is lost, and --doctor must say
/// so. Missing a layout costs a silent failure; flagging one wrongly sends the
/// user to change a setting for nothing.
///
/// <para>The expected answers are xkb's own: level 1 of the key &lt;AB04&gt;,
/// compiled with xkbcomp from xkb-data 2.41 (Ubuntu 24.04) for every layout
/// and variant of evdev.lst. The letter it types is given for each case.</para>
/// </summary>
public class KeyboardLayoutsTests
{
    // --- localectl status -------------------------------------------------------------

    [Fact]
    public void The_layout_of_a_real_localectl_output_is_read()
    {
        // What WSL's Ubuntu 24.04 prints, alignment spaces included.
        string output = "System Locale: LANG=C.UTF-8\n    VC Keymap: (unset)\n   X11 Layout: us\n    X11 Model: pc105\n";

        Assert.Equal(["us"], KeyboardLayouts.FromLocalectl(output));
    }

    [Fact]
    public void Layouts_are_paired_with_their_variants_by_position()
    {
        // "us,fr" with ",bepo": the second layout is Bépo, the first plain.
        string output = "   X11 Layout: us,fr\n    X11 Model: pc105\n  X11 Variant: ,bepo\n";

        Assert.Equal(["us", "fr+bepo"], KeyboardLayouts.FromLocalectl(output));
    }

    [Fact]
    public void Fewer_variants_than_layouts_leave_the_rest_plain()
    {
        string output = "   X11 Layout: fr,us\n  X11 Variant: bepo\n";

        Assert.Equal(["fr+bepo", "us"], KeyboardLayouts.FromLocalectl(output));
    }

    [Fact]
    public void Extra_variants_with_no_layout_are_ignored()
    {
        string output = "   X11 Layout: fr\n  X11 Variant: bepo,dvorak\n";

        Assert.Equal(["fr+bepo"], KeyboardLayouts.FromLocalectl(output));
    }

    [Fact]
    public void No_layout_line_gives_no_layout()
    {
        // A server install, or a localectl without an X11 configuration.
        Assert.Empty(KeyboardLayouts.FromLocalectl("System Locale: LANG=C.UTF-8\n    VC Keymap: (unset)\n"));
    }

    // --- GNOME's input sources ----------------------------------------------------------

    [Fact]
    public void Gnome_input_sources_are_read_in_order()
    {
        // GNOME keeps the layouts in its own settings, not in localectl's.
        Assert.Equal(
            ["fr+bepo", "us"],
            KeyboardLayouts.FromGnomeSources("[('xkb', 'fr+bepo'), ('xkb', 'us')]\n"));
    }

    [Fact]
    public void Input_methods_are_not_layouts()
    {
        // An ibus engine types through the layout beside it.
        Assert.Equal(["us"], KeyboardLayouts.FromGnomeSources("[('xkb', 'us'), ('ibus', 'anthy')]"));
    }

    [Fact]
    public void An_empty_list_of_sources_gives_no_layout()
    {
        // What gsettings really prints when GNOME never stored any source.
        Assert.Empty(KeyboardLayouts.FromGnomeSources("@a(ss) []\n"));
    }

    [Fact]
    public void Spacing_inside_the_tuples_does_not_matter()
    {
        Assert.Equal(["de+neo"], KeyboardLayouts.FromGnomeSources("[( 'xkb' ,'de+neo' )]"));
    }

    [Theory]
    [InlineData("[('xkb', 'us'), ('xkb', 'fr+bepo'")]
    [InlineData("[('xkb', 'us'), ('xkb', 'fr+be")]
    [InlineData("[('xkb', 'us'), ('xkb', '')]")]
    public void Only_complete_tuples_with_a_name_are_read(string output)
    {
        // An output cut short (the process runner caps it and still reports
        // a success), or an empty id, which "gsettings set" accepts: never
        // half a layout name, which the doctor would report as the user's.
        Assert.Equal(["us"], KeyboardLayouts.FromGnomeSources(output));
    }

    // --- Where V is -----------------------------------------------------------------------

    [Theory]
    [InlineData("us+dvorak")] // k
    [InlineData("gb+dvorak")] // k
    [InlineData("de+dvorak")] // k
    [InlineData("fr+dvorak")] // i
    [InlineData("us+dvp")] // k
    [InlineData("pl+dvp")] // k
    [InlineData("us+dvorak-intl")] // k
    [InlineData("us+dvorak-alt-intl")] // k
    [InlineData("us+dvorak-classic")] // k
    [InlineData("us+dvorak-mac")] // k
    [InlineData("us+dvorak-r")] // comma
    [InlineData("gb+dvorakukp")] // k
    [InlineData("pl+dvorak_quotes")] // k
    [InlineData("pl+dvorak_altquotes")] // k
    [InlineData("cz+dvorak-ucw")] // k
    [InlineData("ca+fr-dvorak")] // k
    [InlineData("se+svdvorak")] // k
    [InlineData("se+us_dvorak")] // k
    [InlineData("fr+bepo")] // period
    [InlineData("fr+bepo_latin9")] // period
    [InlineData("fr+bepo_afnor")] // period
    [InlineData("fr+ergol_iso")] // b
    [InlineData("us+colemak_dh_ortho")] // d
    [InlineData("us+workman")] // c
    [InlineData("us+workman-intl")] // c
    [InlineData("de+neo")] // p
    [InlineData("de+bone")] // adiaeresis
    [InlineData("de+koy")] // udiaeresis
    [InlineData("de+adnw")] // comma
    [InlineData("tr+f")] // c
    [InlineData("tr+e")] // u
    [InlineData("tr+ku_f")] // c
    [InlineData("iq+ku_f")] // c
    [InlineData("ua+crh_f")] // c
    [InlineData("pt+nativo")] // b
    [InlineData("br+nativo")] // b
    [InlineData("br+nativo-us")] // b
    [InlineData("pt+nativo-epo")] // b
    [InlineData("lv+ergonomic")] // k
    [InlineData("lt+ratise")] // aogonek
    [InlineData("cz+ucw")] // ccedilla
    [InlineData("in+iipa")] // n
    public void A_layout_that_puts_another_character_at_V_is_reported(string layout)
    {
        // Turkish F was missed by a list that matched parts of names: there
        // Ctrl+V becomes Ctrl+C, copying instead of pasting.
        Assert.Equal([layout], KeyboardLayouts.MovingV([layout]));
    }

    [Theory]
    [InlineData("us")]
    [InlineData("fr")]
    [InlineData("fr+azerty")]
    [InlineData("fr+oss")]
    [InlineData("fr+afnor")]
    [InlineData("fr+ergol")]
    [InlineData("be")]
    [InlineData("de")]
    [InlineData("de+nodeadkeys")]
    [InlineData("ch+fr")]
    [InlineData("gb")]
    [InlineData("us+intl")]
    [InlineData("us+colemak")]
    [InlineData("gb+colemak")]
    [InlineData("us+colemak_dh")]
    [InlineData("us+colemak_dh_wide")]
    [InlineData("us+colemak_dh_iso")]
    [InlineData("us+colemak_dh_wide_iso")]
    [InlineData("gb+colemak_dh")]
    [InlineData("no+colemak_dh")]
    [InlineData("us+dvorak-l")]
    [InlineData("ph+capewell-dvorak")]
    [InlineData("de+neo_qwertz")]
    [InlineData("de+neo_qwerty")]
    [InlineData("us+norman")]
    [InlineData("tr")]
    [InlineData("tr+alt")]
    [InlineData("tr+ku")]
    [InlineData("ua+crh")]
    [InlineData("pt")]
    [InlineData("br")]
    [InlineData("lv")]
    [InlineData("cz+qwerty")]
    [InlineData("in+eng")]
    public void A_layout_that_keeps_V_in_place_is_not_reported(string layout)
    {
        // All of these type v at V. Colemak-DH's row-staggered forms keep V
        // (only the ortholinear one moves it); "neo_qwertz", "dvorak-l" and
        // "capewell-dvorak" only contain the name of a family that moves it.
        Assert.Empty(KeyboardLayouts.MovingV([layout]));
    }

    [Theory]
    [InlineData("ge+ergonomic")] // Georgian_ghan
    [InlineData("ru")] // Cyrillic_em
    [InlineData("ru+phonetic_dvorak")] // Cyrillic_ka
    [InlineData("gr")] // Greek_omega
    public void Layouts_that_type_no_latin_letters_are_not_judged(string layout)
    {
        // "ergonomic" moves V on the Latvian layout only: the Georgian
        // variant of the same name is not taken for it.
        Assert.Empty(KeyboardLayouts.MovingV([layout]));
    }

    [Fact]
    public void Only_the_moving_layouts_are_kept_once_each_in_their_order()
    {
        // The doctor reads localectl and GNOME both: the same layout often
        // comes from each.
        Assert.Equal(
            ["us+dvorak", "fr+bepo"],
            KeyboardLayouts.MovingV(["us", "us+dvorak", "fr+bepo", "fr", "US+DVORAK", "fr+bepo"]));
    }

    [Fact]
    public void The_doctors_path_from_gsettings_to_the_warning()
    {
        IReadOnlyList<string> layouts = KeyboardLayouts.FromGnomeSources("[('xkb', 'fr'), ('xkb', 'tr+f')]");

        Assert.Equal(["tr+f"], KeyboardLayouts.MovingV(layouts));
    }

    [Fact]
    public void Reading_nothing_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => KeyboardLayouts.FromLocalectl(null!));
        Assert.Throws<ArgumentNullException>(() => KeyboardLayouts.FromGnomeSources(null!));
        Assert.Throws<ArgumentNullException>(() => KeyboardLayouts.MovingV(null!));
    }
}
