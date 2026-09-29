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
    /// <summary>
    /// The longest request line the daemon reads. The longest word is six
    /// letters; anything past this is not a command, and a client that sends
    /// more is cut off rather than buffered.
    /// </summary>
    public const int MaxLineLength = 64;

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

    /// <summary>The word a state goes by on the socket and on the console.</summary>
    public static string StateName(DictationState state) => state.ToString().ToLowerInvariant();
}

/// <summary>How the daemon answered a control request.</summary>
public enum ControlOutcome
{
    /// <summary>Acted on, or, for <c>status</c>, answered.</summary>
    Done,

    /// <summary>Understood, but meaningless in the current state: a start while transcribing.</summary>
    Ignored,

    /// <summary>Not understood.</summary>
    Error,
}

/// <summary>
/// The one line the daemon sends back. Its grammar, which <c>--help</c>
/// documents: <c>ok &lt;state&gt;</c>, <c>ignored &lt;state&gt;</c> or
/// <c>error &lt;word&gt;</c>, where the state is one of <c>loading idle
/// recording transcribing failed</c>.
///
/// <para>A closed vocabulary on purpose: the socket never carries dictated
/// text, a path or anything else a client could learn from — only which state
/// the dictation is in, which any process of the user could hear from the
/// tones anyway.</para>
/// </summary>
/// <param name="Outcome">What happened to the request.</param>
/// <param name="Detail">The state reached, or the error word.</param>
public readonly record struct ControlReply(ControlOutcome Outcome, string Detail)
{
    public static ControlReply Done(DictationState state) => new(ControlOutcome.Done, ControlCommands.StateName(state));

    public static ControlReply Ignored(DictationState state) => new(ControlOutcome.Ignored, ControlCommands.StateName(state));

    public static ControlReply UnknownCommand { get; } = new(ControlOutcome.Error, "unknown-command");

    /// <summary>The daemon did not act on the command in time; nothing is known of its state.</summary>
    public static ControlReply Busy { get; } = new(ControlOutcome.Error, "busy");

    public string Format() => Outcome switch
    {
        ControlOutcome.Done => "ok " + Detail,
        ControlOutcome.Ignored => "ignored " + Detail,
        _ => "error " + Detail,
    };

    /// <summary>Reads a reply line on the client side; false for anything off-grammar.</summary>
    public static bool TryParse(string? line, out ControlReply reply)
    {
        reply = default;
        string[] parts = (line ?? string.Empty).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2 || parts[1].Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        ControlOutcome? outcome = parts[0] switch
        {
            "ok" => ControlOutcome.Done,
            "ignored" => ControlOutcome.Ignored,
            "error" => ControlOutcome.Error,
            _ => null,
        };

        if (outcome is null)
        {
            return false;
        }

        reply = new ControlReply(outcome.Value, parts[1]);
        return true;
    }
}
