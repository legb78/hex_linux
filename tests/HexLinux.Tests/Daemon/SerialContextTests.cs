using HexLinux.Daemon;
using Xunit;

namespace HexLinux.Tests.Daemon;

/// <summary>
/// The daemon's single thread. Every decision about a dictation — a key, a
/// segment transcribed, a socket command, a tray click — is taken there in
/// arrival order, which is what lets the orchestration be a close port of
/// HexWin's message loop. What is checked: the order, that one bad callback
/// does not stop dictation until the next login, that an <c>await</c> comes
/// back to the loop, and that nothing posted during shutdown throws on a
/// thread where no one can catch it.
///
/// <para>Most cases run the loop on the test thread itself — callbacks queued,
/// loop completed, then run to the end — which is deterministic. The few that
/// need a second thread wait on events with a bounded patience.</para>
/// </summary>
public class SerialContextTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public void Callbacks_run_in_the_order_they_were_posted_even_those_queued_before_the_end()
    {
        // A key press then its release, a segment then the end of the
        // dictation: handled the other way round, a dictation would stop
        // before it started. Complete lets what is already queued run.
        var errors = new List<Exception>();
        var order = new List<string>();
        using var loop = new SerialContext(errors.Add);

        loop.Post(() => order.Add("press"));
        loop.Post(_ => order.Add("segment"), null);
        loop.Post(() => order.Add("release"));
        loop.Complete();
        loop.Run();

        Assert.Equal(["press", "segment", "release"], order);
        Assert.Empty(errors);
    }

    [Fact]
    public void The_state_argument_reaches_the_callback()
    {
        using var loop = new SerialContext(_ => { });
        object? received = null;
        object sent = new();

        loop.Post(state => received = state, sent);
        loop.Complete();
        loop.Run();

        Assert.Same(sent, received);
    }

    [Fact]
    public void A_callback_that_throws_is_reported_and_the_loop_carries_on()
    {
        // One bad event must not take dictation down until the next login:
        // the daemon logs it and writes the crash file, then goes on.
        var errors = new List<Exception>();
        var order = new List<string>();
        using var loop = new SerialContext(errors.Add);

        loop.Post(() => order.Add("before"));
        loop.Post(() => throw new InvalidDataException("bad event"));
        loop.Post(() => order.Add("after"));
        loop.Complete();
        loop.Run();

        Assert.Equal(["before", "after"], order);
        Assert.Equal("bad event", Assert.IsType<InvalidDataException>(Assert.Single(errors)).Message);
    }

    [Fact]
    public void A_callback_posted_after_the_end_is_dropped_without_an_exception()
    {
        // A transcription finishing during shutdown posts its result: an
        // exception there would surface on a pool thread and abort the process.
        var ran = new List<string>();
        using var loop = new SerialContext(_ => { });

        loop.Complete();
        loop.Post(() => ran.Add("late"));
        loop.Run();

        Assert.Empty(ran);
    }

    [Fact]
    public void A_disposed_loop_drops_posts_and_can_still_be_completed()
    {
        // A keyboard reader thread outliving Main posts into a loop already
        // disposed by its using block.
        var loop = new SerialContext(_ => { });
        loop.Dispose();

        loop.Post(() => throw new InvalidOperationException("must not run"));
        loop.Post(_ => throw new InvalidOperationException("must not run"), null);
        loop.Complete();
    }

    [Fact]
    public void Completing_twice_is_harmless()
    {
        // SIGINT and SIGTERM can both arrive, each handler completing the loop.
        using var loop = new SerialContext(_ => { });

        loop.Complete();
        loop.Complete();
        loop.Run();
    }

    [Fact]
    public void The_loop_knows_its_own_thread_only_while_it_runs()
    {
        // IsOnLoop is how Send avoids waiting on itself.
        using var loop = new SerialContext(_ => { });
        bool inside = false;
        SynchronizationContext? currentInside = null;
        SynchronizationContext? before = SynchronizationContext.Current;

        Assert.False(loop.IsOnLoop);

        loop.Post(() =>
        {
            inside = loop.IsOnLoop;
            currentInside = SynchronizationContext.Current;
        });
        loop.Complete();
        loop.Run();

        Assert.True(inside);
        Assert.Same(loop, currentInside);
        Assert.False(loop.IsOnLoop);
        Assert.Same(before, SynchronizationContext.Current);
    }

    [Fact]
    public void Another_thread_is_never_taken_for_the_loop()
    {
        using var loop = new SerialContext(_ => { });
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Thread thread = Start(loop);

        loop.Post(() =>
        {
            started.Set();
            release.Wait(Patience);
        });

        Assert.True(started.Wait(Patience));
        bool fromTestThread = loop.IsOnLoop;
        release.Set();
        Stop(loop, thread);

        Assert.False(fromTestThread);
    }

    [Fact]
    public async Task An_await_resumes_on_the_loop_thread()
    {
        // The daemon awaits the engine and the injector with
        // ConfigureAwait(true): the rest of the dictation must come back to
        // the loop, not run on the pool thread that completed the task.
        var errors = new List<Exception>();
        using var loop = new SerialContext(errors.Add);
        using var waiting = new ManualResetEventSlim();
        using var resumed = new ManualResetEventSlim();
        var awaited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int loopThread = 0;
        int resumedThread = -1;
        bool resumedOnLoop = false;
        Thread thread = Start(loop);

        loop.Post(async () =>
        {
            loopThread = Environment.CurrentManagedThreadId;
            waiting.Set();
            await awaited.Task.ConfigureAwait(true);
            resumedThread = Environment.CurrentManagedThreadId;
            resumedOnLoop = loop.IsOnLoop;
            resumed.Set();
        });

        Assert.True(waiting.Wait(Patience));
        await Task.Run(awaited.SetResult).WaitAsync(Patience);
        Assert.True(resumed.Wait(Patience));
        Stop(loop, thread);

        Assert.Equal(loopThread, resumedThread);
        Assert.True(resumedOnLoop);
        Assert.Empty(errors);
    }

    [Fact]
    public void An_exception_thrown_after_an_await_in_an_event_handler_is_reported_on_the_loop()
    {
        // Event handlers are async void: their exception is posted back to
        // the loop, where only the loop's catch stands between it and the
        // end of the process.
        var errors = new List<Exception>();
        using var reported = new ManualResetEventSlim();
        using var loop = new SerialContext(error =>
        {
            errors.Add(error);
            reported.Set();
        });
        Thread thread = Start(loop);

        loop.Post(async () =>
        {
            await Task.Yield();
            throw new InvalidDataException("late failure");
        });

        Assert.True(reported.Wait(Patience));
        Stop(loop, thread);

        Assert.IsType<InvalidDataException>(Assert.Single(errors));
    }

    // --- Send -------------------------------------------------------------------------

    [Fact]
    public async Task Send_from_another_thread_runs_the_callback_on_the_loop_and_waits_for_it()
    {
        // Send is the synchronous half of the SynchronizationContext
        // contract: whatever uses it expects the work done when it returns.
        using var loop = new SerialContext(_ => { });
        Thread thread = Start(loop);
        bool ranOnLoop = false;

        await Task.Run(() => loop.Send(_ => ranOnLoop = loop.IsOnLoop, null)).WaitAsync(Patience);

        Assert.True(ranOnLoop);
        Stop(loop, thread);
    }

    [Fact]
    public async Task Send_hands_the_callbacks_exception_to_the_caller_not_to_the_loop()
    {
        // The caller is the one that can act on the failure; the loop's
        // handler would only log it.
        var errors = new List<Exception>();
        using var loop = new SerialContext(errors.Add);
        Thread thread = Start(loop);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.Run(() => loop.Send(_ => throw new InvalidDataException("inner"), null)).WaitAsync(Patience));

        Assert.IsType<InvalidDataException>(failure.InnerException);
        Stop(loop, thread);
        Assert.Empty(errors);
    }

    [Fact]
    public void Send_on_the_loop_itself_runs_at_once_instead_of_waiting_forever()
    {
        // Waiting there for a callback queued behind the current one would
        // hang the daemon.
        using var loop = new SerialContext(_ => { });
        var order = new List<string>();

        loop.Post(() =>
        {
            order.Add("outer starts");
            loop.Send(_ => order.Add("sent"), null);
            order.Add("outer ends");
        });
        loop.Complete();
        loop.Run();

        Assert.Equal(["outer starts", "sent", "outer ends"], order);
    }

    [Fact]
    public void Send_after_the_end_fails_at_once_rather_than_waiting()
    {
        // Nothing will ever run the callback: waiting would block the caller
        // for good.
        using var loop = new SerialContext(_ => { });
        loop.Complete();

        InvalidOperationException ended = Assert.Throws<InvalidOperationException>(() => loop.Send(_ => { }, null));
        Assert.Equal("The daemon loop has ended.", ended.Message);

        loop.Dispose();
        Assert.Throws<InvalidOperationException>(() => loop.Send(_ => { }, null));
    }

    // --- The rest of the contract -------------------------------------------------

    [Fact]
    public void A_copy_of_the_context_is_the_loop_itself()
    {
        // The base class's copy posts to the thread pool: a library that
        // copies the context would then run its callbacks off the loop.
        using var loop = new SerialContext(_ => { });

        Assert.Same(loop, loop.CreateCopy());
    }

    [Fact]
    public void Nothing_null_is_accepted()
    {
        using var loop = new SerialContext(_ => { });

        Assert.Throws<ArgumentNullException>(() => new SerialContext(null!));
        Assert.Throws<ArgumentNullException>(() => loop.Post(null!, null));
        Assert.Throws<ArgumentNullException>(() => loop.Post((Action)null!));
        Assert.Throws<ArgumentNullException>(() => loop.Send(null!, null));
    }

    /// <summary>Runs the loop on a thread of its own, as the daemon runs it on its main thread.</summary>
    private static Thread Start(SerialContext loop)
    {
        var thread = new Thread(loop.Run) { IsBackground = true, Name = "test loop" };
        thread.Start();
        return thread;
    }

    private static void Stop(SerialContext loop, Thread thread)
    {
        loop.Complete();
        Assert.True(thread.Join(Patience), "the loop did not end");
    }
}
