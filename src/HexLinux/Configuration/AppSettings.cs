using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
///
/// <para>ydotool is deliberately absent. Ubuntu 24.04 still ships its 0.1
/// series, whose command line differs from the 1.x one found elsewhere, and
/// both need a daemon of their own on /dev/uinput — ground the built-in
/// <see cref="Uinput"/> sender already covers without either.</para>
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
}

/// <summary>
/// User configuration, read from <c>~/.config/hexlinux/settings.json</c>.
///
/// All the logic in this class is pure: <see cref="Parse(string)"/> and
/// <see cref="Normalize()"/> never touch the disk, which makes them entirely
/// testable. Only <see cref="Load"/> and the rewriting methods do any I/O.
///
/// The principle is that a damaged file must never stop the application from
/// starting: any invalid value is replaced by its default rather than raising
/// an exception.
/// </summary>
public sealed class AppSettings
{
    public const string FileName = "settings.json";

    /// <summary>
    /// Folder of the Parakeet model. A relative path is looked for in the XDG
    /// data folder, then next to the executable; a leading <c>~/</c> stands
    /// for the home folder. A folder and not a file: the model is made of an
    /// encoder, a decoder, a joiner and a vocabulary.
    /// </summary>
    public string ModelPath { get; set; } = DefaultModelPath;

    /// <summary>Keys to hold down to dictate.</summary>
    public string[] Hotkey { get; set; } = [.. DefaultHotkey];

    /// <summary>
    /// Below this, the press counts as accidental and is ignored. It is also
    /// how long a press is held before a dictation is confirmed: a Right Ctrl
    /// released or combined with another key sooner is an ordinary shortcut,
    /// not a dictation, and passes without a tone.
    /// </summary>
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

    /// <summary>
    /// Puts the space French typography wants before ? ! ; : and ». On by
    /// default, as in HexWin; off leaves the punctuation as the engine wrote
    /// it, which is what English expects. A setting rather than a rule because
    /// the engine does not reliably report which language it recognised.
    /// </summary>
    public bool FrenchSpacing { get; set; } = true;

    public InsertionMode Insertion { get; set; } = InsertionMode.Paste;

    /// <summary>The keystroke sent after filling the clipboard, in Paste mode.</summary>
    public PasteShortcut PasteShortcut { get; set; } = PasteShortcut.CtrlV;

    /// <summary>The tool that delivers keystrokes; see <see cref="Configuration.KeySender"/>.</summary>
    public KeySender KeySender { get; set; } = KeySender.Auto;

    /// <summary>
    /// When nothing can send keystrokes — GNOME under Wayland without the udev
    /// rule, typically — leave the dictation in the clipboard and say so,
    /// rather than insert nothing. Off by default, deliberately: the text then
    /// stays in the clipboard, where a clipboard history may keep it long after
    /// it was pasted, which is exactly what HexWin never allows.
    /// </summary>
    public bool ClipboardFallback { get; set; }

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
    /// Keys allowed in a shortcut: modifiers, and function keys no ordinary
    /// keyboard carries. Fn is absent because it is handled by the keyboard's
    /// own controller and emits no code the kernel can see.
    ///
    /// <para><b>Space and Caps Lock, accepted by HexWin, are refused
    /// here.</b> Windows withholds the keys of the shortcut from the
    /// application; Linux cannot (see <c>ChordDetector</c>). Held as a
    /// shortcut, Space would type a space and then repeat it for the whole
    /// dictation, and Caps Lock would toggle capitals every time.</para>
    /// </summary>
    private static readonly string[] KnownHotkeyNames =
    [
        "Ctrl", "LeftCtrl", "RightCtrl",
        "Alt", "LeftAlt", "RightAlt",
        "Shift", "LeftShift", "RightShift",
        "Super", "LeftSuper", "RightSuper",
        "F13", "F14", "F15", "F16", "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24",
    ];

