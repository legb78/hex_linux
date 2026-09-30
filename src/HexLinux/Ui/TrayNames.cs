using System.Globalization;

namespace HexLinux.Ui;

/// <summary>
/// The names on the session bus that the tray protocol is made of.
///
/// <para>The <c>org.kde</c> names rather than the <c>org.freedesktop</c> ones
/// of the specification's wiki page: the hosts implement the protocol under
/// the names KDE first shipped. KDE's own interface files carry them
/// (kstatusnotifieritem: <c>org.kde.StatusNotifierItem.xml</c>,
/// <c>org.kde.StatusNotifierWatcher.xml</c>), and so do the watchers of the
/// GNOME AppIndicator extension (<c>statusNotifierWatcher.js</c>, whose
/// comment still waits for the switch to <c>org.freedesktop</c>) and of
/// Waybar (<c>modules/sni/watcher.cpp</c>). The object paths and the form of
/// the service name are KDE's too.</para>
/// </summary>
public static class TrayNames
{
    public const string WatcherService = "org.kde.StatusNotifierWatcher";
    public const string WatcherPath = "/StatusNotifierWatcher";
    public const string WatcherInterface = "org.kde.StatusNotifierWatcher";

    public const string ItemPath = "/StatusNotifierItem";
    public const string ItemInterface = "org.kde.StatusNotifierItem";

    /// <summary>Where the menu lives, the path KDE's items use.</summary>
    public const string MenuPath = "/MenuBar";
    public const string MenuInterface = "com.canonical.dbusmenu";

    public const string NotificationsService = "org.freedesktop.Notifications";
    public const string NotificationsPath = "/org/freedesktop/Notifications";
    public const string NotificationsInterface = "org.freedesktop.Notifications";

    public const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    /// <summary>
    /// The well-known name of the item: <c>org.kde.StatusNotifierItem-PID-ID</c>,
    /// the form the specification prescribes, ID telling apart the items of
    /// one process. HexLinux has one item, so the ID is always 1.
    /// </summary>
    public static string ItemService(int processId) =>
        string.Create(CultureInfo.InvariantCulture, $"org.kde.StatusNotifierItem-{processId}-1");
}
