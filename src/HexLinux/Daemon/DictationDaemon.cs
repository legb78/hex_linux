using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using HexLinux.Audio;
using HexLinux.Configuration;
using HexLinux.Diagnostics;
using HexLinux.Feedback;
using HexLinux.Input;
using HexLinux.Output;
using HexLinux.Platform;
using HexLinux.Session;
using HexLinux.Transcription;

namespace HexLinux.Daemon;

/// <summary>
/// The application itself: the shortcut, the control channel, and the chain
/// of recording, transcription and insertion — a close port of HexWin's
/// <c>TrayContext</c>.
///
/// <para><b>Everything is decided on one thread</b>, the
/// <see cref="SerialContext"/> loop. The keyboard readers, the capture
/// thread, the socket and the tray only post events to it; transcription and
/// insertion run elsewhere and come back to it
/// (<c>ConfigureAwait(true)</c>). Nothing that can block runs on it: opening
/// the microphone, loading the model, running a tool — all are awaited, so
/// that a key released meanwhile is still seen at once.</para>
///
/// <para><b>One dictation, end to end.</b> The shortcut's press opens the
/// microphone at once but only arms: the dictation is confirmed — state,
/// tone, model load — once the shortcut has been held for the minimum
/// duration without another key (<see cref="StartConfirmation"/>), so that
/// Right Ctrl keeps its ordinary uses without beeping. The microphone opens
/// before the tone, as in HexWin. Segments closed by pauses are transcribed
/// and inserted in the order they were spoken, each chained behind the
/// previous one. Before any insertion, logind is asked again whether the
/// session is unlocked and in front; the last insertion also waits for the
/// modifiers to be let go.</para>
///
/// <para><b>From HexWin's pull request #68.</b> The release does not end the
/// recording at once: the microphone keeps the last word (the tail, see
/// <see cref="RecordingTail"/>) and the state stays on Recording until it is
/// closed, so that the end tone, played on leaving it, is not recorded over
/// that word. Every segment goes through a <see cref="SegmentJoiner"/>, which
/// stitches a sentence cut by a pause and applies the spoken edits; an
/// "efface ça" aimed at a sentence already typed is carried out with
/// Backspaces — only ever over text this dictation inserted, and never while
/// a modifier is held, since Ctrl+Backspace erases a word.</para>
///
/// <para><b>What changed from the port, and why.</b> The chain of segments
/// cannot be poisoned: a segment that fails is logged and the next ones still
/// run. A cancellation is final: segments of a cancelled dictation are not
/// inserted, and its late completion cannot end the next dictation (the
/// coordinator's generation number). Both were latent races in HexWin, made
/// likelier here by a cancel command that can arrive during a transcription.
/// The shortcut only ever acts on a dictation it started itself
/// (<see cref="ChordCommands"/>): its keys are not withheld, and Right Ctrl+C
/// typed during a transcription used to throw the text away. And stopping
/// waits for the work under way, since the engine freed under a running
/// decode crashed the process.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Orchestration shell: needs a microphone, a keyboard or the socket, and a desktop. Verified by the daemon smoke tests; its decisions are the tested pure classes it calls.")]
public sealed class DictationDaemon : IDisposable
{
    /// <summary>
    /// Opening of a recording left out of the level measurement, long enough
    /// to cover the start tone the microphone picks up off the speakers — on
    /// top of the arming time, since the tone plays when the dictation is
    /// confirmed, not when the microphone opens.
    /// </summary>
    private static readonly TimeSpan CueLead = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A stop waits for the tail and the server's backlog (600 ms at most,
    /// <see cref="RecordingTail"/>) and one 50 ms read; past this, the capture
    /// thread is stuck.
    /// </summary>
    private static readonly TimeSpan RecorderStopTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a stop waits for the dictation under way — its transcription,
    /// its paste and the clipboard's restoration — before ending the loop.
    /// </summary>
    private static readonly TimeSpan ShutdownDrain = TimeSpan.FromSeconds(5);

    /// <summary>How long the session check made before an insertion stays good for its keystroke.</summary>
    private static readonly TimeSpan GuardFreshness = TimeSpan.FromMilliseconds(500);

    private readonly SerialContext _loop;
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly SessionLog _log;
    private readonly string _modelPath;
    private readonly DesktopSession _session;
    private readonly IStatusSurface _surface;

    private readonly DictationCoordinator _coordinator = new();
    private readonly DictationFeedback _feedback;
    private readonly AudioRecorder _recorder;
    private readonly EngineHost _engines;
    private readonly HotkeyTracker _tracker;
    private readonly StartConfirmation _confirmation;
    private readonly EvdevKeyboard _keyboard;
    private readonly UinputKeyboard? _uinput;
    private readonly TextInjector _injector;
    private readonly SessionGuard _guard;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>The <c>threads</c> setting resolved: 0 became one per physical core.</summary>
    private readonly int _threads;

    private Timer? _sessionPoll;

    /// <summary>The segments in flight, each chained behind the previous one.</summary>
    private Task _segments = Task.CompletedTask;

