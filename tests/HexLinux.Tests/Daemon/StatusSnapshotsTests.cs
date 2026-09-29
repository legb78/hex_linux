using HexLinux.Configuration;
using HexLinux.Daemon;
using Xunit;

namespace HexLinux.Tests.Daemon;

/// <summary>
/// What the tray is handed. The daemon builds a snapshot after every change
/// and pushes it only when it differs from the last one, by the record's
/// equality: a snapshot that differed for nothing would redraw the icon over
/// and over, one that stayed equal through a change would leave the icon
/// showing "recording" after the microphone closed.
/// </summary>
public class StatusSnapshotsTests
{
    private static readonly AppPaths Paths = AppPaths.Resolve(_ => null, "/home/ana");

    [Fact]
    public void The_snapshot_carries_what_the_tray_shows_and_opens()
    {
        StatusSnapshot snapshot = StatusSnapshots.Build(
            DictationState.Recording, AppSettings.Parse("{}"), tonesEnabled: true, autoStartEnabled: false, Paths);

        Assert.Equal(DictationState.Recording, snapshot.State);
        Assert.Equal("Right Ctrl", snapshot.HotkeyDescription);
        Assert.True(snapshot.TonesEnabled);
        Assert.False(snapshot.AutoStartEnabled);
        Assert.Equal("/home/ana/.config/hexlinux/settings.json", snapshot.SettingsFile);
        Assert.Equal("/home/ana/.local/state/hexlinux", snapshot.LogDirectory);
    }

    [Fact]
    public void A_shortcut_of_several_keys_reads_as_a_chord()
    {
        // The tooltip is where the user checks which keys to hold.
        AppSettings settings = AppSettings.Parse("""{"hotkey": ["LeftCtrl", "LeftAlt"]}""");

        StatusSnapshot snapshot = StatusSnapshots.Build(DictationState.Idle, settings, true, true, Paths);

        Assert.Equal("Left Ctrl + Left Alt", snapshot.HotkeyDescription);
    }

    [Fact]
    public void The_same_moment_gives_an_equal_snapshot()
    {
        // Nothing changed: nothing is pushed to the tray.
        AppSettings settings = AppSettings.Parse("{}");

        Assert.Equal(
            StatusSnapshots.Build(DictationState.Idle, settings, true, false, Paths),
            StatusSnapshots.Build(DictationState.Idle, AppSettings.Parse("{}"), true, false, Paths));
    }

    [Fact]
    public void Any_change_gives_a_different_snapshot()
    {
        AppSettings settings = AppSettings.Parse("{}");
        StatusSnapshot idle = StatusSnapshots.Build(DictationState.Idle, settings, true, false, Paths);

        Assert.NotEqual(idle, StatusSnapshots.Build(DictationState.Recording, settings, true, false, Paths));
        Assert.NotEqual(idle, StatusSnapshots.Build(DictationState.Idle, settings, false, false, Paths));
        Assert.NotEqual(idle, StatusSnapshots.Build(DictationState.Idle, settings, true, true, Paths));
    }

    [Fact]
    public void Building_without_settings_or_paths_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => StatusSnapshots.Build(DictationState.Idle, null!, true, true, Paths));
        Assert.Throws<ArgumentNullException>(() => StatusSnapshots.Build(DictationState.Idle, AppSettings.Parse("{}"), true, true, null!));
    }
}
