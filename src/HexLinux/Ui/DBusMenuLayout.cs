using System.Globalization;

namespace HexLinux.Ui;

/// <summary>The three kinds of value a dbusmenu property of this menu can hold.</summary>
public enum MenuValueKind
{
    Text,
    Flag,
    Number,
}

/// <summary>
/// The value of one dbusmenu property: a string, a boolean or a 32-bit
/// integer, the only types this menu uses. Compared by value, which is what
/// finding the properties that changed needs.
/// </summary>
public readonly record struct MenuValue
{
    private MenuValue(MenuValueKind kind, string text, bool flag, int number)
    {
        Kind = kind;
        Text = text;
        Flag = flag;
        Number = number;
    }

    public MenuValueKind Kind { get; }

    /// <summary>The string, for <see cref="MenuValueKind.Text"/>; empty otherwise.</summary>
    public string Text { get; }

    /// <summary>The boolean, for <see cref="MenuValueKind.Flag"/>.</summary>
    public bool Flag { get; }

    /// <summary>The integer, for <see cref="MenuValueKind.Number"/>.</summary>
    public int Number { get; }

    public static MenuValue Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new MenuValue(MenuValueKind.Text, text, flag: false, number: 0);
    }

    public static MenuValue Of(bool flag) => new(MenuValueKind.Flag, string.Empty, flag, number: 0);

    public static MenuValue Of(int number) => new(MenuValueKind.Number, string.Empty, flag: false, number);

    /// <summary>The value as gdbus would print it, for logs and test messages.</summary>
    public override string ToString() => Kind switch
    {
        MenuValueKind.Text => $"'{Text}'",
        MenuValueKind.Flag => Flag ? "true" : "false",
        _ => Number.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>One entry of a dbusmenu layout: its id, its properties, and its children.</summary>
public sealed record DBusMenuNode(
    int Id,
    IReadOnlyList<KeyValuePair<string, MenuValue>> Properties,
    IReadOnlyList<DBusMenuNode> Children);

/// <summary>The properties of one entry, as <c>GetGroupProperties</c> and <c>ItemsPropertiesUpdated</c> carry them.</summary>
public sealed record DBusMenuItemProperties(int Id, IReadOnlyList<KeyValuePair<string, MenuValue>> Properties);

/// <summary>The properties an entry no longer sets, which hosts must reset to their default.</summary>
public sealed record DBusMenuRemovedProperties(int Id, IReadOnlyList<string> Names);

/// <summary>What changed in the menu between two snapshots.</summary>
/// <param name="Updated">Properties set anew or to another value, per entry.</param>
/// <param name="Removed">Properties back to their default value, per entry.</param>
/// <param name="StructureChanged">
/// Entries appeared or vanished: property updates cannot express that, the
/// layout itself must be announced as changed.
/// </param>
public sealed record DBusMenuUpdate(
    IReadOnlyList<DBusMenuItemProperties> Updated,
    IReadOnlyList<DBusMenuRemovedProperties> Removed,
    bool StructureChanged)
{
    public bool IsEmpty => Updated.Count == 0 && Removed.Count == 0 && !StructureChanged;
}

/// <summary>
/// The tray menu seen through the <c>com.canonical.dbusmenu</c> protocol, the
/// menu half of a StatusNotifierItem.
///
/// <para>Written against the interface description libdbusmenu ships
/// (<c>libdbusmenu-glib/dbus-menu.xml</c>) and the behaviour of its server,
/// the reference implementation. Three rules from there shape this
/// class:</para>
/// <list type="bullet">
/// <item><description>"A property should only be returned if its value is not
/// the default value": an enabled entry does not say <c>enabled</c>, a
/// separator says only its <c>type</c>.</description></item>
/// <item><description>In a label, one underscore marks the access key and two
/// stand for a literal one, so every underscore of a label is
/// doubled.</description></item>
/// <item><description>A change of properties travels as
/// <c>ItemsPropertiesUpdated</c> — new values in one list, properties back to
/// their default in the other — and the layout revision only moves when
/// entries appear or vanish, as libdbusmenu's server does.</description></item>
/// </list>
///
/// <para>Pure, so that every request a host can make is tested without a bus;
/// the D-Bus layer only writes what this class computes.</para>
/// </summary>
public static class DBusMenuLayout
{
    /// <summary>The dbusmenu version libdbusmenu's server reports (<c>DBUSMENU_VERSION_NUMBER</c>).</summary>
    public const uint Version = 3;

    /// <summary>The invisible entry every menu hangs from, id 0 by the protocol.</summary>
    public const int RootId = 0;

    // Property names and values, as dbus-menu.xml spells them.
    public const string TypeProperty = "type";
    public const string LabelProperty = "label";
    public const string EnabledProperty = "enabled";
    public const string ToggleTypeProperty = "toggle-type";
    public const string ToggleStateProperty = "toggle-state";
    public const string ChildrenDisplayProperty = "children-display";

    private const string Separator = "separator";
    private const string Checkmark = "checkmark";
    private const string Submenu = "submenu";

    /// <summary>The root's own properties: it holds the entries as a submenu.</summary>
    public static IReadOnlyList<KeyValuePair<string, MenuValue>> RootProperties { get; } =
        [new(ChildrenDisplayProperty, MenuValue.Of(Submenu))];

    /// <summary>The properties of one entry, defaults left out.</summary>
    public static IReadOnlyList<KeyValuePair<string, MenuValue>> PropertiesOf(TrayMenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.IsSeparator)
        {
            return [new(TypeProperty, MenuValue.Of(Separator))];
        }

        var properties = new List<KeyValuePair<string, MenuValue>>
        {
            new(LabelProperty, MenuValue.Of(EscapeLabel(item.Label))),
        };

        if (!item.Enabled)
        {
            properties.Add(new(EnabledProperty, MenuValue.Of(false)));
        }

        if (item.IsCheckbox)
        {
            // toggle-state defaults to -1, "indeterminate": an unchecked box
            // must therefore say 0 explicitly.
            properties.Add(new(ToggleTypeProperty, MenuValue.Of(Checkmark)));
            properties.Add(new(ToggleStateProperty, MenuValue.Of(item.IsChecked ? 1 : 0)));
        }

        return properties;
    }

    /// <summary>
    /// Doubles every underscore: dbusmenu reads a single one as the mark of an
    /// access key and would swallow it.
    /// </summary>
    public static string EscapeLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        return label.Replace("_", "__", StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="id"/> is the root or one of the entries.</summary>
    public static bool Exists(IReadOnlyList<TrayMenuItem> items, int id)
    {
        ArgumentNullException.ThrowIfNull(items);

        return id == RootId || items.Any(item => item.Id == id);
    }

    /// <summary>
    /// The answer to <c>GetLayout(parentId, recursionDepth, propertyNames)</c>,
    /// or null when <paramref name="parentId"/> names no entry.
    ///
    /// <para>A depth of 0 returns the parent alone; any other value — -1 means
    /// "all" — returns its children as well, the whole depth of this flat
    /// menu. An empty list of names means every property.</para>
    /// </summary>
    public static DBusMenuNode? Layout(
        IReadOnlyList<TrayMenuItem> items,
        int parentId,
        int recursionDepth,
        IReadOnlyCollection<string> propertyNames)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(propertyNames);

        if (parentId == RootId)
        {
            IReadOnlyList<DBusMenuNode> children = recursionDepth == 0
                ? []
                : [.. items.Select(item => new DBusMenuNode(item.Id, Select(PropertiesOf(item), propertyNames), []))];

            return new DBusMenuNode(RootId, Select(RootProperties, propertyNames), children);
        }

        TrayMenuItem? entry = items.FirstOrDefault(item => item.Id == parentId);

        return entry is null ? null : new DBusMenuNode(entry.Id, Select(PropertiesOf(entry), propertyNames), []);
    }

    /// <summary>
    /// The answer to <c>GetGroupProperties(ids, propertyNames)</c>. An empty
    /// list of ids means every entry, the root included; unknown ids are
    /// skipped, as the protocol has no error slot for them here.
    /// </summary>
    public static IReadOnlyList<DBusMenuItemProperties> GroupProperties(
        IReadOnlyList<TrayMenuItem> items,
        IReadOnlyCollection<int> ids,
        IReadOnlyCollection<string> propertyNames)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(propertyNames);

        IEnumerable<int> wanted = ids.Count == 0
            ? items.Select(item => item.Id).Prepend(RootId)
            : ids.Distinct().Where(id => Exists(items, id));

        return [.. wanted.Select(id => new DBusMenuItemProperties(id, Select(AllPropertiesOf(items, id), propertyNames)))];
    }

    /// <summary>
    /// The answer to <c>GetProperty(id, name)</c>: the value, or null when the
    /// entry does not exist or does not set that property.
    /// </summary>
    public static MenuValue? Property(IReadOnlyList<TrayMenuItem> items, int id, string name)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(name);

        if (!Exists(items, id))
        {
            return null;
        }

        foreach (KeyValuePair<string, MenuValue> property in AllPropertiesOf(items, id))
        {
            if (property.Key == name)
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// What changed between the menu of two snapshots, as
    /// <c>ItemsPropertiesUpdated</c> needs it: per entry, the properties with a
    /// new value, and those gone back to their default.
    /// </summary>
    public static DBusMenuUpdate Changes(IReadOnlyList<TrayMenuItem> before, IReadOnlyList<TrayMenuItem> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        bool structureChanged = !before.Select(item => item.Id).SequenceEqual(after.Select(item => item.Id));

        var updated = new List<DBusMenuItemProperties>();
        var removed = new List<DBusMenuRemovedProperties>();

        foreach (TrayMenuItem next in after)
        {
            TrayMenuItem? previous = before.FirstOrDefault(item => item.Id == next.Id);

            if (previous is null || previous == next)
            {
                continue;
            }

            IReadOnlyList<KeyValuePair<string, MenuValue>> old = PropertiesOf(previous);
            IReadOnlyList<KeyValuePair<string, MenuValue>> now = PropertiesOf(next);

            List<KeyValuePair<string, MenuValue>> changed =
                [.. now.Where(property => !old.Contains(property))];
            List<string> gone =
                [.. old.Select(property => property.Key).Where(key => now.All(property => property.Key != key))];

            if (changed.Count > 0)
            {
                updated.Add(new DBusMenuItemProperties(next.Id, changed));
            }

            if (gone.Count > 0)
            {
                removed.Add(new DBusMenuRemovedProperties(next.Id, gone));
            }
        }

        return new DBusMenuUpdate(updated, removed, structureChanged);
    }

    private static IReadOnlyList<KeyValuePair<string, MenuValue>> AllPropertiesOf(IReadOnlyList<TrayMenuItem> items, int id) =>
        id == RootId ? RootProperties : PropertiesOf(items.First(item => item.Id == id));

    private static IReadOnlyList<KeyValuePair<string, MenuValue>> Select(
        IReadOnlyList<KeyValuePair<string, MenuValue>> properties,
        IReadOnlyCollection<string> names) =>
        names.Count == 0 ? properties : [.. properties.Where(property => names.Contains(property.Key))];
}
