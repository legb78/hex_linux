using System.Diagnostics.CodeAnalysis;
using HexLinux.Interop;

namespace HexLinux.Audio;

/// <summary>A recording that was kept, ready to be transcribed.</summary>
/// <param name="Wav">Complete WAV file, header included.</param>
/// <param name="Duration">Real duration, derived from the samples received.</param>
public readonly record struct RecordedAudio(byte[] Wav, TimeSpan Duration);

/// <summary>A segment closed by a pause, and the recording it belongs to.</summary>
/// <param name="Audio">The segment.</param>
/// <param name="Recording">The tag given to <see cref="AudioRecorder.StartAsync"/>.</param>
public readonly record struct RecordedSegment(RecordedAudio Audio, int Recording);

/// <summary>
/// Captures the default microphone of the sound server, straight into the
/// format the recognition engine expects.
///
/// <para>A deliberately thin shell around libpulse-simple: it opens a stream
/// and hands the samples to a <see cref="SpeechSegmenter"/>. The decisions —
/// too short, ceiling reached, pause long enough to cut — belong to
/// <see cref="RecordingGuards"/> and to the segmenter, both testable without
/// a microphone. Same events and same stop semantics as HexWin's
/// recorder.</para>
///
/// <para><b>One thread per recording, and it alone touches the stream.</b> A
/// libpulse-simple stream is not thread-safe, and <c>pa_simple_read</c>
/// blocks: the thread opens the stream, reads 50 ms at a time until asked to
/// stop, then frees it — so a stop waits at most one read. Opening happens
/// there too, off the daemon's loop, because <c>pa_simple_new</c> has no
/// time limit of its own; <see cref="StartAsync"/> gives it one.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "libpulse shell: needs a sound server and a microphone, verified by --record and the daemon smoke tests.")]
public sealed class AudioRecorder : IDisposable
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);

    /// <summary>50 ms of audio: short reads keep a stop responsive.</summary>
    private static readonly uint FragmentBytes = (uint)RecordingFormat.BytesFor(TimeSpan.FromMilliseconds(50));

    private readonly RecordingGuards _guards;
    private readonly Lock _sync = new();

    private Capture? _current;

    /// <param name="guards">Minimum and maximum durations.</param>
    /// <param name="pause">Silence that closes a segment; zero to never cut.</param>
    public AudioRecorder(RecordingGuards guards, TimeSpan pause)
    {
        _guards = guards;
        Pause = pause;
    }

    /// <summary>Silence that closes a segment; read when a recording starts.</summary>
    public TimeSpan Pause { get; set; }

    /// <summary>
    /// Raised, on the capture thread, each time a pause closes a segment
    /// while the recording goes on. The last segment comes out of
    /// <see cref="StopAsync"/> instead. The tag says which recording it
    /// belongs to: a capture being abandoned may still deliver one after the
    /// next has begun.
    /// </summary>
    public event EventHandler<RecordedSegment>? SegmentReady;

    /// <summary>Raised, on the capture thread, when the ceiling is reached; carries the recording's tag.</summary>
    public event EventHandler<int>? MaximumReached;

    public bool IsRecording
    {
        get
        {
            lock (_sync)
            {
                return _current is not null;
            }
        }
    }

    /// <summary>
    /// Opens the microphone. The task completes once the stream is open, or
    /// fails with <see cref="InvalidOperationException"/> (the server refused
    /// or did not answer in time) or <see cref="DllNotFoundException"/>
    /// (libpulse is not installed). Calling it while recording changes
    /// nothing, the recording keeping its first tag.
    /// </summary>
    /// <param name="tag">Carried by the events of this recording.</param>
    public Task StartAsync(int tag = 0)
    {
        Capture capture;

        lock (_sync)
        {
            if (_current is not null)
            {
                return _current.Opened.Task;
            }

            capture = new Capture(this, new SpeechSegmenter(Pause), tag);
            _current = capture;
        }

        var thread = new Thread(capture.Run) { IsBackground = true, Name = "hexlinux-capture" };
        thread.Start();

        return WaitOpenedAsync(capture);
    }

    private async Task WaitOpenedAsync(Capture capture)
    {
        try
        {
            await capture.Opened.Task.WaitAsync(OpenTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Discard();
            throw new InvalidOperationException("the sound server did not open the microphone within 5 s");
        }
    }

    /// <summary>
    /// Closes the microphone and returns what was not yet cut, or <c>null</c>
    /// if the press was too brief to hold speech or nothing worth transcribing
    /// is left.
    /// </summary>
    public Task<RecordedAudio?> StopAsync()
    {
        Capture? capture;

        lock (_sync)
        {
            capture = _current;
            _current = null;
        }

        if (capture is null)
        {
            return Task.FromResult<RecordedAudio?>(null);
        }

        capture.Stopping = true;
        return capture.Finished.Task;
    }

    /// <summary>Closes the microphone and forgets what it heard.</summary>
    public void Discard() => _ = StopAsync();

    public void Dispose() => Discard();

    private static RecordedAudio Wrap(byte[] pcm) =>
        new(WavFile.Create(pcm), RecordingFormat.DurationOf(pcm.Length));

    /// <summary>Whether a recording with <paramref name="tag"/> is the current one.</summary>
    public bool IsCurrent(int tag)
    {
        lock (_sync)
        {
            return _current is { } capture && capture.Tag == tag;
        }
    }

    /// <summary>One recording, from the opening of the stream to its release.</summary>
    private sealed class Capture(AudioRecorder owner, SpeechSegmenter segmenter, int tag)
    {
        public int Tag { get; } = tag;

        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<RecordedAudio?> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool Stopping;

        public void Run()
        {
            nint stream;

            try
            {
                stream = PulseSimple.Open(PulseSimple.StreamRecord, RecordingFormat.SampleRate, "Dictation", FragmentBytes);
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                Opened.TrySetException(ex);
                Finished.TrySetResult(null);
                owner.Forget(this);
                return;
            }

            Opened.TrySetResult();

            long received = 0;
            bool maximumReported = false;

            try
            {
                received = ReadUntilStopped(stream, ref maximumReported);
            }
            finally
            {
                PulseSimple.Free(stream);
            }

            // Duration comes from the samples actually received, not from the
            // clock: this is what the engine will hear.
            if (owner._guards.IsTooShort(RecordingFormat.DurationOf(received)))
            {
                Finished.TrySetResult(null);
                return;
            }

            Finished.TrySetResult(segmenter.Flush() is { } remainder ? Wrap(remainder) : null);
        }

        private unsafe long ReadUntilStopped(nint stream, ref bool maximumReported)
        {
            byte[] buffer = new byte[FragmentBytes];
            long received = 0;
            long maximum = owner._guards.MaximumBytes;

            while (!Stopping)
            {
                int result;
                int error;

                fixed (byte* data = buffer)
                {
                    result = PulseSimple.Read(stream, data, FragmentBytes, out error);
                }

                if (result < 0)
                {
                    // The server went away mid-recording: keep what was heard.
                    break;
                }

                long room = maximum - received;

                if (room <= 0)
                {
                    continue;
                }

                int kept = (int)Math.Min(buffer.Length, room);
                byte[]? closed = segmenter.Push(buffer.AsSpan(0, kept));
                received += kept;

                if (closed is not null)
                {
                    owner.SegmentReady?.Invoke(owner, new RecordedSegment(Wrap(closed), Tag));
                }

                if (received >= maximum && !maximumReported)
                {
                    maximumReported = true;
                    owner.MaximumReached?.Invoke(owner, Tag);
                }
            }

            return received;
        }
    }

    /// <summary>A capture that could not open no longer counts as the current one.</summary>
    private void Forget(Capture capture)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_current, capture))
            {
                _current = null;
            }
        }
    }
}
