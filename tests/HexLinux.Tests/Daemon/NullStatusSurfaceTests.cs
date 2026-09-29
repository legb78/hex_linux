using HexLinux.Daemon;
using Xunit;

namespace HexLinux.Tests.Daemon;

/// <summary>
/// The surface used when nothing can be shown — no session bus, no tray
/// host, no desktop at all. The daemon runs the same behind it, and the one
/// thing it must still do is leave a trace of every notification in the log,
/// where a user whose dictation failed will look.
/// </summary>
public class NullStatusSurfaceTests
{
    [Fact]
    public void A_notification_goes_to_the_log_as_one_line()
    {
        // "Model not found" on a desktop with no notification service must
        // still say what to do.
        var lines = new List<string>();
        using var surface = new NullStatusSurface(lines.Add);

        surface.Notify("HexLinux: model not found", "Download it with scripts/get-model.sh.");

        Assert.Equal(["HexLinux: model not found: Download it with scripts/get-model.sh."], lines);
    }

    [Fact]
    public void Nothing_is_visible_and_updates_write_nothing()
    {
        // Updates come after every state change: logging them would fill the
        // log with one line per key press.
        var lines = new List<string>();
        using var surface = new NullStatusSurface(lines.Add);

        surface.Update(new StatusSnapshot(DictationState.Recording, "Right Ctrl", true, false, "/s.json", "/log"));

        Assert.False(surface.IsVisible);
        Assert.Empty(lines);
    }

    [Fact]
    public void Subscribing_to_requests_is_accepted_and_nothing_is_ever_raised()
    {
        // The daemon subscribes whatever the surface it is given: there is
        // no menu to click here, and subscribing must not fail for it.
        using var surface = new NullStatusSurface(_ => { });
        int raised = 0;
        EventHandler<SurfaceRequest> handler = (_, _) => raised++;

        surface.Requested += handler;
        surface.Update(new StatusSnapshot(DictationState.Idle, "Right Ctrl", true, false, "/s.json", "/log"));
        surface.Requested -= handler;

        Assert.Equal(0, raised);
    }

    [Fact]
    public void A_surface_without_a_log_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => new NullStatusSurface(null!));
    }
}
