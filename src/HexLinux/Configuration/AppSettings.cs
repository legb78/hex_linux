using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HexLinux.Configuration;

/// <summary>
/// How the transcribed text is handed back to the focused application.
/// </summary>
public enum InsertionMode
{
    /// <summary>Clipboard, then the paste shortcut. Instant, even on a long text.</summary>
    Paste,

    /// <summary>Simulated typing, character by character. Slower, and it needs
    /// a typing tool the desktop accepts — see <see cref="KeySender"/>.</summary>
    Type,
}

/// <summary>
/// What tells the user that a recording has started and stopped.
///
/// <para>Only the tones exist on Linux so far. The Windows version also draws
/// a circle at the top of the screen; on Wayland a window cannot place itself
/// on screen, and the protocol that would allow it (layer-shell) is not
/// offered by GNOME. Rather than accept a value that would do nothing, the
/// setting only offers what works — the same reasoning that removed the GPU
/// providers on Windows.</para>
/// </summary>
public enum FeedbackMode
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>A short tone when it starts, a lower one when it stops.</summary>
    Sound,
}

/// <summary>The keystroke that asks the focused application to paste.</summary>
public enum PasteShortcut
{
    /// <summary>What nearly every graphical application understands.</summary>
    CtrlV,

    /// <summary>What terminals understand, where Ctrl+V means something else.</summary>
    CtrlShiftV,

    /// <summary>The oldest convention, still honoured by most toolkits and terminals.</summary>
    ShiftInsert,
}

/// <summary>
/// Which tool delivers the keystrokes — the paste shortcut, or the typed text.
///
/// <para>Linux has no single answer. Wayland deliberately lets no ordinary
/// application send keys to another; X11 lets any application do it. What
/// works therefore depends on the session, and <see cref="Auto"/> picks from
/// what is installed and permitted. The others force a choice when the
/// automatic one is wrong for a particular desktop.</para>
/// </summary>
public enum KeySender
{
    Auto,

    /// <summary>
    /// A virtual keyboard created through /dev/uinput. Works under X11 and
    /// every Wayland compositor, since the keys enter below the desktop, but
    /// needs write access to /dev/uinput. Paste only: which character a key
    /// produces depends on the layout, so text cannot be typed this way.
    /// </summary>
    Uinput,

    /// <summary>xdotool, through XTest. X11 only, including XWayland windows.</summary>
    Xdotool,

    /// <summary>wtype, through the virtual-keyboard protocol. wlroots compositors (Sway, Hyprland…).</summary>
    Wtype,

    /// <summary>ydotool, uinput through its own daemon. Types only what a US layout can.</summary>
    Ydotool,
}

/// <summary>
/// User configuration, read from <c>~/.config/hexlinux/settings.json</c>.
///
/// All the logic in this class is pure: <see cref="Parse"/> and
/// <see cref="Normalize"/> never touch the disk, which makes them entirely
/// testable. Only <see cref="Load"/> does any I/O.
///
/// The principle is that a damaged file must never stop the application from
/// starting: any invalid value is replaced by its default rather than raising
/// an exception.
/// </summary>
public sealed class AppSettings
{
    public const string FileName = "settings.json";

    /// <summary>
    /// Folder of the Parakeet model. A relative path is looked for next to
    /// the executable and up the tree, then in the XDG data folder. A folder
    /// and not a file: the model is made of an encoder, a decoder, a joiner
    /// and a vocabulary.
    /// </summary>
    public string ModelPath { get; set; } = DefaultModelPath;

    /// <summary>Keys to hold down to dictate.</summary>
    public string[] Hotkey { get; set; } = [.. DefaultHotkey];

    /// <summary>Below this, the press counts as accidental and is ignored.</summary>
    public int MinRecordingMilliseconds { get; set; } = 250;

    /// <summary>Cuts the recording off if the key stays held down.</summary>
    public int MaxRecordingSeconds { get; set; } = 120;

    /// <summary>
    /// Inserts a long dictation sentence by sentence, each pause closing a
    /// segment transcribed while the user keeps talking. Off by default: the
    /// text then arrives at release, and nobody finds it appearing
    /// mid-sentence without having asked for it.
    /// </summary>
    public bool Segmentation { get; set; }

    /// <summary>
    /// A pause in speech this long closes a segment, when
    /// <see cref="Segmentation"/> is on. Kept while it is off, so turning it
    /// back on finds the duration the user had chosen. Zero also turns the
    /// cutting off.
    /// </summary>
    public int PauseMilliseconds { get; set; } = DefaultPauseMilliseconds;

