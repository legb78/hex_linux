using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using HexLinux.Audio;
using HexLinux.Cli;
using HexLinux.Configuration;
using HexLinux.Daemon;
using HexLinux.Diagnostics;
using HexLinux.Feedback;
using HexLinux.Input;
using HexLinux.Output;
using HexLinux.Platform;
using HexLinux.Session;
using HexLinux.Transcription;
using HexLinux.Ui;

namespace HexLinux;

/// <summary>
/// Entry point: reads the command line (<see cref="CommandLine"/>, tested) and
/// runs the mode it names — the daemon, a control command, or one of the
/// diagnostic modes that check one layer each, with no model, no microphone
/// or no keyboard needed by the others.
///
/// <para>Exit codes, as HexWin's: 0 success, 1 generic failure, 2 model
/// missing, 3 failure, 4 silent recording, 5 keyboard unavailable.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Entry point: routes to the modes, whose decisions all live in tested classes.")]
internal static class Program
{
    private const int Success = 0;
    private const int Failure = 1;
    private const int ModelMissing = 2;
    private const int Broken = 3;
    private const int Silent = 4;
    private const int NoKeyboard = 5;

    private static int Main(string[] args)
    {
        CliRequest request = CommandLine.Parse(args);

        return request.Mode switch
        {
            CliMode.Help => PrintHelp(),
            CliMode.Invalid => PrintError(request.Error!),
            CliMode.Control => SendControl(request.Command),
            CliMode.Record => RecordToFile(request.Path!, request.Seconds),
            CliMode.Transcribe => TranscribeFile(request),
            CliMode.WatchHotkey => WatchHotkey(),
            CliMode.Inject => InjectText(request),
            CliMode.TestFeedback => TestFeedback(),
            CliMode.Doctor => Doctor(),
            CliMode.AutoStart => SetAutoStart(request.AutoStartOn),
            _ => RunDaemon(),
        };
    }

    private static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";

    private static int PrintHelp()
    {
        Console.WriteLine($"HexLinux {Version} — local hold-to-talk dictation");
        Console.WriteLine();
        Console.WriteLine(CommandLine.Usage);
        return Success;
    }

    private static int PrintError(string error)
    {
        Console.Error.WriteLine(error);
        return Failure;
    }

    /// <summary>
    /// Settings for a diagnostic mode: the commented file is created on first
    /// use, as for the daemon, and what could not be used is printed.
    /// </summary>
    private static AppSettings LoadSettings(AppPaths paths, out IReadOnlyList<string> notes)
    {
        AppSettings settings = SettingsStore.Load(paths, message => Console.Error.WriteLine(message), out notes);

        foreach (string note in notes)
        {
            Console.Error.WriteLine($"settings.json: {note}");
        }

        return settings;
    }

    // --- The daemon -------------------------------------------------------------

