using System.Diagnostics.CodeAnalysis;
using HexLinux.Daemon;
using Tmds.DBus.Protocol;

namespace HexLinux.Ui;

/// <summary>
/// The <c>/StatusNotifierItem</c> object: what the tray host reads to draw the
/// icon and its tooltip.
///
/// <para>Answers from the view of the moment and nothing else: no argument a
/// caller sends is ever used to reach a file or run a command. The methods a
/// host calls on a click — <c>Activate</c>, <c>SecondaryActivate</c>,
/// <c>ContextMenu</c>, <c>Scroll</c> — are acknowledged and do nothing: the
/// item declares <c>ItemIsMenu</c>, so a click opens the menu, which is where
/// V1's actions are. A left click that started a dictation by accident would
/// open the microphone behind the user's back.</para>
///
/// <para>Never throws: an exception escaping a method handler makes the D-Bus
/// library close the whole connection, and the icon with it.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "D-Bus shell: needs a session bus; verified on a private bus by the front harness.")]
internal sealed class StatusNotifierItemObject : IPathMethodHandler
{
    /// <summary>The value of <c>Id</c>: stable across sessions, hosts remember per-item choices by it.</summary>
    private const string ItemId = "hexlinux";

    private const string Category = "ApplicationStatus";

    /// <summary>
    /// The interface as KDE's <c>org.kde.StatusNotifierItem.xml</c> describes
    /// it (the Qt annotations left out), returned to <c>Introspect</c>.
    /// </summary>
    private static readonly ReadOnlyMemory<byte> InterfaceXml = """
        <interface name="org.kde.StatusNotifierItem">
          <property name="Category" type="s" access="read"/>
          <property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="WindowId" type="i" access="read"/>
          <property name="IconThemePath" type="s" access="read"/>
          <property name="Menu" type="o" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/>
          <property name="IconName" type="s" access="read"/>
          <property name="IconPixmap" type="a(iiay)" access="read"/>
          <property name="OverlayIconName" type="s" access="read"/>
          <property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionIconName" type="s" access="read"/>
          <property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionMovieName" type="s" access="read"/>
          <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
          <method name="ProvideXdgActivationToken">
            <arg name="token" type="s" direction="in"/>
          </method>
          <method name="ContextMenu">
            <arg name="x" type="i" direction="in"/>
            <arg name="y" type="i" direction="in"/>
          </method>
          <method name="Activate">
            <arg name="x" type="i" direction="in"/>
            <arg name="y" type="i" direction="in"/>
          </method>
          <method name="SecondaryActivate">
            <arg name="x" type="i" direction="in"/>
            <arg name="y" type="i" direction="in"/>
          </method>
          <method name="Scroll">
            <arg name="delta" type="i" direction="in"/>
            <arg name="orientation" type="s" direction="in"/>
          </method>
          <signal name="NewTitle"/>
          <signal name="NewIcon"/>
          <signal name="NewAttentionIcon"/>
          <signal name="NewOverlayIcon"/>
          <signal name="NewMenu"/>
          <signal name="NewToolTip"/>
          <signal name="NewStatus">
            <arg name="status" type="s"/>
          </signal>
        </interface>

        """u8.ToArray();

    /// <summary>Every property, in the order <c>GetAll</c> lists them.</summary>
    private static readonly string[] PropertyNames =
    [
        "Category", "Id", "Title", "Status", "WindowId", "IconThemePath", "Menu", "ItemIsMenu",
        "IconName", "IconPixmap", "OverlayIconName", "OverlayIconPixmap",
        "AttentionIconName", "AttentionIconPixmap", "AttentionMovieName", "ToolTip",
    ];

    private readonly Func<TrayView> _view;
    private readonly IReadOnlyDictionary<DictationState, IReadOnlyList<IconPixmap>> _icons;
    private readonly Action<string> _log;

    public StatusNotifierItemObject(
        Func<TrayView> view,
        IReadOnlyDictionary<DictationState, IReadOnlyList<IconPixmap>> icons,
        Action<string> log)
    {
        _view = view;
        _icons = icons;
        _log = log;
    }

    public string Path => TrayNames.ItemPath;

