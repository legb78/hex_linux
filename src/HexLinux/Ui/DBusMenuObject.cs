using System.Diagnostics.CodeAnalysis;
using Tmds.DBus.Protocol;

namespace HexLinux.Ui;

/// <summary>
/// The <c>/MenuBar</c> object: the tray menu over <c>com.canonical.dbusmenu</c>,
/// which the host reads to draw the menu and calls when an entry is clicked.
///
/// <para>Every answer comes from <see cref="DBusMenuLayout"/> applied to the
/// view of the moment. A click is acknowledged first and acted upon after,
/// off the connection's thread: the host is not kept waiting, and the daemon
/// can take its time without holding up the next message.</para>
///
/// <para>What a caller sends is only ever compared with the menu: an id picks
/// an entry of the current menu or nothing. An unknown id is refused with an
/// error, as libdbusmenu's server refuses it, and triggers nothing; a click on
/// a separator or a disabled entry is acknowledged and ignored.</para>
///
/// <para>Never throws, for the same reason as the item object: an escaping
/// exception would close the connection.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "D-Bus shell: needs a session bus; verified on a private bus by the front harness.")]
internal sealed class DBusMenuObject : IPathMethodHandler
{
    /// <summary>The interface as libdbusmenu's <c>dbus-menu.xml</c> describes it, returned to <c>Introspect</c>.</summary>
    private static readonly ReadOnlyMemory<byte> InterfaceXml = """
        <interface name="com.canonical.dbusmenu">
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconThemePath" type="as" access="read"/>
          <method name="GetLayout">
            <arg type="i" name="parentId" direction="in"/>
            <arg type="i" name="recursionDepth" direction="in"/>
            <arg type="as" name="propertyNames" direction="in"/>
            <arg type="u" name="revision" direction="out"/>
            <arg type="(ia{sv}av)" name="layout" direction="out"/>
          </method>
          <method name="GetGroupProperties">
            <arg type="ai" name="ids" direction="in"/>
            <arg type="as" name="propertyNames" direction="in"/>
            <arg type="a(ia{sv})" name="properties" direction="out"/>
          </method>
          <method name="GetProperty">
            <arg type="i" name="id" direction="in"/>
            <arg type="s" name="name" direction="in"/>
            <arg type="v" name="value" direction="out"/>
          </method>
          <method name="Event">
            <arg type="i" name="id" direction="in"/>
            <arg type="s" name="eventId" direction="in"/>
            <arg type="v" name="data" direction="in"/>
            <arg type="u" name="timestamp" direction="in"/>
          </method>
          <method name="EventGroup">
            <arg type="a(isvu)" name="events" direction="in"/>
            <arg type="ai" name="idErrors" direction="out"/>
          </method>
          <method name="AboutToShow">
            <arg type="i" name="id" direction="in"/>
            <arg type="b" name="needUpdate" direction="out"/>
          </method>
          <method name="AboutToShowGroup">
            <arg type="ai" name="ids" direction="in"/>
            <arg type="ai" name="updatesNeeded" direction="out"/>
            <arg type="ai" name="idErrors" direction="out"/>
          </method>
          <signal name="ItemsPropertiesUpdated">
            <arg type="a(ia{sv})" name="updatedProps" direction="out"/>
            <arg type="a(ias)" name="removedProps" direction="out"/>
          </signal>
          <signal name="LayoutUpdated">
            <arg type="u" name="revision" direction="out"/>
            <arg type="i" name="parent" direction="out"/>
          </signal>
          <signal name="ItemActivationRequested">
            <arg type="i" name="id" direction="out"/>
            <arg type="u" name="timestamp" direction="out"/>
          </signal>
        </interface>

        """u8.ToArray();

    private static readonly string[] PropertyNames = ["Version", "TextDirection", "Status", "IconThemePath"];

    /// <summary>The event a host sends when an entry is activated; "hovered", "opened" and "closed" need no action.</summary>
    private const string Clicked = "clicked";

    private readonly Func<TrayView> _view;
    private readonly Func<uint> _revision;
    private readonly Action<TrayMenuItem, TrayView> _clicked;
    private readonly Action<string> _log;

    public DBusMenuObject(Func<TrayView> view, Func<uint> revision, Action<TrayMenuItem, TrayView> clicked, Action<string> log)
    {
        _view = view;
        _revision = revision;
        _clicked = clicked;
        _log = log;
    }

    public string Path => TrayNames.MenuPath;

    public bool HandlesChildPaths => false;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        try
        {
            Handle(context);
        }
        catch (Exception ex)
        {
            _log($"tray menu: the call {context.Request.MemberAsString} failed: {ex.GetType().Name}: {ex.Message}");
            context.ReplyError(DBusWire.Failed, "The call could not be handled.");
        }