    /// <summary>Names HexWin accepts and HexLinux refuses on purpose, with the reason given back.</summary>
    private static readonly Dictionary<string, string> RefusedHotkeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = "it would type spaces while held: HexLinux cannot withhold keys from the desktop",
        ["CapsLock"] = "it would toggle capitals at every press: HexLinux cannot withhold keys from the desktop",
    };

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

    /// <summary>
    /// HexWin's feedback modes that draw its on-screen circle, read as the
    /// part HexLinux can honour. "Both" keeps the tones it asked for; "Visual"
    /// asked for no sound at all, and must not start beeping because the
    /// circle does not exist here.
    /// </summary>
    private static readonly Dictionary<string, FeedbackMode> FeedbackAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Both"] = FeedbackMode.Sound,
        ["Visual"] = FeedbackMode.None,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly PropertyInfo[] Settable =
        [.. typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite)];

    /// <summary>The JSON names of every setting, as settings.json spells them.</summary>
    public static IReadOnlyList<string> SettingNames { get; } =
        [.. Settable.Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))];

    // --- Reading --------------------------------------------------------------

    /// <summary>
    /// Reads the file if it exists, otherwise returns the defaults. An
    /// unreadable or malformed file also yields the defaults: dictation must
    /// keep working even when the configuration is broken.
    /// </summary>
    public static AppSettings Load(string path) => Load(path, out _);

    /// <summary>
    /// Same, and says what was set aside: every value that could not be used
    /// as written, for the log and for <c>--doctor</c>.
    /// </summary>
    public static AppSettings Load(string path, out IReadOnlyList<string> notes)
    {
        if (!File.Exists(path))
        {
            notes = [];
            return new AppSettings();
        }

        try
        {
            return Parse(File.ReadAllText(path), out notes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes = [$"{path} could not be read ({ex.Message}): every setting is at its default"];
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
    /// included. That is what HexWin does. Each value here is read on its own,
    /// so a typo costs only the setting it sits in.</para>
    /// </summary>
    public static AppSettings Parse(string json) => Parse(json, out _);

    /// <summary>
    /// Same, and lists, in plain words, every value that was set aside and
    /// why. A setting that silently falls back to its default is the hardest
    /// kind of fault to find; the list is what the log and
    /// <c>--doctor</c> show.
    /// </summary>
    public static AppSettings Parse(string json, out IReadOnlyList<string> notes)
    {
        var settings = new AppSettings();
        List<string> found = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, DocumentOptions);

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    Apply(settings, property, found);
                }
            }
            else
            {
                found.Add("settings.json does not hold a JSON object: every setting is at its default");
            }
        }
        catch (JsonException ex)
        {
            // Not JSON at all: the defaults stand.
            found.Add($"settings.json is not valid JSON ({ex.Message}): every setting is at its default");
        }

        settings.Normalize(found);
        notes = found;
        return settings;
    }

    private static void Apply(AppSettings settings, JsonProperty property, List<string> notes)
    {
        PropertyInfo? target = Array.Find(
            Settable,
            candidate => string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            // A key left over from another version, or meant for HexWin.
            notes.Add($"\"{property.Name}\" is not a HexLinux setting: ignored");
            return;
        }

        if (target.PropertyType == typeof(FeedbackMode)
            && property.Value.ValueKind == JsonValueKind.String
            && FeedbackAliases.TryGetValue(property.Value.GetString()!.Trim(), out FeedbackMode alias))
        {
            settings.Feedback = alias;
            notes.Add($"\"feedback\": {property.Value.GetRawText()} is a HexWin mode: read as \"{alias}\"");
            return;
        }

        try
        {
            target.SetValue(settings, property.Value.Deserialize(target.PropertyType, JsonOptions));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // This value alone is unreadable; its default stands.
            notes.Add($"\"{property.Name}\": {Shorten(property.Value.GetRawText())} is not a valid value: default used");
        }
    }

    private static string Shorten(string raw) => raw.Length <= 40 ? raw : raw[..37] + "...";

    /// <summary>
    /// The whole configuration as JSON, without comments. Only used to write
    /// a file from scratch, when there is no commented one to copy: an
    /// existing file is always rewritten line by line instead (see
    /// <see cref="RewriteValues"/>).
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// The silence that actually closes a segment: zero when segmentation is
    /// off, whatever the pause says. A method rather than a property, so the
    /// serializer does not write it to the file as a setting of its own.
    /// </summary>
    public TimeSpan SegmentPause() =>
        Segmentation ? TimeSpan.FromMilliseconds(PauseMilliseconds) : TimeSpan.Zero;

    // --- Writing back -----------------------------------------------------------

    /// <summary>
    /// Rewrites one setting in the file, leaving every other line as it was
    /// (lines are written back with LF endings, the Linux convention, and
    /// with a final line break).
    ///
    /// <para>Serialising the whole object would be shorter and wrong: the file
    /// is full of comments explaining what each value does, and
    /// <see cref="JsonSerializer"/> cannot write them back. A user who ticked a
    /// menu entry would silently lose the documentation they rely on. So the
    /// one line is replaced and nothing else is touched.</para>
    ///
    /// <para>Returns false when the file cannot be read or written — it may be
    /// open in an editor, or sit in a read-only folder. The caller has already
    /// applied the change in memory; failing to persist it is worth a log line,
    /// never an interruption.</para>
    /// </summary>
    public static bool TryRewriteValue(string path, string key, string jsonValue) =>
        RewriteValues(path, [new(key, jsonValue)], appendMissing: false).Count == 0;

    /// <summary>
    /// Rewrites several settings in one pass, under the same rule as
    /// <see cref="TryRewriteValue"/>: each value replaces its own line, and
    /// every other line — comments above all — stays as it was.
    ///
    /// <para>With <paramref name="appendMissing"/>, a key the file does not
    /// carry yet is added just before the closing brace. A settings.json kept
    /// from an older version lacks the settings introduced since. The addition
    /// is refused rather than risked when the line before the brace ends in a
    /// comment: the comma it needs would land inside the comment, and the file
    /// would no longer parse.</para>
    ///
    /// <para>A key the file does carry, on a line that cannot be rewritten
    /// (see <see cref="ReplaceLine"/>), is reported and <b>never appended a
    /// second time</b>: the copy at the bottom would win when the file is
    /// read, silently overriding the line the user reads and edits, and the
    /// file would grow by one line at every tick of the menu.</para>
    ///
    /// <para>The file is written once, or not at all. Returns the keys that
    /// could not be persisted — every key, when the file cannot be read or
    /// written.</para>
    /// </summary>
    public static IReadOnlyList<string> RewriteValues(
        string path,
        IReadOnlyList<KeyValuePair<string, string>> values,
        bool appendMissing)
    {
        ArgumentNullException.ThrowIfNull(values);

        string[] allKeys = [.. values.Select(value => value.Key)];

        try
        {
            if (!File.Exists(path))
            {
                return allKeys;
            }

            List<string> lines = [.. File.ReadAllLines(path)];
            List<KeyValuePair<string, string>> absent = [];
            List<string> refused = [];

            foreach (KeyValuePair<string, string> value in values)
            {
                switch (ReplaceLine(lines, value.Key, value.Value))
                {
                    case LineRewrite.Absent:
                        absent.Add(value);
                        break;

                    case LineRewrite.Refused:
                        refused.Add(value.Key);
                        break;

                    default:
                        break;
                }
            }

            if (appendMissing && absent.Count > 0 && TryAppend(lines, absent))
            {
                absent.Clear();
            }

            if (refused.Count + absent.Count < values.Count)
            {
                File.WriteAllLines(path, lines);
            }

            HashSet<string> failed = [.. refused, .. absent.Select(value => value.Key)];
            return [.. allKeys.Where(failed.Contains)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return allKeys;
        }
    }

    /// <summary>What became of the line of one key.</summary>
    private enum LineRewrite
    {
        /// <summary>The value was replaced.</summary>
        Replaced,

        /// <summary>No line holds the key: the file predates the setting.</summary>
        Absent,

        /// <summary>A line holds the key, but cannot be rewritten safely.</summary>
        Refused,
    }

    /// <summary>
    /// Replaces the value on the line that holds <paramref name="key"/>.
    ///
    /// <para><b>A line that carries a comment after its value is left
    /// alone</b>, and the key reported as not persisted. The value and the
    /// comment cannot be told apart without parsing the JSON: rewriting
    /// blindly would drop the comma with the comment, and a single missing
    /// comma makes the whole file unreadable — every setting back to its
    /// default at the next start, for the sake of one tick in a menu.</para>
    ///
    /// <para>So is a line whose value does not stand whole on it: a value
    /// continued on the next lines, as a formatter lays out a list, or a
    /// second setting sharing the line. Replacing the line would leave the
    /// rest of the old value dangling below it, or erase the other
    /// setting.</para>
    /// </summary>
    private static LineRewrite ReplaceLine(List<string> lines, string key, string jsonValue)
    {
        var pattern = new Regex($@"^(\s*""{Regex.Escape(key)}""\s*:\s*)(.*?)(,?)\s*$");

        for (int i = 0; i < lines.Count; i++)
        {
            Match match = pattern.Match(lines[i]);

            if (!match.Success)
            {
                continue;
            }

            string current = match.Groups[2].Value;

            if (current.Contains("//", StringComparison.Ordinal)
                || current.Contains("/*", StringComparison.Ordinal)
                || !IsWholeValue(current))
            {
                return LineRewrite.Refused;
            }

            lines[i] = match.Groups[1].Value + jsonValue + match.Groups[3].Value;
            return LineRewrite.Replaced;
        }

        return LineRewrite.Absent;
    }

    /// <summary>True when <paramref name="json"/> is exactly one complete JSON value.</summary>
    private static bool IsWholeValue(string json)
    {
        try
        {
            JsonDocument.Parse(json).Dispose();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryAppend(List<string> lines, List<KeyValuePair<string, string>> values)
    {
        int closing = lines.FindLastIndex(line => line.Trim() == "}");

        if (closing < 0)
        {
            return false;
        }

        int previous = closing - 1;

        while (previous >= 0 && (lines[previous].Trim().Length == 0 || lines[previous].TrimStart().StartsWith("//", StringComparison.Ordinal)))
        {
            previous--;
        }

        if (previous < 0 || lines[previous].Contains("//", StringComparison.Ordinal) || lines[previous].Contains("/*", StringComparison.Ordinal))
        {
            return false;
        }

        string last = lines[previous].TrimEnd();

        if (!last.EndsWith(',') && !last.EndsWith('{'))
        {
            lines[previous] = last + ",";
        }

        string indent = last.EndsWith('{')
            ? "  "
            : lines[previous][..(lines[previous].Length - lines[previous].TrimStart().Length)];

        lines.InsertRange(closing, values.Select((value, index) =>
            $"{indent}\"{value.Key}\": {value.Value}{(index < values.Count - 1 ? "," : "")}"));

        return true;
    }

    // --- Validation -----------------------------------------------------------

    /// <summary>
    /// Repairs any out-of-range value in place. Called after every read, and
    /// again after a command-line option overrides a setting.
    /// </summary>
    public void Normalize() => Normalize(null);

    private void Normalize(List<string>? notes)
    {
        if (string.IsNullOrWhiteSpace(ModelPath))
        {
            notes?.Add("\"modelPath\" is empty: the default model is used");
            ModelPath = DefaultModelPath;
        }

        Hotkey = NormalizeHotkey(Hotkey, notes);
        Provider = NormalizeProvider(Provider, notes);

        MinRecordingMilliseconds = Clamp("minRecordingMilliseconds", MinRecordingMilliseconds, 0, MinRecordingMillisecondsCeiling, notes);
        MaxRecordingSeconds = Clamp("maxRecordingSeconds", MaxRecordingSeconds, MaxRecordingSecondsFloor, MaxRecordingSecondsCeiling, notes);

        // Zero stays allowed: that is how the cutting is turned off.
        PauseMilliseconds = Clamp("pauseMilliseconds", PauseMilliseconds, 0, MaxPauseMilliseconds, notes);
        Threads = Clamp("threads", Threads, 1, MaxThreads, notes);

        // Zero stays allowed: that is how the model is kept resident.
        UnloadAfterMinutes = Clamp("unloadAfterMinutes", UnloadAfterMinutes, 0, MaxUnloadAfterMinutes, notes);

        // A number is accepted by the serializer for an enum, whatever its
        // value: 42 has to be caught here.
        Insertion = Defined("insertion", Insertion, InsertionMode.Paste, notes);
        PasteShortcut = Defined("pasteShortcut", PasteShortcut, PasteShortcut.CtrlV, notes);
        KeySender = Defined("keySender", KeySender, KeySender.Auto, notes);
        Feedback = Defined("feedback", Feedback, FeedbackMode.Sound, notes);
    }

    private static int Clamp(string name, int value, int min, int max, List<string>? notes)
    {
        int clamped = Math.Clamp(value, min, max);

        if (clamped != value)
        {
            notes?.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"\"{name}\": {value} is outside {min} to {max}: {clamped} used"));
        }

        return clamped;
    }

    private static T Defined<T>(string name, T value, T fallback, List<string>? notes)
        where T : struct, Enum
    {
        if (Enum.IsDefined(value))
        {
            return value;
        }

        notes?.Add($"\"{name}\": {Convert.ToInt32(value, CultureInfo.InvariantCulture)} is not a valid value: \"{fallback}\" used");
        return fallback;
    }

    private static string NormalizeProvider(string? provider, List<string>? notes)
    {
        string? match = KnownProviders.FirstOrDefault(
            known => string.Equals(known, provider?.Trim(), StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            notes?.Add($"\"provider\": \"{provider}\" is not available: \"{DefaultProvider}\" used");
        }

        // The processor is always available: it is the only fallback that
        // cannot leave the application with no way to transcribe.
        return match ?? DefaultProvider;
    }

    private static string[] NormalizeHotkey(string[]? hotkey, List<string>? notes)
    {
        if (hotkey is null || hotkey.Length == 0)
        {
            notes?.Add("\"hotkey\" is empty: the default shortcut is used");
            return [.. DefaultHotkey];
        }

        string?[] canonical = [.. hotkey.Select(CanonicalHotkeyName)];

        // An unknown key invalidates the whole shortcut; it is never simply
        // dropped. Dropping a key WIDENS the combination instead of narrowing
        // it: ["Ctrl", "Fn"] would become ["Ctrl"], and dictation would fire on
        // every press of Ctrl. Falling back to the known default beats
        // producing a shortcut more permissive than intended.
        int rejected = Array.FindIndex(canonical, name => name is null);

        if (rejected >= 0)
        {
            string name = hotkey[rejected]?.Trim() ?? "null";
            string reason = RefusedHotkeyNames.TryGetValue(name, out string? why) ? why : "not a key a shortcut can use";

            notes?.Add($"\"hotkey\": \"{name}\" is refused ({reason}): the default shortcut is used");
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
