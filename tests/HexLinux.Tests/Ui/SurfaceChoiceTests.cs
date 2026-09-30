using HexLinux.Ui;
using Xunit;

namespace HexLinux.Tests.Ui;

public class SurfaceChoiceTests
{
    [Fact]
    public void A_tray_host_on_the_bus_gets_the_icon()
    {
        // KDE, or GNOME with the AppIndicator extension.
        Assert.Equal(SurfaceKind.Tray, SurfaceChoice.Decide(sessionBusReachable: true, trayHostPresent: true));
    }

    [Fact]
    public void A_bus_without_a_tray_host_gets_notifications_only()
    {
        // GNOME without the extension, or WSL: notifications still reach the
        // user, and the daemon keeps running.
        Assert.Equal(SurfaceKind.NotificationsOnly, SurfaceChoice.Decide(sessionBusReachable: true, trayHostPresent: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_session_bus_leaves_only_the_log(bool trayHostPresent)
    {
        // An SSH login or a service outside the session: nothing to show on.
        Assert.Equal(SurfaceKind.LogOnly, SurfaceChoice.Decide(sessionBusReachable: false, trayHostPresent));
    }

    [Fact]
    public void Without_a_tray_the_log_says_no_tray_host()
    {
        // The line users and the checklist look for when no icon shows up, with
        // the fix for the most common desktop.
        string line = SurfaceChoice.Describe(SurfaceKind.NotificationsOnly);

        Assert.Contains("no tray host", line, StringComparison.Ordinal);
        Assert.Contains("AppIndicator", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_bus_the_log_says_so()
    {
        Assert.Contains("no session bus", SurfaceChoice.Describe(SurfaceKind.LogOnly), StringComparison.Ordinal);
    }

    [Fact]
    public void A_registered_icon_is_announced()
    {
        Assert.Contains("registered", SurfaceChoice.Describe(SurfaceKind.Tray), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreachable_bus_names_the_reason_and_the_fallback()
    {
        string line = SurfaceChoice.Unreachable("Connection refused");

        Assert.Contains("Connection refused", line, StringComparison.Ordinal);
        Assert.Contains("notify-send", line, StringComparison.Ordinal);
    }
}
