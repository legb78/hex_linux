using System.Globalization;
using HexLinux.Configuration;
using HexLinux.Session;

namespace HexLinux.Cli;

/// <summary>What the command line asks for.</summary>
public enum CliMode
{
    /// <summary>No argument: run the dictation daemon.</summary>
    Daemon,
    Help,
    Record,
    Transcribe,
    WatchHotkey,
    Inject,
    TestFeedback,
    Doctor,
    AutoStart,

    /// <summary>One of the control commands, sent to the running daemon.</summary>
    Control,

    /// <summary>The arguments made no sense; <see cref="CliRequest.Error"/> says why.</summary>
    Invalid,
}

/// <summary>A parsed command line.</summary>
public sealed record CliRequest(CliMode Mode)
{
    /// <summary>The WAV to write (<c>--record</c>) or to read (<c>--transcribe</c>).</summary>
    public string? Path { get; init; }

    public int Seconds { get; init; } = CommandLine.DefaultSeconds;

    public string? Model { get; init; }

    public string? Provider { get; init; }

    /// <summary>The text of <c>--inject</c>; "-" means read it from standard input.</summary>
    public string? Text { get; init; }

    public InsertionMode InjectMode { get; init; } = InsertionMode.Paste;

    public int Delay { get; init; } = CommandLine.DefaultDelay;

    /// <summary>A forced sender for <c>--inject</c>, or null to follow settings.json.</summary>
    public KeySender? Sender { get; init; }

    public bool AutoStartOn { get; init; }

    public ControlCommand Command { get; init; }

    public string? Error { get; init; }

    public bool TextFromStandardInput => Text == "-";
}

/// <summary>
/// Reads the command line.
///
/// <para>Pure, and deliberately stricter than HexWin's: an unknown option is
/// an error, not a reason to start the daemon. With a daemon that already
/// runs, a mistyped <c>--toogle</c> must say so rather than attempt a second
/// instance. Numbers stay lenient, as in HexWin: an unreadable or non-positive
/// <c>--seconds</c> or <c>--delay</c> falls back to its default, and an option
/// left without its value at the end of the line is as good as absent.</para>
/// </summary>
public static class CommandLine
{
    public const int DefaultSeconds = 5;
    public const int DefaultDelay = 4;

    private static readonly Dictionary<string, CliMode> ValuedModes = new(StringComparer.Ordinal)
    {
        ["--record"] = CliMode.Record,
        ["--transcribe"] = CliMode.Transcribe,
        ["--inject"] = CliMode.Inject,
        ["--autostart"] = CliMode.AutoStart,
    };

    private static readonly Dictionary<string, CliMode> FlagModes = new(StringComparer.Ordinal)
    {
        ["--watch-hotkey"] = CliMode.WatchHotkey,
        ["--test-feedback"] = CliMode.TestFeedback,
        ["--doctor"] = CliMode.Doctor,
        ["--toggle"] = CliMode.Control,
        ["--start"] = CliMode.Control,
        ["--stop"] = CliMode.Control,
        ["--cancel"] = CliMode.Control,
        ["--status"] = CliMode.Control,
    };

    /// <summary>Each option, with the modes it belongs to.</summary>
    private static readonly Dictionary<string, CliMode[]> Options = new(StringComparer.Ordinal)
    {
        ["--seconds"] = [CliMode.Record],
        ["--model"] = [CliMode.Transcribe],
        ["--provider"] = [CliMode.Transcribe],
        ["--mode"] = [CliMode.Inject],
        ["--delay"] = [CliMode.Inject],
        ["--sender"] = [CliMode.Inject],
    };

