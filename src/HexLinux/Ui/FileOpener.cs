using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace HexLinux.Ui;

/// <summary>
/// Opens the settings file or the log folder with the desktop's default
/// application, through <c>xdg-open</c>.
///
/// <para>The path comes from the daemon's snapshot, never from the D-Bus
/// caller who clicked. <c>xdg-open</c> is found on the <c>PATH</c> and run by
/// absolute path, the path handed over as a single argument (see
/// <see cref="OpenCommand"/>): no shell ever reads it.</para>
///
/// <para>Its standard streams are inherited, not redirected: <c>xdg-open</c>
/// may hand over to an application that keeps running, and a pipe nobody
/// reads would end up blocking it, or kill it once closed.</para>
///
/// <para>Nothing here waits: the opener runs on its own, and when it gives
/// up — or is not installed at all, as in WSL — the user gets a notification
/// naming the path, so that it can still be opened by hand.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Starts a child process of the desktop; verified by the front harness with a stub xdg-open.")]
internal sealed class FileOpener
{
    private readonly Action<string> _log;
    private readonly Action<string, string> _notify;

    public FileOpener(Action<string> log, Action<string, string> notify)
    {
        _log = log;
        _notify = notify;
    }

    /// <summary>Starts opening <paramref name="path"/> and returns at once. Never throws.</summary>
    public void Open(string path)
    {
        try
        {
            if (!OpenCommand.CanOpen(path))
            {
                Fail(path, TrayText.NotAnAbsolutePath);
                return;
            }

            string? program = ExecutableSearch.Find(
                OpenCommand.Program,
                Environment.GetEnvironmentVariable("PATH"),
                DesktopNotifier.IsExecutableFile);

            if (program is null)
            {
                Fail(path, TrayText.OpenerMissing);
                return;
            }

            var start = new ProcessStartInfo(program) { UseShellExecute = false };

            foreach (string argument in OpenCommand.Arguments(path))
            {
                start.ArgumentList.Add(argument);
            }

            Process? process = Process.Start(start);

            if (process is null)
            {
                Fail(path, TrayText.OpenerMissing);
                return;
            }

            _ = ReportFailureAsync(process, path);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or ArgumentException)
        {
            Fail(path, $"xdg-open could not be started: {ex.Message}");
        }
    }

    /// <summary>
    /// Waits in the background for xdg-open to end, and tells the user when it
    /// gave up. Awaiting the exit rather than subscribing to an event also
    /// covers an opener that is already gone by the time anyone looks.
    /// </summary>
    private async Task ReportFailureAsync(Process process, string path)
    {
        try
        {
            using (process)
            {
                await process.WaitForExitAsync().ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    Fail(path, TrayText.OpenerFailed(process.ExitCode));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _log($"xdg-open ended, exit code unknown: {ex.Message}");
        }
    }

    private void Fail(string path, string reason)
    {
        _log($"cannot open {path}: {reason}");
        _notify(TrayText.CannotOpen, TrayText.CannotOpenBody(path, reason));
    }
}
