using System.Globalization;
using HexLinux.Diagnostics;
using Xunit;

namespace HexLinux.Tests.Diagnostics;

/// <summary>
/// The shape of a log line: HexWin's, so the two logs read alike, and fixed,
/// so scripts that read it — and people comparing two logs from two machines
/// — are never thrown by a locale.
/// </summary>
public class LogLineTests
{
    [Fact]
    public void A_line_is_the_time_to_the_second_two_spaces_and_the_message()
    {
        Assert.Equal(
            "2026-09-29 08:05:03  model loaded in 1.8 s\n",
            LogLine.Format(new DateTime(2026, 9, 29, 8, 5, 3, 750), "model loaded in 1.8 s"));
    }

    [Fact]
    public void The_hours_run_to_24_without_am_or_pm()
    {
        Assert.Equal("2026-09-29 23:59:59  stopped\n", LogLine.Format(new DateTime(2026, 9, 29, 23, 59, 59), "stopped"));
    }

    [Fact]
    public void A_locale_with_other_separators_does_not_change_the_line()
    {
        // In a custom format ":" stands for the culture's time separator,
        // which a locale may set to ".": the line must keep its colons.
        CultureInfo original = CultureInfo.CurrentCulture;
        var dotted = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        dotted.DateTimeFormat.TimeSeparator = ".";
        dotted.DateTimeFormat.DateSeparator = ".";

        try
        {
            CultureInfo.CurrentCulture = dotted;

            Assert.Equal("2026-09-29 08:05:03  started\n", LogLine.Format(new DateTime(2026, 9, 29, 8, 5, 3), "started"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Formatting_no_message_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => LogLine.Format(DateTime.Now, null!));
    }
}
