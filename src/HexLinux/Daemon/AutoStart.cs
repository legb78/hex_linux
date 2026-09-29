using System.Diagnostics.CodeAnalysis;
using HexLinux.Configuration;
using HexLinux.Interop;

namespace HexLinux.Daemon;

/// <summary>
/// Starting with the desktop session, through an XDG autostart entry in
/// <c>~/.config/autostart</c> — the Linux counterpart of HexWin's Run key.
///
/// <para>What the entry says is <see cref="DesktopEntry"/>'s business, and
/// what may be started at every login too (<see cref="DesktopEntry.CheckAutostart"/>).
/// This shell only reads and writes the file — through a temporary file and a
/// rename, so that a desktop reading the folder at that moment never finds
/// half an entry.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Writes into the user's autostart folder, verified by --autostart on|off and the tray's switch.")]
public static class AutoStart
{
    public static bool IsEnabled(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return File.Exists(paths.AutostartFile);
    }

    /// <summary>
    /// Writes or removes the entry. Returns false with the reason when it
    /// could not; a warning may accompany a success.
    /// </summary>
    public static bool SetEnabled(AppPaths paths, bool enabled, out string message)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            if (!enabled)
            {
                File.Delete(paths.AutostartFile);
                message = $"autostart off: {paths.AutostartFile} removed";
                return true;
            }

            string? executable = Environment.ProcessPath;

            if (executable is null)
            {
                message = "the path of the running executable is unknown";
                return false;
            }

            (string? refusal, string? warning) = Check(executable);

            if (refusal is not null)
            {
                message = refusal;
                return false;
            }

            Write(paths, DesktopEntry.Build(executable));
            message = $"autostart on: {paths.AutostartFile} starts {executable}" + (warning is null ? string.Empty : $" (note: {warning})");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            message = $"the autostart entry could not be changed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// When the entry exists but starts an executable that is gone —
    /// HexLinux was moved or updated elsewhere — points it at this one
    /// (<see cref="DesktopEntry.ShouldRepoint"/>: an entry starting another
    /// copy that still works is left alone). Only the <c>Exec</c> line
    /// changes: whatever else is in the file was the user's or the desktop's
    /// doing (a "don't start at login" switch writes <c>Hidden=true</c> or
    /// <c>X-GNOME-Autostart-enabled=false</c>) and stays. Called when the
    /// daemon starts; says what it did, or null.
    /// </summary>
    public static string? RefreshIfMoved(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            string? executable = Environment.ProcessPath;

            if (executable is null || !File.Exists(paths.AutostartFile))
            {
                return null;
            }

            string current = File.ReadAllText(paths.AutostartFile);

            if (!DesktopEntry.ShouldRepoint(current, executable, IsUsable)
                || DesktopEntry.WithExec(current, executable) is not { } wanted)
            {
                return null;
            }

            if (Check(executable).Refusal is not null)
            {
                return null;
            }

            Write(paths, wanted);
            return $"autostart entry updated to start {executable}, the executable it named being gone";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The facts <see cref="DesktopEntry.CheckAutostart"/> decides on, read from the disk.</summary>
    private static (string? Refusal, string? Warning) Check(string executable)
    {
        string folder = Path.GetDirectoryName(executable)!;

        // What "dotnet build" leaves next to a framework-dependent apphost,
        // and a self-contained single-file build does not.
        bool needsDotnetRuntime = File.Exists(Path.Combine(folder, Path.GetFileName(executable) + ".runtimeconfig.json"));

        return DesktopEntry.CheckAutostart(
            executable,
            File.GetUnixFileMode(executable),
            File.GetUnixFileMode(folder),
            new FileOwners(Libc.OwnerOf(executable), Libc.OwnerOf(folder), Libc.GetEffectiveUid()),
            needsDotnetRuntime);
    }

    /// <summary>An executable file, as far as its mode says.</summary>
    private static bool IsUsable(string path)
    {
        try
        {
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            return File.Exists(path) && (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static void Write(AppPaths paths, string content)
    {
        Directory.CreateDirectory(paths.AutostartDirectory);

        string temporary = paths.AutostartFile + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, paths.AutostartFile, overwrite: true);
    }
}
