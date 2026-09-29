using System.Diagnostics.CodeAnalysis;
using System.Text;
using HexLinux.Configuration;
using HexLinux.Platform;

namespace HexLinux.Output;

/// <summary>What an insertion came to.</summary>
public enum InsertionStatus
{
    Inserted,

    /// <summary>Left in the clipboard for the user to paste (<c>clipboardFallback</c>).</summary>
    CopiedOnly,

    Failed,

    /// <summary>
    /// Held back at the last moment: the session locked, or went to the
    /// background, while the clipboard was being prepared.
    /// </summary>
    Refused,
}

/// <summary>The outcome of one insertion, with a reason when it did not work; never the text.</summary>
public readonly record struct InsertionResult(InsertionStatus Status, string? Problem = null);

/// <summary>
/// Hands the transcribed text to the focused application, the way the
/// <see cref="InjectionPlanner"/> decided.
///
/// <para>Two routes, as on Windows. Pasting goes through the clipboard then
/// the paste shortcut: instant whatever the length, and the default. Typing
/// sends the characters through xdotool or wtype, more slowly, for
/// applications that ignore the clipboard. The text always travels on the
/// tools' standard input, never in their arguments.</para>
///
/// <para><b>Held modifiers.</b> HexWin released the modifiers still held
/// before pasting. The uinput keyboard cannot: libinput keeps each keyboard's
/// key state apart, so a release written to the virtual keyboard does not
/// release a key held on the real one, while the compositor still combines
/// both into one set of modifiers. xdotool clears them itself
/// (<c>--clearmodifiers</c>); for the others the daemon waits, before the end
/// of a dictation, for the user's fingers to leave them (see
/// <see cref="ModifierGuard"/>).</para>
///
/// <para>Everything here blocks — tools to run, a delay before restoring the
/// clipboard — and so runs off the daemon's loop, which awaits it.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Writes into the focused window of the real desktop, verified by --inject and the smoke tests. The decisions are InjectionPlanner's, which is tested.")]
public sealed class TextInjector
{
    /// <summary>
    /// Grace period left to the target application to read the clipboard
    /// before it is restored — HexWin's value. Too short and the paste picks
    /// up the old content; too long and a paste the user makes right after
    /// gets the dictation again.
    /// </summary>
    private static readonly TimeSpan ClipboardRestoreDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Between filling the clipboard and pressing the shortcut. The clipboard
    /// tool's forked child owns the selection by then (xclip takes it before
    /// forking); the pause covers a compositor slower to announce it — a
    /// precaution, not a measured need.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan KeysTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long a refused wtype probe is believed before it is asked again.</summary>
    private static readonly TimeSpan WtypeRetryAfter = TimeSpan.FromMinutes(1);

    private readonly UinputKeyboard? _uinput;
    private readonly Action<string> _log;
    private bool? _wtypeWorks;
    private long _wtypeProbedAt;

    /// <param name="uinput">The virtual keyboard, created once by the daemon; null when unavailable.</param>
    /// <param name="log">For failures: tool names and exit codes, never text.</param>
    public TextInjector(UinputKeyboard? uinput, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _uinput = uinput;
        _log = log;
    }

    /// <summary>
    /// What the planner needs, looked at now: tools can be installed while
    /// the daemon runs. The wtype probe runs under Wayland only; a success is
    /// kept for good, a refusal is asked again after
    /// <see cref="WtypeRetryAfter"/>: a probe made while the compositor was
    /// still starting must not turn wtype off until the daemon restarts.
    /// </summary>
    public (InjectionContext Context, IReadOnlyDictionary<string, string> Tools) Survey(DesktopSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        IReadOnlyDictionary<string, string> tools = ToolLocator.Locate();

        bool probeDue = _wtypeWorks is null
            || (_wtypeWorks == false && Environment.TickCount64 - _wtypeProbedAt >= (long)WtypeRetryAfter.TotalMilliseconds);

        if (probeDue && session.Server == DisplayServer.Wayland && tools.TryGetValue(ToolLocator.Wtype, out string? wtype))
        {
            ToolCommand probe = ToolCommands.WtypeProbe();
            _wtypeWorks = ProcessRunner.Run(wtype, probe.Arguments, ReadOnlyMemory<byte>.Empty, KeysTimeout).Succeeded;
            _wtypeProbedAt = Environment.TickCount64;
        }

        return (new InjectionContext(session, tools.Keys.ToHashSet(StringComparer.Ordinal), _uinput is not null, _wtypeWorks), tools);
    }