    public bool HandlesChildPaths => false;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        try
        {
            Handle(context);
        }
        catch (Exception ex)
        {
            _log($"tray: the call {context.Request.InterfaceAsString}.{context.Request.MemberAsString} failed: {ex.GetType().Name}: {ex.Message}");
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

        switch (request.InterfaceAsString, request.MemberAsString, request.SignatureAsString)
        {
            case (TrayNames.PropertiesInterface, "Get", "ss"):
                {
                    Reader reader = request.GetBodyReader();
                    string @interface = reader.ReadString();
                    string name = reader.ReadString();
                    ReplyProperty(context, @interface, name);
                    return;
                }

            case (TrayNames.PropertiesInterface, "GetAll", "s"):
                {
                    Reader reader = request.GetBodyReader();
                    ReplyAllProperties(context, reader.ReadString());
                    return;
                }

            case (TrayNames.PropertiesInterface, "Set", "ssv"):
                context.ReplyError(DBusWire.PropertyReadOnly, "Every property of this item is read-only.");
                return;

            case (TrayNames.ItemInterface, "Activate" or "SecondaryActivate" or "ContextMenu", "ii"):
            case (TrayNames.ItemInterface, "Scroll", "is"):
            case (TrayNames.ItemInterface, "ProvideXdgActivationToken", "s"):
                DBusWire.ReplyEmpty(context);
                return;
        }

        // Anything else: the library answers UnknownMethod when the context is
        // disposed without a reply.
    }

    private static bool IsOurs(string @interface) =>
        @interface.Length == 0 || @interface == TrayNames.ItemInterface;

    private void ReplyProperty(MethodContext context, string @interface, string name)
    {
        if (!IsOurs(@interface))
        {
            context.ReplyError(DBusWire.InvalidArgs, $"No interface {@interface} on {TrayNames.ItemPath}.");
            return;
        }

        if (Array.IndexOf(PropertyNames, name) < 0)
        {
            context.ReplyError(DBusWire.UnknownProperty, $"No property {name} on {TrayNames.ItemInterface}.");
            return;
        }

        TrayView view = _view();
        MessageWriter writer = context.CreateReplyWriter("v");

        try
        {
            WriteVariant(ref writer, name, view);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private void ReplyAllProperties(MethodContext context, string @interface)
    {
        if (!IsOurs(@interface))
        {
            // The specification: a valid interface with no properties gets an
            // empty array. Hosts ask for ours only, so any other name is one
            // this object does not implement, and says so.
            context.ReplyError(DBusWire.InvalidArgs, $"No interface {@interface} on {TrayNames.ItemPath}.");
            return;
        }

        // One view for the whole answer: a snapshot arriving in the middle
        // must not give the icon of one state and the tooltip of another.
        TrayView view = _view();
        MessageWriter writer = context.CreateReplyWriter("a{sv}");

        try
        {
            ArrayStart dictionary = writer.WriteDictionaryStart();

            foreach (string name in PropertyNames)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString(name);
                WriteVariant(ref writer, name, view);
            }

            writer.WriteDictionaryEnd(dictionary);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private void WriteVariant(ref MessageWriter writer, string name, TrayView view)
    {
        switch (name)
        {
            case "Category":
                writer.WriteVariantString(Category);
                break;
            case "Id":
                writer.WriteVariantString(ItemId);
                break;
            case "Title":
                writer.WriteVariantString(TrayText.Product);
                break;
            case "Status":
                writer.WriteVariantString(view.Status);
                break;
            case "WindowId":
                // No window to associate: the specification's "just set 0".
                writer.WriteVariantInt32(0);
                break;
            case "Menu":
                writer.WriteVariantObjectPath(TrayNames.MenuPath);
                break;
            case "ItemIsMenu":
                writer.WriteVariantBool(true);
                break;
            case "IconPixmap":
                DBusWire.WriteVariantPixmaps(ref writer, _icons[view.State]);
                break;
            case "AttentionIconPixmap":
                // Shown by hosts that swap icons in the NeedsAttention status,
                // which only the failed state uses: the same crossed disc.
                DBusWire.WriteVariantPixmaps(ref writer, _icons[DictationState.Failed]);
                break;
            case "OverlayIconPixmap":
                DBusWire.WriteVariantPixmaps(ref writer, []);
                break;
            case "ToolTip":
                DBusWire.WriteVariantToolTip(ref writer, view.ToolTip);
                break;
            default:
                // IconName, IconThemePath and the other names: empty, so that
                // hosts use the pixmaps. The specification has hosts prefer a
                // name when both exist.
                writer.WriteVariantString(string.Empty);
                break;
        }
    }
}
