namespace HexLinux.Daemon;

/// <summary>
/// What the status surface shows, as one immutable snapshot.
///
/// <para>One record rather than a method per detail, so that the surface can
/// never show a state and a tooltip from two different moments: the daemon
/// builds a snapshot on its loop and hands it over whole.</para>
/// </summary>
/// <param name="State">Where the dictation stands; decides the icon.</param>
/// <param name="HotkeyDescription">The shortcut as a person reads it, e.g. "Right Ctrl".</param>
/// <param name="TonesEnabled">Whether the start and end tones are on, for the menu's check mark.</param>
/// <param name="AutoStartEnabled">Whether HexLinux starts with the session, for the menu's check mark.</param>
/// <param name="SettingsFile">Full path of settings.json, for "Open settings file".</param>
/// <param name="LogDirectory">Full path of the log folder, for "Open log folder".</param>
public sealed record StatusSnapshot(
    DictationState State,
    string HotkeyDescription,
    bool TonesEnabled,
    bool AutoStartEnabled,
    string SettingsFile,
    string LogDirectory);

/// <summary>What the user asked for from the surface.</summary>
public enum SurfaceRequestKind
{
    /// <summary>Start a dictation if idle, finish it if recording — the socket's toggle.</summary>
    ToggleDictation,

    /// <summary>Turn the tones on or off; <see cref="SurfaceRequest.Value"/> says which.</summary>
    SetTones,

    /// <summary>Start with the session or not; <see cref="SurfaceRequest.Value"/> says which.</summary>
    SetAutoStart,

    /// <summary>Stop the daemon.</summary>
    Quit,
}

/// <summary>One action asked for from the surface.</summary>
/// <param name="Kind">Which action.</param>
/// <param name="Value">The new value, for the two switches; ignored otherwise.</param>
public sealed record SurfaceRequest(SurfaceRequestKind Kind, bool Value = false);

/// <summary>
/// What the user sees of the daemon: the tray icon and the notifications.
///
/// <para>The contract between the two halves of the application. The daemon
/// only ever talks to this interface, so it runs the same with a tray icon, on
/// a desktop that shows none, or with no graphical session at all; the
/// implementation behind it belongs to the interface layer, which can change
/// without the daemon noticing.</para>
/// </summary>
public interface IStatusSurface : IDisposable
{
    /// <summary>True when a tray host is actually showing the icon.</summary>
    bool IsVisible { get; }

    /// <summary>
    /// Called on the daemon loop whenever the snapshot changes. Never blocks,
    /// never throws: the loop is the one that handles the keyboard, so a
    /// synchronous D-Bus call here would hold every dictation up. The daemon
    /// still guards each call, but the surface must not rely on it.
    /// </summary>
    void Update(StatusSnapshot snapshot);

    /// <summary>A desktop notification. Never throws, never blocks the loop.</summary>
    void Notify(string title, string body);

    /// <summary>
    /// A user action. May be raised on any thread: the daemon posts it onto
    /// its loop before acting on it.
    /// </summary>
    event EventHandler<SurfaceRequest>? Requested;
}

/// <summary>
/// The surface used when nothing can be shown: no session bus, no tray host,
/// no desktop. Notifications go to the log, so that a failure still leaves a
/// trace where the user will look.
/// </summary>
public sealed class NullStatusSurface : IStatusSurface
{
    private readonly Action<string> _log;

    public NullStatusSurface(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
    }

    public bool IsVisible => false;

    /// <summary>Never raised: there is nothing for the user to click.</summary>
    public event EventHandler<SurfaceRequest>? Requested
    {
        add { }
        remove { }
    }

    public void Update(StatusSnapshot snapshot)
    {
        // Nothing to show.
    }

    public void Notify(string title, string body) => _log($"{title}: {body}");

    public void Dispose()
    {
        // Nothing to release.
    }
}
