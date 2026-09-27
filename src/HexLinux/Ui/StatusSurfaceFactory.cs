using HexLinux.Daemon;

namespace HexLinux.Ui;

/// <summary>
/// Picks the best status surface the desktop allows.
///
/// <para>The one entry point from the daemon into the interface layer. Until
/// the tray icon exists, it hands back the surface that only logs; the
/// interface layer replaces this body, and the daemon does not change.</para>
/// </summary>
public static class StatusSurfaceFactory
{
    public static IStatusSurface Create(Action<string> log) => new NullStatusSurface(log);
}
