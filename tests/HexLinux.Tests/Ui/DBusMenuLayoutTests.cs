using HexLinux.Daemon;
using HexLinux.Ui;
using Xunit;

namespace HexLinux.Tests.Ui;

/// <summary>
/// Every request a dbusmenu host makes, answered without a bus. The rules
/// tested come from libdbusmenu's <c>dbus-menu.xml</c> and its server.
/// </summary>
public class DBusMenuLayoutTests
{
    private static IReadOnlyList<TrayMenuItem> Menu(
        DictationState state = DictationState.Idle,
        bool tones = true,
        bool autoStart = false) =>
        TrayMenu.Build(new StatusSnapshot(state, "Right Ctrl", tones, autoStart, "/s/settings.json", "/s/logs"));

    private static Dictionary<string, MenuValue> AsDictionary(IReadOnlyList<KeyValuePair<string, MenuValue>> properties) =>
        properties.ToDictionary(property => property.Key, property => property.Value);

    // --- Properties of one entry ------------------------------------------------------

    [Fact]
    public void A_separator_says_only_its_type()
    {
        // "A property should only be returned if its value is not the default."
        IReadOnlyList<KeyValuePair<string, MenuValue>> properties = DBusMenuLayout.PropertiesOf(TrayMenuItem.Separator(2));

        Assert.Equal([new("type", MenuValue.Of("separator"))], properties);
    }

    [Fact]
    public void An_enabled_entry_does_not_say_it_is_enabled()
    {
        // enabled defaults to true.
        IReadOnlyList<KeyValuePair<string, MenuValue>> properties =
            DBusMenuLayout.PropertiesOf(TrayMenuItem.Action(9, MenuCommand.Quit, "Quit", enabled: true));

        Assert.Equal([new("label", MenuValue.Of("Quit"))], properties);
    }

