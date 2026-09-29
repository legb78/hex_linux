using System.Diagnostics.CodeAnalysis;
using Tmds.DBus.Protocol;

namespace HexLinux.Ui;

/// <summary>
/// Writes the tray's values in D-Bus wire format.
///
/// <para>Nothing is decided here: the values come from the pure layer
/// (<see cref="TrayIcon"/>, <see cref="DBusMenuLayout"/>), and this class only
/// lays them out in the order and with the signatures the two protocols
/// define. It is checked against a real bus by the front's harness (gdbus
/// and busctl reading the properties and the layout back), which a unit test
/// could not do: the library that writes these bytes offers no public way to
/// parse them again.</para>
///
/// <para>The writer is a ref struct handed along by reference: every call
/// must reach the one instance, never a copy.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "D-Bus serialisation shell, verified on a private bus by the front harness.")]
internal static class DBusWire
{
    /// <summary>Signature of an icon: an array of (width, height, pixels).</summary>
    public const string PixmapsSignature = "a(iiay)";

    /// <summary>Signature of the tooltip: (icon name, icon, title, description).</summary>
    public const string ToolTipSignature = "(sa(iiay)ss)";

    /// <summary>Signature of one dbusmenu layout node, recursive through its variants.</summary>
    public const string LayoutNodeSignature = "(ia{sv}av)";

    public const string InvalidArgs = "org.freedesktop.DBus.Error.InvalidArgs";
    public const string UnknownProperty = "org.freedesktop.DBus.Error.UnknownProperty";
    public const string PropertyReadOnly = "org.freedesktop.DBus.Error.PropertyReadOnly";
    public const string Failed = "org.freedesktop.DBus.Error.Failed";

    /// <summary>Writes <c>a(iiay)</c>.</summary>
    public static void WritePixmaps(ref MessageWriter writer, IReadOnlyList<IconPixmap> pixmaps)
    {
        ArrayStart array = writer.WriteArrayStart(DBusType.Struct);

        foreach (IconPixmap pixmap in pixmaps)
        {
            writer.WriteStructureStart();
            writer.WriteInt32(pixmap.Width);
            writer.WriteInt32(pixmap.Height);
            writer.WriteArray(pixmap.Argb32);
        }

        writer.WriteArrayEnd(array);
    }

    /// <summary>Writes a variant holding <c>a(iiay)</c>.</summary>
    public static void WriteVariantPixmaps(ref MessageWriter writer, IReadOnlyList<IconPixmap> pixmaps)
    {
        writer.WriteSignature(PixmapsSignature);
        WritePixmaps(ref writer, pixmaps);
    }

    /// <summary>
    /// Writes a variant holding the tooltip: no icon of its own — hosts show
    /// the item's — the line of the state as title, and no description.
    /// </summary>
    public static void WriteVariantToolTip(ref MessageWriter writer, string title)
    {
        writer.WriteSignature(ToolTipSignature);
        writer.WriteStructureStart();
        writer.WriteString(string.Empty);
        WritePixmaps(ref writer, []);
        writer.WriteString(title);
        writer.WriteString(string.Empty);
    }

    /// <summary>Writes a dbusmenu value as a variant of its own type.</summary>
    public static void WriteVariantMenuValue(ref MessageWriter writer, MenuValue value)
    {
        switch (value.Kind)
        {
            case MenuValueKind.Text:
                writer.WriteVariantString(value.Text);
                break;
            case MenuValueKind.Flag:
                writer.WriteVariantBool(value.Flag);
                break;
            default:
                writer.WriteVariantInt32(value.Number);
                break;
        }
    }

    /// <summary>Writes an <c>a{sv}</c> of dbusmenu properties.</summary>
    public static void WriteMenuProperties(ref MessageWriter writer, IReadOnlyList<KeyValuePair<string, MenuValue>> properties)
    {
        ArrayStart dictionary = writer.WriteDictionaryStart();

        foreach (KeyValuePair<string, MenuValue> property in properties)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(property.Key);
            WriteVariantMenuValue(ref writer, property.Value);
        }

        writer.WriteDictionaryEnd(dictionary);
    }

    /// <summary>Writes one <c>(ia{sv}av)</c> node and, inside its variants, its children.</summary>
    public static void WriteLayoutNode(ref MessageWriter writer, DBusMenuNode node)
    {
        writer.WriteStructureStart();
        writer.WriteInt32(node.Id);
        WriteMenuProperties(ref writer, node.Properties);

        ArrayStart children = writer.WriteArrayStart(DBusType.Variant);

        foreach (DBusMenuNode child in node.Children)
        {
            writer.WriteSignature(LayoutNodeSignature);
            WriteLayoutNode(ref writer, child);
        }

        writer.WriteArrayEnd(children);
    }

    /// <summary>Writes <c>a(ia{sv})</c>, the shape of GetGroupProperties and of the first half of ItemsPropertiesUpdated.</summary>
    public static void WriteItemProperties(ref MessageWriter writer, IReadOnlyList<DBusMenuItemProperties> items)
    {
        ArrayStart array = writer.WriteArrayStart(DBusType.Struct);

        foreach (DBusMenuItemProperties item in items)
        {
            writer.WriteStructureStart();
            writer.WriteInt32(item.Id);
            WriteMenuProperties(ref writer, item.Properties);
        }

        writer.WriteArrayEnd(array);
    }

    /// <summary>Writes <c>a(ias)</c>, the second half of ItemsPropertiesUpdated.</summary>
    public static void WriteRemovedProperties(ref MessageWriter writer, IReadOnlyList<DBusMenuRemovedProperties> items)
    {
        ArrayStart array = writer.WriteArrayStart(DBusType.Struct);

        foreach (DBusMenuRemovedProperties item in items)
        {
            writer.WriteStructureStart();
            writer.WriteInt32(item.Id);
            writer.WriteArray(item.Names.ToArray());
        }

        writer.WriteArrayEnd(array);
    }

    /// <summary>Replies with no value, the answer to a method that returns nothing.</summary>
    public static void ReplyEmpty(MethodContext context)
    {
        MessageWriter writer = context.CreateReplyWriter(null);

        try
        {
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Sends a signal with no arguments; false when the connection is already gone.</summary>
    public static bool TrySendSignal(DBusConnection connection, string path, string @interface, string member)
    {
        MessageWriter writer = connection.GetMessageWriter();

        try
        {
            writer.WriteSignalHeader(path: path, @interface: @interface, member: member);
            return connection.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Sends a signal with one string argument.</summary>
    public static bool TrySendSignal(DBusConnection connection, string path, string @interface, string member, string argument)
    {
        MessageWriter writer = connection.GetMessageWriter();

        try
        {
            writer.WriteSignalHeader(path: path, @interface: @interface, member: member, signature: "s");
            writer.WriteString(argument);
            return connection.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }
}