    public static CliRequest Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            return new CliRequest(CliMode.Daemon);
        }

        if (args.Any(arg => arg is "--help" or "-h"))
        {
            return new CliRequest(CliMode.Help);
        }

        string? modeFlag = null;
        string? modeValue = null;
        Dictionary<string, string?> options = new(StringComparer.Ordinal);

        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            bool valued = ValuedModes.ContainsKey(arg);

            if (valued || FlagModes.ContainsKey(arg))
            {
                if (modeFlag is not null)
                {
                    return Invalid($"Only one mode at a time: {modeFlag} and {arg} were both given.");
                }

                modeFlag = arg;

                if (valued)
                {
                    if (index + 1 >= args.Count)
                    {
                        return Invalid($"{arg} needs a value (see --help).");
                    }

                    modeValue = args[++index];
                }

                continue;
            }

            if (Options.ContainsKey(arg))
            {
                // A value missing at the very end counts as no option at all.
                options[arg] = index + 1 < args.Count ? args[++index] : null;
                continue;
            }

            return Invalid($"Unknown option: {arg} (see --help).");
        }

        if (modeFlag is null)
        {
            return Invalid($"No mode was given: {string.Join(' ', options.Keys)} needs one (see --help).");
        }

        CliMode mode = ValuedModes.TryGetValue(modeFlag, out CliMode valuedMode) ? valuedMode : FlagModes[modeFlag];

        foreach (string option in options.Keys)
        {
            if (!Options[option].Contains(mode))
            {
                return Invalid($"{option} does not apply to {modeFlag}.");
            }
        }

        return Build(mode, modeFlag, modeValue, options);
    }

    private static CliRequest Build(CliMode mode, string flag, string? value, Dictionary<string, string?> options)
    {
        switch (mode)
        {
            case CliMode.Record:
                return new CliRequest(mode) { Path = value, Seconds = Positive(options, "--seconds", DefaultSeconds) };

            case CliMode.Transcribe:
                return new CliRequest(mode)
                {
                    Path = value,
                    Model = options.GetValueOrDefault("--model"),
                    Provider = options.GetValueOrDefault("--provider"),
                };

            case CliMode.Inject:
                KeySender? sender = null;

                if (options.GetValueOrDefault("--sender") is { } senderName)
                {
                    if (!TryParseSender(senderName, out KeySender parsed))
                    {
                        return Invalid($"Unknown sender: {senderName} (auto, uinput, xdotool or wtype).");
                    }

                    sender = parsed;
                }

                // Refused like an unknown sender, not read as Paste: Type is
                // what a user picks to keep the text out of the clipboard,
                // and "--mode Typo" used to send it there (QA-10).
                InsertionMode injectMode = InsertionMode.Paste;

                if (options.GetValueOrDefault("--mode") is { } modeName)
                {
                    if (modeName.Equals("Type", StringComparison.OrdinalIgnoreCase))
                    {
                        injectMode = InsertionMode.Type;
                    }
                    else if (!modeName.Equals("Paste", StringComparison.OrdinalIgnoreCase))
                    {
                        return Invalid($"Unknown mode: {modeName} (Paste or Type).");
                    }
                }

                return new CliRequest(mode)
                {
                    Text = value,
                    InjectMode = injectMode,
                    Delay = Positive(options, "--delay", DefaultDelay),
                    Sender = sender,
                };

            case CliMode.AutoStart:
                return value?.ToLowerInvariant() switch
                {
                    "on" => new CliRequest(mode) { AutoStartOn = true },
                    "off" => new CliRequest(mode) { AutoStartOn = false },
                    _ => Invalid($"--autostart takes on or off, not {value}."),
                };

            case CliMode.Control:
                return new CliRequest(mode) { Command = ControlCommands.Parse(flag[2..]) };

            default:
                return new CliRequest(mode);
        }
    }

    private static bool TryParseSender(string name, out KeySender sender)
    {
        sender = KeySender.Auto;

        return name.ToLowerInvariant() switch
        {
            "auto" => true,
            "uinput" => Set(KeySender.Uinput, out sender),
            "xdotool" => Set(KeySender.Xdotool, out sender),
            "wtype" => Set(KeySender.Wtype, out sender),
            _ => false,
        };

        static bool Set(KeySender value, out KeySender target)
        {
            target = value;
            return true;
        }
    }

    private static int Positive(Dictionary<string, string?> options, string name, int fallback) =>
        int.TryParse(options.GetValueOrDefault(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : fallback;

    private static CliRequest Invalid(string error) => new(CliMode.Invalid) { Error = error };

    /// <summary>What <c>--help</c> prints.</summary>
    public const string Usage =
        """
        Usage:
          hexlinux
              run the dictation daemon: hold the shortcut (Right Ctrl by default),
              speak, release, and the text lands at the cursor

          hexlinux --toggle | --start | --stop | --cancel | --status
              drive the running daemon through its control socket. Bind
              "hexlinux --toggle" to a desktop shortcut to dictate without any
              keyboard permission: press to start, press again to stop.
              Prints the state reached (loading, idle, recording, transcribing
              or failed), "ignored (<state>)" when the command means nothing in
              that state, or "error: <word>".

          hexlinux --doctor
              check everything dictation needs and say what is missing

          hexlinux --transcribe file.wav [--model folder] [--provider cpu]
              transcribe a 16 kHz mono 16-bit WAV file (the header is checked
              first) and print the text and the time taken

          hexlinux --record out.wav [--seconds 5]
              record the microphone, write the WAV and measure the level captured

          hexlinux --watch-hotkey
              show when the shortcut starts, stops and cancels, and the name of the
              shortcut keys pressed (letters and digits are never shown)

          hexlinux --inject "some text" [--mode Paste|Type] [--delay 4] [--sender auto|uinput|xdotool|wtype]
              insert a text into the focused window after a delay. "--inject -" reads
              the text from standard input. Do not test with secrets: arguments are
              visible to every user of the machine.

          hexlinux --test-feedback
              play the start and end tones, with no model and no microphone

          hexlinux --autostart on|off
              start HexLinux, or stop starting it, with the desktop session

          hexlinux --help

        Exit codes:
          0  success
          1  generic failure: bad arguments, daemon not running or already running,
             recording too short, a file --transcribe cannot take
          2  the model is missing or incomplete (run get-model.sh)
          3  failure: microphone, tones, transcription or insertion impossible,
             or a running daemon that did not answer
          4  the recording was silent
          5  no keyboard can be read: the shortcut is unavailable (see --doctor)
        """;
}