    [Fact]
    public void A_disabled_entry_says_so()
    {
        Dictionary<string, MenuValue> properties =
            AsDictionary(DBusMenuLayout.PropertiesOf(TrayMenuItem.Action(1, MenuCommand.ToggleDictation, "Dictate now", enabled: false)));

        Assert.Equal(MenuValue.Of(false), properties["enabled"]);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void A_checkbox_always_states_its_mark(bool isChecked, int state)
    {
        // toggle-state defaults to -1, "indeterminate": an unchecked box that
        // left the property out would be drawn neither checked nor unchecked.
        Dictionary<string, MenuValue> properties =
            AsDictionary(DBusMenuLayout.PropertiesOf(TrayMenuItem.Checkbox(6, MenuCommand.ToggleTones, "Play tones", isChecked)));

        Assert.Equal(MenuValue.Of("checkmark"), properties["toggle-type"]);
        Assert.Equal(MenuValue.Of(state), properties["toggle-state"]);
    }

    [Fact]
    public void Underscores_in_labels_are_doubled()
    {
        // A single underscore marks the access key and is not displayed: the
        // label "open_me" would show as "openme".
        Assert.Equal("open__me", DBusMenuLayout.EscapeLabel("open_me"));
        Assert.Equal("Open settings file", DBusMenuLayout.EscapeLabel("Open settings file"));
    }

    [Fact]
    public void Labels_are_escaped_on_the_wire()
    {
        IReadOnlyList<KeyValuePair<string, MenuValue>> properties =
            DBusMenuLayout.PropertiesOf(TrayMenuItem.Action(3, MenuCommand.OpenSettingsFile, "a_b", enabled: true));

        Assert.Equal(MenuValue.Of("a__b"), AsDictionary(properties)["label"]);
    }

    // --- GetLayout -------------------------------------------------------------------

    [Fact]
    public void The_whole_layout_hangs_from_the_root()
    {
        // GetLayout(0, -1, []): what every host asks first.
        DBusMenuNode? root = DBusMenuLayout.Layout(Menu(), parentId: 0, recursionDepth: -1, []);

        Assert.NotNull(root);
        Assert.Equal(0, root.Id);
        Assert.Equal([new("children-display", MenuValue.Of("submenu"))], root.Properties);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], root.Children.Select(child => child.Id));
        Assert.All(root.Children, child => Assert.Empty(child.Children));
    }

    [Fact]
    public void A_depth_of_zero_returns_the_parent_alone()
    {
        DBusMenuNode? root = DBusMenuLayout.Layout(Menu(), parentId: 0, recursionDepth: 0, []);

        Assert.NotNull(root);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void A_depth_of_one_already_reaches_every_entry()
    {
        // The menu is flat: one level holds everything.
        DBusMenuNode? root = DBusMenuLayout.Layout(Menu(), parentId: 0, recursionDepth: 1, []);

        Assert.NotNull(root);
        Assert.Equal(9, root.Children.Count);
    }

    [Fact]
    public void An_entry_can_be_asked_for_alone()
    {
        DBusMenuNode? tones = DBusMenuLayout.Layout(Menu(), parentId: TrayMenu.TonesId, recursionDepth: -1, []);

        Assert.NotNull(tones);
        Assert.Equal(TrayMenu.TonesId, tones.Id);
        Assert.Empty(tones.Children);
        Assert.Equal(MenuValue.Of("Play tones"), AsDictionary(tones.Properties)["label"]);
    }

    [Fact]
    public void An_unknown_parent_has_no_layout()
    {
        // Answered with an error by the D-Bus layer, as libdbusmenu does.
        Assert.Null(DBusMenuLayout.Layout(Menu(), parentId: 42, recursionDepth: -1, []));
    }

    [Fact]
    public void The_property_names_asked_for_filter_the_answer()
    {
        // Hosts often ask for a fixed list of names; nothing else is sent.
        DBusMenuNode? root = DBusMenuLayout.Layout(Menu(), parentId: 0, recursionDepth: -1, ["label"]);

        Assert.NotNull(root);
        Assert.Empty(root.Properties);
        Assert.All(root.Children, child => Assert.All(child.Properties, property => Assert.Equal("label", property.Key)));
        Assert.Empty(root.Children.Single(child => child.Id == 2).Properties);
    }

    // --- GetGroupProperties and GetProperty ----------------------------------------------

    [Fact]
    public void An_empty_list_of_ids_means_every_entry()
    {
        // dbus-menu.xml: "If the list is empty, all menu items should be sent."
        IReadOnlyList<DBusMenuItemProperties> all = DBusMenuLayout.GroupProperties(Menu(), [], []);

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], all.Select(item => item.Id));
    }

    [Fact]
    public void Unknown_and_repeated_ids_are_skipped()
    {
        IReadOnlyList<DBusMenuItemProperties> some = DBusMenuLayout.GroupProperties(Menu(), [6, 99, 6], ["toggle-state"]);

        DBusMenuItemProperties tones = Assert.Single(some);
        Assert.Equal(6, tones.Id);
        Assert.Equal([new("toggle-state", MenuValue.Of(1))], tones.Properties);
    }

    [Fact]
    public void A_single_property_can_be_read()
    {
        Assert.Equal(MenuValue.Of(0), DBusMenuLayout.Property(Menu(autoStart: false), TrayMenu.AutoStartId, "toggle-state"));
        Assert.Equal(MenuValue.Of("submenu"), DBusMenuLayout.Property(Menu(), 0, "children-display"));
    }

    [Fact]
    public void A_property_left_at_its_default_or_an_unknown_entry_reads_as_nothing()
    {
        Assert.Null(DBusMenuLayout.Property(Menu(), TrayMenu.QuitId, "enabled"));
        Assert.Null(DBusMenuLayout.Property(Menu(), 99, "label"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(-1, false)]
    public void Only_the_root_and_the_entries_exist(int id, bool exists)
    {
        // The id of a click is only ever compared with the menu: anything
        // else a caller sends is refused and triggers nothing.
        Assert.Equal(exists, DBusMenuLayout.Exists(Menu(), id));
    }

    // --- ItemsPropertiesUpdated --------------------------------------------------------

    [Fact]
    public void Nothing_changed_means_nothing_to_send()
    {
        Assert.True(DBusMenuLayout.Changes(Menu(), Menu()).IsEmpty);
    }

    [Fact]
    public void A_switched_tone_updates_one_mark()
    {
        // The host redraws one check mark, not the whole menu.
        DBusMenuUpdate update = DBusMenuLayout.Changes(Menu(tones: true), Menu(tones: false));

        DBusMenuItemProperties changed = Assert.Single(update.Updated);
        Assert.Equal(TrayMenu.TonesId, changed.Id);
        Assert.Equal([new("toggle-state", MenuValue.Of(0))], changed.Properties);
        Assert.Empty(update.Removed);
        Assert.False(update.StructureChanged);
    }

    [Fact]
    public void An_entry_that_gets_disabled_says_so()
    {
        DBusMenuUpdate update = DBusMenuLayout.Changes(Menu(DictationState.Idle), Menu(DictationState.Transcribing));

        DBusMenuItemProperties changed = Assert.Single(update.Updated);
        Assert.Equal(TrayMenu.DictateId, changed.Id);
        Assert.Equal([new("enabled", MenuValue.Of(false))], changed.Properties);
    }

    [Fact]
    public void An_entry_enabled_again_has_the_property_removed()
    {
        // Back to the default, the property is no longer sent: the host must
        // be told to drop its "false", or the entry would stay greyed out.
        DBusMenuUpdate update = DBusMenuLayout.Changes(Menu(DictationState.Transcribing), Menu(DictationState.Idle));

        DBusMenuRemovedProperties removed = Assert.Single(update.Removed);
        Assert.Equal(TrayMenu.DictateId, removed.Id);
        Assert.Equal(["enabled"], removed.Names);
        Assert.Empty(update.Updated);
    }

    [Fact]
    public void Recording_renames_the_dictate_entry()
    {
        DBusMenuUpdate update = DBusMenuLayout.Changes(Menu(DictationState.Idle), Menu(DictationState.Recording));

        DBusMenuItemProperties changed = Assert.Single(update.Updated);
        Assert.Equal([new("label", MenuValue.Of("Finish dictation"))], changed.Properties);
    }

    [Fact]
    public void Entries_that_appear_change_the_structure()
    {
        // Property updates cannot add an entry: the layout must be announced.
        IReadOnlyList<TrayMenuItem> shorter = [.. Menu().Take(3)];

        Assert.True(DBusMenuLayout.Changes(shorter, Menu()).StructureChanged);
    }

    // --- Values ------------------------------------------------------------------------

    [Fact]
    public void Values_print_as_gdbus_prints_them()
    {
        // What the harness compares with gdbus output, and what logs show.
        Assert.Equal("'Quit'", MenuValue.Of("Quit").ToString());
        Assert.Equal("false", MenuValue.Of(false).ToString());
        Assert.Equal("true", MenuValue.Of(true).ToString());
        Assert.Equal("-1", MenuValue.Of(-1).ToString());
    }

    [Fact]
    public void Values_compare_by_kind_and_content()
    {
        // 0 and false are different values on the wire: "i" and "b".
        Assert.NotEqual(MenuValue.Of(0), MenuValue.Of(false));
        Assert.Equal(MenuValue.Of("a"), MenuValue.Of("a"));
        Assert.Equal(MenuValueKind.Number, MenuValue.Of(3).Kind);
        Assert.Equal(3, MenuValue.Of(3).Number);
        Assert.True(MenuValue.Of(true).Flag);
    }
}
