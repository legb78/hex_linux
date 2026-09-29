using System.Diagnostics.CodeAnalysis;
using HexLinux.Daemon;

namespace HexLinux.Ui;

/// <summary>
/// Picks the best status surface the desktop allows.
///
/// <para>The one entry point from the daemon into the interface layer. With
/// no session bus to reach — an SSH login, a service outside the user's
/// session — it hands back the surface that only logs. Otherwise it hands
/// back the tray surface, which connects in the background and is, from one
/// moment to the next, the tray icon when a tray host is running and
/// notifications only when none is (see <see cref="SurfaceChoice"/>).</para>
///
/// <para>Returns at once in every case: the daemon calls it at start-up, on
/// the loop that must go on to run the dictations, and no D-Bus answer is
/// awaited here.</para>
/// </summary>
public static class StatusSurfaceFactory
{
    [ExcludeFromCodeCoverage(Justification = "Reads the real environment and starts the D-Bus surface; the decisions it applies are tested in SessionBusAddress and SurfaceChoice.")]
    public static IStatusSurface Create(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        string? address = SessionBusAddress.Resolve(Environment.GetEnvironmentVariable, File.Exists);

        if (address is null)
        {
            log(SurfaceChoice.Describe(SurfaceKind.LogOnly));
            return new NullStatusSurface(log);
        }

        try
        {
            return new TraySurface(address, log);
        }
        catch (Exception ex)
        {
            // Deliberately broad: whatever keeps the tray from starting, the
            // daemon must still start, with its messages in the log.
            log(SurfaceChoice.Unreachable($"{ex.GetType().Name}: {ex.Message}"));
            return new NullStatusSurface(log);
        }
    }
}
