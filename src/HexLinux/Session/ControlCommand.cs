using HexLinux.Daemon;
using HexLinux.Input;

namespace HexLinux.Session;

/// <summary>A request sent to the running daemon through its control socket.</summary>
public enum ControlCommand
{
    /// <summary>Not a command this daemon knows.</summary>
    Unknown,

    /// <summary>Start a dictation if idle, finish it if recording.</summary>
    Toggle,

    Start,
    Stop,
    Cancel,

    /// <summary>Report the current state, change nothing.</summary>
    Status,
}

/// <summary>
/// The control channel's small vocabulary, and what each word asks of the
/// dictation in a given state.
///
/// <para><b>What it is for.</b> Holding a key needs <c>/dev/input</c>, and
/// reading <c>/dev/input</c> needs a permission that also lets a process read
/// everything typed on the machine. A user who would rather not grant it binds
/// <c>hexlinux --toggle</c> to a shortcut of their desktop instead — GNOME,
/// KDE and every tiling compositor let a key run a command — and dictates
/// press-to-start, press-to-stop. It is also how the whole chain is exercised
/// where no keyboard device exists at all, such as WSL.</para>
///
/// <para>Pure: the socket that carries it lives elsewhere.</para>
/// </summary>
public static class ControlCommands
{
    public static ControlCommand Parse(string? line) => line?.Trim().ToLowerInvariant() switch
    {
        "toggle" => ControlCommand.Toggle,
        "start" => ControlCommand.Start,
        "stop" => ControlCommand.Stop,
        "cancel" => ControlCommand.Cancel,
        "status" => ControlCommand.Status,
        _ => ControlCommand.Unknown,
    };

    public static string Name(ControlCommand command) => command.ToString().ToLowerInvariant();

    /// <summary>
    /// The action a command amounts to, given where the dictation stands.
    ///
    /// <para>A toggle pressed during a transcription does nothing, exactly
    /// as the held shortcut does: the coordinator would refuse the start
    /// anyway, and a toggle that silently queued one would start a recording
    /// the user no longer expects.</para>
    /// </summary>
    public static ChordAction Resolve(ControlCommand command, DictationState state) => command switch
    {
        ControlCommand.Toggle => state switch
        {
            DictationState.Idle => ChordAction.Start,
            DictationState.Recording => ChordAction.Stop,
            _ => ChordAction.None,
        },
        ControlCommand.Start => state == DictationState.Idle ? ChordAction.Start : ChordAction.None,
        ControlCommand.Stop => state == DictationState.Recording ? ChordAction.Stop : ChordAction.None,
        ControlCommand.Cancel => state is DictationState.Recording or DictationState.Transcribing
            ? ChordAction.Cancel
            : ChordAction.None,
        _ => ChordAction.None,
    };
}
