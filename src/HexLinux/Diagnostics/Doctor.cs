using System.Text;
using HexLinux.Configuration;
using HexLinux.Input;
using HexLinux.Output;
using HexLinux.Platform;
using HexLinux.Session;

namespace HexLinux.Diagnostics;

/// <summary>How a check came out.</summary>
public enum CheckStatus
{
    Ok,
    Info,
    Warning,
    Error,
}

/// <summary>One line of the doctor's report, and what to do about it.</summary>
public sealed record DoctorCheck(string Area, CheckStatus Status, string Summary, string? Hint = null);

/// <summary>What opening a device gave.</summary>
public enum DeviceAccess
{
    Accessible,
    PermissionDenied,
    Missing,
    Failed,
}

/// <summary>One keyboard able to send the shortcut, and whether it could be opened.</summary>
public sealed record KeyboardFact(string Name, string Path, DeviceAccess Access);

/// <summary>Whether a recording stream could be opened on the sound server.</summary>
public enum AudioStatus
{
    Works,
    Failed,
    LibraryMissing,
}

/// <summary>
/// Everything the doctor looked at, collected by the caller. Plain data: the
/// collecting touches the machine, the judging below does not.
/// </summary>
public sealed record DoctorFacts
{
    public required DesktopSession Session { get; init; }

    /// <summary>The socket <c>WAYLAND_DISPLAY</c> names, when it names one.</summary>
    public string? WaylandSocket { get; init; }

    public bool WaylandSocketExists { get; init; }

    public required string SettingsFile { get; init; }

    public bool SettingsFileExists { get; init; }

    /// <summary>What settings.json had that could not be used, from <see cref="AppSettings.Parse(string, out IReadOnlyList{string})"/>.</summary>
    public IReadOnlyList<string> SettingsNotes { get; init; } = [];

    public required AppSettings Settings { get; init; }

    public string? ModelDirectory { get; init; }

    public IReadOnlyList<string> ModelProblems { get; init; } = [];

    public IReadOnlyList<KeyboardFact> Keyboards { get; init; } = [];

    public DeviceAccess Uinput { get; init; } = DeviceAccess.Missing;

    public IReadOnlyDictionary<string, string> Tools { get; init; } = new Dictionary<string, string>();

    public bool? WtypeWorks { get; init; }

    public AudioStatus Audio { get; init; }

    public string? AudioDetail { get; init; }

    public GuardDecision Guard { get; init; }

    /// <summary>The keyboard layouts in use, as <c>layout+variant</c>.</summary>
    public IReadOnlyList<string> Layouts { get; init; } = [];

    /// <summary>The running daemon's state word, or null when none answers.</summary>
    public string? DaemonState { get; init; }

    public bool AutostartEnabled { get; init; }
}

/// <summary>The doctor's verdict: every check, the two insertion plans, and the exit code.</summary>
public sealed record DoctorReport(IReadOnlyList<DoctorCheck> Checks, InjectionPlan PastePlan, InjectionPlan TypePlan, int ExitCode)
{
    public string Render()
    {
        var text = new StringBuilder("HexLinux doctor\n\n");

        foreach (DoctorCheck check in Checks)
        {
            string tag = check.Status switch
            {
                CheckStatus.Ok => "  ok",
                CheckStatus.Info => "info",
                CheckStatus.Warning => "warn",
                _ => "FAIL",
            };

            text.Append('[').Append(tag).Append("] ").Append(check.Area.PadRight(12)).Append(check.Summary).Append('\n');

            if (check.Hint is not null)
            {
                text.Append(' ', 19).Append("-> ").Append(check.Hint).Append('\n');
            }
        }

        text.Append('\n').Append(ExitCode == 0
            ? "Nothing essential is missing."
            : $"Something essential is missing (exit code {ExitCode}).").Append('\n');

        return text.ToString();
    }
}

/// <summary>
/// Judges the facts the doctor collected.
///
/// <para>Pure, so that every verdict is a test: which problem is essential,
/// which only a warning. <b>Essential</b> means dictation cannot work at all:
/// no model (exit code 2), no microphone, no way to insert with the
/// configured mode, or a logind that cannot be asked, whose session guard then
/// refuses every insertion (3). A keyboard that cannot be read is a warning only: the
/// shortcut is then unavailable, but <c>hexlinux --toggle</c> still dictates
/// from a desktop shortcut, with no permission at all.</para>
/// </summary>
public static class DoctorEvaluation
{
    private const string UdevHint = "run scripts/install-udev-rules.sh, or bind \"hexlinux --toggle\" to a desktop shortcut";