    /// <summary>
    /// ONNX Runtime compute provider. Only "cpu" is accepted; see
    /// <see cref="KnownProviders"/> for why.
    /// </summary>
    public string Provider { get; set; } = DefaultProvider;

    /// <summary>
    /// Threads allotted to decoding. Past a handful the gain collapses: the
    /// model is small and synchronisation costs more than the parallelism
    /// brings.
    /// </summary>
    public int Threads { get; set; } = DefaultThreads;

    /// <summary>
    /// Minutes without a dictation after which the model is released. Zero
    /// keeps it resident forever.
    /// </summary>
    public int UnloadAfterMinutes { get; set; } = DefaultUnloadAfterMinutes;

    public InsertionMode Insertion { get; set; } = InsertionMode.Paste;

    /// <summary>The keystroke sent after filling the clipboard, in Paste mode.</summary>
    public PasteShortcut PasteShortcut { get; set; } = PasteShortcut.CtrlV;

    /// <summary>The tool that delivers keystrokes; see <see cref="Configuration.KeySender"/>.</summary>
    public KeySender KeySender { get; set; } = KeySender.Auto;

    /// <summary>Cue marking the start and the end of a recording.</summary>
    public FeedbackMode Feedback { get; set; } = FeedbackMode.Sound;

    /// <summary>Logs every dictation: duration, captured level, characters produced.</summary>
    public bool LogEnabled { get; set; } = true;

    // --- Reference values -----------------------------------------------------

    private const string DefaultModelPath = "models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";
    private const string DefaultProvider = "cpu";
    private const int DefaultThreads = 4;
    private const int DefaultUnloadAfterMinutes = 5;

    /// <summary>
    /// Long enough to sit between two sentences, short enough that the text
    /// shows up while the next one is being spoken. Gaps between words are
    /// well under half of it.
    /// </summary>
    private const int DefaultPauseMilliseconds = 700;

    // The bounds Normalize enforces.
    internal const int MinRecordingMillisecondsCeiling = 5_000;
    internal const int MaxRecordingSecondsFloor = 5;
    internal const int MaxRecordingSecondsCeiling = 600;
    internal const int MaxPauseMilliseconds = 5_000;
    internal const int MaxUnloadAfterMinutes = 1_440;
    internal const int MaxThreads = 32;

    /// <summary>
    /// The right Control key, alone. The keys of the shortcut are not withheld
    /// from the desktop on Linux (see <c>ChordDetector</c>), so the default has
    /// to be a key that does nothing when pressed on its own, that nobody
    /// types text with, and that leaves a paste unchanged when it is still
    /// held — which is the case with sentence-by-sentence insertion, where
    /// Ctrl+V is sent while the user keeps holding the shortcut. Right Shift,
    /// the Windows default, fails the last two: it shifts capitals, and turns
    /// Ctrl+V into Ctrl+Shift+V.
    /// </summary>
    private static readonly string[] DefaultHotkey = ["RightCtrl"];

    /// <summary>
    /// Compute providers that are actually available. The processor is the
    /// only one, and that is not a choice: the sherpa-onnx native libraries
    /// published on NuGet are built for it alone. The Windows version found
    /// out the hard way that a GPU provider is accepted and then silently
    /// falls back to the processor; offering it would promise an acceleration
    /// that cannot happen.
    /// </summary>
    private static readonly string[] KnownProviders = ["cpu"];

    /// <summary>
    /// Keys allowed in a shortcut. Deliberately narrow: modifiers and keys
    /// that type nothing. Fn is absent because it is handled by the keyboard's
    /// own controller and emits no code the kernel can see.
    /// </summary>
    private static readonly string[] KnownHotkeyNames =
    [
        "Ctrl", "LeftCtrl", "RightCtrl",
        "Alt", "LeftAlt", "RightAlt",
        "Shift", "LeftShift", "RightShift",
        "Super", "LeftSuper", "RightSuper",
        "CapsLock", "Space",
        "F13", "F14", "F15", "F16", "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24",
    ];

