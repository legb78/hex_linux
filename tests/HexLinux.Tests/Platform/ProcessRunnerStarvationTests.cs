using System.Text;
using HexLinux.Platform;
using Xunit;

namespace HexLinux.Tests.Platform;

/// <summary>
/// Runs alone: the case below ties up the thread pool on purpose, which would
/// slow every test running beside it.
/// </summary>
[CollectionDefinition(nameof(ThreadPoolStarvation), DisableParallelization = true)]
public sealed class ThreadPoolStarvation;

/// <summary>
/// The process shell under a starved thread pool.
///
/// <para>Found by CI on a four-core runner, where the whole suite starts
/// dozens of tools at once: the reader of a tool's output was queued on the
/// thread pool and had not even started when the tool exited. A clipboard tool
/// that had printed the user's content came back with an empty output and a
/// success, and the clipboard was cleared instead of restored. The daemon
/// meets the same pool whenever a burst of work lands on it.</para>
/// </summary>
[Trait("Category", "Shell")]
[Collection(nameof(ThreadPoolStarvation))]
public sealed class ProcessRunnerStarvationTests : IDisposable
{
    private readonly FakeToolFolder _tools = new();

    public void Dispose() => _tools.Dispose();

    [Fact]
    public void A_tool_s_output_survives_a_starved_thread_pool()
    {
        string cat = _tools.Add("cat-tool", "exec cat");

        // More blocked work items than the pool has threads to begin with:
        // anything queued behind them waits for the pool to grow, about one
        // thread every half second.
        ThreadPool.GetMinThreads(out int workers, out _);

        // Not disposed: blockers still queued when the test ends wait on it
        // later, and a disposed event would throw on their pool thread.
        var release = new ManualResetEventSlim();

        for (int i = 0; i < workers + 16; i++)
        {
            ThreadPool.QueueUserWorkItem(_ => release.Wait());
        }

        try
        {
            ProcessResult result = ProcessRunner.Run(cat, [], Encoding.UTF8.GetBytes("what the user had copied"), TimeSpan.FromSeconds(5));

            Assert.True(result.Succeeded);
            Assert.Equal("what the user had copied", result.OutputText);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void A_forking_tool_gets_its_input_under_a_starved_thread_pool()
    {
        // The clipboard writer's input is fed the same way: left unwritten,
        // the tool waits on its standard input until the time limit.
        string sink = _tools.Add("sink", "cat > \"$DIR/received\"");

        ThreadPool.GetMinThreads(out int workers, out _);

        // Not disposed: blockers still queued when the test ends wait on it
        // later, and a disposed event would throw on their pool thread.
        var release = new ManualResetEventSlim();

        for (int i = 0; i < workers + 16; i++)
        {
            ThreadPool.QueueUserWorkItem(_ => release.Wait());
        }

        try
        {
            ProcessResult result = ProcessRunner.RunForking(sink, [], Encoding.UTF8.GetBytes("dictated"), TimeSpan.FromSeconds(5));

            Assert.True(result.Succeeded);
            Assert.Equal("dictated", _tools.Read("received"));
        }
        finally
        {
            release.Set();
        }
    }
}
