using HexLinux.Daemon;
using HexLinux.Ui;
using Xunit;

namespace HexLinux.Tests.Ui;

public class TrayMenuTests
{
    private static StatusSnapshot Snapshot(
        DictationState state = DictationState.Idle,
        bool tones = true,
        bool autoStart = false,
        string settings = "/home/ada/.config/hexlinux/settings.json",
        string logs = "/home/ada/.local/state/hexlinux") =>
        new(state, "Right Ctrl", tones, autoStart, settings, logs);

    private static TrayMenuItem Entry(StatusSnapshot snapshot, int id) =>
        TrayMenu.Build(snapshot).Single(item => item.Id == id);

    [Fact]
    public void The_entries_come_in_the_expected_order()
    {
        // The brief's menu: dictate, the two files, the two switches, quit,
        // in groups separated by lines.
        IReadOnlyList<TrayMenuItem> menu = TrayMenu.Build(Snapshot());

        Assert.Equal(
            ["Dictate now", "", "Open settings file", "Open log folder", "", "Play tones", "Start at login", "", "Quit"],
            menu.Select(item => item.Label));
        Assert.Equal(
            [false, true, false, false, true, false, false, true, false],
            menu.Select(item => item.IsSeparator));
    }

    [Fact]
    public void The_ids_are_fixed_and_unique()
    {
        // Hosts cache the layout and send clicks by id: an id that moved
        // between two snapshots would click the wrong entry.
        IReadOnlyList<TrayMenuItem> idle = TrayMenu.Build(Snapshot());
        IReadOnlyList<TrayMenuItem> recording = TrayMenu.Build(Snapshot(DictationState.Recording, tones: false, autoStart: true));

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], idle.Select(item => item.Id));
        Assert.Equal(idle.Select(item => item.Id), recording.Select(item => item.Id));
        Assert.Equal(TrayMenu.DictateId, idle[0].Id);
        Assert.Equal(TrayMenu.QuitId, idle[^1].Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void The_check_marks_follow_the_snapshot(bool tones, bool autoStart)
    {
        // The daemon's word is the only truth: a mark that moved on a click
        // the daemon then failed to apply would lie.
        StatusSnapshot snapshot = Snapshot(tones: tones, autoStart: autoStart);

        TrayMenuItem tonesEntry = Entry(snapshot, TrayMenu.TonesId);
        TrayMenuItem autoStartEntry = Entry(snapshot, TrayMenu.AutoStartId);

        Assert.True(tonesEntry.IsCheckbox);
        Assert.True(autoStartEntry.IsCheckbox);
        Assert.Equal(tones, tonesEntry.IsChecked);
        Assert.Equal(autoStart, autoStartEntry.IsChecked);
    }

    [Theory]
    [InlineData(DictationState.Idle, true)]
    [InlineData(DictationState.Recording, true)]
    [InlineData(DictationState.Loading, false)]
    [InlineData(DictationState.Transcribing, false)]
    [InlineData(DictationState.Failed, false)]
    public void Dictating_is_offered_only_when_the_daemon_would_accept_it(DictationState state, bool enabled)
    {
        // A toggle during a transcription is refused by design (two dictations
        // would tread on each other); a live entry would promise otherwise.
        Assert.Equal(enabled, Entry(Snapshot(state), TrayMenu.DictateId).Enabled);
    }

    [Fact]
    public void While_recording_the_entry_offers_to_finish()
    {
        // The same toggle now ends the dictation and inserts the text.
        Assert.Equal("Finish dictation", Entry(Snapshot(DictationState.Recording), TrayMenu.DictateId).Label);
        Assert.Equal("Dictate now", Entry(Snapshot(DictationState.Idle), TrayMenu.DictateId).Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("settings.json")]
    public void A_file_entry_is_disabled_until_there_is_an_absolute_path(string path)
    {
        // Before the first snapshot the paths are empty; a relative one would
        // be opened against whatever directory the daemon runs in.
        StatusSnapshot snapshot = Snapshot(settings: path, logs: path);

        Assert.False(Entry(snapshot, TrayMenu.OpenSettingsId).Enabled);
        Assert.False(Entry(snapshot, TrayMenu.OpenLogId).Enabled);
    }

    [Fact]
    public void Quitting_is_always_possible()
    {
        // Even with the model broken, the user must be able to stop the daemon.
        Assert.True(Entry(Snapshot(DictationState.Failed), TrayMenu.QuitId).Enabled);
    }

    // --- What a click asks for ---------------------------------------------------------

    [Fact]
    public void Dictate_asks_for_the_toggle()
    {
        Assert.Equal(new SurfaceRequest(SurfaceRequestKind.ToggleDictation), TrayMenu.RequestFor(MenuCommand.ToggleDictation, Snapshot()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_switch_asks_for_the_opposite_of_the_mark_it_showed(bool shown)
    {
        // Two quick clicks on a menu that has not refreshed yet ask twice for
        // the same value — harmless — instead of cancelling each other out.
        StatusSnapshot snapshot = Snapshot(tones: shown, autoStart: shown);

        Assert.Equal(new SurfaceRequest(SurfaceRequestKind.SetTones, !shown), TrayMenu.RequestFor(MenuCommand.ToggleTones, snapshot));
        Assert.Equal(new SurfaceRequest(SurfaceRequestKind.SetAutoStart, !shown), TrayMenu.RequestFor(MenuCommand.ToggleAutoStart, snapshot));
    }

    [Fact]
    public void Quit_asks_to_stop_the_daemon()
    {
        Assert.Equal(new SurfaceRequest(SurfaceRequestKind.Quit), TrayMenu.RequestFor(MenuCommand.Quit, Snapshot()));
    }

    [Theory]
    [InlineData(MenuCommand.OpenSettingsFile)]
    [InlineData(MenuCommand.OpenLogFolder)]
    [InlineData(MenuCommand.None)]
    public void Opening_a_file_asks_nothing_of_the_daemon(MenuCommand command)
    {
        // The surface opens the files itself; the contract has no request for it.
        Assert.Null(TrayMenu.RequestFor(command, Snapshot()));
    }
}
