using System.Text;
using HexLinux.Platform;
using Xunit;

namespace HexLinux.Tests.Platform;

/// <summary>
/// How the outcome of a tool run is read. The clipboard and the injector act
/// on <see cref="ProcessResult.Succeeded"/> alone: a tool killed for taking
/// too long must never count as a success, whatever code it left behind, or
/// the clipboard would be "restored" from output that was cut short.
/// </summary>
public class ProcessResultTests
{
    [Fact]
    public void A_zero_exit_code_in_time_is_a_success()
    {
        var result = new ProcessResult(0, [], string.Empty, TimedOut: false, Truncated: false);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(127)]
    public void A_non_zero_exit_code_is_a_failure(int exitCode)
    {
        // wl-paste fails on an empty clipboard ("Nothing is copied"); -1 is
        // a tool that never ran; 127 is a program missing behind the
        // /bin/sh trampoline of the forking tools.
        var result = new ProcessResult(exitCode, [], "error", TimedOut: false, Truncated: false);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void A_run_that_timed_out_is_a_failure_even_with_a_zero_code()
    {
        var result = new ProcessResult(0, [], string.Empty, TimedOut: true, Truncated: false);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Truncated_output_alone_does_not_decide_success()
    {
        // The cap is Clipboard.Capture's business: it checks Truncated itself
        // and clears the clipboard instead of restoring a partial copy.
        var result = new ProcessResult(0, [1, 2], string.Empty, TimedOut: false, Truncated: true);

        Assert.True(result.Succeeded);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void The_output_reads_as_UTF_8()
    {
        // wl-paste --list-types and loginctl print UTF-8.
        var result = new ProcessResult(0, Encoding.UTF8.GetBytes("text/plain;charset=utf-8\nété\n"), string.Empty, false, false);

        Assert.Equal("text/plain;charset=utf-8\nété\n", result.OutputText);
    }

    [Fact]
    public void An_empty_output_reads_as_an_empty_string()
    {
        Assert.Equal(string.Empty, new ProcessResult(0, [], string.Empty, false, false).OutputText);
    }

    [Fact]
    public void A_tool_that_could_not_start_is_a_failure_that_says_why()
    {
        // Removed between the PATH lookup and the run, or not executable.
        ProcessResult result = ProcessResult.NotStarted("xclip is not installed");

        Assert.False(result.Succeeded);
        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Equal("xclip is not installed", result.Error);
        Assert.False(result.TimedOut);
        Assert.False(result.Truncated);
    }
}
