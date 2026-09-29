namespace HexLinux.Configuration;

/// <summary>
/// Where HexLinux keeps its files, following the XDG Base Directory
/// specification.
///
/// <para>The Windows version keeps settings.json next to the executable. On
/// Linux the executable often lands somewhere the user cannot write — a
/// package, <c>/opt</c>, <c>~/.local/bin</c> shared with other tools — and
/// users expect configuration in <c>~/.config</c>, data in
/// <c>~/.local/share</c> and logs in <c>~/.local/state</c>.</para>
///
/// <para>Pure: the environment is passed in, so every case the specification
/// describes can be tested without touching the variables of the test
/// process.</para>
/// </summary>
/// <param name="ConfigDirectory">settings.json lives here.</param>
/// <param name="DataDirectory">The downloaded model lives here.</param>
/// <param name="StateDirectory">The log lives here.</param>
/// <param name="RuntimeDirectory">The control socket lives here.</param>
/// <param name="AutostartDirectory">
/// The desktop's autostart folder, <c>~/.config/autostart</c>: shared with
/// every other application, so not inside <paramref name="ConfigDirectory"/>.
/// </param>
public sealed record AppPaths(
    string ConfigDirectory,
    string DataDirectory,
    string StateDirectory,
    string RuntimeDirectory,
    string AutostartDirectory)
{
    public const string ApplicationFolder = "hexlinux";

    public string SettingsFile => Path.Combine(ConfigDirectory, AppSettings.FileName);

    public string LogFile => Path.Combine(StateDirectory, "hexlinux.log");

    public string CrashFile => Path.Combine(StateDirectory, "crash.log");

    public string ControlSocket => Path.Combine(RuntimeDirectory, "control.sock");

    /// <summary>
    /// Held locked for as long as a daemon runs: the one reliable answer to
    /// "is another one running?", where a socket file left behind by a crash
    /// would lie.
    /// </summary>
    public string LockFile => Path.Combine(RuntimeDirectory, "daemon.lock");

    /// <summary>The entry that starts HexLinux with the session.</summary>
    public string AutostartFile => Path.Combine(AutostartDirectory, Daemon.DesktopEntry.FileName);

    /// <summary>
    /// Resolves the four folders.
    ///
    /// <para>The specification says a relative path in an XDG variable is
    /// invalid and must be ignored, which is what happens here: it would
    /// otherwise depend on the directory the daemon happened to be started
    /// from.</para>
    ///
    /// <para><c>XDG_RUNTIME_DIR</c> has no default in the specification. It is
    /// set by every systemd-logind session; without it, the control socket
    /// falls back to the state folder, which is private to the user as
    /// well.</para>
    /// </summary>
    public static AppPaths Resolve(Func<string, string?> environment, string home)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        string config = Base(environment, "XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
        string data = Base(environment, "XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");
        string state = Base(environment, "XDG_STATE_HOME") ?? Path.Combine(home, ".local", "state");

        string stateDirectory = Path.Combine(state, ApplicationFolder);
        string? runtime = Base(environment, "XDG_RUNTIME_DIR");

        return new AppPaths(
            Path.Combine(config, ApplicationFolder),
            Path.Combine(data, ApplicationFolder),
            stateDirectory,
            runtime is null ? stateDirectory : Path.Combine(runtime, ApplicationFolder),
            Path.Combine(config, "autostart"));
    }

    /// <summary>Variant wired to the real environment of the process.</summary>
    public static AppPaths FromEnvironment() =>
        Resolve(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static string? Base(Func<string, string?> environment, string variable)
    {
        string? value = environment(variable)?.Trim();

        return string.IsNullOrEmpty(value) || !Path.IsPathRooted(value) ? null : value;
    }
}