    /// <summary>Segments seen so far in the current dictation.</summary>
    private int _segmentCount;

    /// <summary>Stitches the segments of the current dictation back together.</summary>
    private SegmentJoiner _joiner = new();

    /// <summary>
    /// The generation whose microphone is reading its tail after the release,
    /// or 0. A release and the ceiling can both land during the tail.
    /// </summary>
    private int _endingGeneration;

    /// <summary>The coordinator's generation of the current dictation.</summary>
    private int _generation;

    /// <summary>The generation of the last dictation the shortcut itself started, or 0.</summary>
    private int _chordGeneration;

    /// <summary>Tag of the last recording started, and of the one the current dictation uses.</summary>
    private int _lastRecording;
    private int _activeRecording;

    private TimeSpan _armedAt;
    private TimeSpan _armedFor;

    private GuardDecision? _lastGuard;
    private StatusSnapshot? _lastSnapshot;
    private bool _surfaceFailureLogged;

    /// <summary>Session checks started so far, and the newest one whose answer was applied.</summary>
    private long _checksStarted;
    private long _checkApplied;

    /// <summary>Set once a stop was asked for: no dictation starts any more.</summary>
    private bool _stopping;
    private int _stopRequested;

    public DictationDaemon(
        SerialContext loop,
        AppPaths paths,
        AppSettings settings,
        SessionLog log,
        string modelPath,
        IStatusSurface surface)
    {
        _loop = loop;
        _paths = paths;
        _settings = settings;
        _log = log;
        _modelPath = modelPath;
        _surface = surface;
        _session = DesktopSession.FromEnvironment();

        IReadOnlyDictionary<string, string> tools = ToolLocator.Locate();
        _guard = new SessionGuard(tools);

        // Created once and kept: the compositor needs a moment to pick a new
        // input device up, so one made at paste time would not be heard.
        _uinput = UinputKeyboard.TryCreate(out _, out string? uinputProblem);

        if (_uinput is null && _session.Server == DisplayServer.Wayland)
        {
            _log.Write($"virtual keyboard unavailable: {uinputProblem}");
        }

        _injector = new TextInjector(_uinput, _log.Write);
        _threads = DecodingThreads.Resolve(settings.Threads);

        _engines = new EngineHost(
            modelPath,
            settings.Provider,
            _threads,
            settings.FrenchSpacing,
            IdlePolicy.FromMinutes(settings.UnloadAfterMinutes),
            log);

        _recorder = new AudioRecorder(
            RecordingGuards.From(settings),
            settings.SegmentPause(),
            settings.SegmentPause() > TimeSpan.Zero ? CreateSpeechDetectors(modelPath) : null);
        _recorder.SegmentReady += (_, segment) => _loop.Post(() => OnSegmentReady(segment));
        _recorder.MaximumReached += (_, recording) => _loop.Post(() => OnMaximumReached(recording));
        _recorder.CaptureLost += (_, recording) => _loop.Post(() => OnCaptureLost(recording));

        var detector = new ChordDetector(settings.Hotkey);
        _tracker = new HotkeyTracker(detector);
        _confirmation = new StartConfirmation(TimeSpan.FromMilliseconds(settings.MinRecordingMilliseconds));

        _keyboard = new EvdevKeyboard(
            detector.Codes,
            (device, inputEvent) => _loop.Post(() => Handle(_tracker.OnEvent(device, inputEvent))),
            device => _loop.Post(() => Handle(_tracker.OnDeviceRemoved(device))),
            _log.Write);

        _feedback = new DictationFeedback(settings.Feedback, log);

        _surface.Requested += (_, request) => _loop.Post(() => OnSurfaceRequest(request));
        _coordinator.StateChanged += (_, state) => OnStateChanged(state);
    }

    /// <summary>Starts listening. Called on the loop's thread, before the loop runs.</summary>
    public void Start()
    {
        _log.Write($"HexLinux {typeof(DictationDaemon).Assembly.GetName().Version?.ToString(3)} started: "
            + $"{_session.Server} session, shortcut {HotkeyText.Describe(_settings.Hotkey)}, insertion {_settings.Insertion}");

        // Nothing is said here about the tray: a surface connects in the
        // background, so it is never visible yet at this point, and it logs
        // what it finds itself (no session bus, no tray host) once it knows.

        if (AutoStart.RefreshIfMoved(_paths) is { } moved)
        {
            _log.Write(moved);
        }

        PushSnapshot();
        _keyboard.Start();

        // One shot, rescheduled after each answer (SessionPollPolicy).
        _sessionPoll = new Timer(_ => PollSession(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        _ = LoadEngineAsync();
    }

    /// <summary>
    /// Asks the daemon to stop — a signal, the end of the session. May be
    /// called on any thread. The work under way ends first, a few seconds at
    /// most; a second request does not wait.
    /// </summary>
    public void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) == 1)
        {
            _loop.Complete();
            return;
        }