    private static int RunDaemon()
    {
        AppPaths paths = AppPaths.FromEnvironment();
        List<string> early = [];
        AppSettings settings = SettingsStore.Load(paths, early.Add, out IReadOnlyList<string> notes);
        SessionLog log = SessionLog.Create(settings.LogEnabled, paths);

        foreach (string line in early.Concat(notes.Select(note => $"settings.json: {note}")))
        {
            log.Write(line);
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => SessionLog.WriteCrash(paths, (Exception)e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Write($"internal error: {e.Exception.GetBaseException().GetType().Name}: {e.Exception.GetBaseException().Message}");
            e.SetObserved();
        };

        try
        {
            return RunDaemon(paths, settings, log);
        }
        catch (Exception ex)
        {
            // A daemon that vanishes in silence cannot be diagnosed: the trace
            // written here is sometimes the only clue.
            string? crash = SessionLog.WriteCrash(paths, ex);
            Console.Error.WriteLine($"HexLinux stopped on an error: {ex.Message}");
            Console.Error.WriteLine(crash is null ? "(crash.log could not be written)" : $"Details: {crash}");
            return Failure;
        }
    }

    private static int RunDaemon(AppPaths paths, AppSettings settings, SessionLog log)
    {
        string? modelPath = ModelLocator.Resolve(settings.ModelPath, AppContext.BaseDirectory, paths.DataDirectory);

        if (modelPath is null)
        {
            string message = $"Model not found: {settings.ModelPath}. Download it with scripts/get-model.sh, then start HexLinux again.";
            Console.Error.WriteLine(message);
            log.Write(message);

            using IStatusSurface notifier = CreateSurface(log);
            notifier.Notify("Model not found", "Download it with scripts/get-model.sh, then start HexLinux again.");
            return ModelMissing;
        }

        using var loop = new SerialContext(error =>
        {
            log.Write($"internal error: {error.GetType().Name}: {error.Message}");
            SessionLog.WriteCrash(paths, error);
        });

        // Registered before the control socket exists: a signal that comes
        // as soon as the socket is seen (a script waiting for it, a session
        // closing during start-up) must end the loop, not kill the process
        // with the socket file left behind. Completing the loop before it
        // runs makes Run return at once.
        using PosixSignalRegistration interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => Stop(context, loop, log));
        using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => Stop(context, loop, log));
        using PosixSignalRegistration hangUp = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => Stop(context, loop, log));

        DictationDaemon? daemon = null;

        using ControlServer? control = ControlServer.TryStart(
            paths,
            command => daemon is { } running
                ? running.HandleControlAsync(command)
                : Task.FromResult(ControlReply.Done(DictationState.Loading)),
            log.Write,
            out bool alreadyRunning);

        if (alreadyRunning)
        {
            Console.Error.WriteLine("HexLinux is already running (hexlinux --status asks it how it is doing).");
            return Failure;
        }

        if (control is null)
        {
            Console.Error.WriteLine("HexLinux cannot take its single-instance lock: see the log.");
            return Failure;
        }

        using IStatusSurface surface = CreateSurface(log);

        SynchronizationContext.SetSynchronizationContext(loop);
        daemon = new DictationDaemon(loop, paths, settings, log, modelPath, surface);

        daemon.Start();

        if (control.SocketPath is { } socket)
        {
            log.Write($"control socket: {socket}");
        }

        loop.Run();

        daemon.Dispose();
        log.Write("stopped");
        return Success;
    }

    private static IStatusSurface CreateSurface(SessionLog log)
    {
        try
        {
            return StatusSurfaceFactory.Create(log.Write);
        }
        catch (Exception ex)
        {
            log.Write($"no status surface ({ex.GetType().Name}: {ex.Message}): notifications go to this log");
            return new NullStatusSurface(log.Write);
        }
    }

    /// <summary>A signal ends the loop cleanly instead of killing the process mid-dictation.</summary>
    private static void Stop(PosixSignalContext context, SerialContext loop, SessionLog log)
    {
        context.Cancel = true;
        log.Write($"stopping ({context.Signal})");
        loop.Complete();
    }

    // --- Control commands -------------------------------------------------------

    private static int SendControl(ControlCommand command)
    {
        string? line = ControlClient.Send(AppPaths.FromEnvironment(), command);

        if (line is null)
        {
            Console.Error.WriteLine("HexLinux is not running.");
            return Failure;
        }

        if (!ControlReply.TryParse(line, out ControlReply reply))
        {
            Console.Error.WriteLine($"Unexpected answer from the daemon: {line}");
            return Broken;
        }

        Console.WriteLine(reply.Outcome switch
        {
            ControlOutcome.Done => reply.Detail,
            ControlOutcome.Ignored => $"ignored ({reply.Detail})",
            _ => $"error: {reply.Detail}",
        });

        return reply.Outcome == ControlOutcome.Error ? Broken : Success;
    }

    // --- Diagnostic modes ---------------------------------------------------------

    /// <summary>
    /// Microphone diagnostic: records a few seconds, writes the WAV and
    /// measures the level. The measurement is the point: a muted or
    /// unplugged microphone gives a valid, silent file, which the engine then
    /// transcribes as nothing — the fault would be hunted on the wrong side.
    /// </summary>
    private static int RecordToFile(string outputPath, int seconds)
    {
        AppSettings settings = LoadSettings(AppPaths.FromEnvironment(), out _);

        // No cutting here: the file must hold the whole recording.
        using var recorder = new AudioRecorder(RecordingGuards.From(settings), TimeSpan.Zero);

        try
        {
            recorder.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"Recording failed: {ex.Message}");
            return Broken;
        }

        Console.WriteLine($"Recording for {seconds} s: speak now.");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));

        RecordedAudio? recorded = recorder.StopAsync().GetAwaiter().GetResult();

        if (recorded is not { } audio)
        {
            Console.Error.WriteLine("Recording too short: nothing was kept.");
            return Failure;
        }

        if (!WritePrivately(outputPath, audio.Wav, out string? error))
        {
            Console.Error.WriteLine($"Cannot write {outputPath}: {error}");
            return Broken;
        }

        ReadOnlySpan<byte> pcm = audio.Wav.AsSpan(WavFile.HeaderSize);
        double peak = AudioLevel.Peak(pcm);

        Console.WriteLine($"Written  : {outputPath}");
        Console.WriteLine($"Duration : {audio.Duration.TotalSeconds:F2} s");
        Console.WriteLine($"Level    : {peak * 100:F1} %");

        if (AudioLevel.IsSilent(pcm))
        {
            Console.WriteLine();
            Console.Error.WriteLine("The signal is silent. Check which microphone is the default one");
            Console.Error.WriteLine("(pactl get-default-source, or the sound settings of the desktop),");
            Console.Error.WriteLine("that it is not muted, and that it is plugged in.");
            return Silent;
        }

        return Success;
    }

    /// <summary>A recording is the user's voice: the file is created readable by them only.</summary>
    private static bool WritePrivately(string path, byte[] content, out string? error)
    {
        try
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });

            stream.Write(content);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Transcription diagnostic, the main troubleshooting tool: the whole
    /// engine chain, with no microphone and no keyboard involved.
    /// </summary>
    private static int TranscribeFile(CliRequest request)
    {
        string wavPath = request.Path!;

        if (!File.Exists(wavPath))
        {
            Console.Error.WriteLine($"File not found: {wavPath}");
            return Failure;
        }

        AppPaths paths = AppPaths.FromEnvironment();
        AppSettings settings = LoadSettings(paths, out _);

        if (request.Model is not null)
        {
            settings.ModelPath = request.Model;
        }

        if (request.Provider is not null)
        {
            settings.Provider = request.Provider;
        }

        // A command-line option goes through the same validation as the file.
        settings.Normalize();

        string? modelPath = ModelLocator.Resolve(settings.ModelPath, AppContext.BaseDirectory, paths.DataDirectory);

        if (modelPath is null)
        {
            Console.Error.WriteLine($"Model not found: {settings.ModelPath}");
            Console.Error.WriteLine("Download it with: scripts/get-model.sh");
            return ModelMissing;
        }

        try
        {
            Console.WriteLine($"Model    : {Path.GetFileName(modelPath)}");
            Console.WriteLine($"Compute  : {settings.Provider}, {settings.Threads} threads");
            Console.WriteLine("Loading the model...");

            using ParakeetEngine engine = ParakeetEngine.Load(modelPath, settings.Provider, settings.Threads, settings.FrenchSpacing);

            Console.WriteLine();

            using FileStream wav = File.OpenRead(wavPath);
            TranscriptionResult result = engine.TranscribeAsync(wav).GetAwaiter().GetResult();

            Console.WriteLine(result.Text.Length > 0 ? result.Text : "(nothing usable)");
            Console.WriteLine();
            Console.WriteLine($"Transcribed in {result.Duration.TotalSeconds:F2} s");
            return Success;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ModelMissing;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DllNotFoundException)
        {
            Console.Error.WriteLine($"Transcription failed: {ex.Message}");
            return Broken;
        }
    }

    /// <summary>
    /// Shortcut diagnostic: listens to the keyboards and shows every start,
    /// stop and cancel, and the name of the shortcut keys pressed — to find
    /// the name of a key before writing it into settings.json. Letters,
    /// digits and every other key show nothing: this mode must not be a way
    /// to watch what is typed.
    /// </summary>
    private static int WatchHotkey()
    {
        AppSettings settings = LoadSettings(AppPaths.FromEnvironment(), out _);
        var detector = new ChordDetector(settings.Hotkey);
        var tracker = new HotkeyTracker(detector);

        using var loop = new SerialContext(error => Console.Error.WriteLine($"internal error: {error.Message}"));

        HashSet<int> codes = [.. LinuxKeys.ShortcutCodes, .. detector.Codes];

        void Show(ChordAction action)
        {
            string? word = action switch
            {
                ChordAction.Start => "start",
                ChordAction.Stop => "stop",
                ChordAction.Cancel => "cancel",
                _ => null,
            };

            if (word is not null)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]  {word}");
            }
        }

        void OnEvent(string device, InputEvent inputEvent)
        {
            if (inputEvent.IsKey && inputEvent.Transition == KeyTransition.Pressed && LinuxKeys.NameOf(inputEvent.Code) is { } name)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]  key {name}");
            }

            Show(tracker.OnEvent(device, inputEvent));
        }

        using var keyboard = new EvdevKeyboard(
            codes,
            (device, inputEvent) => loop.Post(() => OnEvent(device, inputEvent)),
            device => loop.Post(() => Show(tracker.OnDeviceRemoved(device))),
            message => Console.Error.WriteLine(message));

        Console.WriteLine($"Shortcut: {HotkeyText.Describe(settings.Hotkey)} ({string.Join(" + ", settings.Hotkey)})");

        foreach (string caveat in HotkeyText.Caveats(settings.Hotkey, settings.Segmentation))
        {
            Console.WriteLine($"Note: {caveat}");
        }

        keyboard.Start();

        if (keyboard.OpenKeyboards.Count == 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("No keyboard can be read, so the shortcut cannot be watched.");
            Console.Error.WriteLine("Run hexlinux --doctor to see why (permission, or no /dev/input at all as under WSL).");
            return NoKeyboard;
        }

        Console.WriteLine();
        Console.WriteLine("Hold the shortcut a few seconds, then release it. Ctrl+C to quit.");
        Console.WriteLine();

        using PosixSignalRegistration interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            loop.Complete();
        });

        loop.Run();
        return Success;
    }

    /// <summary>
    /// Insertion diagnostic: waits a few seconds — long enough to put the
    /// cursor in the target window — then inserts the text there, the way the
    /// daemon would, and says which tools it used.
    /// </summary>
    private static int InjectText(CliRequest request)
    {
        string text = request.TextFromStandardInput ? Console.In.ReadToEnd().TrimEnd('\n') : request.Text!;
        AppSettings settings = LoadSettings(AppPaths.FromEnvironment(), out _);
        KeySender sender = request.Sender ?? settings.KeySender;
        DesktopSession session = DesktopSession.FromEnvironment();

        // Created now, not at the last moment: the countdown gives the
        // compositor time to pick the new keyboard up.
        using UinputKeyboard? uinput = sender is KeySender.Auto or KeySender.Uinput ? UinputKeyboard.TryCreate(out _, out _) : null;

        var injector = new TextInjector(uinput, message => Console.Error.WriteLine(message));
        (InjectionContext context, IReadOnlyDictionary<string, string> tools) = injector.Survey(session);
        InjectionPlan plan = InjectionPlanner.Plan(request.InjectMode, sender, settings.ClipboardFallback, context);

        Console.WriteLine($"Mode     : {request.InjectMode}");
        Console.WriteLine($"Session  : {session.Server}{(session.Desktop.Length > 0 ? $" ({session.Desktop})" : "")}");
        Console.WriteLine($"Plan     : {plan.Describe()}");

        if (plan.Outcome == InjectionOutcome.Impossible)
        {
            return Broken;
        }

        Console.WriteLine();
        Console.Write($"Put the cursor in the target window. Inserting in {request.Delay} s...");

        for (int remaining = request.Delay; remaining > 0; remaining--)
        {
            Console.Write($" {remaining}");
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }

        Console.WriteLine();

        GuardDecision guard = new SessionGuard(tools).Check();

        if (!guard.Allowed)
        {
            Console.Error.WriteLine($"Not inserted: {guard.Reason}");
            return Broken;
        }

        InsertionResult result = injector.InsertAsync(InsertionText.Sanitize(text), plan, settings.PasteShortcut, tools).GetAwaiter().GetResult();

        switch (result.Status)
        {
            case InsertionStatus.Inserted:
                Console.WriteLine("Inserted.");
                return Success;
            case InsertionStatus.CopiedOnly:
                Console.WriteLine("Left in the clipboard: press Ctrl+V to paste it.");
                return Success;
            default:
                Console.Error.WriteLine($"Not inserted: {result.Problem}");
                return Broken;
        }
    }

    /// <summary>
    /// Tone diagnostic: plays a whole dictation's worth of tones, with no
    /// model, no microphone and no shortcut — two seconds to judge them.
    /// </summary>
    private static int TestFeedback()
    {
        AppSettings settings = LoadSettings(AppPaths.FromEnvironment(), out _);

        Console.WriteLine($"Feedback: {settings.Feedback}");
        Console.WriteLine();

        if (settings.Feedback == FeedbackMode.None)
        {
            Console.WriteLine("No feedback is configured.");
            Console.WriteLine("Set \"feedback\" to \"Sound\" in settings.json to hear the tones.");
            return Success;
        }

        var policy = new FeedbackPolicy(settings.Feedback);
        var tones = new CueTones(SessionLog.Silent);
        policy.Next(DictationState.Idle);

        (DictationState State, string Label, int HoldMilliseconds)[] script =
        [
            (DictationState.Recording, "recording starts", 3_000),
            (DictationState.Transcribing, "recording ends", 800),
            (DictationState.Idle, "back to idle", 300),
        ];

        foreach ((DictationState state, string label, int hold) in script)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]  {label}");
            Task playing = tones.PlayAsync(policy.Next(state));
            Thread.Sleep(hold);
            playing.Wait(TimeSpan.FromSeconds(2));
        }

        Console.WriteLine();
        Console.WriteLine("Done.");
        return Success;
    }

    private static int Doctor()
    {
        AppPaths paths = AppPaths.FromEnvironment();
        AppSettings settings = SettingsStore.Load(paths, message => Console.Error.WriteLine(message), out IReadOnlyList<string> notes);

        DoctorReport report = DoctorEvaluation.Evaluate(DoctorProbe.Collect(paths, settings, notes));

        Console.Write(report.Render());
        return report.ExitCode;
    }

    private static int SetAutoStart(bool enabled)
    {
        bool done = AutoStart.SetEnabled(AppPaths.FromEnvironment(), enabled, out string message);

        (done ? Console.Out : Console.Error).WriteLine(message);
        return done ? Success : Failure;
    }
}
