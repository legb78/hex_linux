using HexLinux.Daemon;

namespace HexLinux.Ui;

/// <summary>
/// Every text the tray shows: the tooltip of each state, the menu labels, and
/// the messages of the surface's own failures.
///
/// <para>Gathered in one place, in English only for V1, so that a French
/// interface later (HexWin has one) means one more set of these strings, not
/// a hunt through the code. The wording follows HexWin's tray, with the
/// product name in front: the tooltip is read in a panel full of other
/// icons.</para>
///
/// <para>None of these texts ever carries dictated text: only states, the
/// shortcut, paths and exit codes. A notification is displayed by another
/// process and may be logged by it.</para>
/// </summary>
public static class TrayText
{
    public const string Product = "HexLinux";

    public const string DictateNow = "Dictate now";

    /// <summary>
    /// The same entry while the microphone runs: the toggle then ends the
    /// dictation, and the text is inserted, which "stop" alone would not say.
    /// </summary>
    public const string FinishDictation = "Finish dictation";

    public const string OpenSettingsFile = "Open settings file";
    public const string OpenLogFolder = "Open log folder";
    public const string PlayTones = "Play tones";
    public const string StartAtLogin = "Start at login";
    public const string Quit = "Quit";

    /// <summary>Title of the notification raised when a file or folder cannot be opened.</summary>
    public const string CannotOpen = "Cannot open";

    /// <summary>Why nothing opened when xdg-open itself is missing, as in WSL.</summary>
    public const string OpenerMissing = "xdg-open was not found. Install xdg-utils, or open it yourself.";

    /// <summary>Why nothing opened when the path is not one xdg-open can be handed.</summary>
    public const string NotAnAbsolutePath = "Not an absolute path.";

    /// <summary>
    /// The tooltip of a state, as HexWin words it: "HexLinux — ready (Right
    /// Ctrl)". Without a shortcut to name, the parenthesis is left out rather
    /// than shown empty.
    /// </summary>
    public static string ToolTip(DictationState state, string? hotkeyDescription) => state switch
    {
        DictationState.Loading => $"{Product} — loading the model...",
        DictationState.Idle when string.IsNullOrWhiteSpace(hotkeyDescription) => $"{Product} — ready",
        DictationState.Idle => $"{Product} — ready ({hotkeyDescription!.Trim()})",
        DictationState.Recording => $"{Product} — recording",
        DictationState.Transcribing => $"{Product} — transcribing...",
        _ => $"{Product} — the model could not be loaded",
    };

    /// <summary>Why xdg-open gave up, with its exit code: the log and the bug report need it.</summary>
    public static string OpenerFailed(int exitCode) => $"xdg-open gave up (exit code {exitCode}).";

    /// <summary>
    /// Body of the "cannot open" notification: the path first, so that the
    /// user can open it by hand, then the reason.
    /// </summary>
    public static string CannotOpenBody(string path, string reason) => $"{path}\n{reason}";
}
