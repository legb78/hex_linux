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
/// folder logind maintains for its seats while it runs — and loginctl is
/// installed.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Runs loginctl against the real logind, verified by --doctor and the smoke tests. The decision is SessionGuardPolicy, which is tested.")]
public sealed class SessionGuard
{
    private const string SeatsDirectory = "/run/systemd/seats";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly string? _loginctl;
    private readonly bool _logindPresent;

    public SessionGuard(IReadOnlyDictionary<string, string> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        _loginctl = tools.GetValueOrDefault(ToolLocator.Loginctl);
        _logindPresent = Directory.Exists(SeatsDirectory) && _loginctl is not null;
    }

    public GuardDecision Check() => SessionGuardPolicy.Decide(
        _logindPresent,
        Environment.GetEnvironmentVariable("XDG_SESSION_ID"),
        id => Ask(ToolCommands.ShowSession(id)),
        () => Ask(ToolCommands.ShowUserDisplay(Libc.GetUid())));

    private LoginctlAnswer Ask(ToolCommand command)
    {
        if (_loginctl is null)
        {
            return LoginctlAnswer.Failed;
        }

        ProcessResult result = ProcessRunner.Run(_loginctl, command.Arguments, input: null, Timeout, 64 * 1024);

        return result.Succeeded ? new LoginctlAnswer(true, result.OutputText) : LoginctlAnswer.Failed;
    }
}