    public static DoctorReport Evaluate(DoctorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        AppSettings settings = facts.Settings;
        List<DoctorCheck> checks = [];

        checks.AddRange(SessionChecks(facts));
        checks.AddRange(SettingsChecks(facts));

        bool modelMissing = facts.ModelDirectory is null || facts.ModelProblems.Count > 0;
        checks.Add(ModelCheck(facts));

        checks.Add(KeyboardCheck(facts));
        checks.Add(new DoctorCheck("shortcut", CheckStatus.Info, HotkeyText.Describe(settings.Hotkey)));
        checks.AddRange(HotkeyText.Caveats(settings.Hotkey, settings.Segmentation)
            .Select(caveat => new DoctorCheck("shortcut", CheckStatus.Warning, caveat)));

        checks.Add(UinputCheck(facts));
        checks.AddRange(ToolChecks(facts));

        bool audioBroken = facts.Audio != AudioStatus.Works;
        checks.Add(AudioCheck(facts));

        var context = new InjectionContext(
            facts.Session,
            facts.Tools.Keys.ToHashSet(StringComparer.Ordinal),
            facts.Uinput == DeviceAccess.Accessible,
            facts.WtypeWorks);

        InjectionPlan paste = InjectionPlanner.Plan(InsertionMode.Paste, settings.KeySender, settings.ClipboardFallback, context);
        InjectionPlan type = InjectionPlanner.Plan(InsertionMode.Type, settings.KeySender, settings.ClipboardFallback, context);

        checks.Add(PlanCheck("paste", paste, settings.Insertion == InsertionMode.Paste));
        checks.Add(PlanCheck("type", type, settings.Insertion == InsertionMode.Type));

        InjectionPlan configured = settings.Insertion == InsertionMode.Type ? type : paste;
        bool insertionImpossible = configured.Outcome == InjectionOutcome.Impossible;

        checks.AddRange(InsertionNotes(facts, paste));
        checks.Add(GuardCheck(facts.Guard));

        checks.Add(new DoctorCheck(
            "daemon",
            CheckStatus.Info,
            facts.DaemonState is null ? "not running" : $"running ({facts.DaemonState})"));

        checks.Add(new DoctorCheck(
            "autostart",
            CheckStatus.Info,
            facts.AutostartEnabled ? "on: HexLinux starts with the session" : "off (hexlinux --autostart on)"));

        int exitCode = modelMissing ? 2 : audioBroken || insertionImpossible || GuardFails(facts.Guard) ? 3 : 0;

        return new DoctorReport(checks, paste, type, exitCode);
    }

    private static IEnumerable<DoctorCheck> SessionChecks(DoctorFacts facts)
    {
        DesktopSession session = facts.Session;
        string desktop = session.Desktop.Length > 0 ? session.Desktop : "desktop unknown";

        yield return session.Server switch
        {
            DisplayServer.Wayland => new DoctorCheck(
                "session",
                CheckStatus.Ok,
                $"Wayland ({desktop}){(session.HasX11Display ? ", with an X11 display for XWayland" : "")}"),
            DisplayServer.X11 => new DoctorCheck("session", CheckStatus.Ok, $"X11 ({desktop})"),
            _ => new DoctorCheck(
                "session",
                CheckStatus.Warning,
                "no graphical session: neither WAYLAND_DISPLAY nor DISPLAY leads to a display",
                "start HexLinux from the desktop session"),
        };

        if (facts.WaylandSocket is not null && !facts.WaylandSocketExists)
        {
            yield return new DoctorCheck(
                "session",
                CheckStatus.Warning,
                $"WAYLAND_DISPLAY names {facts.WaylandSocket}, which does not exist: Wayland tools cannot connect"
                + (session.HasX11Display ? ", so X11 is used instead" : string.Empty),
                "under WSL with systemd, WAYLAND_DISPLAY=/mnt/wslg/runtime-dir/wayland-0 reaches WSLg's compositor");
        }
    }

