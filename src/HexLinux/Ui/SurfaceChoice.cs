namespace HexLinux.Ui;

/// <summary>What the user can see of the daemon, from nothing to the full tray icon.</summary>
public enum SurfaceKind
{
    /// <summary>No session bus: the daemon's messages go to its log only.</summary>
    LogOnly,

    /// <summary>A session bus but no tray host: desktop notifications, no icon.</summary>
    NotificationsOnly,

    /// <summary>A tray host shows the icon and its menu, and notifications work.</summary>
    Tray,
}

/// <summary>
/// Decides which surface the desktop allows, and says so in the log.
///
/// <para>The decision is revisited while the daemon runs, not taken once at
/// start: at login, an autostart entry often starts before the panel that
/// provides the tray host, and a panel can crash and come back. The surface
/// therefore stays on the bus, notifications only, and turns into the tray
/// icon as soon as a host appears — then back again if the host goes
/// away.</para>
///
/// <para>Every line names what is missing and what it costs, because the
/// log is where a user looks when no icon shows up: on GNOME, for instance,
/// the tray host is the AppIndicator extension, which Ubuntu enables and
/// other distributions may not ship.</para>
/// </summary>
public static class SurfaceChoice
{
    public static SurfaceKind Decide(bool sessionBusReachable, bool trayHostPresent) => (sessionBusReachable, trayHostPresent) switch
    {
        (false, _) => SurfaceKind.LogOnly,
        (true, false) => SurfaceKind.NotificationsOnly,
        (true, true) => SurfaceKind.Tray,
    };

    /// <summary>The log line for a surface; the one without a tray says "no tray host".</summary>
    public static string Describe(SurfaceKind kind) => kind switch
    {
        SurfaceKind.LogOnly =>
            "no session bus: no tray icon and no desktop notifications, messages go to this log",
        SurfaceKind.NotificationsOnly =>
            "no tray host (no StatusNotifierWatcher on the session bus): desktop notifications only; "
            + "the icon appears as soon as a tray host starts (on GNOME, the AppIndicator extension)",
        _ => "tray icon registered with the StatusNotifierWatcher",
    };

    /// <summary>
    /// The log line when a session bus address exists but the connection
    /// failed: no icon, and notifications try notify-send before the log.
    /// </summary>
    public static string Unreachable(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        return $"session bus unreachable ({reason}): no tray icon; notifications fall back to notify-send, then to this log";
    }
}
