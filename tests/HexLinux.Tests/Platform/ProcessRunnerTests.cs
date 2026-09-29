using System.Diagnostics;
using System.Text;
using HexLinux.Platform;
using Xunit;

namespace HexLinux.Tests.Platform;

/// <summary>
/// The process shell every external tool goes through, run against fake tools
/// (review finding RV-18: it carried the no-deadlock and time-limit guarantees
/// yet only the manual smoke tests exercised it). Each case is a way a real
/// tool once could, or could, freeze the daemon's insertion: a clipboard tool
/// that forks and keeps its output open, one that never exits, one that prints
/// an image of many megabytes.
/// </summary>
[Trait("Category", "Shell")]
public sealed class ProcessRunnerTests : IDisposable
{
    private readonly FakeToolFolder _tools = new();

    public void Dispose() => _tools.Dispose();

    [Fact]
    public void The_input_reaches_the_tool_on_its_standard_input()
    {
        // The text travels on stdin, never in argv: that is the privacy rule.
        string cat = _tools.Add("cat-tool", "exec cat");

        ProcessResult result = ProcessRunner.Run(cat, [], Encoding.UTF8.GetBytes("Dictée : àéè"), TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded);
        Assert.Equal("Dictée : àéè", result.OutputText);
    }

    [Fact]
    public void Arguments_are_never_parsed_by_a_shell()
    {
        // A MIME type or a session id is all that goes in argv; even so,
        // nothing in an argument may ever be run.
        string echo = _tools.Add("echo-args", "for a in \"$@\"; do printf '%s\\n' \"$a\"; done");
        string[] arguments = ["$(touch " + _tools.File("pwned") + ")", "a b", "; rm -rf /", "-n", "`id`"];

        ProcessResult result = ProcessRunner.Run(echo, arguments, input: null, TimeSpan.FromSeconds(5));

        Assert.Equal(string.Join('\n', arguments) + "\n", result.OutputText);
        Assert.False(_tools.Has("pwned"));
    }

    [Fact]
    public void A_tool_that_never_exits_is_killed_at_its_time_limit()
    {
        // A clipboard owner that never answers makes wl-paste wait for ever:
        // the insertion must give up, and the tool must not be left behind.
        string hang = _tools.Add("hang", "echo $$ > \"$DIR/pid\"\nexec sleep 30");
        var clock = Stopwatch.StartNew();

        ProcessResult result = ProcessRunner.Run(hang, [], input: null, TimeSpan.FromMilliseconds(500));

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"returned after {clock.Elapsed.TotalSeconds:F1} s");
        Assert.True(WaitGone(int.Parse(_tools.Read("pid").Trim(), System.Globalization.CultureInfo.InvariantCulture)), "the tool is still running");
    }

    [Fact]
    public void An_output_larger_than_the_pipe_is_read_while_the_tool_runs()
    {
        // A copied screenshot is several megabytes: waiting for the exit
        // before reading would block on the full 64 KB pipe until the time
        // limit, and the clipboard would never be saved.
        string big = _tools.Add("big", "head -c 3000000 /dev/zero");

        ProcessResult result = ProcessRunner.Run(big, [], input: null, TimeSpan.FromSeconds(10), 32 * 1024 * 1024);

        Assert.True(result.Succeeded);
        Assert.False(result.Truncated);
        Assert.Equal(3_000_000, result.Output.Length);
    }

    [Fact]
    public void An_output_past_the_cap_stops_the_tool_at_once()
    {
        // Past the cap nothing more is wanted: the tool is stopped rather
        // than left to fill the pipe until the time limit.
        string huge = _tools.Add("huge", "exec cat /dev/zero");
        var clock = Stopwatch.StartNew();

        ProcessResult result = ProcessRunner.Run(huge, [], input: null, TimeSpan.FromSeconds(20), 1024 * 1024);

        Assert.True(result.Truncated);
        Assert.True(result.Output.Length <= 1024 * 1024);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"returned after {clock.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public void A_tool_that_stops_reading_its_input_is_no_error_of_ours()
    {
        // A tool refusing its input (a wtype with no compositor) exits early:
        // its exit code says why, and feeding it must not throw.
        string refuse = _tools.Add("refuse", "exit 3");

        ProcessResult result = ProcessRunner.Run(refuse, [], new byte[1024 * 1024], TimeSpan.FromSeconds(5));

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void A_missing_tool_is_reported_not_thrown()
    {
        // Uninstalled between the survey and the insertion.
        ProcessResult result = ProcessRunner.Run(_tools.File("not-there"), [], input: null, TimeSpan.FromSeconds(5));

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void A_tool_that_forks_a_server_is_waited_for_its_parent_only()
    {
        // wl-copy and xclip -i fork a child that keeps serving the clipboard
        // — with the parent's output — until the next copy. Waiting for that
        // output to close would wait for the user's next copy.
        string copy = _tools.Add("fake-copy", "cat > \"$DIR/received\"\nsleep 3 &\necho $! > \"$DIR/child\"\nexit 0");
        var clock = Stopwatch.StartNew();

        ProcessResult result = ProcessRunner.RunForking(copy, ["--type", "text/plain"], Encoding.UTF8.GetBytes("the dictation"), TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"returned after {clock.Elapsed.TotalSeconds:F1} s");
        Assert.Equal("the dictation", _tools.Read("received"));

        Kill(_tools.Read("child"));
    }

    [Fact]
    public void The_trampoline_of_a_forking_tool_parses_no_argument_either()
    {
        // The /bin/sh trampoline receives the path and the arguments as $0
        // and $@: they are expanded as words, never read as shell text.
        string record = _tools.Add("record-args", "for a in \"$@\"; do printf '%s\\n' \"$a\"; done > \"$DIR/args\"");
        string[] arguments = ["$(touch " + _tools.File("pwned") + ")", "a;b", ">x", "$HOME"];

        ProcessResult result = ProcessRunner.RunForking(record, arguments, ReadOnlyMemory<byte>.Empty, TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded);
        Assert.Equal(string.Join('\n', arguments) + "\n", _tools.Read("args"));
        Assert.False(_tools.Has("pwned"));
        Assert.False(File.Exists(Path.Combine(Environment.CurrentDirectory, "x")));
    }

    [Fact]
    public void A_forking_tool_whose_parent_hangs_is_killed_at_its_time_limit()
    {
        string hang = _tools.Add("hang-copy", "exec sleep 30");

        ProcessResult result = ProcessRunner.RunForking(hang, [], ReadOnlyMemory<byte>.Empty, TimeSpan.FromMilliseconds(500));

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
    }

    private static bool WaitGone(int pid)
    {
        for (int i = 0; i < 50; i++)
        {
            if (!Directory.Exists($"/proc/{pid}"))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static void Kill(string pid)
    {
        if (int.TryParse(pid.Trim(), System.Globalization.CultureInfo.InvariantCulture, out int id))
        {
            try
            {
                using Process child = Process.GetProcessById(id);
                child.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }
    }
}