    private static IEnumerable<DoctorCheck> SettingsChecks(DoctorFacts facts)
    {
        yield return facts.SettingsFileExists
            ? new DoctorCheck("settings", CheckStatus.Ok, facts.SettingsFile)
            : new DoctorCheck("settings", CheckStatus.Info, $"{facts.SettingsFile} does not exist yet: defaults in use");

        foreach (string note in facts.SettingsNotes)
        {
            yield return new DoctorCheck("settings", CheckStatus.Warning, note);
        }
    }

    private static DoctorCheck ModelCheck(DoctorFacts facts)
    {
        if (facts.ModelDirectory is null)
        {
            return new DoctorCheck(
                "model",
                CheckStatus.Error,
                $"\"{facts.Settings.ModelPath}\" was found neither in the data folder nor next to the executable",
                "download it with get-model.sh (next to hexlinux in the release, scripts/get-model.sh in a clone)");
        }

        return facts.ModelProblems.Count == 0
            ? new DoctorCheck("model", CheckStatus.Ok, facts.ModelDirectory)
            : new DoctorCheck(
                "model",
                CheckStatus.Error,
                "incomplete: " + string.Join("; ", facts.ModelProblems),
                "download it again with get-model.sh --force (next to hexlinux in the release, scripts/ in a clone)");
    }

    private static DoctorCheck KeyboardCheck(DoctorFacts facts)
    {
        string hotkey = HotkeyText.Describe(facts.Settings.Hotkey);
        KeyboardFact[] readable = [.. facts.Keyboards.Where(keyboard => keyboard.Access == DeviceAccess.Accessible)];
        KeyboardFact[] denied = [.. facts.Keyboards.Where(keyboard => keyboard.Access == DeviceAccess.PermissionDenied)];

        if (facts.Keyboards.Count == 0)
        {
            return new DoctorCheck(
                "keyboards",
                CheckStatus.Warning,
                $"no keyboard able to send {hotkey} was found: the shortcut is unavailable",
                "bind \"hexlinux --toggle\" to a desktop shortcut to dictate without it");
        }

        if (readable.Length == 0)
        {
            return new DoctorCheck(
                "keyboards",
                CheckStatus.Warning,
                $"{facts.Keyboards.Count} keyboard(s) found, none readable"
                + (denied.Length > 0 ? " (permission denied)" : string.Empty) + ": the shortcut is unavailable",
                UdevHint);
        }

        string names = string.Join(", ", readable.Select(keyboard => $"{keyboard.Name} ({keyboard.Path})"));

        return denied.Length == 0
            ? new DoctorCheck("keyboards", CheckStatus.Ok, $"{readable.Length} readable: {names}")
            : new DoctorCheck("keyboards", CheckStatus.Warning, $"{readable.Length} readable ({names}), {denied.Length} not readable", UdevHint);
    }

    private static DoctorCheck UinputCheck(DoctorFacts facts)
    {
        // Only needed where nothing else can send keys: under Wayland.
        CheckStatus unavailable = facts.Session.Server == DisplayServer.Wayland ? CheckStatus.Warning : CheckStatus.Info;

        return facts.Uinput switch
        {
            DeviceAccess.Accessible => new DoctorCheck("uinput", CheckStatus.Ok, "writable: the paste shortcut can be sent under any compositor"),
            DeviceAccess.PermissionDenied => new DoctorCheck(
                "uinput",
                unavailable,
                "/dev/uinput is not writable: needed only to paste under GNOME or KDE Wayland",
                "scripts/install-udev-rules.sh --with-uinput"),
            DeviceAccess.Missing => new DoctorCheck("uinput", unavailable, "/dev/uinput does not exist (the uinput module is not loaded)"),
            _ => new DoctorCheck("uinput", unavailable, "/dev/uinput could not be opened"),
        };
    }