    /// <summary>
    /// The Windows names of the Super keys, accepted so that a settings.json
    /// carried over from HexWin keeps its shortcut instead of falling back to
    /// the default.
    /// </summary>
    private static readonly Dictionary<string, string> HotkeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Win"] = "Super",
        ["LeftWin"] = "LeftSuper",
        ["RightWin"] = "RightSuper",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly PropertyInfo[] Settable =
        [.. typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite)];

    // --- Reading --------------------------------------------------------------

    /// <summary>
    /// Reads the file if it exists, otherwise returns the defaults. An
    /// unreadable or malformed file also yields the defaults: dictation must
    /// keep working even when the configuration is broken.
    /// </summary>
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Builds a valid configuration from JSON. Unknown keys are ignored,
    /// invalid values are replaced by their default, and unreadable JSON
    /// simply yields the default configuration.
    ///
    /// <para><b>Read one setting at a time, on purpose.</b> Handing the whole
    /// object to the serializer makes one bad value fatal to all the others:
    /// an unknown enum name — <c>"feedback": "Circle"</c> — or a string where
    /// a number belongs throws, and the file is discarded whole, hotkey
    /// included. Each value here is read on its own, so a typo costs only the
    /// setting it sits in.</para>
    /// </summary>
    public static AppSettings Parse(string json)
    {
        var settings = new AppSettings();

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, DocumentOptions);

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    Apply(settings, property);
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON at all: the defaults stand.
        }

        settings.Normalize();
        return settings;
    }

    private static void Apply(AppSettings settings, JsonProperty property)
    {
        PropertyInfo? target = Array.Find(
            Settable,
            candidate => string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            // A key left over from another version, or meant for HexWin.
            return;
        }

        try
        {
            target.SetValue(settings, property.Value.Deserialize(target.PropertyType, JsonOptions));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // This value alone is unreadable; its default stands.
        }
    }

    /// <summary>
    /// The silence that actually closes a segment: zero when segmentation is
    /// off, whatever the pause says.
    /// </summary>
    public TimeSpan SegmentPause() =>
        Segmentation ? TimeSpan.FromMilliseconds(PauseMilliseconds) : TimeSpan.Zero;

    // --- Validation -----------------------------------------------------------

    /// <summary>
    /// Repairs any out-of-range value in place. Called after every read.
    /// </summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(ModelPath))
        {
            ModelPath = DefaultModelPath;
        }

        Hotkey = NormalizeHotkey(Hotkey);
        Provider = NormalizeProvider(Provider);

        MinRecordingMilliseconds = Math.Clamp(MinRecordingMilliseconds, 0, MinRecordingMillisecondsCeiling);
        MaxRecordingSeconds = Math.Clamp(MaxRecordingSeconds, MaxRecordingSecondsFloor, MaxRecordingSecondsCeiling);

        // Zero stays allowed: that is how the cutting is turned off.
        PauseMilliseconds = Math.Clamp(PauseMilliseconds, 0, MaxPauseMilliseconds);
        Threads = Math.Clamp(Threads, 1, MaxThreads);

        // Zero stays allowed: that is how the model is kept resident.
        UnloadAfterMinutes = Math.Clamp(UnloadAfterMinutes, 0, MaxUnloadAfterMinutes);

        // A number is accepted by the serializer for an enum, whatever its
        // value: 42 has to be caught here.
        if (!Enum.IsDefined(Insertion))
        {
            Insertion = InsertionMode.Paste;
        }

        if (!Enum.IsDefined(PasteShortcut))
        {
            PasteShortcut = PasteShortcut.CtrlV;
        }

        if (!Enum.IsDefined(KeySender))
        {
            KeySender = KeySender.Auto;
        }

        if (!Enum.IsDefined(Feedback))
        {
            Feedback = FeedbackMode.Sound;
        }
    }

    private static string NormalizeProvider(string? provider)
    {
        string? match = KnownProviders.FirstOrDefault(
            known => string.Equals(known, provider?.Trim(), StringComparison.OrdinalIgnoreCase));

        // The processor is always available: it is the only fallback that
        // cannot leave the application with no way to transcribe.
        return match ?? DefaultProvider;
    }

    private static string[] NormalizeHotkey(string[]? hotkey)
    {
        if (hotkey is null || hotkey.Length == 0)
        {
            return [.. DefaultHotkey];
        }

        string?[] canonical = [.. hotkey.Select(CanonicalHotkeyName)];

        // An unknown key invalidates the whole shortcut; it is never simply
        // dropped. Dropping a key WIDENS the combination instead of narrowing
        // it: ["Ctrl", "Fn"] would become ["Ctrl"], and dictation would fire on
        // every press of Ctrl. Falling back to the known default beats
        // producing a shortcut more permissive than intended.
        if (canonical.Any(name => name is null))
        {
            return [.. DefaultHotkey];
        }

        return [.. canonical.OfType<string>().Distinct(StringComparer.Ordinal)];
    }

    private static string? CanonicalHotkeyName(string? name)
    {
        string? trimmed = name?.Trim();

        if (trimmed is not null && HotkeyAliases.TryGetValue(trimmed, out string? alias))
        {
            trimmed = alias;
        }

        return KnownHotkeyNames.FirstOrDefault(
            known => string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
