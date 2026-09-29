using HexLinux.Ui;
using Xunit;

namespace HexLinux.Tests.Ui;

public class TrayNamesTests
{
    [Fact]
    public void The_item_name_carries_the_process_id()
    {
        // org.kde.StatusNotifierItem-PID-ID: two daemons of two users, or a
        // daemon restarted while the old one's name lingers, never collide.
        Assert.Equal("org.kde.StatusNotifierItem-4077-1", TrayNames.ItemService(4077));
    }

    [Fact]
    public void The_names_are_the_ones_the_hosts_implement()
    {
        // KDE, the GNOME AppIndicator extension and Waybar all use the
        // org.kde names and these paths; a typo here means no icon anywhere.
        Assert.Equal("org.kde.StatusNotifierWatcher", TrayNames.WatcherService);
        Assert.Equal("/StatusNotifierWatcher", TrayNames.WatcherPath);
        Assert.Equal("/StatusNotifierItem", TrayNames.ItemPath);
        Assert.Equal("org.kde.StatusNotifierItem", TrayNames.ItemInterface);
        Assert.Equal("com.canonical.dbusmenu", TrayNames.MenuInterface);
        Assert.Equal("/org/freedesktop/Notifications", TrayNames.NotificationsPath);
    }
}
