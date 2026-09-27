namespace HexLinux.Session;

/// <summary>
/// What systemd-logind says of the user's graphical session: whether it is
/// the one in front of the screen, and whether that screen is locked.
///
/// <para><b>Why it matters more on Linux than on Windows.</b> The keyboard is
/// read from <c>/dev/input</c>, below the desktop. The kernel reports keys
/// there whatever the desktop is doing: on the lock screen, and while another
/// user's session is in front after a user switch. Windows stops delivering
/// keys to a hook in both cases; Linux does not. Without this check, the
/// shortcut pressed on a locked screen would dictate into the password field,
/// and the shortcut pressed by the next user would dictate into their
/// session.</para>
///
/// <para>A value that could not be read is null, and counts as allowing:
/// outside logind — a container, WSL — there is no session to protect, and
/// refusing every insertion there would make HexLinux unusable where it
/// cannot do harm.</para>
/// </summary>
/// <param name="Active">True when this session is the one in the foreground of its seat.</param>
/// <param name="Locked">True when its screen locker is up.</param>
public readonly record struct LogindState(bool? Active, bool? Locked)
{
    public static readonly LogindState Unknown = new(null, null);

    /// <summary>
    /// Whether dictated text may be inserted now. Only a positive "inactive"
    /// or "locked" refuses.
    /// </summary>
    public bool AllowsInsertion => Active != false && Locked != true;

    /// <summary>
    /// Reads the output of
    /// <c>loginctl show-session ID --property=Active --property=LockedHint</c>:
    /// one <c>Key=value</c> per line, <c>yes</c> or <c>no</c>.
    /// </summary>
    public static LogindState Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        bool? active = null;
        bool? locked = null;

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0)
            {
                continue;
            }

            string key = line[..equals];
            bool? value = ReadBoolean(line[(equals + 1)..]);

            if (key == "Active")
            {
                active = value;
            }
            else if (key == "LockedHint")
            {
                locked = value;
            }
        }

        return new LogindState(active, locked);
    }

    private static bool? ReadBoolean(string value) => value.Trim() switch
    {
        "yes" => true,
        "no" => false,
        _ => null,
    };
}
