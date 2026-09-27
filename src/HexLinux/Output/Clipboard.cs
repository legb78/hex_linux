using System.Diagnostics.CodeAnalysis;
using System.Text;
using HexLinux.Platform;

namespace HexLinux.Output;

/// <summary>
/// What the clipboard held before a paste: one format and its bytes, kept in
/// memory only — never in a temporary file.
/// </summary>
/// <param name="Format">The MIME type or X11 target kept.</param>
/// <param name="Data">Its content.</param>
public sealed record ClipboardSnapshot(string? Format, byte[]? Data)
{
    /// <summary>Nothing to put back: the clipboard is cleared instead.</summary>
    public static ClipboardSnapshot Nothing { get; } = new(null, null);

    public bool HasContent => Format is not null && Data is not null;
}

/// <summary>
/// The desktop clipboard, through wl-clipboard, xclip or xsel: save, fill,
/// restore, clear.
///
/// <para><b>Restoring matters, and so does clearing.</b> Without restoring,
/// every dictation would overwrite what the user had copied, which gets
/// noticed at the worst possible moment. And when there was nothing to save
/// — an empty clipboard, a format that could not be read, content past the
/// size cap — the clipboard is cleared rather than left holding the
/// dictation: the Windows version's rule, since any program can read the
/// clipboard later.</para>
///
/// <para>Only the CLIPBOARD selection is touched, never PRIMARY — the one a
/// mere mouse selection fills, which the user never chose to share.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Drives the desktop's clipboard tools, verified by --inject and the smoke tests. The formats and command lines are ClipboardFormats and ToolCommands, which are tested.")]
public sealed class Clipboard
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(3);

    private readonly IReadOnlyDictionary<string, string> _tools;
    private readonly Action<string> _log;

    /// <param name="tools">Tool names and their full paths (<see cref="ToolLocator.Locate"/>).</param>
    /// <param name="log">For failures; never receives clipboard content.</param>
    public Clipboard(IReadOnlyDictionary<string, string> tools, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(log);

        _tools = tools;
        _log = log;
    }

    /// <summary>Copies what the clipboard holds now, in its preferred format.</summary>
    public ClipboardSnapshot Capture(ClipboardTool tool)
    {
        string? format;

        if (ToolCommands.ListFormats(tool) is { } list)
        {
            ProcessResult listed = Run(list, input: null, ClipboardFormats.MaxSnapshotBytes);

            if (!listed.Succeeded)
            {
                // wl-paste answers "Nothing is copied" with an error: an
                // empty clipboard, nothing to save.
                return ClipboardSnapshot.Nothing;
            }

            format = ClipboardFormats.Preferred(ClipboardFormats.ParseList(listed.OutputText));
        }
        else
        {
            format = ToolCommands.TextFormat(tool);
        }

        if (format is null)
        {
            return ClipboardSnapshot.Nothing;
        }

        ProcessResult read = Run(ToolCommands.Read(tool, format), input: null, ClipboardFormats.MaxSnapshotBytes);

        if (read.Truncated)
        {
            _log("the clipboard holds more than 32 MB: it will be cleared after the paste, not restored");
            return ClipboardSnapshot.Nothing;
        }

        return read.Succeeded && read.Output.Length > 0 ? new ClipboardSnapshot(format, read.Output) : ClipboardSnapshot.Nothing;
    }

    /// <summary>Puts <paramref name="text"/> in the clipboard, through standard input.</summary>
    public bool SetText(ClipboardTool tool, string text) =>
        RunForking(ToolCommands.Write(tool, ToolCommands.TextFormat(tool)), Encoding.UTF8.GetBytes(text), "fill");

    /// <summary>Puts the saved content back, or clears the clipboard when there is none.</summary>
    public void Restore(ClipboardTool tool, ClipboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.HasContent && RunForking(ToolCommands.Write(tool, snapshot.Format!), snapshot.Data!, "restore"))
        {
            return;
        }

        Clear(tool);
    }

    public void Clear(ClipboardTool tool) => RunForking(ToolCommands.Clear(tool), [], "clear");

    private ProcessResult Run(ToolCommand command, ReadOnlyMemory<byte>? input, int maxOutput)
    {
        if (!_tools.TryGetValue(command.Tool, out string? path))
        {
            return ProcessResult.NotStarted($"{command.Tool} is not installed");
        }

        return ProcessRunner.Run(path, command.Arguments, input, ToolTimeout, maxOutput);
    }

    private bool RunForking(ToolCommand command, byte[] input, string what)
    {
        if (!_tools.TryGetValue(command.Tool, out string? path))
        {
            _log($"clipboard {what} impossible: {command.Tool} is not installed");
            return false;
        }

        ProcessResult result = ProcessRunner.RunForking(path, command.Arguments, input, ToolTimeout);

        if (!result.Succeeded)
        {
            // The exit code only: the tool's own messages could quote what it was given.
            _log($"clipboard {what} failed: {command.Tool} exited with {result.ExitCode}{(result.TimedOut ? " (timed out)" : "")}");
        }

        return result.Succeeded;
    }
}
