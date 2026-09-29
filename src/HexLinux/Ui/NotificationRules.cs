namespace HexLinux.Ui;

/// <summary>
/// How a desktop notification is worded on the wire, whichever way it
/// travels: the <c>org.freedesktop.Notifications</c> interface, or
/// <c>notify-send</c> when that call fails.
///
/// <para><b>What a notification may carry.</b> A title and a body describing a
/// failure — never dictated text, clipboard content or a window title. The
/// server displays them, may keep them in a history, and the
/// <c>notify-send</c> fallback puts them on a command line that every local
/// user can read in <c>/proc/&lt;pid&gt;/cmdline</c>. The daemon only ever
/// passes failure messages; this layer adds paths and exit codes, nothing
/// else.</para>
/// </summary>
public static class NotificationRules
{
    /// <summary>The application name the server shows next to the notification.</summary>
    public const string AppName = TrayText.Product;

    /// <summary>
    /// A standard name of the freedesktop.org Icon Naming Specification, so
    /// that the icon theme in use provides the picture; the autostart entry
    /// names the same one.
    /// </summary>
    public const string Icon = "audio-input-microphone";

    /// <summary>
    /// The body as the server must receive it. The specification lets a
    /// server read the body as XML markup, in which case it announces the
    /// <c>body-markup</c> capability: a path such as <c>/home/a&amp;b</c> would
    /// then be cut at the ampersand, so the three XML-special characters are
    /// escaped. A server without that capability shows the text as it is,
    /// and must receive it unescaped, or it would print the entities.
    /// </summary>
    public static string Body(string body, bool serverReadsMarkup)
    {
        ArgumentNullException.ThrowIfNull(body);

        return serverReadsMarkup
            ? body.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
            : body;
    }

    /// <summary>
    /// The arguments of <c>notify-send</c> (libnotify): application name,
    /// icon and normal urgency, then <c>--</c>, so that a title beginning
    /// with a dash is still read as the title and not as an option. The body
    /// is left unescaped: <c>notify-send</c> cannot know whether the server
    /// reads markup either.
    /// </summary>
    public static IReadOnlyList<string> NotifySendArguments(string title, string body)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);

        return ["-a", AppName, "-i", Icon, "-u", "normal", "--", title, body];
    }
}

/// <summary>
/// Remembers the id the server gave the last notification of each title, so
/// that a failure repeated at every dictation — a locked session, a missing
/// microphone — replaces its own notification instead of piling up a new one
/// each time.
///
/// <para>The specification's <c>replaces_id</c>: the server "must atomically
/// replace the given notification with this one", and answers with the same
/// id. Zero means "a new notification". Thread-safe: notifications are sent
/// from background tasks.</para>
/// </summary>
public sealed class NotificationLedger
{
    /// <summary>
    /// The daemon's titles are a handful of constants; the bound only guards
    /// against a caller that would build titles on the fly.
    /// </summary>
    private const int MaxTitles = 32;

    private readonly Dictionary<string, uint> _lastIds = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>The id to replace for <paramref name="title"/>, or 0 for a new notification.</summary>
    public uint ReplacesIdFor(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        lock (_gate)
        {
            return _lastIds.TryGetValue(title, out uint id) ? id : 0;
        }
    }

    /// <summary>Records the id the server returned for <paramref name="title"/>.</summary>
    public void Remember(string title, uint id)
    {
        ArgumentNullException.ThrowIfNull(title);

        lock (_gate)
        {
            if (id == 0)
            {
                // The specification promises the server never returns 0; one
                // that does has told us nothing worth keeping.
                _lastIds.Remove(title);
                return;
            }

            if (!_lastIds.ContainsKey(title) && _lastIds.Count >= MaxTitles)
            {
                _lastIds.Clear();
            }

            _lastIds[title] = id;
        }
    }
}