    private static IEnumerable<DoctorCheck> ToolChecks(DoctorFacts facts)
    {
        string[] found = [.. ToolLocator.Known.Where(facts.Tools.ContainsKey)];
        string[] missing = [.. ToolLocator.Known.Where(tool => !facts.Tools.ContainsKey(tool))];

        yield return new DoctorCheck(
            "tools",
            CheckStatus.Info,
            $"found: {(found.Length > 0 ? string.Join(' ', found) : "none")}; missing: {(missing.Length > 0 ? string.Join(' ', missing) : "none")}");

        if (facts.WtypeWorks == false)
        {
            yield return new DoctorCheck("tools", CheckStatus.Info, "wtype is installed but this compositor refuses it (no virtual-keyboard protocol)");
        }
    }

    private static DoctorCheck AudioCheck(DoctorFacts facts) => facts.Audio switch
    {
        AudioStatus.Works => new DoctorCheck("audio", CheckStatus.Ok, "the microphone opens (default source of the sound server)"),
        AudioStatus.LibraryMissing => new DoctorCheck(
            "audio",
            CheckStatus.Error,
            "libpulse-simple.so.0 is missing",
            "install libpulse0 (it also serves PipeWire through pipewire-pulse)"),
        _ => new DoctorCheck(
            "audio",
            CheckStatus.Error,
            $"the microphone cannot be opened: {facts.AudioDetail ?? "unknown error"}",
            "check that PulseAudio or PipeWire runs and that a default source exists (pactl get-default-source)"),
    };

    private static DoctorCheck PlanCheck(string area, InjectionPlan plan, bool configured)
    {
        string suffix = configured ? " (configured)" : string.Empty;

        return plan.Outcome switch
        {
            InjectionOutcome.Insert => new DoctorCheck(area, CheckStatus.Ok, plan.Describe() + suffix),
            InjectionOutcome.ClipboardOnly => new DoctorCheck(area, CheckStatus.Warning, plan.Describe() + suffix),
            _ => new DoctorCheck(area, configured ? CheckStatus.Error : CheckStatus.Warning, plan.Describe() + suffix),
        };
    }

    private static IEnumerable<DoctorCheck> InsertionNotes(DoctorFacts facts, InjectionPlan paste)
    {
        AppSettings settings = facts.Settings;

        if (settings.ClipboardFallback)
        {
            yield return new DoctorCheck(
                "insertion",
                CheckStatus.Info,
                "clipboardFallback is on: when nothing can send keys, the dictation is left in the clipboard, "
                + "where a clipboard history may keep it");
        }

        if (settings.PasteShortcut == PasteShortcut.ShiftInsert)
        {
            yield return new DoctorCheck(
                "insertion",
                CheckStatus.Info,
                "Shift+Insert pastes the PRIMARY selection, not the clipboard, in xterm and similar terminals");
        }

        IReadOnlyList<string> moving = KeyboardLayouts.MovingV(facts.Layouts);

        if (paste.Keys == KeyStroker.Uinput && settings.PasteShortcut != PasteShortcut.ShiftInsert && moving.Count > 0)
        {
            yield return new DoctorCheck(
                "layout",
                CheckStatus.Warning,
                $"the layout {string.Join(", ", moving)} puts another letter where QWERTY has V: the virtual keyboard's "
                + "paste shortcut would press that letter instead",
                "set \"pasteShortcut\" to \"ShiftInsert\", or \"keySender\" to Xdotool or Wtype where they work");
        }
    }

    private static DoctorCheck GuardCheck(GuardDecision guard)
    {
        if (!guard.LogindKnown)
        {
            return new DoctorCheck("logind", CheckStatus.Warning, guard.Reason, "lock screens and user switches cannot be detected here");
        }

        if (GuardFails(guard))
        {
            return new DoctorCheck(
                "logind",
                CheckStatus.Error,
                "every insertion would be refused: " + guard.Reason,
                "run HexLinux from the graphical session, and check that loginctl answers there (loginctl show-session $XDG_SESSION_ID)");
        }

        return guard.Allowed
            ? new DoctorCheck("logind", CheckStatus.Ok, guard.Reason)
            : new DoctorCheck("logind", CheckStatus.Warning, "dictation would be refused right now: " + guard.Reason);
    }

    /// <summary>
    /// logind is there but could not say which session is in front: the guard
    /// then refuses every insertion, not only the current one. A locked
    /// screen or another user's session in front passes; this does not.
    /// </summary>
    private static bool GuardFails(GuardDecision guard) =>
        guard.LogindKnown && !guard.Allowed && !guard.Locked && !guard.Inactive;
}
