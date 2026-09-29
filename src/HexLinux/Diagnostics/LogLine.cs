using System.Globalization;

namespace HexLinux.Diagnostics;

/// <summary>
/// The shape of one line of the log: the local time to the second, two
/// spaces, the message — the format HexWin's log has always had, so the two
/// read alike.
///
/// <para>Invariant culture on purpose: the log is read by people and by
/// scripts, and a date that changes shape with the locale defeats both.</para>
/// </summary>
public static class LogLine
{
    public static string Format(DateTime time, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return string.Create(CultureInfo.InvariantCulture, $"{time:yyyy-MM-dd HH:mm:ss}  {message}\n");
    }
}
