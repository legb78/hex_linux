namespace HexLinux.Ui;

/// <summary>
/// Finds the full path of a command on the <c>PATH</c>, so that the surface
/// runs <c>xdg-open</c> and <c>notify-send</c> by absolute path.
///
/// <para>Starting a bare name would leave the lookup to the runtime, which
/// also reads an empty <c>PATH</c> entry as the current directory: a daemon
/// has no business running whatever file of that name sits where it was
/// started. Here empty and relative entries are skipped, and a missing
/// command is known before anything is started, so the user can be told
/// which package to install.</para>
///
/// <para>The same rules as <c>Platform/ToolLocator.Find</c>, kept in the
/// interface layer so that it depends on nothing but the front/back contract.
/// Pure: the executable test is passed in.</para>
/// </summary>
public static class ExecutableSearch
{
    public static string? Find(string command, string? path, Func<string, bool> isExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(isExecutable);

        if (command.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("A command name, not a path, is expected.", nameof(command));
        }

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string directory in path.Split(':'))
        {
            if (!directory.StartsWith('/'))
            {
                continue;
            }

            string candidate = Path.Combine(directory, command);

            if (isExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
