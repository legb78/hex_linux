using HexLinux.Daemon;
using Xunit;

namespace HexLinux.Tests.Daemon;

/// <summary>
/// The races between the user and the machine: a shortcut pressed again
/// during a transcription, a release with no start, a cancel arriving while
/// segments are still being transcribed. The cases below the generation
/// heading are the Linux additions: a cancel command can now come from the
/// socket at any moment, so a cancelled dictation must stay cancelled even
/// once a new one has started.
/// </summary>
public class DictationCoordinatorTests
{
    private static DictationCoordinator Ready()
    {
        var coordinator = new DictationCoordinator();
        coordinator.MarkReady();
        return coordinator;
    }

    [Fact]
    public void The_application_starts_out_loading()
    {
        // The model weighs several hundred megabytes: there is inevitably a
        // moment when the shortcut cannot respond yet.
        Assert.Equal(DictationState.Loading, new DictationCoordinator().State);
    }

    [Fact]
    public void The_shortcut_does_nothing_while_loading()
    {
        // Without this guard, a dictation would start with no engine to
        // handle it.
        Assert.False(new DictationCoordinator().TryStartRecording());
    }

    [Fact]
    public void The_shortcut_does_nothing_if_the_model_failed()
    {
        var coordinator = new DictationCoordinator();
        coordinator.MarkFailed();

        Assert.False(coordinator.TryStartRecording());
    }

    [Fact]
    public void A_complete_dictation_returns_to_waiting()
    {
        DictationCoordinator coordinator = Ready();

        Assert.True(coordinator.TryStartRecording());
        Assert.Equal(DictationState.Recording, coordinator.State);

        Assert.True(coordinator.TryStartTranscribing());
        Assert.Equal(DictationState.Transcribing, coordinator.State);

        coordinator.Complete();
        Assert.Equal(DictationState.Idle, coordinator.State);
    }

    [Fact]
    public void A_second_dictation_is_refused_during_transcription()
    {
        // The central case. The user releases, then presses again right away
        // while the engine is working. With no guard, two dictations would
        // tread on each other and the text would arrive out of order.
        DictationCoordinator coordinator = Ready();
        coordinator.TryStartRecording();
        coordinator.TryStartTranscribing();

        Assert.False(coordinator.TryStartRecording());
        Assert.Equal(DictationState.Transcribing, coordinator.State);
    }

    [Fact]
    public void A_second_start_during_recording_is_refused()
    {
        DictationCoordinator coordinator = Ready();
        coordinator.TryStartRecording();

        Assert.False(coordinator.TryStartRecording());
    }

    [Fact]
    public void Transcribing_with_no_prior_recording_is_refused()
    {
        // A release can arrive with no matching start, after a reset of the
        // shortcut caused by a locked session.
        DictationCoordinator coordinator = Ready();

        Assert.False(coordinator.TryStartTranscribing());
    }

    [Fact]
    public void Cancelling_during_recording_signals_that_it_must_stop()
    {
        DictationCoordinator coordinator = Ready();
        coordinator.TryStartRecording();

        Assert.True(coordinator.Cancel());
        Assert.Equal(DictationState.Idle, coordinator.State);
    }

    [Fact]
    public void Cancelling_while_idle_signals_nothing()
    {
        Assert.False(Ready().Cancel());
    }

    [Fact]
    public void The_shortcut_works_again_after_a_cancellation()
    {
        DictationCoordinator coordinator = Ready();
        coordinator.TryStartRecording();
        coordinator.Cancel();

        Assert.True(coordinator.TryStartRecording());
    }

    [Fact]
    public void Completing_while_idle_changes_nothing()
    {
        DictationCoordinator coordinator = Ready();
        coordinator.Complete();

        Assert.Equal(DictationState.Idle, coordinator.State);
    }

    // --- Notification -----------------------------------------------------------

    [Fact]
    public void Every_change_is_signalled()
    {
        // This is what makes the tray icon follow.
        var observed = new List<DictationState>();
        var coordinator = new DictationCoordinator();
        coordinator.StateChanged += (_, state) => observed.Add(state);

        coordinator.MarkReady();
        coordinator.TryStartRecording();
        coordinator.TryStartTranscribing();
        coordinator.Complete();

        Assert.Equal(
            [DictationState.Idle, DictationState.Recording, DictationState.Transcribing, DictationState.Idle],
            observed);
    }

    [Fact]
    public void A_change_with_no_effect_is_not_signalled()
    {
        // Otherwise the icon would be redrawn for nothing, which makes it
        // flicker.
        var observed = new List<DictationState>();
        DictationCoordinator coordinator = Ready();
        coordinator.StateChanged += (_, state) => observed.Add(state);

        coordinator.Complete();
        coordinator.MarkReady();

        Assert.Empty(observed);
    }