        return default;
    }

    private void Handle(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([InterfaceXml, IntrospectionXml.DBusProperties]);
            return;
        }

        Message request = context.Request;
        TrayView view = _view();

        switch (request.InterfaceAsString, request.MemberAsString, request.SignatureAsString)
        {
            case (TrayNames.MenuInterface, "GetLayout", "iias"):
                {
                    Reader reader = request.GetBodyReader();
                    int parentId = reader.ReadInt32();
                    int depth = reader.ReadInt32();
                    string[] names = reader.ReadArrayOfString();
                    ReplyLayout(context, view, parentId, depth, names);
                    return;
                }

            case (TrayNames.MenuInterface, "GetGroupProperties", "aias"):
                {
                    Reader reader = request.GetBodyReader();
                    int[] ids = reader.ReadArrayOfInt32();
                    string[] names = reader.ReadArrayOfString();
                    ReplyGroupProperties(context, view, ids, names);
                    return;
                }

            case (TrayNames.MenuInterface, "GetProperty", "is"):
                {
                    Reader reader = request.GetBodyReader();
                    int id = reader.ReadInt32();
                    string name = reader.ReadString();
                    ReplyProperty(context, view, id, name);
                    return;
                }

            case (TrayNames.MenuInterface, "Event", "isvu"):
                {
                    Reader reader = request.GetBodyReader();
                    int id = reader.ReadInt32();
                    string eventId = reader.ReadString();
                    _ = reader.ReadVariantValue();
                    _ = reader.ReadUInt32();

                    if (!DBusMenuLayout.Exists(view.Menu, id))
                    {
                        context.ReplyError(DBusWire.InvalidArgs, $"No menu entry {id}.");
                        return;
                    }

                    DBusWire.ReplyEmpty(context);
                    Act(view, id, eventId);
                    return;
                }

            case (TrayNames.MenuInterface, "EventGroup", "a(isvu)"):
                {
                    var events = new List<(int Id, string EventId)>();
                    Reader reader = request.GetBodyReader();
                    ArrayEnd end = reader.ReadArrayStart(DBusType.Struct);

                    while (reader.HasNext(end))
                    {
                        reader.AlignStruct();
                        int id = reader.ReadInt32();
                        string eventId = reader.ReadString();
                        _ = reader.ReadVariantValue();
                        _ = reader.ReadUInt32();
                        events.Add((id, eventId));
                    }

                    ReplyEventGroup(context, view, events);
                    return;
                }

            case (TrayNames.MenuInterface, "AboutToShow", "i"):
                {
                    int id = request.GetBodyReader().ReadInt32();

                    if (!DBusMenuLayout.Exists(view.Menu, id))
                    {
                        context.ReplyError(DBusWire.InvalidArgs, $"No menu entry {id}.");
                        return;
                    }

                    // The menu is always current: nothing to refresh before
                    // it opens.
                    ReplyBoolean(context, false);
                    return;
                }

            case (TrayNames.MenuInterface, "AboutToShowGroup", "ai"):
                {
                    int[] ids = request.GetBodyReader().ReadArrayOfInt32();
                    int[] unknown = [.. ids.Where(id => !DBusMenuLayout.Exists(view.Menu, id))];

                    if (ids.Length > 0 && unknown.Length == ids.Length)
                    {
                        context.ReplyError(DBusWire.InvalidArgs, "None of these menu entries exists.");
                        return;
                    }

                    MessageWriter writer = context.CreateReplyWriter("aiai");

                    try
                    {
                        writer.WriteArray(Array.Empty<int>());
                        writer.WriteArray(unknown);
                        context.Reply(writer.CreateMessage());
                    }
                    finally
                    {
                        writer.Dispose();
                    }

                    return;
                }

            case (TrayNames.PropertiesInterface, "Get", "ss"):
                {
                    Reader reader = request.GetBodyReader();
                    string @interface = reader.ReadString();
                    string name = reader.ReadString();
                    ReplyMenuProperty(context, @interface, name);
                    return;
                }

            case (TrayNames.PropertiesInterface, "GetAll", "s"):
                ReplyAllMenuProperties(context, request.GetBodyReader().ReadString());
                return;

            case (TrayNames.PropertiesInterface, "Set", "ssv"):
                context.ReplyError(DBusWire.PropertyReadOnly, "Every property of this menu is read-only.");
                return;
        }
    }

    /// <summary>Hands a click on to the surface; other events and inert entries need nothing.</summary>
    private void Act(TrayView view, int id, string eventId)
    {
        if (eventId != Clicked)
        {
            return;
        }

        TrayMenuItem? item = view.Menu.FirstOrDefault(entry => entry.Id == id);

        if (item is null || item.IsSeparator || !item.Enabled)
        {
            return;
        }

        _clicked(item, view);
    }

    private void ReplyLayout(MethodContext context, TrayView view, int parentId, int depth, string[] names)
    {
        DBusMenuNode? node = DBusMenuLayout.Layout(view.Menu, parentId, depth, names);

        if (node is null)
        {
            context.ReplyError(DBusWire.InvalidArgs, $"No menu entry {parentId}.");
            return;
        }

        MessageWriter writer = context.CreateReplyWriter("u" + DBusWire.LayoutNodeSignature);

        try
        {
            writer.WriteUInt32(_revision());
            DBusWire.WriteLayoutNode(ref writer, node);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void ReplyGroupProperties(MethodContext context, TrayView view, int[] ids, string[] names)
    {
        IReadOnlyList<DBusMenuItemProperties> items = DBusMenuLayout.GroupProperties(view.Menu, ids, names);
        MessageWriter writer = context.CreateReplyWriter("a(ia{sv})");

        try
        {
            DBusWire.WriteItemProperties(ref writer, items);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void ReplyProperty(MethodContext context, TrayView view, int id, string name)
    {
        MenuValue? value = DBusMenuLayout.Property(view.Menu, id, name);

        if (value is null)
        {
            context.ReplyError(DBusWire.InvalidArgs, $"Menu entry {id} has no property {name}.");
            return;
        }

        MessageWriter writer = context.CreateReplyWriter("v");

        try
        {
            DBusWire.WriteVariantMenuValue(ref writer, value.Value);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private void ReplyEventGroup(MethodContext context, TrayView view, List<(int Id, string EventId)> events)
    {
        int[] unknown = [.. events.Select(entry => entry.Id).Where(id => !DBusMenuLayout.Exists(view.Menu, id))];

        // dbus-menu.xml: "If none of the ones in the list can be found, a DBus
        // error is returned."
        if (events.Count > 0 && unknown.Length == events.Count)
        {
            context.ReplyError(DBusWire.InvalidArgs, "None of these menu entries exists.");
            return;
        }

        MessageWriter writer = context.CreateReplyWriter("ai");

        try
        {
            writer.WriteArray(unknown);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }

        foreach ((int id, string eventId) in events)
        {
            if (DBusMenuLayout.Exists(view.Menu, id))
            {
                Act(view, id, eventId);
            }
        }
    }

    private static void ReplyBoolean(MethodContext context, bool value)
    {
        MessageWriter writer = context.CreateReplyWriter("b");

        try
        {
            writer.WriteBool(value);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static bool IsOurs(string @interface) =>
        @interface.Length == 0 || @interface == TrayNames.MenuInterface;

    private static void ReplyMenuProperty(MethodContext context, string @interface, string name)
    {
        if (!IsOurs(@interface))
        {
            context.ReplyError(DBusWire.InvalidArgs, $"No interface {@interface} on {TrayNames.MenuPath}.");
            return;
        }

        if (Array.IndexOf(PropertyNames, name) < 0)
        {
            context.ReplyError(DBusWire.UnknownProperty, $"No property {name} on {TrayNames.MenuInterface}.");
            return;
        }

        MessageWriter writer = context.CreateReplyWriter("v");

        try
        {
            WriteMenuPropertyVariant(ref writer, name);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void ReplyAllMenuProperties(MethodContext context, string @interface)
    {
        if (!IsOurs(@interface))
        {
            context.ReplyError(DBusWire.InvalidArgs, $"No interface {@interface} on {TrayNames.MenuPath}.");
            return;
        }

        MessageWriter writer = context.CreateReplyWriter("a{sv}");

        try
        {
            ArrayStart dictionary = writer.WriteDictionaryStart();

            foreach (string name in PropertyNames)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString(name);
                WriteMenuPropertyVariant(ref writer, name);
            }

            writer.WriteDictionaryEnd(dictionary);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WriteMenuPropertyVariant(ref MessageWriter writer, string name)
    {
        switch (name)
        {
            case "Version":
                writer.WriteVariantUInt32(DBusMenuLayout.Version);
                break;
            case "TextDirection":
                // The labels are English, written left to right.
                writer.WriteVariantString("ltr");
                break;
            case "Status":
                writer.WriteVariantString("normal");
                break;
            default:
                // IconThemePath: no icon of our own in the menu.
                writer.WriteSignature("as");
                writer.WriteArray(Array.Empty<string>());
                break;
        }
    }
}
