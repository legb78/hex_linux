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
/// <para><b>What changed from the port, and why.</b> The chain of segments
/// cannot be poisoned: a segment that fails is logged and the next ones still
/// run. A cancellation is final: segments of a cancelled dictation are not
/// inserted, and its late completion cannot end the next dictation (the
/// coordinator's generation number). Both were latent races in HexWin, made
/// likelier here by a cancel command that can arrive during a
/// transcription.</para>
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

    private static readonly TimeSpan SessionPollInterval = TimeSpan.FromSeconds(2);

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

    private Timer? _sessionPoll;

    /// <summary>The segments in flight, each chained behind the previous one.</summary>
    private Task _segments = Task.CompletedTask;

    /// <summary>Segments seen so far in the current dictation.</summary>
    private int _segmentCount;

    /// <summary>The coordinator's generation of the current dictation.</summary>
    private int _generation;

    /// <summary>Tag of the last recording started, and of the one the current dictation uses.</summary>
    private int _lastRecording;
    private int _activeRecording;

    private TimeSpan _armedAt;
    private TimeSpan _armedFor;

    private GuardDecision? _lastGuard;
    private StatusSnapshot? _lastSnapshot;
    private bool _surfaceFailureLogged;

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

        _engines = new EngineHost(
            modelPath,
            settings.Provider,
            settings.Threads,
            settings.FrenchSpacing,
            IdlePolicy.FromMinutes(settings.UnloadAfterMinutes),
            log);

        _recorder = new AudioRecorder(RecordingGuards.From(settings), settings.SegmentPause());
        _recorder.SegmentReady += (_, segment) => _loop.Post(() => OnSegmentReady(segment));
        _recorder.MaximumReached += (_, recording) => _loop.Post(() => OnMaximumReached(recording));

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

        _sessionPoll = new Timer(_ => PollSession(), null, TimeSpan.Zero, SessionPollInterval);

        _ = LoadEngineAsync();
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

            _log.Write($"ready ({_settings.Provider}, {_settings.Threads} threads)");
            _coordinator.MarkReady();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or DllNotFoundException or ObjectDisposedException)
        {
            _log.Write($"model loading failed: {ex.Message}");
            _coordinator.MarkFailed();
            Notify("Model unusable", "The speech model is missing or damaged. Run scripts/get-model.sh, then restart HexLinux.");
        }
    }

    // --- The shortcut -----------------------------------------------------------

    private void Handle(ChordAction action)
    {
        switch (action)
        {
            case ChordAction.Start:
                OnChordStart();
                break;
            case ChordAction.Stop:
                OnChordStop();
                break;
            case ChordAction.Cancel:
                OnChordCancel();
                break;
            default:
                break;
        }
    }

    private void OnChordStart()
    {
        if (_coordinator.State != DictationState.Idle || !SessionAllowsStart())
        {
            return;
        }

        _armedAt = _clock.Elapsed;
        int arming = _confirmation.Arm(_armedAt);
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

        if (!_confirmation.TryConfirm(arming, _clock.Elapsed))
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
        BeginRecording(recording);
    }

    private void OnChordStop()
    {
        if (_confirmation.Disarm())
        {
            // A shortcut, not a dictation: nothing was said to the user, and
            // nothing is logged.
            DiscardIfCurrent(_lastRecording);
            return;
        }

        OnDictationEnded();
    }

    private void OnChordCancel()
    {
        if (_confirmation.Disarm())
        {
            DiscardIfCurrent(_lastRecording);
            return;
        }

        OnDictationCancelled();
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

    private void BeginRecording(int recording)
    {
        if (!_coordinator.TryStartRecording())
        {
            DiscardIfCurrent(recording);
            return;
        }

        _activeRecording = recording;
        _generation = _coordinator.Generation;
        _segmentCount = 0;

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

    private void OnDictationEnded()
    {
        if (!_coordinator.TryStartTranscribing())
        {
            return;
        }

        _ = EndAsync(_generation);
    }

    private async Task EndAsync(int generation)
    {
        // Null when the press was too brief, or when what is left after the
        // last pause holds no speech.
        RecordedAudio? remainder = await _recorder.StopAsync().ConfigureAwait(true);

        if (remainder is { } audio)
        {
            Enqueue(audio, generation, final: true);
        }

        await FinishAsync(generation).ConfigureAwait(true);
    }

    private void OnDictationCancelled()
    {
        int generation = _generation;

        if (_coordinator.Cancel())
        {
            _recorder.Discard();
            _log.Write("dictation cancelled");
            _ = FinishAsync(generation);
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

        _segments = TranscribeAfterAsync(_segments, audio, ordinal, generation, lead, final);
    }

    private async Task TranscribeAfterAsync(Task previous, RecordedAudio audio, int ordinal, int generation, TimeSpan lead, bool final)
    {
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Already logged by the segment that failed; this one still runs.
        }

        await TranscribeAsync(audio, ordinal, generation, lead, final).ConfigureAwait(true);
    }

    /// <summary>
    /// Waits for every segment to be inserted, then puts the daemon back to
    /// rest — unless another dictation has begun since.
    /// </summary>
    private async Task FinishAsync(int generation)
    {
        // A segment closed in the last instants of the recording may still be
        // on its way to the loop. Yielding once lets it join the chain before
        // the chain is awaited: both travel through the same queue, in order.
        await Task.Yield();

        try
        {
            await _segments.ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Every segment logs its own failure.
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

    private async Task TranscribeAsync(RecordedAudio audio, int ordinal, int generation, TimeSpan lead, bool final)
    {
        try
        {
            if (!_coordinator.MayInsert(generation))
            {
                return;
            }

            // Returns at once if the model is there, otherwise waits for the
            // load that started with the dictation.
            ParakeetEngine engine = await _engines.GetAsync().ConfigureAwait(true);

            using var wav = new MemoryStream(audio.Wav);
            TranscriptionResult result = await engine.TranscribeAsync(wav).ConfigureAwait(true);

            double peak = AudioLevel.Peak(audio.Wav.AsSpan(WavFile.HeaderSize), lead);

            _log.Write(DictationReport.Describe(audio.Duration, ordinal, peak, result.Duration, result.Text.Length));

            if (DictationReport.WhyNothing(result.Text.Length, peak) is { } nothing)
            {
                _log.Write(nothing);
            }

            string text = InsertionText.ForSegment(result.Text, ordinal);

            if (text.Length == 0)
            {
                return;
            }

            await InsertAsync(text, generation, final).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Deliberately broad. Whatever escapes here — a tool gone, a
            // native failure — must cost this segment only: an exception left
            // in the chain would make every later dictation fail in silence.
            // The type and the message only, never the text.
            _log.Write($"segment {ordinal} failed: {ex.GetType().Name}: {ex.Message}");
            Notify("Dictation failed", "A dictation could not be transcribed or inserted. The log has the details.");
        }
    }

    private async Task InsertAsync(string text, int generation, bool final)
    {
        if (final)
        {
            await WaitForModifiersAsync().ConfigureAwait(true);
        }

        GuardDecision guard = await Task.Run(_guard.Check).ConfigureAwait(true);

        if (!guard.Allowed)
        {
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

        (InjectionContext context, IReadOnlyDictionary<string, string> tools) =
            await Task.Run(() => _injector.Survey(_session)).ConfigureAwait(true);

        InjectionPlan plan = InjectionPlanner.Plan(_settings.Insertion, _settings.KeySender, _settings.ClipboardFallback, context);
        InsertionResult inserted = await _injector.InsertAsync(text, plan, _settings.PasteShortcut, tools).ConfigureAwait(true);

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

    private void DiscardIfCurrent(int recording)
    {
        if (_recorder.IsCurrent(recording))
        {
            _recorder.Discard();
        }
    }

    // --- The session ------------------------------------------------------------

    /// <summary>Off the loop, every couple of seconds: loginctl takes a process start.</summary>
    private void PollSession()
    {
        GuardDecision decision = _guard.Check();
        _loop.Post(() => OnSessionChecked(decision));
    }

    private void OnSessionChecked(GuardDecision decision)
    {
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

            switch (ControlCommands.Resolve(command, _coordinator.State))
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

        // A shortcut being armed gives way to the command, which takes over
        // the microphone it already opened, and its tag with it.
        bool armed = _confirmation.Disarm();
        int recording = armed && _recorder.IsCurrent(_lastRecording) ? _lastRecording : ++_lastRecording;

        if (!await OpenMicrophoneAsync(recording, () => true).ConfigureAwait(true))
        {
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
                _loop.Complete();
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

    public void Dispose()
    {
        _sessionPoll?.Dispose();
        _keyboard.Dispose();
        _recorder.Dispose();
        _engines.Dispose();
        _uinput?.Dispose();
    }
}