    /// <summary>Carries out <paramref name="plan"/> for <paramref name="text"/>.</summary>
    /// <param name="text">What to insert.</param>
    /// <param name="plan">How, from <see cref="InjectionPlanner"/>.</param>
    /// <param name="shortcut">The paste keystroke.</param>
    /// <param name="tools">Tool names and their full paths.</param>
    /// <param name="sessionRefusal">
    /// Asked just before a key is sent or the clipboard is left filled: null
    /// to go on, or why not. The session was checked before the insertion
    /// began, but the clipboard tools can take seconds — time enough to lock
    /// the screen, which would then receive the paste.
    /// </param>
    public async Task<InsertionResult> InsertAsync(
        string text,
        InjectionPlan plan,
        PasteShortcut shortcut,
        IReadOnlyDictionary<string, string> tools,
        Func<string?>? sessionRefusal = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(tools);

        if (text.Length == 0)
        {
            return new InsertionResult(InsertionStatus.Inserted);
        }

        var clipboard = new Clipboard(tools, _log);
        Func<string?> refusal = sessionRefusal ?? (() => null);

        switch (plan.Outcome)
        {
            case InjectionOutcome.Impossible:
                return new InsertionResult(InsertionStatus.Failed, plan.Problem);

            case InjectionOutcome.ClipboardOnly:
                if (await Task.Run(refusal).ConfigureAwait(false) is { } lockedMeanwhile)
                {
                    return new InsertionResult(InsertionStatus.Refused, lockedMeanwhile);
                }

                bool copied = await Task.Run(() => clipboard.SetText(plan.Clipboard, text)).ConfigureAwait(false);

                return copied
                    ? new InsertionResult(InsertionStatus.CopiedOnly, plan.Problem)
                    : new InsertionResult(InsertionStatus.Failed, "the clipboard could not be filled");
        }

        if (plan.Mode == InsertionMode.Type)
        {
            return await Task.Run(() => refusal() is { } refused
                ? new InsertionResult(InsertionStatus.Refused, refused)
                : Type(text, plan.Keys, tools)).ConfigureAwait(false);
        }

        return await PasteAsync(text, plan, shortcut, tools, clipboard, refusal).ConfigureAwait(false);
    }

    private async Task<InsertionResult> PasteAsync(
        string text,
        InjectionPlan plan,
        PasteShortcut shortcut,
        IReadOnlyDictionary<string, string> tools,
        Clipboard clipboard,
        Func<string?> refusal)
    {
        ClipboardSnapshot previous = await Task.Run(() => clipboard.Capture(plan.Clipboard)).ConfigureAwait(false);

        if (!await Task.Run(() => clipboard.SetText(plan.Clipboard, text)).ConfigureAwait(false))
        {
            return new InsertionResult(InsertionStatus.Failed, "the clipboard could not be filled");
        }

        await Task.Delay(SettleDelay).ConfigureAwait(false);

        // The last moment before a key leaves: a screen locked while the
        // clipboard was saved and filled must not receive the paste. The
        // clipboard is put back as if nothing had happened.
        if (await Task.Run(refusal).ConfigureAwait(false) is { } refused)
        {
            await Task.Run(() => clipboard.Restore(plan.Clipboard, previous)).ConfigureAwait(false);
            return new InsertionResult(InsertionStatus.Refused, refused);
        }

        bool pressed = await Task.Run(() => PressPaste(plan.Keys, shortcut, tools)).ConfigureAwait(false);

        // Deferred: the target application has not read the clipboard yet
        // when the keystroke returns. Called even when nothing was saved, so
        // the dictation never stays in the clipboard.
        if (pressed)
        {
            await Task.Delay(ClipboardRestoreDelay).ConfigureAwait(false);
        }

        await Task.Run(() => clipboard.Restore(plan.Clipboard, previous)).ConfigureAwait(false);

        return pressed
            ? new InsertionResult(InsertionStatus.Inserted)
            : new InsertionResult(InsertionStatus.Failed, "the paste shortcut could not be sent");
    }

    private bool PressPaste(KeyStroker keys, PasteShortcut shortcut, IReadOnlyDictionary<string, string> tools)
    {
        if (keys == KeyStroker.Uinput)
        {
            bool sent = _uinput?.Send(KeySequences.Paste(shortcut)) == true;

            if (!sent)
            {
                _log("the virtual keyboard did not take the paste shortcut");
            }

            return sent;
        }

        ToolCommand command = ToolCommands.PasteKeys(keys, shortcut);
        return RunTool(command, ReadOnlyMemory<byte>.Empty, KeysTimeout, tools);
    }

    private InsertionResult Type(string text, KeyStroker keys, IReadOnlyDictionary<string, string> tools)
    {
        // xdotool waits 12 ms between keystrokes by default.
        TimeSpan timeout = KeysTimeout + TimeSpan.FromMilliseconds(20.0 * text.Length);
        ToolCommand command = ToolCommands.TypeText(keys);

        return RunTool(command, Encoding.UTF8.GetBytes(text), timeout, tools)
            ? new InsertionResult(InsertionStatus.Inserted)
            : new InsertionResult(InsertionStatus.Failed, $"{command.Tool} could not type the text");
    }

    private bool RunTool(ToolCommand command, ReadOnlyMemory<byte> input, TimeSpan timeout, IReadOnlyDictionary<string, string> tools)
    {
        if (!tools.TryGetValue(command.Tool, out string? path))
        {
            _log($"{command.Tool} is not installed");
            return false;
        }

        ProcessResult result = ProcessRunner.Run(path, command.Arguments, input, timeout, 64 * 1024);

        if (!result.Succeeded)
        {
            // The exit code only: the tool's messages could quote its input.
            _log($"{command.Tool} exited with {result.ExitCode}{(result.TimedOut ? " (timed out)" : "")}");
        }

        return result.Succeeded;
    }
}
