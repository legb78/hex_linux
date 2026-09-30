using HexLinux.Daemon;
using HexLinux.Ui;
using Xunit;

namespace HexLinux.Tests.Ui;

/// <summary>
/// Which signals the host receives. A missing one leaves a stale icon on the
/// panel; one too many makes some panels redraw and flicker.
/// </summary>
public class TrayPresentationTests
{
    private static TrayView View(
        DictationState state = DictationState.Idle,
        string hotkey = "Right Ctrl",
        bool tones = true) =>
        TrayPresentation.From(new StatusSnapshot(state, hotkey, tones, AutoStartEnabled: false, "/s/settings.json", "/s/logs"));

    [Fact]
    public void A_view_is_made_of_its_snapshot()
    {
        TrayView view = View(DictationState.Idle);

        Assert.Equal(DictationState.Idle, view.State);
        Assert.Equal("HexLinux — ready (Right Ctrl)", view.ToolTip);
        Assert.Equal("Active", view.Status);
        Assert.Equal(9, view.Menu.Count);
    }

    [Fact]
    public void Only_the_failed_state_asks_for_attention()
    {
        // NeedsAttention brings an icon out of the panel's overflow: right for
        // a model that must be downloaded, wrong for an ordinary dictation.
        Assert.Equal("NeedsAttention", TrayPresentation.StatusFor(DictationState.Failed));
        Assert.Equal("Active", TrayPresentation.StatusFor(DictationState.Loading));
        Assert.Equal("Active", TrayPresentation.StatusFor(DictationState.Idle));
        Assert.Equal("Active", TrayPresentation.StatusFor(DictationState.Recording));
        Assert.Equal("Active", TrayPresentation.StatusFor(DictationState.Transcribing));
    }

    [Fact]
    public void The_same_snapshot_twice_changes_nothing()
    {
        Assert.Equal(TrayChanges.None, TrayPresentation.Compare(View(), View()));
    }

    [Fact]
    public void Starting_a_dictation_changes_icon_tooltip_and_menu()
    {
        // The dot turns red, the tooltip says "recording", and the menu entry
        // becomes "Finish dictation"; the status stays Active.
        Assert.Equal(
            TrayChanges.Icon | TrayChanges.ToolTip | TrayChanges.Menu,
            TrayPresentation.Compare(View(DictationState.Idle), View(DictationState.Recording)));
    }

    [Fact]
    public void A_failed_model_also_changes_the_status()
    {
        Assert.Equal(
            TrayChanges.Icon | TrayChanges.ToolTip | TrayChanges.Status | TrayChanges.Menu,
            TrayPresentation.Compare(View(DictationState.Idle), View(DictationState.Failed)));
    }

    [Fact]
    public void Switching_the_tones_only_touches_the_menu()
    {
        Assert.Equal(TrayChanges.Menu, TrayPresentation.Compare(View(tones: true), View(tones: false)));
    }

    [Fact]
    public void A_new_shortcut_only_touches_the_tooltip()
    {
        Assert.Equal(TrayChanges.ToolTip, TrayPresentation.Compare(View(hotkey: "Right Ctrl"), View(hotkey: "F13")));
    }

    [Fact]
    public void Before_the_first_snapshot_the_tray_shows_loading_with_nothing_to_open()
    {
        // The surface exists before the daemon has said anything: it must not
        // offer to open an empty path or claim tones are on.
        TrayView initial = TrayPresentation.From(TrayPresentation.InitialSnapshot);

        Assert.Equal(DictationState.Loading, initial.State);
        Assert.False(initial.Menu.Single(item => item.Id == TrayMenu.DictateId).Enabled);
        Assert.False(initial.Menu.Single(item => item.Id == TrayMenu.OpenSettingsId).Enabled);
        Assert.False(initial.Menu.Single(item => item.Id == TrayMenu.OpenLogId).Enabled);
        Assert.All(initial.Menu.Where(item => item.IsCheckbox), item => Assert.False(item.IsChecked));
    }
}
