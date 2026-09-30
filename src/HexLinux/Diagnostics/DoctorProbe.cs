using System.Diagnostics.CodeAnalysis;
using HexLinux.Configuration;
using HexLinux.Daemon;
using HexLinux.Input;
using HexLinux.Interop;
using HexLinux.Output;
using HexLinux.Platform;
using HexLinux.Session;
using HexLinux.Transcription;

namespace HexLinux.Diagnostics;

/// <summary>
/// Collects what <c>--doctor</c> reports on, by actually trying: opening each
/// keyboard, opening <c>/dev/uinput</c>, opening a recording stream, asking
/// logind, asking the daemon. A check that reasoned about permissions
/// instead of trying would get some combination wrong; the judging itself is
/// <see cref="DoctorEvaluation"/>, which is tested.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Probes the real machine; verified by running --doctor. The verdict is DoctorEvaluation, which is tested.")]
internal static class DoctorProbe
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(3);

    public static DoctorFacts Collect(AppPaths paths, AppSettings settings, IReadOnlyList<string> notes)
    {
        Func<string, string?> environment = Environment.GetEnvironmentVariable;
        DesktopSession session = DesktopSession.Detect(environment, File.Exists);
        string? waylandSocket = DesktopSession.WaylandSocketPath(environment);
        IReadOnlyDictionary<string, string> tools = ToolLocator.Locate();

        string? model = ModelLocator.Resolve(settings.ModelPath, AppContext.BaseDirectory, paths.DataDirectory);

        return new DoctorFacts
        {
            Session = session,
            WaylandSocket = waylandSocket,
            WaylandSocketExists = waylandSocket is not null && File.Exists(waylandSocket),
            SettingsFile = paths.SettingsFile,
            SettingsFileExists = File.Exists(paths.SettingsFile),
            SettingsNotes = notes,
            Settings = settings,
            ModelDirectory = model,
            ModelProblems = model is null ? [] : ModelFiles.Problems(model, path => File.Exists(path) ? new FileInfo(path).Length : null),
            Keyboards = Keyboards(settings),
            Uinput = UinputKeyboard.Probe(),
            Tools = tools,
            WtypeWorks = WtypeWorks(session, tools),
            Audio = ProbeAudio(out string? audioDetail),
            AudioDetail = audioDetail,
            Guard = new SessionGuard(tools).Check(),
            Layouts = Layouts(tools, session),
            DaemonState = DaemonState(paths),
            AutostartEnabled = AutoStart.IsEnabled(paths),
        };
    }

    private static IReadOnlyList<KeyboardFact> Keyboards(AppSettings settings)
    {
        string text;

        try
        {
            text = File.ReadAllText(InputDeviceCatalog.SourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        IReadOnlySet<int> codes = new ChordDetector(settings.Hotkey).Codes;

        return
        [
            .. InputDeviceCatalog.Keyboards(InputDeviceCatalog.Parse(text), codes)
                .Select(device => new KeyboardFact(
                    device.Name,
                    device.DevicePath!,
                    DeviceFile.Probe(device.DevicePath!, Libc.ReadOnly | Libc.NonBlocking))),
        ];
    }

    private static bool? WtypeWorks(DesktopSession session, IReadOnlyDictionary<string, string> tools)
    {
        if (session.Server != DisplayServer.Wayland || !tools.TryGetValue(ToolLocator.Wtype, out string? wtype))
        {
            return null;
        }

        return ProcessRunner.Run(wtype, ToolCommands.WtypeProbe().Arguments, ReadOnlyMemory<byte>.Empty, ToolTimeout).Succeeded;
    }

    /// <summary>Opens and closes a recording stream, bounded: the sound server may not answer.</summary>
    private static AudioStatus ProbeAudio(out string? detail)
    {
        string? failure = null;
        AudioStatus status = AudioStatus.Failed;

        Task probe = Task.Run(() =>
        {
            try
            {
                nint stream = PulseSimple.Open(PulseSimple.StreamRecord, 16_000, "HexLinux doctor", 1600);
                PulseSimple.Free(stream);
                status = AudioStatus.Works;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                status = AudioStatus.LibraryMissing;
                failure = ex.Message;
            }
            catch (InvalidOperationException ex)
            {
                failure = ex.Message;
            }
        });

        if (!probe.Wait(TimeSpan.FromSeconds(5)))
        {
            detail = "the sound server did not answer within 5 s";
            return AudioStatus.Failed;
        }

        detail = failure;
        return status;
    }

    private static IReadOnlyList<string> Layouts(IReadOnlyDictionary<string, string> tools, DesktopSession session)
    {
        List<string> layouts = [];

        if (tools.TryGetValue(ToolLocator.Localectl, out string? localectl))
        {
            ProcessResult status = ProcessRunner.Run(localectl, ["status"], input: null, ToolTimeout);

            if (status.Succeeded)
            {
                layouts.AddRange(KeyboardLayouts.FromLocalectl(status.OutputText));
            }
        }

        if (session.IsGnome && tools.TryGetValue(ToolLocator.Gsettings, out string? gsettings))
        {
            ProcessResult sources = ProcessRunner.Run(gsettings, ["get", "org.gnome.desktop.input-sources", "sources"], input: null, ToolTimeout);

            if (sources.Succeeded)
            {
                layouts.AddRange(KeyboardLayouts.FromGnomeSources(sources.OutputText));
            }
        }

        return layouts;
    }

    private static string? DaemonState(AppPaths paths) =>
        ControlClient.Send(paths, ControlCommand.Status) is { Delivery: ControlDelivery.Answered, Line: { } line }
        && ControlReply.TryParse(line, out ControlReply reply)
            ? reply.Detail
            : null;
}
