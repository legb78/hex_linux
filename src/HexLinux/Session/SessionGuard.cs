using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using HexLinux.Interop;
using HexLinux.Output;
using HexLinux.Platform;

namespace HexLinux.Session;

/// <summary>
/// Asks systemd-logind whether the user's session may take a dictation now,
/// through <c>loginctl</c>.
///
/// <para>The decision is <see cref="SessionGuardPolicy"/>'s; this shell only
/// runs loginctl — found on the PATH once, run by its absolute path, with a
/// time limit, and with nothing in its arguments but a session number or a
/// user id.</para>
///
/// <para>logind counts as present when <c>/run/systemd/seats</c> exists — the
/// folder logind maintains for its seats while it runs. loginctl is looked
/// for on the PATH, then where systemd installs it: a daemon started with a
/// reduced PATH must not take a running logind for an absent one, which would
/// let it type into a locked screen.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Runs loginctl against the real logind, verified by --doctor and the smoke tests. The decision is SessionGuardPolicy, which is tested.")]
public sealed class SessionGuard
{
    private const string SeatsDirectory = "/run/systemd/seats";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>Where systemd puts loginctl: /usr/bin on merged-/usr systems, /bin on older ones.</summary>
    private static readonly string[] UsualLoginctl = ["/usr/bin/loginctl", "/bin/loginctl"];

    private readonly string? _loginctl;
    private readonly bool _logindPresent;

    public SessionGuard(IReadOnlyDictionary<string, string> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        _loginctl = tools.GetValueOrDefault(ToolLocator.Loginctl) ?? Array.Find(UsualLoginctl, IsExecutable);
        _logindPresent = Directory.Exists(SeatsDirectory);
    }

    public GuardDecision Check()
    {
        uint uid = Libc.GetUid();

        return SessionGuardPolicy.Decide(
            _logindPresent,
            _loginctl is not null,
            uid,
            Environment.GetEnvironmentVariable("XDG_SESSION_ID"),
            id => Ask(ToolCommands.ShowSession(id)),
            () => Ask(ToolCommands.ShowUserDisplay(uid)),
            () => Ask(ToolCommands.ShowSeatActiveSession()));
    }

    /// <summary>
    /// For an insertion about to press keys: null when it may go on, or why
    /// not. logind is asked again only once <paramref name="freshness"/> has
    /// passed since this call — the check just made before stays good for
    /// that long, and asking again before every keystroke would cost a
    /// process each time. Past it, a slow clipboard tool may have left the
    /// user time to lock the screen, and the answer is renewed.
    /// </summary>
    public Func<string?> RefusalAfter(TimeSpan freshness)
    {
        var since = Stopwatch.StartNew();

        return () =>
        {
            if (since.Elapsed < freshness)
            {
                return null;
            }

            GuardDecision again = Check();
            return again.Allowed ? null : again.Reason;
        };
    }

    private LoginctlAnswer Ask(ToolCommand command)
    {
        if (_loginctl is null)
        {
            return LoginctlAnswer.Failed;
        }

        ProcessResult result = ProcessRunner.Run(_loginctl, command.Arguments, input: null, Timeout, 64 * 1024);

        return result.Succeeded ? new LoginctlAnswer(true, result.OutputText) : LoginctlAnswer.Failed;
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            return File.Exists(path) && (File.GetUnixFileMode(path) & UnixFileMode.OtherExecute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
