using HexLinux.Daemon;

namespace HexLinux.Ui;

/// <summary>What an entry of the tray menu does when clicked.</summary>
public enum MenuCommand
{
    /// <summary>Nothing: a separator.</summary>
    None,

    ToggleDictation,
    OpenSettingsFile,
    OpenLogFolder,
    ToggleTones,
    ToggleAutoStart,
    Quit,
}

/// <summary>One entry of the tray menu, as the snapshot of the moment makes it.</summary>
/// <param name="Id">Stable across snapshots: the tray host remembers entries by it.</param>
/// <param name="Command">What a click does.</param>
/// <param name="Label">The text shown; empty for a separator.</param>
/// <param name="IsSeparator">A line between groups of entries.</param>
/// <param name="Enabled">False when a click could only be refused.</param>
/// <param name="IsCheckbox">An entry that carries a check mark.</param>
/// <param name="IsChecked">The check mark, for a checkbox.</param>
public sealed record TrayMenuItem(
    int Id,
    MenuCommand Command,
    string Label,
    bool IsSeparator,
    bool Enabled,
    bool IsCheckbox,
    bool IsChecked)
{
    public static TrayMenuItem Action(int id, MenuCommand command, string label, bool enabled) =>
        new(id, command, label, IsSeparator: false, enabled, IsCheckbox: false, IsChecked: false);

    public static TrayMenuItem Checkbox(int id, MenuCommand command, string label, bool isChecked) =>
        new(id, command, label, IsSeparator: false, Enabled: true, IsCheckbox: true, isChecked);

    public static TrayMenuItem Separator(int id) =>
        new(id, MenuCommand.None, string.Empty, IsSeparator: true, Enabled: true, IsCheckbox: false, IsChecked: false);
}

/// <summary>
/// The tray menu, built from the daemon's snapshot.
///
/// <para>HexWin's menu, less what V1 does not have on Linux (the settings
/// window, the on-screen circle), plus "Dictate now": without it, a user
/// whose keyboard cannot be read — no udev rule yet — would have no way to
/// dictate from the tray at all.</para>
///
/// <para>The check marks come from the snapshot and only from it. A click
/// asks the daemon for the change and leaves the menu as it was; the mark
/// moves when the daemon's next snapshot says the change took. A choice that
/// failed to apply — an autostart entry that could not be written — is
/// therefore never shown as done.</para>
///
/// <para>Pure: the D-Bus layer turns this list into the dbusmenu protocol, see
/// <see cref="DBusMenuLayout"/>.</para>
/// </summary>
public static class TrayMenu
{
    // The ids are fixed: hosts cache the layout and refer to entries by id,
    // so an entry must keep its id from one snapshot to the next.
    public const int DictateId = 1;
    public const int OpenSettingsId = 3;
    public const int OpenLogId = 4;
    public const int TonesId = 6;
    public const int AutoStartId = 7;
    public const int QuitId = 9;

    public static IReadOnlyList<TrayMenuItem> Build(StatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        bool recording = snapshot.State == DictationState.Recording;

        return
        [
            // Only offered when the daemon would accept it: while the model
            // loads, while a transcription runs or once the model failed, a
            // toggle is refused anyway, and a live entry would claim otherwise.
            TrayMenuItem.Action(
                DictateId,
                MenuCommand.ToggleDictation,
                recording ? TrayText.FinishDictation : TrayText.DictateNow,
                enabled: snapshot.State is DictationState.Idle or DictationState.Recording),
            TrayMenuItem.Separator(2),
            TrayMenuItem.Action(
                OpenSettingsId,
                MenuCommand.OpenSettingsFile,
                TrayText.OpenSettingsFile,
                enabled: OpenCommand.CanOpen(snapshot.SettingsFile)),
            TrayMenuItem.Action(
                OpenLogId,
                MenuCommand.OpenLogFolder,
                TrayText.OpenLogFolder,
                enabled: OpenCommand.CanOpen(snapshot.LogDirectory)),
            TrayMenuItem.Separator(5),
            TrayMenuItem.Checkbox(TonesId, MenuCommand.ToggleTones, TrayText.PlayTones, snapshot.TonesEnabled),
            TrayMenuItem.Checkbox(AutoStartId, MenuCommand.ToggleAutoStart, TrayText.StartAtLogin, snapshot.AutoStartEnabled),
            TrayMenuItem.Separator(8),
            TrayMenuItem.Action(QuitId, MenuCommand.Quit, TrayText.Quit, enabled: true),
        ];
    }

    /// <summary>
    /// What the daemon is asked for when <paramref name="command"/> is
    /// clicked in a menu that showed <paramref name="shown"/>; null for the
    /// commands the surface carries out itself (opening a file or folder) and
    /// for separators.
    ///
    /// <para>A checkbox asks for the opposite of the mark it showed, not for a
    /// flip of whatever the daemon holds now: two quick clicks on a stale menu
    /// then ask twice for the same value, which is harmless, instead of
    /// cancelling each other out.</para>
    /// </summary>
    public static SurfaceRequest? RequestFor(MenuCommand command, StatusSnapshot shown)
    {
        ArgumentNullException.ThrowIfNull(shown);

        return command switch
        {
            MenuCommand.ToggleDictation => new SurfaceRequest(SurfaceRequestKind.ToggleDictation),
            MenuCommand.ToggleTones => new SurfaceRequest(SurfaceRequestKind.SetTones, !shown.TonesEnabled),
            MenuCommand.ToggleAutoStart => new SurfaceRequest(SurfaceRequestKind.SetAutoStart, !shown.AutoStartEnabled),
            MenuCommand.Quit => new SurfaceRequest(SurfaceRequestKind.Quit),
            _ => null,
        };
    }
}