    // --- Generations ------------------------------------------------------------

    [Fact]
    public void Each_dictation_that_starts_takes_the_next_generation()
    {
        // Segments and completions carry this number, so that they can be
        // recognised once another dictation has started.
        DictationCoordinator coordinator = Ready();
        Assert.Equal(0, coordinator.Generation);

        coordinator.TryStartRecording();
        Assert.Equal(1, coordinator.Generation);

        coordinator.Complete();
        coordinator.TryStartRecording();
        Assert.Equal(2, coordinator.Generation);
    }

    [Fact]
    public void A_refused_start_takes_no_generation()
    {
        // Otherwise a start refused during a transcription would make the
        // running dictation's segments look outdated, and drop them.
        DictationCoordinator coordinator = Ready();
        coordinator.TryStartRecording();
        coordinator.TryStartTranscribing();
        int running = coordinator.Generation;

        Assert.False(coordinator.TryStartRecording());
        Assert.Equal(running, coordinator.Generation);
        Assert.True(coordinator.MayInsert(running));
    }

    [Fact]
    public void The_current_dictation_may_insert_its_text()
    {
        DictationCoordinator coordinator = Ready();
        coordinator.TryStartRecording();
        coordinator.TryStartTranscribing();

        Assert.True(coordinator.MayInsert(coordinator.Generation));
    }

    [Fact]
    public void Nothing_more_of_a_cancelled_dictation_is_inserted()
    {
        // hexlinux --cancel during a transcription: the user saw the wrong
        // window had the focus. The segment still in the engine must not land.
        DictationCoordinator coordinator = Ready();
        coordinator.TryStartRecording();
        coordinator.TryStartTranscribing();
        int cancelled = coordinator.Generation;

        Assert.True(coordinator.Cancel());
        Assert.Equal(DictationState.Idle, coordinator.State);
        Assert.False(coordinator.MayInsert(cancelled));
    }

    [Fact]
    public void A_cancelled_dictation_stays_cancelled_after_a_new_one_starts()
    {
        // Cancelling returns to Idle at once, so a new dictation can start
        // while segments of the old one are still being transcribed: they
        // must not be inserted into the new one's window.
        DictationCoordinator coordinator = Ready();
        coordinator.TryStartRecording();
        int cancelled = coordinator.Generation;
        coordinator.Cancel();

        coordinator.TryStartRecording();

        Assert.False(coordinator.MayInsert(cancelled));
        Assert.True(coordinator.MayInsert(coordinator.Generation));
    }

    [Fact]
    public void Text_of_an_older_dictation_is_not_inserted_even_if_it_was_not_cancelled()
    {
        DictationCoordinator coordinator = Ready();
        coordinator.TryStartRecording();
        int older = coordinator.Generation;
        coordinator.Complete(older);
        coordinator.TryStartRecording();

        Assert.False(coordinator.MayInsert(older));
    }

    [Fact]
    public void The_late_completion_of_a_cancelled_dictation_does_not_end_the_next_one()
    {
        // The latent race of the Windows version, made likely by a cancel
        // command: without the generation, the old completion would end the
        // new dictation halfway through, microphone left open.
        DictationCoordinator coordinator = Ready();
        coordinator.TryStartRecording();
        coordinator.TryStartTranscribing();
        int cancelled = coordinator.Generation;
        coordinator.Cancel();
        coordinator.TryStartRecording();

        coordinator.Complete(cancelled);

        Assert.Equal(DictationState.Recording, coordinator.State);
    }

    [Fact]
    public void The_completion_of_the_current_dictation_ends_it()
    {
        DictationCoordinator coordinator = Ready();
        coordinator.TryStartRecording();
        coordinator.TryStartTranscribing();

        coordinator.Complete(coordinator.Generation);

        Assert.Equal(DictationState.Idle, coordinator.State);
    }

    [Fact]
    public void A_completion_after_a_cancellation_changes_nothing()
    {
        // The cancelled dictation's chain finishes while the daemon is idle:
        // no state change, so no icon redrawn for nothing.
        var observed = new List<DictationState>();
        DictationCoordinator coordinator = Ready();
        coordinator.TryStartRecording();
        int cancelled = coordinator.Generation;
        coordinator.Cancel();
        coordinator.StateChanged += (_, state) => observed.Add(state);

        coordinator.Complete(cancelled);

        Assert.Equal(DictationState.Idle, coordinator.State);
        Assert.Empty(observed);
    }
}