        _loop.Post(() => _ = StopAsync());
    }

    /// <summary>
    /// Stops taking dictations, lets the one under way finish what cannot be
    /// cut — a decode inside the engine, a paste waiting to restore the
    /// clipboard — then ends the loop. Nothing more is inserted: the session
    /// is closing, or the user asked to quit.
    /// </summary>
    private async Task StopAsync()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;

        if (_confirmation.Disarm())
        {
            DiscardIfCurrent(_lastRecording);
        }

        if (_coordinator.State is DictationState.Recording or DictationState.Transcribing)
        {
            _log.Write("stopping during a dictation: nothing more of it is inserted");
            OnDictationCancelled();
        }

        if (!_segments.IsCompleted)
        {
            Task drained = await Task.WhenAny(_segments, Task.Delay(ShutdownDrain)).ConfigureAwait(true);

            if (drained != _segments)
            {
                _log.Write($"the dictation under way did not finish within {ShutdownDrain.TotalSeconds:F0} s");
            }
        }

        _loop.Complete();
    }

    // --- Engine lifecycle -------------------------------------------------------

    /// <summary>
    /// Prepares the engine. The model is only loaded now if the user asked
    /// for it to stay resident; otherwise loading waits for the first
    /// dictation, where it runs while the user speaks. It is checked in every
    /// case: finding it missing while the user is speaking would be the worst
    /// moment.
    /// </summary>
    private async Task LoadEngineAsync()
    {
        try
        {
            if (_settings.UnloadAfterMinutes == 0)
            {
                await _engines.GetAsync().ConfigureAwait(true);
            }
            else
            {
                await Task.Run(() => ParakeetEngine.Validate(_modelPath)).ConfigureAwait(true);
                _log.Write("model checked, loading deferred to the first dictation");
            }

            _log.Write($"ready ({_settings.Provider}, {_threads} threads)");
            _coordinator.MarkReady();
        }
        catch (Exception ex)
        {
            // Deliberately broad: nobody awaits this task, and an exception
            // left in it would leave the daemon "loading" for ever — grey
            // icon, dead shortcut, no word said (RV-05).
            _log.Write($"model loading failed: {ex.GetType().Name}: {ex.Message}");
            _coordinator.MarkFailed();
            Notify("Model unusable", "The speech model is missing or damaged. Run get-model.sh again, then restart HexLinux.");
        }
    }

    /// <summary>
    /// Silero VAD when its model sits next to the engine's folder, as
    /// get-model.sh installs it; the level threshold otherwise (null), which
    /// was the only detector before and still works in a quiet room. Only
    /// called when segmentation is on: without it, nothing asks for a
    /// verdict, and the model would take memory for nothing.
    /// </summary>
    private DetectorPool? CreateSpeechDetectors(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return null;
        }

        string file = SileroModel.PathFor(modelPath);
        ISpeechDetector? first = TryCreateSilero(file);

        if (first is null)
        {
            return null;
        }

        _log.Write("pauses found by Silero VAD");

        // A second detector is only made when two recordings overlap; if that
        // one fails, the recording falls back to the level rather than crash
        // the capture thread.
        return new DetectorPool(() => TryCreateSilero(file) ?? new LevelSpeechDetector(), first);
    }

    /// <summary>
    /// A Silero detector, or null after a log line. The file is read whole
    /// and checked every time, before sherpa-onnx sees it: one that is not the
    /// model aborts the process (see <see cref="SileroModel"/>).
    /// </summary>
    private ISpeechDetector? TryCreateSilero(string file)
    {
        try
        {
            if (SileroModel.Problem(File.Exists(file) ? File.ReadAllBytes(file) : null) is { } problem)
            {
                _log.Write($"speech detector {problem} ({file}): pauses found by the sound level");
                return null;
            }

            return new SileroSpeechDetector(file);
        }
        catch (Exception ex)
        {
            // Deliberately broad: this may run on a capture thread, where an
            // escaping exception would end the daemon — an unreadable file,
            // hashing unavailable, the native library missing.
            _log.Write($"speech detector not loaded ({ex.GetType().Name}: {ex.Message}): pauses found by the sound level");
            return null;
        }
    }

    // --- The shortcut -----------------------------------------------------------

    private void Handle(ChordAction action)
    {
        bool chordOwnsDictation = _chordGeneration != 0 && _chordGeneration == _coordinator.Generation;

        switch (ChordCommands.Resolve(action, _coordinator.State, _confirmation.IsArmed, chordOwnsDictation))
        {
            case ChordEffect.Arm:
                OnChordArm();
                break;

            case ChordEffect.Disarm:
                // A shortcut, not a dictation: nothing was said to the user,
                // and nothing is logged.
                _confirmation.Disarm();
                DiscardIfCurrent(_lastRecording);
                break;

            case ChordEffect.Stop:
                OnDictationEnded();
                break;

            case ChordEffect.Cancel:
                OnDictationCancelled();
                break;

            default:
                break;
        }
    }

    private void OnChordArm()
    {
        if (_stopping || !SessionAllowsStart())
        {
            return;
        }

        _armedAt = _clock.Elapsed;
        int arming = _confirmation.Arm(_armedAt);

        // The last answer may be a quarter of a minute old: asked again now,
        // it arrives well within the confirmation delay, and a screen locked
        // since then disarms before any tone.
        RequestSessionCheck();

        _ = ArmAsync(arming);
    }

    /// <summary>
    /// Opens the microphone at once, then confirms the dictation once the
    /// shortcut has been held long enough. A release or another key before
    /// that disarms, and the recording is dropped without a sound.
    /// </summary>
    private async Task ArmAsync(int arming)
    {
        int recording = ++_lastRecording;

        // A shortcut already let go needs no word about the microphone.
        if (!await OpenMicrophoneAsync(recording, () => _confirmation.IsArmed).ConfigureAwait(true))
        {
            _confirmation.Disarm();
            return;
        }

        TimeSpan remaining = _confirmation.Remaining(_clock.Elapsed);

        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining).ConfigureAwait(true);
        }

        if (_stopping || !_confirmation.TryConfirm(arming, _clock.Elapsed))
        {
            // Released or interrupted meanwhile: the stop already dropped the
            // recording — unless a command started a dictation on it since.
            if (_activeRecording != recording)
            {
                DiscardIfCurrent(recording);
            }

            return;
        }

        _armedFor = _clock.Elapsed - _armedAt;

        if (BeginRecording(recording))
        {
            _chordGeneration = _coordinator.Generation;
        }
    }

    // --- One dictation, end to end ----------------------------------------------

    /// <summary>
    /// Opens the microphone, off the loop. The microphone opens before the
    /// state changes, and so before the tone — HexWin's order, found by
    /// measurement: opening a capture stream there briefly silenced playback,
    /// cutting a tone started first in two.
    /// </summary>
    private async Task<bool> OpenMicrophoneAsync(int recording, Func<bool> stillWanted)
    {
        try
        {
            await _recorder.StartAsync(recording).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            _recorder.Discard();

            if (stillWanted())
            {
                _log.Write($"microphone unavailable: {ex.Message}");
                Notify("No microphone", "The microphone could not be opened. Run hexlinux --doctor to see why.");
            }

            return false;
        }
    }

    /// <summary>Returns false when the coordinator refused the start.</summary>
    private bool BeginRecording(int recording)
    {
        if (!_coordinator.TryStartRecording())
        {
            DiscardIfCurrent(recording);
            return false;
        }

        _activeRecording = recording;
        _generation = _coordinator.Generation;
        _segmentCount = 0;
        _joiner = new SegmentJoiner();

        // A chain left completed — faulted or not — starts afresh. One still
        // running, from a cancelled dictation, is kept: the new segments wait
        // for it rather than share the engine with it.
        if (_segments.IsCompleted)
        {
            _segments = Task.CompletedTask;
        }

        // The reload starts now, not on the release: it runs while the user is
        // speaking.
        _engines.SetBusy(true);
        _engines.BeginLoad();
        return true;
    }

    private void OnSegmentReady(RecordedSegment segment)
    {
        if (segment.Recording == _activeRecording && _coordinator.State == DictationState.Recording)
        {
            Enqueue(segment.Audio, _generation, final: false);
        }
    }

    private void OnMaximumReached(int recording)
    {
        if (recording == _activeRecording && _coordinator.State == DictationState.Recording)
        {
            _log.Write($"maximum duration reached ({_settings.MaxRecordingSeconds} s): transcribing");
            OnDictationEnded();
        }
    }

    /// <summary>
    /// The sound server stopped delivering mid-recording. What was heard is
    /// transcribed and inserted, and the user learns why the dictation ended
    /// early — rather than keep "recording" nothing until the ceiling.
    /// </summary>
    private void OnCaptureLost(int recording)
    {
        if (recording == _activeRecording && _coordinator.State == DictationState.Recording)
        {
            _log.Write("the microphone stopped delivering during the recording: transcribing what was heard");
            Notify("Microphone lost", "The microphone stopped during the dictation. What was said until then is inserted.");
            OnDictationEnded();
        }
    }

    private void OnDictationEnded()
    {
        if (_coordinator.State != DictationState.Recording || _endingGeneration == _generation)
        {
            return;
        }

        _ = EndDictationAsync(_generation);
    }

    /// <summary>
    /// The release: the microphone reads its tail first, the state staying on
    /// Recording meanwhile — the end tone is played on leaving it, and through
    /// speakers it would otherwise be recorded over the last word.
    /// </summary>
    private async Task EndDictationAsync(int generation)
    {
        _endingGeneration = generation;
        RecordedAudio? remainder;

        try
        {
            // Null when the press was too brief, or when what is left after
            // the last pause holds no speech.
            remainder = await _recorder.StopAsync().WaitAsync(RecorderStopTimeout).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            // The capture thread is stuck in the sound server: the end of the
            // recording is lost, but the daemon must not stay "transcribing"
            // until it is restarted.
            _log.Write($"the microphone did not close within {RecorderStopTimeout.TotalSeconds:F0} s: the end of the recording is lost");
            Notify("Microphone not responding", "The end of the dictation was lost. Run hexlinux --doctor if it happens again.");
            remainder = null;
        }
        finally
        {
            if (_endingGeneration == generation)
            {
                _endingGeneration = 0;
            }
        }

        // False when the dictation was cancelled during the tail — and a new
        // one may even have started since, which this must not end.
        if (generation != _coordinator.Generation || !_coordinator.TryStartTranscribing())
        {
            return;
        }

        if (remainder is { } audio)
        {
            Enqueue(audio, generation, final: true);
        }

        await FinishAsync(generation, _joiner, keepText: true).ConfigureAwait(true);
    }

    private void OnDictationCancelled()
    {
        int generation = _generation;

        if (_coordinator.Cancel())
        {
            _recorder.Discard();
            _log.Write("dictation cancelled");
            _ = FinishAsync(generation, _joiner, keepText: false);
        }
    }

    private void Enqueue(RecordedAudio audio, int generation, bool final)
    {
        if (generation != _generation)
        {
            return;
        }

        int ordinal = ++_segmentCount;

        // Only the first segment holds the start tone, and only when a tone
        // is played; the arming time comes first.
        TimeSpan lead = _feedback.PlaysTone && ordinal == 1 ? _armedFor + CueLead : TimeSpan.Zero;

        // The joiner is taken now: a segment of a cancelled dictation still
        // being transcribed when the next one starts must not touch the new
        // one's.
        _segments = TranscribeAfterAsync(_segments, new Segment(audio, ordinal, generation, lead, final, _joiner));
    }

    /// <summary>One segment of a dictation, with everything its transcription needs.</summary>
    private sealed record Segment(RecordedAudio Audio, int Ordinal, int Generation, TimeSpan Lead, bool Final, SegmentJoiner Joiner);

    private async Task TranscribeAfterAsync(Task previous, Segment segment)
    {
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Already logged by the segment that failed; this one still runs.
        }

        await TranscribeAsync(segment).ConfigureAwait(true);
    }

    /// <summary>
    /// Waits for every segment to be inserted, then puts the daemon back to
    /// rest — unless another dictation has begun since.
    /// </summary>
    /// <param name="generation">The dictation that is over.</param>
    /// <param name="joiner">Its joiner, which may still hold a full stop.</param>
    /// <param name="keepText">
    /// False for a cancelled dictation: the full stop the joiner may still
    /// hold is not inserted after text the user abandoned.
    /// </param>
    private async Task FinishAsync(int generation, SegmentJoiner joiner, bool keepText)
    {
        // A segment closed in the last instants of the recording may still be
        // on its way to the loop. Yielding once lets it join the chain before
        // the chain is awaited: both travel through the same queue, in order.
        await Task.Yield();

        try
        {
            await _segments.ConfigureAwait(true);

            // Held back from a segment in case the next one continued its
            // sentence; the dictation ended on a pause instead.
            if (keepText && _coordinator.MayInsert(generation) && joiner.Finish() is { Length: > 0 } held)
            {
                await InsertAsync(new SegmentInsertion(0, held), generation, final: true, joiner).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            // Every segment logs its own failure; this is the held full stop's.
            _log.Write($"end of dictation failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _coordinator.Complete(generation);

            if (generation == _coordinator.Generation)
            {
                _engines.SetBusy(false);
            }
        }
    }

    private async Task TranscribeAsync(Segment segment)
    {
        (RecordedAudio audio, int ordinal, int generation, TimeSpan lead, bool final, SegmentJoiner joiner) = segment;

        try
        {
            if (!_coordinator.MayInsert(generation))
            {
                return;
            }

            // Loads the model if needed — the load started with the dictation
            // is usually done — and holds it while decoding: a stop waits for
            // the decode instead of freeing the engine under it.
            using var wav = new MemoryStream(audio.Wav);
            TranscriptionResult result = await _engines.TranscribeAsync(wav).ConfigureAwait(true);

            double peak = AudioLevel.Peak(audio.Wav.AsSpan(WavFile.HeaderSize), lead);

            _log.Write(DictationReport.Describe(audio.Duration, ordinal, peak, result.Duration, result.Text.Length));

            if (DictationReport.WhyNothing(result.Text.Length, peak) is { } nothing)
            {
                _log.Write(nothing);
            }

            // Made safe first, so that what the joiner counts is exactly what
            // reaches the document; the joiner then puts the spaces between
            // segments itself.
            string text = InsertionText.Sanitize(result.Text);

            if (text.Length == 0 || !_coordinator.MayInsert(generation))
            {
                return;
            }

            // An erase of a sentence already typed waits for the modifiers
            // to be let go: with Ctrl, each Backspace would take a word. If
            // one stays held, the joiner is told before it decides, so that
            // it keeps that sentence and joins the rest after it.
            if (joiner.PendingErase(text) > 0 && !await WaitToEraseAsync(generation).ConfigureAwait(true))
            {
                if (!_coordinator.MayInsert(generation))
                {
                    return;
                }

                joiner.Invalidate();
                _log.Write("not erased: a modifier was still held");
                Notify("Not erased", "The sentence to erase was left as it was: release every key before saying the erase command.");
            }

            SegmentInsertion insertion = joiner.Next(text, isLast: final);

            if (insertion.Erase > 0 || insertion.Text.Length > 0)
            {
                await InsertAsync(insertion, generation, final, joiner).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            // Deliberately broad. Whatever escapes here — a tool gone, a
            // native failure — must cost this segment only: an exception left
            // in the chain would make every later dictation fail in silence.
            // The type and the message only, never the text. What reached the
            // document is unknown now: nothing more is erased.
            joiner.Invalidate();
            _log.Write($"segment {ordinal} failed: {ex.GetType().Name}: {ex.Message}");
            Notify("Dictation failed", "A dictation could not be transcribed or inserted. The log has the details.");
        }
    }

    /// <summary>
    /// Carries out what the joiner decided for one segment: the Backspaces of
    /// a spoken erase first, then the text. Whatever does not reach the
    /// document makes the joiner stop erasing (<see cref="SegmentJoiner.Invalidate"/>):
    /// its count would no longer match what is there.
    /// </summary>
    private async Task InsertAsync(SegmentInsertion insertion, int generation, bool final, SegmentJoiner joiner)
    {
        if (final)
        {
            await WaitForModifiersAsync().ConfigureAwait(true);
        }

        GuardDecision guard = await Task.Run(_guard.Check).ConfigureAwait(true);

        if (!guard.Allowed)
        {
            joiner.Invalidate();
            _log.Write($"not inserted: {guard.Reason}");
            Notify("Dictation not inserted", "The session was locked, not in front, or could not be checked.");
            return;
        }

        // Checked again after every wait: a cancel may have come meanwhile.
        if (!_coordinator.MayInsert(generation))
        {
            _log.Write("not inserted: the dictation was cancelled");
            return;
        }

        // Asked again right before the keystroke if the clipboard tools took
        // their time: a screen locked meanwhile must not receive the paste.
        Func<string?> sessionRefusal = _guard.RefusalAfter(GuardFreshness);

        (InjectionContext context, IReadOnlyDictionary<string, string> tools) =
            await Task.Run(() => _injector.Survey(_session)).ConfigureAwait(true);

        InjectionPlan plan = InjectionPlanner.Plan(_settings.Insertion, _settings.KeySender, _settings.ClipboardFallback, context);
        // The modifiers were let go before the joiner decided (TranscribeAsync).
        if (insertion.Erase > 0)
        {
            InsertionResult erased = await _injector.EraseAsync(insertion.Erase, plan, tools, sessionRefusal).ConfigureAwait(true);

            // The count only, never what it erases.
            if (erased.Status == InsertionStatus.Inserted)
            {
                _log.Write($"  -> {insertion.Erase} characters erased on request");
            }
            else
            {
                joiner.Invalidate();
                _log.Write($"not erased ({insertion.Erase} characters): {erased.Problem}");

                if (erased.Status == InsertionStatus.Refused)
                {
                    // Locked meanwhile: the text must not go in either.
                    Notify("Dictation not inserted", "The session was locked, not in front, or could not be checked.");
                    return;
                }

                Notify("Not erased", "The sentence to erase was left as it was: nothing could send Backspace. Run hexlinux --doctor.");
            }
        }

        if (insertion.Text.Length == 0)
        {
            return;
        }

        InsertionResult inserted = await _injector.InsertAsync(insertion.Text, plan, _settings.PasteShortcut, tools, sessionRefusal).ConfigureAwait(true);

        if (inserted.Status != InsertionStatus.Inserted)
        {
            joiner.Invalidate();
        }

        switch (inserted.Status)
        {
            case InsertionStatus.CopiedOnly:
                _log.Write($"left in the clipboard: {inserted.Problem}");
                Notify("Text copied", "Press Ctrl+V to paste it.");
                break;

            case InsertionStatus.Failed:
                _log.Write($"not inserted: {inserted.Problem}");
                Notify("Dictation not inserted", "Nothing could insert the text here. Run hexlinux --doctor to see what is missing.");
                break;

            case InsertionStatus.Refused:
                _log.Write($"not inserted: {inserted.Problem}");
                Notify("Dictation not inserted", "The session was locked, not in front, or could not be checked.");
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Before the last insertion of a dictation, waits — a second at most —
    /// for the modifiers still held on the physical keyboard to be let go.
    /// </summary>
    private async Task WaitForModifiersAsync()
    {
        var waited = Stopwatch.StartNew();

        while (true)
        {
            IReadOnlySet<int> blocking = ModifierGuard.Blocking(_tracker.HeldModifiers(), _settings.Insertion, _settings.PasteShortcut);

            if (!ModifierGuard.ShouldWait(blocking, waited.Elapsed))
            {
                if (blocking.Count > 0)
                {
                    _log.Write("inserted although a modifier was still held after 1 s");
                }

                return;
            }

            await Task.Delay(ModifierGuard.PollInterval).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Waits until no modifier is held on the physical keyboards, so that the
    /// Backspaces erase characters, not words (see <see cref="EraseGuard"/>).
    /// False when the dictation was cancelled meanwhile, or a modifier stayed
    /// held too long after it.
    /// </summary>
    private async Task<bool> WaitToEraseAsync(int generation)
    {
        var sinceRecording = new Stopwatch();

        while (_coordinator.MayInsert(generation))
        {
            bool recording = _coordinator.State == DictationState.Recording && generation == _coordinator.Generation;

            if (!recording && !sinceRecording.IsRunning)
            {
                sinceRecording.Start();
            }

            switch (EraseGuard.Decide(_tracker.HeldModifiers(), recording, sinceRecording.Elapsed))
            {
                case EraseDecision.Erase:
                    return true;
                case EraseDecision.GiveUp:
                    return false;
                default:
                    await Task.Delay(ModifierGuard.PollInterval).ConfigureAwait(true);
                    break;
            }
        }

        return false;
    }

    private void DiscardIfCurrent(int recording)
    {
        if (_recorder.IsCurrent(recording))
        {
            _recorder.Discard();
        }
    }

    // --- The session ------------------------------------------------------------

    /// <summary>
    /// Off the loop, on the timer's thread: loginctl takes a process start.
    /// Nothing may escape — an exception on a timer thread ends the process —
    /// and the next check must always be scheduled.
    /// </summary>
    private void PollSession()
    {
        long ticket = Interlocked.Increment(ref _checksStarted);

        try
        {
            GuardDecision decision = _guard.Check();
            _loop.Post(() => OnSessionChecked(ticket, decision));
        }
        catch (Exception ex)
        {
            _loop.Post(() =>
            {
                _log.Write($"session check failed: {ex.GetType().Name}: {ex.Message}");
                ScheduleSessionCheck(SessionPollPolicy.Watchful);
            });
        }
    }

    private void OnSessionChecked(long ticket, GuardDecision decision)
    {
        // Two checks can overlap — the timer's and one asked for by a press:
        // an older answer arriving last must not undo a newer one.
        if (ticket < _checkApplied)
        {
            return;
        }

        _checkApplied = ticket;

        if (_lastGuard?.Reason != decision.Reason)
        {
            _log.Write($"session: {decision.Reason}");
        }

        _lastGuard = decision;

        // Another user's session in front: stop reading the keyboards
        // entirely, as a login manager would expect of a background session.
        if (decision.Inactive)
        {
            _keyboard.Suspend();
        }
        else
        {
            _keyboard.Resume();
        }

        // Locked or left behind while dictating: the microphone stops now,
        // whoever started the dictation — the shortcut, a command or the tray
        // (RV-08).
        if (!decision.AllowsStart)
        {
            if (_confirmation.Disarm())
            {
                DiscardIfCurrent(_lastRecording);
                _log.Write($"dictation refused: {decision.Reason}");
            }
            else if (_coordinator.State == DictationState.Recording)
            {
                _log.Write($"dictation stopped: {decision.Reason}");
                OnDictationCancelled();
            }
        }

        bool busy = _confirmation.IsArmed || _coordinator.State is DictationState.Recording or DictationState.Transcribing;
        ScheduleSessionCheck(SessionPollPolicy.NextCheck(decision, busy));
    }

    /// <summary>Asks logind now, off the loop, instead of at the next scheduled check.</summary>
    private void RequestSessionCheck() => ScheduleSessionCheck(TimeSpan.Zero);

    private void ScheduleSessionCheck(TimeSpan due)
    {
        if (_stopping)
        {
            return;
        }

        try
        {
            _sessionPoll?.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Stopping: no more checks.
        }
    }

    private bool SessionAllowsStart()
    {
        if (_lastGuard is { AllowsStart: false } guard)
        {
            _log.Write($"dictation refused: {guard.Reason}");
            return false;
        }

        return true;
    }

    // --- Control socket and tray ------------------------------------------------

    /// <summary>
    /// A command from the control socket, already parsed off the loop. Called
    /// on the server's thread; answered once the loop has acted on it.
    /// </summary>
    public Task<ControlReply> HandleControlAsync(ControlCommand command)
    {
        var reply = new TaskCompletionSource<ControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop.Post(() => _ = RunCommandAsync(command, reply));
        return reply.Task;
    }

    private async Task RunCommandAsync(ControlCommand command, TaskCompletionSource<ControlReply>? reply)
    {
        try
        {
            if (command == ControlCommand.Status)
            {
                reply?.TrySetResult(ControlReply.Done(_coordinator.State));
                return;
            }

            switch (_stopping ? ChordAction.None : ControlCommands.Resolve(command, _coordinator.State))
            {
                case ChordAction.Start:
                    await StartFromCommandAsync().ConfigureAwait(true);
                    break;
                case ChordAction.Stop:
                    OnDictationEnded();
                    break;
                case ChordAction.Cancel:
                    OnDictationCancelled();
                    break;
                default:
                    reply?.TrySetResult(ControlReply.Ignored(_coordinator.State));
                    return;
            }

            reply?.TrySetResult(ControlReply.Done(_coordinator.State));
        }
        catch (Exception ex)
        {
            _log.Write($"control command failed: {ex.GetType().Name}: {ex.Message}");
            reply?.TrySetResult(new ControlReply(ControlOutcome.Error, "internal"));
        }
    }

    /// <summary>
    /// A start asked for explicitly — a command, the tray — needs no arming:
    /// nobody sends <c>--start</c> by accident.
    /// </summary>
    private async Task StartFromCommandAsync()
    {
        if (!SessionAllowsStart())
        {
            return;
        }

        // The last answer may be old: a screen locked since is seen within
        // moments, and the recording stopped.
        RequestSessionCheck();

        // A shortcut being armed gives way to the command, which takes over
        // the microphone it already opened, and its tag with it.
        bool armed = _confirmation.Disarm();
        int recording = armed && _recorder.IsCurrent(_lastRecording) ? _lastRecording : ++_lastRecording;

        if (!await OpenMicrophoneAsync(recording, () => true).ConfigureAwait(true))
        {
            return;
        }

        if (_stopping)
        {
            DiscardIfCurrent(recording);
            return;
        }

        _armedFor = TimeSpan.Zero;
        BeginRecording(recording);
    }

    private void OnSurfaceRequest(SurfaceRequest request)
    {
        switch (request.Kind)
        {
            case SurfaceRequestKind.ToggleDictation:
                _ = RunCommandAsync(ControlCommand.Toggle, reply: null);
                break;

            case SurfaceRequestKind.SetTones:
                _feedback.SetPlaysTone(request.Value);
                _settings.Feedback = request.Value ? FeedbackMode.Sound : FeedbackMode.None;
                _ = PersistFeedbackAsync(_settings.Feedback);
                PushSnapshot();
                break;

            case SurfaceRequestKind.SetAutoStart:
                _ = SetAutoStartAsync(request.Value);
                break;

            case SurfaceRequestKind.Quit:
                _log.Write("quit asked from the tray");
                Interlocked.Exchange(ref _stopRequested, 1);
                _ = StopAsync();
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Writes the tones switch back to settings.json, line by line so that
    /// its comments survive. The change is already in effect: a file that
    /// cannot be written costs the memory of the choice, worth a log line.
    /// </summary>
    private async Task PersistFeedbackAsync(FeedbackMode mode)
    {
        string path = _paths.SettingsFile;
        IReadOnlyList<string> missed = await Task.Run(
            () => AppSettings.RewriteValues(path, [new("feedback", $"\"{mode}\"")], appendMissing: true)).ConfigureAwait(true);

        if (missed.Count > 0)
        {
            _log.Write($"tones set to {mode}, but settings.json could not be updated");
        }
    }

    private async Task SetAutoStartAsync(bool enabled)
    {
        (bool done, string message) = await Task.Run(() => (AutoStart.SetEnabled(_paths, enabled, out string text), text)).ConfigureAwait(true);

        _log.Write(message);

        if (!done)
        {
            Notify("Autostart unchanged", "Starting with the session could not be changed. The log says why.");
        }

        PushSnapshot();
    }

    // --- What the user sees -------------------------------------------------------

    private void OnStateChanged(DictationState state)
    {
        _feedback.Apply(state);
        PushSnapshot();
    }

    private void PushSnapshot()
    {
        StatusSnapshot snapshot = StatusSnapshots.Build(
            _coordinator.State,
            _settings,
            _feedback.PlaysTone,
            AutoStart.IsEnabled(_paths),
            _paths);

        if (snapshot == _lastSnapshot)
        {
            return;
        }

        _lastSnapshot = snapshot;

        try
        {
            _surface.Update(snapshot);
        }
        catch (Exception ex)
        {
            // The surface promises never to throw; the loop does not rely on it.
            ReportSurfaceFailure(ex);
        }
    }

    private void Notify(string title, string body)
    {
        try
        {
            _surface.Notify(title, body);
        }
        catch (Exception ex)
        {
            ReportSurfaceFailure(ex);
        }
    }

    private void ReportSurfaceFailure(Exception error)
    {
        if (!_surfaceFailureLogged)
        {
            _surfaceFailureLogged = true;
            _log.Write($"the status surface failed: {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Called on the main thread once the loop has ended. The engine waits,
    /// a few seconds at most, for a load or a decode still in its native code.
    /// </summary>
    public void Dispose()
    {
        _stopping = true;
        _sessionPoll?.Dispose();
        _keyboard.Dispose();
        _recorder.Dispose();
        _engines.Dispose();
        _uinput?.Dispose();
    }
}
