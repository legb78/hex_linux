using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace HexLinux.Platform;

/// <summary>What a tool did: its exit code, and what it printed.</summary>
/// <param name="ExitCode">The exit code, or -1 when it could not run or was killed.</param>
/// <param name="Output">Standard output, cut at the cap asked for.</param>
/// <param name="Error">The start of standard error, for the diagnostics.</param>
/// <param name="TimedOut">True when it was killed for taking too long.</param>
/// <param name="Truncated">True when the output went past the cap.</param>
public sealed record ProcessResult(int ExitCode, byte[] Output, string Error, bool TimedOut, bool Truncated)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    public string OutputText => Encoding.UTF8.GetString(Output);

    public static ProcessResult NotStarted(string reason) => new(-1, [], reason, false, false);
}

/// <summary>
/// Runs the external tools — clipboard, keystrokes, loginctl — with a time
/// limit, and never through a shell that parses their arguments.
///
/// <para><b>Two ways of running, because two kinds of tools.</b> Most tools
/// print and exit: their output is read while they run — reading only after
/// the exit would deadlock as soon as the output fills the 64 KB pipe, a
/// copied image being enough. wl-copy, xclip and xsel in input mode behave
/// otherwise: they fork a child that stays alive to serve the clipboard
/// until something else is copied. That child inherits standard output and
/// error; were they pipes of ours, reading them to the end would wait for the
/// user's next copy. For those, only standard input is connected, the rest
/// goes to <c>/dev/null</c>, and only the parent is waited for.</para>
///
/// <para>.NET cannot point a child's output at <c>/dev/null</c> directly, so
/// the forking tools go through a constant <c>/bin/sh</c> trampoline,
/// <c>exec "$0" "$@" &gt;/dev/null 2&gt;&amp;1</c>: the tool's absolute path
/// and its arguments arrive as <c>$0</c> and <c>$@</c> through
/// <see cref="ProcessStartInfo.ArgumentList"/>, and are never parsed as shell
/// text. The data itself travels on standard input, never in the arguments,
/// which every local user can read in <c>/proc</c>.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Starts real processes; the command lines it runs come from ToolCommands, which is tested.")]
public static class ProcessRunner
{
    private const string Shell = "/bin/sh";
    private const string DetachScript = "exec \"$0\" \"$@\" >/dev/null 2>&1";
    private const int ErrorCap = 2048;

    /// <summary>Runs a tool that prints and exits, feeding it <paramref name="input"/>.</summary>
    public static ProcessResult Run(
        string path,
        IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte>? input,
        TimeSpan timeout,
        int maxOutputBytes = 1024 * 1024)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;

        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("the process did not start");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return ProcessResult.NotStarted(ex.Message);
        }

        using (process)
        {
            var output = new MemoryStream();
            bool truncated = false;

            Task reading = Task.Run(() =>
            {
                truncated = CopyCapped(process.StandardOutput.BaseStream, output, maxOutputBytes);

                // Past the cap the rest is not wanted: stop the tool now
                // rather than let it block on a full pipe until the timeout.
                if (truncated)
                {
                    Kill(process);
                }
            });
            Task<string> errors = Task.Run(() => ReadCapped(process.StandardError.BaseStream, ErrorCap));
            Task writing = Task.Run(() => Feed(process, input));

            bool exited = process.WaitForExit(timeout);

            if (!exited)
            {
                Kill(process);
            }

            // The readers end when the pipes close, which the exit (or the
            // kill) guarantees; bounded anyway, a grandchild could hold them.
            Task.WaitAll([reading, errors, writing], TimeSpan.FromSeconds(1));

            int exitCode = exited ? process.ExitCode : -1;
            string error = errors.IsCompletedSuccessfully ? errors.Result : string.Empty;

            return new ProcessResult(exitCode, output.ToArray(), error, !exited, truncated);
        }
    }

    /// <summary>
    /// Runs a tool that forks to serve the clipboard: standard input only,
    /// the rest to <c>/dev/null</c>, and only the parent waited for.
    /// </summary>
    public static ProcessResult RunForking(string path, IReadOnlyList<string> arguments, ReadOnlyMemory<byte> input, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(Shell)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
        };

        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(DetachScript);
        start.ArgumentList.Add(path);

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;

        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("the process did not start");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return ProcessResult.NotStarted(ex.Message);
        }

        using (process)
        {
            Task writing = Task.Run(() => Feed(process, input));
            bool exited = process.WaitForExit(timeout);

            if (!exited)
            {
                Kill(process);
            }

            writing.Wait(TimeSpan.FromSeconds(1));

            return new ProcessResult(exited ? process.ExitCode : -1, [], string.Empty, !exited, false);
        }
    }

    private static void Feed(Process process, ReadOnlyMemory<byte>? input)
    {
        try
        {
            using Stream stdin = process.StandardInput.BaseStream;

            if (input is { Length: > 0 } data)
            {
                stdin.Write(data.Span);
            }
        }
        catch (IOException)
        {
            // The tool exited without reading everything: its exit code says why.
        }
    }

    private static bool CopyCapped(Stream source, MemoryStream target, int cap)
    {
        byte[] buffer = new byte[64 * 1024];

        try
        {
            int read;

            while ((read = source.Read(buffer)) > 0)
            {
                if (target.Length + read > cap)
                {
                    return true;
                }

                target.Write(buffer, 0, read);
            }
        }
        catch (IOException)
        {
            // The pipe was closed by a kill.
        }

        return false;
    }

    private static string ReadCapped(Stream source, int cap)
    {
        using var capped = new MemoryStream();
        bool truncated = CopyCapped(source, capped, cap);

        if (truncated)
        {
            // Drain the rest so the tool is never blocked on a full pipe.
            try
            {
                source.CopyTo(Stream.Null);
            }
            catch (IOException)
            {
                // Closed.
            }
        }

        return Encoding.UTF8.GetString(capped.ToArray()).Trim();
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }
}
