using System.Text.Json;
using HexLinux.Configuration;
using Xunit;

namespace HexLinux.Tests.Configuration;

/// <summary>
/// The rule checked end to end here: no input, however damaged, may stop the
/// application from starting with a usable configuration — and, unlike
/// HexWin, one bad value costs only its own setting, and says so.
///
/// The second half checks the other promise the file makes: when the tray
/// writes a choice back, only that one line changes. settings.json is the
/// documentation of every value as much as the configuration; a rewrite that
/// dropped its comments, or left it unparseable, would cost the user either
/// the explanations or every setting at the next start.
/// </summary>
public class AppSettingsTests : IDisposable
{
    private const string DefaultModel = "models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";

    // A private file for the tests that read or rewrite one, deleted afterwards.
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hexlinux-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static string Defaults => new AppSettings().ToJson();

    /// <summary>The value of one setting, by its settings.json name, as JSON.</summary>
    private static JsonElement ValueOf(AppSettings settings, string key)
    {
        using JsonDocument document = JsonDocument.Parse(settings.ToJson());
        return document.RootElement.GetProperty(key).Clone();
    }

    [Fact]
    public void An_empty_json_gives_the_default_values()
    {
        // The defaults every other test relies on, and that settings.json
        // documents.
        AppSettings settings = AppSettings.Parse("{}");

        Assert.Equal(["RightCtrl"], settings.Hotkey);
        Assert.Equal(250, settings.MinRecordingMilliseconds);
        Assert.Equal(120, settings.MaxRecordingSeconds);
        Assert.False(settings.Segmentation);
        Assert.Equal(700, settings.PauseMilliseconds);
        Assert.Equal("cpu", settings.Provider);
        Assert.Equal(4, settings.Threads);
        Assert.Equal(5, settings.UnloadAfterMinutes);
        Assert.True(settings.FrenchSpacing);
        Assert.Equal(InsertionMode.Paste, settings.Insertion);
        Assert.Equal(PasteShortcut.CtrlV, settings.PasteShortcut);
        Assert.Equal(KeySender.Auto, settings.KeySender);
        Assert.False(settings.ClipboardFallback);
        Assert.Equal(FeedbackMode.Sound, settings.Feedback);
        Assert.Equal(DefaultModel, settings.ModelPath);
        Assert.True(settings.LogEnabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{this is not valid}")]
    [InlineData("[1, 2, 3]")]
    public void An_unreadable_json_gives_the_default_values(string json)
    {
        AppSettings settings = AppSettings.Parse(json);

        Assert.Equal("cpu", settings.Provider);
        Assert.Equal(["RightCtrl"], settings.Hotkey);
    }

    [Fact]
    public void Unknown_keys_are_ignored()
    {
        // A key left over from an earlier version, or from HexWin's file,
        // must not fail everything: HexWin's circle settings are the real case.
        AppSettings settings = AppSettings.Parse(
            """{"provider": "cpu", "language": "fr", "feedbackColor": "auto", "feedbackSize": 64}""");

        Assert.Equal("cpu", settings.Provider);
    }

    [Fact]
    public void A_setting_written_in_another_case_is_still_read()
    {
        // A hand-edited file that capitalises a key, as the C# property or an
        // old README spelled it, must not lose that setting.
        AppSettings settings = AppSettings.Parse("""{"Threads": 8, "HOTKEY": ["F13"]}""", out IReadOnlyList<string> notes);

        Assert.Equal(8, settings.Threads);
        Assert.Equal(["F13"], settings.Hotkey);
        Assert.Empty(notes);
    }

    [Fact]
    public void Every_setting_the_code_reads_has_a_settings_json_name()
    {
        // The names the tray rewrites and --doctor quotes: they must be the
        // camelCase spellings the file uses, one per setting.
        string[] expected =
        [
            "modelPath", "hotkey", "minRecordingMilliseconds", "maxRecordingSeconds", "segmentation",
            "pauseMilliseconds", "provider", "threads", "unloadAfterMinutes", "frenchSpacing", "insertion",
            "pasteShortcut", "keySender", "clipboardFallback", "feedback", "logEnabled",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), AppSettings.SettingNames.Order(StringComparer.Ordinal));
    }

    // --- One bad value costs only itself ------------------------------------------

    [Fact]
    public void One_bad_value_costs_only_its_own_setting()
    {
        // The HexWin bug this reading fixes: there, the unknown "Circle" or
        // the string "four" made the whole file fall back to the defaults,
        // hotkey included, with nothing to say why.
        AppSettings settings = AppSettings.Parse(
            """
            {
              "feedback": "Circle",
              "hotkey": ["RightAlt"],
              "threads": "four",
              "insertion": "Type",
              "pauseMilliseconds": 900
            }
            """);

        Assert.Equal(FeedbackMode.Sound, settings.Feedback);
        Assert.Equal(["RightAlt"], settings.Hotkey);
        Assert.Equal(4, settings.Threads);
        Assert.Equal(InsertionMode.Type, settings.Insertion);
        Assert.Equal(900, settings.PauseMilliseconds);
    }

    [Fact]
    public void A_file_mixing_good_and_bad_values_keeps_every_good_one_and_reports_every_bad_one()
    {
        // A file edited by hand over months: half the values fine, the rest
        // typos, stale names and wrong types. Each good value must survive,
        // each bad one fall back alone, and --doctor list exactly those.
        AppSettings settings = AppSettings.Parse(
            """
            {
              "modelPath": "~/models/parakeet",
              "hotkey": ["RightAlt", "Space"],
              "minRecordingMilliseconds": "250ms",
              "maxRecordingSeconds": 60,
              "segmentation": "yes",
              "pauseMilliseconds": 900,
              "unloadAfterMinutes": -3,
              "provider": "cuda",
              "threads": 8,
              "frenchSpacing": false,
              "insertion": "Typing",
              "pasteShortcut": "CtrlShiftV",
              "keySender": "Ydotool",
              "clipboardFallback": null,
              "feedback": "None",
              "logEnabled": false
            }
            """,
            out IReadOnlyList<string> notes);

        // Kept as written.
        Assert.Equal("~/models/parakeet", settings.ModelPath);
        Assert.Equal(60, settings.MaxRecordingSeconds);
        Assert.Equal(900, settings.PauseMilliseconds);
        Assert.Equal(8, settings.Threads);
        Assert.False(settings.FrenchSpacing);
        Assert.Equal(PasteShortcut.CtrlShiftV, settings.PasteShortcut);
        Assert.Equal(FeedbackMode.None, settings.Feedback);
        Assert.False(settings.LogEnabled);

        // Each back to its own default, or its nearest bound.
        Assert.Equal(["RightCtrl"], settings.Hotkey);
        Assert.Equal(250, settings.MinRecordingMilliseconds);
        Assert.False(settings.Segmentation);
        Assert.Equal(0, settings.UnloadAfterMinutes);
        Assert.Equal("cpu", settings.Provider);
        Assert.Equal(InsertionMode.Paste, settings.Insertion);
        Assert.Equal(KeySender.Auto, settings.KeySender);
        Assert.False(settings.ClipboardFallback);

        // One note per setting set aside, and none for the others.
        string[] rejected =
        [
            "hotkey", "minRecordingMilliseconds", "segmentation", "unloadAfterMinutes",
            "provider", "insertion", "keySender", "clipboardFallback",
        ];

        Assert.Equal(rejected.Length, notes.Count);

        foreach (string key in rejected)
        {
            Assert.Single(notes, note => note.StartsWith($"\"{key}\"", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("minRecordingMilliseconds", "\"250\"")]
    [InlineData("maxRecordingSeconds", "12.5")]
    [InlineData("pauseMilliseconds", "true")]
    [InlineData("threads", "null")]
    [InlineData("threads", "[8]")]
    [InlineData("unloadAfterMinutes", "99999999999")]
    public void A_whole_number_setting_written_wrongly_keeps_its_default_and_says_so(string key, string value)
    {
        // "250" in quotes, a decimal, a number too large for the setting: the
        // usual slips when typing a duration by hand.
        AppSettings settings = AppSettings.Parse($$"""{"{{key}}": {{value}}}""", out IReadOnlyList<string> notes);

        Assert.Equal(Defaults, settings.ToJson());
        Assert.Equal($"\"{key}\": {value} is not a valid value: default used", Assert.Single(notes));
    }

    [Theory]
    [InlineData("segmentation", "\"true\"")]
    [InlineData("frenchSpacing", "1")]
    [InlineData("clipboardFallback", "\"yes\"")]
    [InlineData("logEnabled", "null")]
    public void A_switch_written_wrongly_keeps_its_default_and_says_so(string key, string value)
    {
        // "yes" or 1 for a switch: a wrong guess must neither turn it on nor
        // cost the other settings. clipboardFallback in particular must stay
        // off, since on leaves dictations in the clipboard.
        AppSettings settings = AppSettings.Parse($$"""{"{{key}}": {{value}}}""", out IReadOnlyList<string> notes);

        Assert.Equal(Defaults, settings.ToJson());
        Assert.Equal($"\"{key}\": {value} is not a valid value: default used", Assert.Single(notes));
    }

    [Theory]
    [InlineData("insertion", "\"Typing\"")]
    [InlineData("pasteShortcut", "\"Ctrl+V\"")]
    [InlineData("keySender", "\"Ydotool\"")]
    [InlineData("feedback", "\"Circle\"")]
    [InlineData("insertion", "null")]
    [InlineData("keySender", "true")]
    public void A_choice_written_wrongly_keeps_its_default_and_says_so(string key, string value)
    {
        // A name typed from memory, or one another version offered: ydotool
        // is not supported, and HexWin's circle does not exist here.
        AppSettings settings = AppSettings.Parse($$"""{"{{key}}": {{value}}}""", out IReadOnlyList<string> notes);

        Assert.Equal(Defaults, settings.ToJson());
        Assert.Equal($"\"{key}\": {value} is not a valid value: default used", Assert.Single(notes));
    }

    [Theory]
    [InlineData("modelPath", "42")]
    [InlineData("modelPath", "[\"models/m\"]")]
    [InlineData("provider", "true")]
    [InlineData("provider", "{\"name\": \"cpu\"}")]
    public void A_text_setting_written_wrongly_keeps_its_default_and_says_so(string key, string value)
    {
        AppSettings settings = AppSettings.Parse($$"""{"{{key}}": {{value}}}""", out IReadOnlyList<string> notes);

        Assert.Equal(Defaults, settings.ToJson());
        Assert.Equal($"\"{key}\": {value} is not a valid value: default used", Assert.Single(notes));
    }

    [Theory]
    [InlineData("\"RightCtrl\"")]
    [InlineData("[183]")]
    [InlineData("{\"key\": \"F13\"}")]
    public void A_shortcut_that_is_not_a_list_of_names_keeps_the_default_and_says_so(string value)
    {
        // A single name without the brackets, or the key code evtest showed
        // (183 is F13): the shortcut falls back, the rest of the file stands.
        AppSettings settings = AppSettings.Parse($$"""{"hotkey": {{value}}, "threads": 8}""", out IReadOnlyList<string> notes);

        Assert.Equal(["RightCtrl"], settings.Hotkey);
        Assert.Equal(8, settings.Threads);
        Assert.Equal($"\"hotkey\": {value} is not a valid value: default used", Assert.Single(notes));
    }

    [Fact]
    public void A_long_bad_value_is_shortened_in_its_note()
    {
        // A paragraph pasted into the wrong place must not flood the log and
        // the --doctor output: 37 characters and an ellipsis say enough.
        string pasted = new('x', 100);

        AppSettings.Parse($$"""{"threads": "{{pasted}}"}""", out IReadOnlyList<string> notes);

        string note = Assert.Single(notes);
        Assert.Equal($"\"threads\": \"{new string('x', 36)}... is not a valid value: default used", note);
    }

    [Fact]
    public void A_number_where_an_enum_belongs_falls_back_to_its_default()
    {
        // The serializer accepts any number for an enum: 42 has to be caught
        // after the reading.
        Assert.Equal(InsertionMode.Paste, AppSettings.Parse("""{"insertion": 42}""").Insertion);
    }

    [Theory]
    [InlineData("insertion", "42", "Paste")]
    [InlineData("pasteShortcut", "-1", "CtrlV")]
    [InlineData("keySender", "4", "Auto")]
    [InlineData("feedback", "2", "Sound")]
    public void A_number_naming_no_choice_is_reported_with_the_choice_used(string key, string value, string fallback)
    {
        // What the serializer lets through has to be caught, and reported,
        // after the reading: an unknown number would otherwise reach the
        // planner as a mode nobody handles.
        AppSettings settings = AppSettings.Parse($$"""{"{{key}}": {{value}}}""", out IReadOnlyList<string> notes);

        Assert.Equal(Defaults, settings.ToJson());
        Assert.Equal($"\"{key}\": {value} is not a valid value: \"{fallback}\" used", Assert.Single(notes));
    }

    [Fact]
    public void Every_value_set_aside_is_reported_in_words()
    {
        // What --doctor shows: a setting silently back to its default is the
        // hardest kind of fault to find.
        AppSettings.Parse(
            """{"feedback": "Circle", "threads": 1000, "hotkey": ["Space"], "bogus": 1}""",
            out IReadOnlyList<string> notes);

        Assert.Contains(notes, note => note.Contains("\"feedback\"", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("\"threads\": 1000 is outside 1 to 32: 32 used", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("\"Space\" is refused", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("\"bogus\" is not a HexLinux setting", StringComparison.Ordinal));
    }

    [Fact]
    public void A_key_that_is_not_a_setting_is_named_in_its_note()
    {
        // HexWin's circle keys in a copied file: ignored, but said, so that
        // the user knows the size they set there does nothing.
        AppSettings.Parse("""{"feedbackSize": 64}""", out IReadOnlyList<string> notes);

        Assert.Equal("\"feedbackSize\" is not a HexLinux setting: ignored", Assert.Single(notes));
    }

    [Fact]
    public void A_clean_file_reports_nothing()
    {
        AppSettings.Parse("""{"hotkey": ["RightCtrl"], "threads": 4}""", out IReadOnlyList<string> notes);

        Assert.Empty(notes);
    }

    [Fact]
    public void An_unreadable_file_is_reported_once()
    {
        AppSettings.Parse("{nope", out IReadOnlyList<string> notes);

        Assert.Single(notes);
        Assert.Contains("not valid JSON", notes[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"RightCtrl\"")]
    [InlineData("null")]
    public void A_file_that_is_json_but_not_an_object_is_reported(string json)
    {
        // Valid JSON of the wrong shape: a list pasted over the whole file.
        AppSettings settings = AppSettings.Parse(json, out IReadOnlyList<string> notes);

        Assert.Equal(Defaults, settings.ToJson());
        Assert.Equal("settings.json does not hold a JSON object: every setting is at its default", Assert.Single(notes));
    }

    // --- Compute provider -----------------------------------------------------

    [Theory]
    [InlineData("cpu", "cpu")]
    [InlineData("CPU", "cpu")]
    [InlineData("  Cpu  ", "cpu")]
    public void The_processor_is_recognised_whatever_the_case(string written, string expected)
    {
        AppSettings settings = AppSettings.Parse($$"""{"provider": "{{written}}"}""");

        Assert.Equal(expected, settings.Provider);
    }

    [Theory]
    [InlineData("directml")]
    [InlineData("cuda")]
    public void The_GPU_providers_are_refused_as_unavailable(string provider)
    {
        // HexWin removed them after testing: sherpa-onnx accepted them, then
        // fell back to the processor. The NuGet packages are built for the
        // processor alone, on Linux too.
        AppSettings settings = AppSettings.Parse($$"""{"provider": "{{provider}}"}""", out IReadOnlyList<string> notes);

        Assert.Equal("cpu", settings.Provider);
        Assert.Equal($"\"provider\": \"{provider}\" is not available: \"cpu\" used", Assert.Single(notes));
    }

    [Theory]
    [InlineData("vulkan")]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unknown_provider_falls_back_to_the_processor(string provider)
    {
        AppSettings settings = AppSettings.Parse($$"""{"provider": "{{provider}}"}""");

        Assert.Equal("cpu", settings.Provider);
    }

    [Fact]
    public void A_null_provider_falls_back_to_the_processor()
    {
        AppSettings settings = AppSettings.Parse("""{"provider": null}""", out IReadOnlyList<string> notes);

        Assert.Equal("cpu", settings.Provider);
        Assert.Single(notes, note => note.StartsWith("\"provider\"", StringComparison.Ordinal));
    }

    // --- Threads --------------------------------------------------------------

    [Theory]
    [InlineData(-4, 1)]
    [InlineData(0, 1)]
    [InlineData(4, 4)]
    [InlineData(1_000, 32)]
    public void The_thread_count_is_brought_back_within_bounds(int written, int expected)
    {
        // Zero threads would stall decoding; a thousand would saturate the
        // machine while speeding nothing up, the model being small.
        AppSettings settings = AppSettings.Parse($$"""{"threads": {{written}}}""");

        Assert.Equal(expected, settings.Threads);
    }

    // --- Shortcut -------------------------------------------------------------

    [Fact]
    public void The_Fn_key_is_refused_and_falls_back_to_the_default()
    {
        // Fn is handled by the keyboard's own controller: it emits no code the
        // kernel can see. Accepting it would give a shortcut that never fires.
        AppSettings settings = AppSettings.Parse("""{"hotkey": ["Ctrl", "Fn"]}""", out IReadOnlyList<string> notes);

        Assert.Equal(["RightCtrl"], settings.Hotkey);
        Assert.Equal(
            "\"hotkey\": \"Fn\" is refused (not a key a shortcut can use): the default shortcut is used",
            Assert.Single(notes));
    }

    [Fact]
    public void An_unknown_key_invalidates_the_whole_shortcut()
    {
        // Dropping the offending key would widen the combination instead of
        // narrowing it: ["LeftAlt", "Unknown"] would become ["LeftAlt"] and
        // dictation would fire on every Alt.
        AppSettings settings = AppSettings.Parse("""{"hotkey": ["LeftAlt", "Unknown"]}""");

        Assert.Equal(["RightCtrl"], settings.Hotkey);
    }

    [Theory]
    [InlineData("Space")]
    [InlineData("CapsLock")]
    public void Keys_the_desktop_would_still_type_with_are_refused(string key)
    {
        // Accepted by HexWin, which withholds the shortcut's keys. Linux
        // cannot: held, Space would type spaces for the whole dictation and
        // Caps Lock would toggle capitals.
        AppSettings settings = AppSettings.Parse($$"""{"hotkey": ["{{key}}"]}""");

        Assert.Equal(["RightCtrl"], settings.Hotkey);
    }

    [Theory]
    [InlineData("Space", "it would type spaces while held")]
    [InlineData("space", "it would type spaces while held")]
    [InlineData("CapsLock", "it would toggle capitals at every press")]
    public void The_refusal_of_a_HexWin_key_gives_its_reason(string key, string reason)
    {
        // A HexWin user whose Caps Lock shortcut stops working needs to read
        // why in --doctor, not guess: decision P9.
        AppSettings.Parse($$"""{"hotkey": ["{{key}}"]}""", out IReadOnlyList<string> notes);

        string note = Assert.Single(notes);
        Assert.StartsWith($"\"hotkey\": \"{key}\" is refused ({reason}", note, StringComparison.Ordinal);
        Assert.Contains("HexLinux cannot withhold keys from the desktop", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_key_combined_with_a_valid_one_still_costs_the_whole_shortcut()
    {
        // Right Ctrl+Space would still type a space at every dictation.
        AppSettings settings = AppSettings.Parse("""{"hotkey": ["RightCtrl", "Space"]}""");

        Assert.Equal(["RightCtrl"], settings.Hotkey);
    }

    [Theory]
    [InlineData("Ctrl")]
    [InlineData("LeftCtrl")]
    [InlineData("RightCtrl")]
    [InlineData("Alt")]
    [InlineData("LeftAlt")]
    [InlineData("RightAlt")]
    [InlineData("Shift")]
    [InlineData("LeftShift")]
    [InlineData("RightShift")]
    [InlineData("Super")]
    [InlineData("LeftSuper")]
    [InlineData("RightSuper")]
    [InlineData("F13")]
    [InlineData("F14")]
    [InlineData("F15")]
    [InlineData("F16")]
    [InlineData("F17")]
    [InlineData("F18")]
    [InlineData("F19")]
    [InlineData("F20")]
    [InlineData("F21")]
    [InlineData("F22")]
    [InlineData("F23")]
    [InlineData("F24")]
    public void Every_key_name_settings_json_documents_is_accepted(string key)
    {
        // The list in the comments of settings.json is a promise: a name it
        // offers and the code refused would silently bring back Right Ctrl.
        AppSettings settings = AppSettings.Parse($$"""{"hotkey": ["{{key}}"]}""", out IReadOnlyList<string> notes);

        Assert.Equal([key], settings.Hotkey);
        Assert.Empty(notes);
    }

    [Fact]
    public void A_fully_valid_shortcut_is_kept()
    {
        AppSettings settings = AppSettings.Parse("""{"hotkey": ["F13"]}""");

        Assert.Equal(["F13"], settings.Hotkey);
    }

    [Fact]
    public void The_shortcut_is_recognised_whatever_the_case()
    {
        AppSettings settings = AppSettings.Parse("""{"hotkey": ["ctrl", "SUPER"]}""");

        Assert.Equal(["Ctrl", "Super"], settings.Hotkey);
    }

    [Fact]
    public void Spaces_around_a_key_name_are_ignored()
    {
        // " F13" typed with a stray space must not cost the shortcut.
        AppSettings settings = AppSettings.Parse("""{"hotkey": [" F13 ", "RightAlt "]}""");

        Assert.Equal(["F13", "RightAlt"], settings.Hotkey);
    }

    [Fact]
    public void The_shortcut_is_deduplicated()
    {
        AppSettings settings = AppSettings.Parse("""{"hotkey": ["Ctrl", "ctrl", "Super"]}""");

        Assert.Equal(["Ctrl", "Super"], settings.Hotkey);
    }

    [Fact]
    public void HexWin_names_of_the_Super_keys_are_read_as_such()
    {
        // A settings.json carried over from HexWin keeps its shortcut.
        Assert.Equal(["Super"], AppSettings.Parse("""{"hotkey": ["Win"]}""").Hotkey);
        Assert.Equal(["LeftSuper", "RightSuper"], AppSettings.Parse("""{"hotkey": ["LeftWin", "RightWin"]}""").Hotkey);
    }

    [Fact]
    public void A_HexWin_name_and_its_Linux_name_count_as_one_key()
    {
        // ["Win", "Super"] names the same key twice: the chord must not wait
        // for a second Super key that does not exist.
        AppSettings settings = AppSettings.Parse("""{"hotkey": ["win", "Super"]}""", out IReadOnlyList<string> notes);

        Assert.Equal(["Super"], settings.Hotkey);
        Assert.Empty(notes);
    }

    [Theory]
    [InlineData("""{"hotkey": []}""")]
    [InlineData("""{"hotkey": null}""")]
    [InlineData("""{"hotkey": "RightCtrl"}""")]
    public void An_empty_or_malformed_shortcut_falls_back_to_the_default(string json)
    {
        // Without this rule, the daemon would start with no way at all to
        // trigger dictation, in silence.
        AppSettings settings = AppSettings.Parse(json);

        Assert.Equal(["RightCtrl"], settings.Hotkey);
    }

    [Theory]
    [InlineData("""{"hotkey": []}""")]
    [InlineData("""{"hotkey": null}""")]
    public void An_empty_shortcut_is_reported(string json)
    {
        AppSettings.Parse(json, out IReadOnlyList<string> notes);

        Assert.Equal("\"hotkey\" is empty: the default shortcut is used", Assert.Single(notes));
    }

    [Fact]
    public void A_null_inside_the_shortcut_is_refused_by_name()
    {
        // ["RightCtrl", null] left by a broken edit: refused like any unknown
        // key, and named so that the note is readable.
        AppSettings settings = AppSettings.Parse("""{"hotkey": ["RightAlt", null]}""", out IReadOnlyList<string> notes);

        Assert.Equal(["RightCtrl"], settings.Hotkey);
        Assert.Equal(
            "\"hotkey\": \"null\" is refused (not a key a shortcut can use): the default shortcut is used",
            Assert.Single(notes));
    }

    // --- Durations ------------------------------------------------------------

    [Theory]
    [InlineData(-100, 0)]
    [InlineData(0, 0)]
    [InlineData(250, 250)]
    [InlineData(999_999, 5_000)]
    public void The_minimum_duration_is_brought_back_within_bounds(int written, int expected)
    {
        AppSettings settings = AppSettings.Parse($$"""{"minRecordingMilliseconds": {{written}}}""");

        Assert.Equal(expected, settings.MinRecordingMilliseconds);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(120, 120)]
    [InlineData(10_000, 600)]
    public void The_maximum_duration_is_brought_back_within_bounds(int written, int expected)
    {
        AppSettings settings = AppSettings.Parse($$"""{"maxRecordingSeconds": {{written}}}""");

        Assert.Equal(expected, settings.MaxRecordingSeconds);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(700, 700)]
    [InlineData(60_000, 5_000)]
    public void The_pause_is_brought_back_within_bounds(int written, int expected)
    {
        AppSettings settings = AppSettings.Parse($$"""{"pauseMilliseconds": {{written}}}""");

        Assert.Equal(expected, settings.PauseMilliseconds);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(100_000, 1_440)]
    public void The_unload_delay_is_brought_back_within_bounds(int written, int expected)
    {
        // Zero stays allowed: it keeps the model resident.
        AppSettings settings = AppSettings.Parse($$"""{"unloadAfterMinutes": {{written}}}""");

        Assert.Equal(expected, settings.UnloadAfterMinutes);
    }

    [Theory]
    [InlineData("minRecordingMilliseconds", -100, "\"minRecordingMilliseconds\": -100 is outside 0 to 5000: 0 used")]
    [InlineData("maxRecordingSeconds", 1, "\"maxRecordingSeconds\": 1 is outside 5 to 600: 5 used")]
    [InlineData("pauseMilliseconds", 60_000, "\"pauseMilliseconds\": 60000 is outside 0 to 5000: 5000 used")]
    [InlineData("threads", 0, "\"threads\": 0 is outside 1 to 32: 1 used")]
    [InlineData("unloadAfterMinutes", 100_000, "\"unloadAfterMinutes\": 100000 is outside 0 to 1440: 1440 used")]
    public void A_number_out_of_range_is_reported_with_the_bound_used(string key, int written, string expected)
    {
        // The value is repaired, not discarded: the note says both what was
        // written and what is now in effect.
        AppSettings.Parse($$"""{"{{key}}": {{written}}}""", out IReadOnlyList<string> notes);

        Assert.Equal(expected, Assert.Single(notes));
    }

    [Theory]
    [InlineData("minRecordingMilliseconds", 0)]
    [InlineData("minRecordingMilliseconds", 5_000)]
    [InlineData("maxRecordingSeconds", 5)]
    [InlineData("maxRecordingSeconds", 600)]
    [InlineData("pauseMilliseconds", 0)]
    [InlineData("pauseMilliseconds", 5_000)]
    [InlineData("threads", 1)]
    [InlineData("threads", 32)]
    [InlineData("unloadAfterMinutes", 0)]
    [InlineData("unloadAfterMinutes", 1_440)]
    public void A_number_at_a_bound_is_kept_without_a_note(string key, int written)
    {
        // The bounds are allowed values: a user who set exactly the maximum
        // must not be told it was out of range.
        AppSettings settings = AppSettings.Parse($$"""{"{{key}}": {{written}}}""", out IReadOnlyList<string> notes);

        Assert.Equal(written, ValueOf(settings, key).GetInt32());
        Assert.Empty(notes);
    }

    // --- The settings Linux added -------------------------------------------------

    [Fact]
    public void French_spacing_is_on_by_default_as_in_HexWin()
    {
        Assert.True(AppSettings.Parse("{}").FrenchSpacing);
        Assert.False(AppSettings.Parse("""{"frenchSpacing": false}""").FrenchSpacing);
    }

    [Theory]
    [InlineData("\"no\"")]
    [InlineData("0")]
    [InlineData("null")]
    public void A_bad_french_spacing_value_falls_back_to_on(string value)
    {
        // The default keeps HexWin's behaviour: the safe fallback is the one
        // the user already knows.
        Assert.True(AppSettings.Parse($$"""{"frenchSpacing": {{value}}}""").FrenchSpacing);
    }

    [Fact]
    public void The_clipboard_fallback_is_off_unless_asked_for()
    {
        // Off by default: leaving the dictation in the clipboard is the
        // privacy trade-off HexWin never makes.
        Assert.False(AppSettings.Parse("{}").ClipboardFallback);
        Assert.True(AppSettings.Parse("""{"clipboardFallback": true}""").ClipboardFallback);
        Assert.False(AppSettings.Parse("""{"clipboardFallback": "yes"}""").ClipboardFallback);
    }

    [Theory]
    [InlineData("\"CtrlShiftV\"", PasteShortcut.CtrlShiftV)]
    [InlineData("\"shiftinsert\"", PasteShortcut.ShiftInsert)]
    [InlineData("\"AltV\"", PasteShortcut.CtrlV)]
    [InlineData("7", PasteShortcut.CtrlV)]
    public void The_paste_shortcut_is_read_or_falls_back(string value, PasteShortcut expected)
    {
        Assert.Equal(expected, AppSettings.Parse($$"""{"pasteShortcut": {{value}}}""").PasteShortcut);
    }

    [Theory]
    [InlineData("\"Xdotool\"", KeySender.Xdotool)]
    [InlineData("\"uinput\"", KeySender.Uinput)]
    [InlineData("\"Wtype\"", KeySender.Wtype)]
    [InlineData("\"Auto\"", KeySender.Auto)]
    [InlineData("\"Ydotool\"", KeySender.Auto)]
    [InlineData("\"ydotool\"", KeySender.Auto)]
    [InlineData("9", KeySender.Auto)]
    public void The_key_sender_is_read_or_falls_back(string value, KeySender expected)
    {
        // ydotool is not supported: a file naming it falls back to Auto.
        Assert.Equal(expected, AppSettings.Parse($$"""{"keySender": {{value}}}""").KeySender);
    }

    [Fact]
    public void Ydotool_is_not_a_key_sender_at_all()
    {
        // Ubuntu 24.04 ships ydotool 0.1, whose command line differs from
        // 1.x: the choice was removed rather than half supported. The senders
        // are exactly the four settings.json documents.
        Assert.Equal(["Auto", "Uinput", "Xdotool", "Wtype"], Enum.GetNames<KeySender>());
    }

    [Theory]
    [InlineData("\"Both\"", FeedbackMode.Sound)]
    [InlineData("\"Visual\"", FeedbackMode.None)]
    [InlineData("\"visual\"", FeedbackMode.None)]
    [InlineData("\" BOTH \"", FeedbackMode.Sound)]
    public void HexWin_feedback_modes_keep_what_they_asked_for_that_Linux_can_do(string value, FeedbackMode expected)
    {
        // "Visual" asked for no sound at all: it must not start beeping
        // because the circle does not exist here.
        Assert.Equal(expected, AppSettings.Parse($$"""{"feedback": {{value}}}""").Feedback);
    }

    [Theory]
    [InlineData("Both", "Sound")]
    [InlineData("Visual", "None")]
    public void A_HexWin_feedback_mode_is_reported_as_read_differently(string written, string read)
    {
        // The file keeps saying "Both" while HexLinux plays tones only:
        // --doctor tells the user what it understood.
        AppSettings.Parse($$"""{"feedback": "{{written}}"}""", out IReadOnlyList<string> notes);

        Assert.Equal($"\"feedback\": \"{written}\" is a HexWin mode: read as \"{read}\"", Assert.Single(notes));
    }

    [Theory]
    [InlineData("\"Sound\"", FeedbackMode.Sound)]
    [InlineData("\"none\"", FeedbackMode.None)]
    public void The_Linux_feedback_modes_are_read_without_a_note(string value, FeedbackMode expected)
    {
        AppSettings settings = AppSettings.Parse($$"""{"feedback": {{value}}}""", out IReadOnlyList<string> notes);

        Assert.Equal(expected, settings.Feedback);
        Assert.Empty(notes);
    }

    [Theory]
    [InlineData("\"Circle\"")]
    [InlineData("\"\"")]
    [InlineData("42")]
    public void An_unknown_feedback_mode_falls_back_to_the_default(string value)
    {
        // A setting typed from memory must not cost the user their dictation.
        AppSettings settings = AppSettings.Parse($$"""{"feedback": {{value}}}""");

        Assert.Equal(FeedbackMode.Sound, settings.Feedback);
    }

    // --- Miscellaneous --------------------------------------------------------

    [Theory]
    [InlineData("""{"modelPath": ""}""")]
    [InlineData("""{"modelPath": "   "}""")]
    [InlineData("""{"modelPath": null}""")]
    public void An_empty_model_path_falls_back_to_the_default(string json)
    {
        AppSettings settings = AppSettings.Parse(json, out IReadOnlyList<string> notes);

        Assert.Equal(DefaultModel, settings.ModelPath);
        Assert.Equal("\"modelPath\" is empty: the default model is used", Assert.Single(notes));
    }

    [Theory]
    [InlineData("~/models/parakeet")]
    [InlineData("~")]
    [InlineData("/opt/models/parakeet")]
    public void A_model_path_is_kept_as_written_for_the_locator_to_expand(string written)
    {
        // "~/" is expanded where the home folder is known (ModelLocator), not
        // here: the file keeps what the user wrote, and --doctor shows it.
        AppSettings settings = AppSettings.Parse($$"""{"modelPath": "{{written}}"}""", out IReadOnlyList<string> notes);

        Assert.Equal(written, settings.ModelPath);
        Assert.Empty(notes);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_accepted_in_the_file()
    {
        // settings.json contains some: it is written to be read and edited by
        // somebody who does not program.
        AppSettings settings = AppSettings.Parse(
            """
            {
              // the shortcut
              "hotkey": ["F13"],
            }
            """);

        Assert.Equal(["F13"], settings.Hotkey);
    }

    [Fact]
    public void Comments_of_every_form_and_place_are_skipped_when_reading()
    {
        // A user annotating their own values: after the value, in a block,
        // between two settings. Reading must see through all of them, even
        // though the tray will then refuse to rewrite the annotated lines.
        AppSettings settings = AppSettings.Parse(
            """
            {
              /* chosen after trying Right Alt */
              "hotkey": ["F13"], // the pedal
              "threads": /* half the cores */ 8,
              "feedback": "None",
            }
            """,
            out IReadOnlyList<string> notes);

        Assert.Equal(["F13"], settings.Hotkey);
        Assert.Equal(8, settings.Threads);
        Assert.Equal(FeedbackMode.None, settings.Feedback);
        Assert.Empty(notes);
    }

    [Fact]
    public void A_round_trip_through_json_preserves_the_values()
    {
        var original = new AppSettings
        {
            ModelPath = "models/another-model",
            Hotkey = ["F13"],
            MinRecordingMilliseconds = 400,
            MaxRecordingSeconds = 60,
            Segmentation = true,
            PauseMilliseconds = 900,
            Provider = "cpu",
            Threads = 8,
            UnloadAfterMinutes = 0,
            FrenchSpacing = false,
            Insertion = InsertionMode.Type,
            PasteShortcut = PasteShortcut.ShiftInsert,
            KeySender = KeySender.Wtype,
            ClipboardFallback = true,
            Feedback = FeedbackMode.None,
            LogEnabled = false,
        };

        AppSettings reread = AppSettings.Parse(original.ToJson(), out IReadOnlyList<string> notes);

        Assert.Equal(original.ToJson(), reread.ToJson());
        Assert.Empty(notes);
    }

    [Fact]
    public void A_file_written_from_scratch_carries_every_setting_under_its_file_name()
    {
        // ToJson writes the file when there is no commented one to copy: it
        // must hold every setting, spelled as settings.json spells it, so that
        // the tray can later rewrite any of them in place.
        using JsonDocument document = JsonDocument.Parse(new AppSettings().ToJson());

        string[] keys = [.. document.RootElement.EnumerateObject().Select(property => property.Name)];

        Assert.Equal(AppSettings.SettingNames.Order(StringComparer.Ordinal), keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_enums_are_written_out_in_words()
    {
        // To stay readable in settings.json, rather than an opaque integer.
        string json = new AppSettings { Insertion = InsertionMode.Type }.ToJson();

        Assert.Contains("\"Type\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_effective_pause_is_not_written_as_a_setting()
    {
        // SegmentPause is derived; were it a property, the serializer would
        // add it to every file written from scratch.
        Assert.DoesNotContain("segmentPause", new AppSettings { Segmentation = true }.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Segmentation_is_off_unless_asked_for()
    {
        // Text appearing mid-sentence is a surprise to anyone who did not turn
        // it on; a file without the key must behave that way.
        AppSettings settings = AppSettings.Parse("""{"pauseMilliseconds": 700}""");

        Assert.False(settings.Segmentation);
        Assert.Equal(TimeSpan.Zero, settings.SegmentPause());
    }

    [Fact]
    public void Segmentation_on_cuts_at_the_configured_pause()
    {
        AppSettings settings = AppSettings.Parse("""{"segmentation": true, "pauseMilliseconds": 450}""");

        Assert.Equal(TimeSpan.FromMilliseconds(450), settings.SegmentPause());
    }

    [Fact]
    public void A_pause_of_zero_keeps_the_cutting_off_even_when_asked_for()
    {
        AppSettings settings = AppSettings.Parse("""{"segmentation": true, "pauseMilliseconds": 0}""");

        Assert.Equal(TimeSpan.Zero, settings.SegmentPause());
    }

    [Fact]
    public void A_command_line_override_is_repaired_by_the_same_rules_as_the_file()
    {
        // --transcribe --model "" --provider cuda: the options reach the
        // settings after the file was read, and Normalize must hold them to
        // the same rules, or the command line would bypass every check.
        var settings = new AppSettings
        {
            ModelPath = " ",
            Provider = "cuda",
            Hotkey = ["Space"],
            Threads = 0,
            MaxRecordingSeconds = 100_000,
            Insertion = (InsertionMode)9,
            PasteShortcut = (PasteShortcut)9,
            KeySender = (KeySender)9,
            Feedback = (FeedbackMode)9,
        };

        settings.Normalize();

        Assert.Equal(DefaultModel, settings.ModelPath);
        Assert.Equal("cpu", settings.Provider);
        Assert.Equal(["RightCtrl"], settings.Hotkey);
        Assert.Equal(1, settings.Threads);
        Assert.Equal(600, settings.MaxRecordingSeconds);
        Assert.Equal(InsertionMode.Paste, settings.Insertion);
        Assert.Equal(PasteShortcut.CtrlV, settings.PasteShortcut);
        Assert.Equal(KeySender.Auto, settings.KeySender);
        Assert.Equal(FeedbackMode.Sound, settings.Feedback);
    }

    [Fact]
    public void A_null_shortcut_set_in_code_is_repaired()
    {
        // Hotkey is a settable array: a caller clearing it must not leave the
        // detector with nothing to watch.
        var settings = new AppSettings { Hotkey = null! };

        settings.Normalize();

        Assert.Equal(["RightCtrl"], settings.Hotkey);
    }

    // --- Loading from disk ------------------------------------------------------

    [Fact]
    public void A_missing_file_gives_the_default_values()
    {
        string absent = Path.Combine(Path.GetTempPath(), $"hexlinux-{Guid.NewGuid():N}.json");

        AppSettings settings = AppSettings.Load(absent, out IReadOnlyList<string> notes);

        Assert.Equal("cpu", settings.Provider);
        Assert.Empty(notes);
    }

    [Fact]
    public void A_file_that_is_present_is_read_back_correctly()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hexlinux-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"threads": 8, "hotkey": ["F14"]}""");

        try
        {
            AppSettings settings = AppSettings.Load(path);

            Assert.Equal(8, settings.Threads);
            Assert.Equal(["F14"], settings.Hotkey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Loading_a_file_passes_its_notes_on()
    {
        // The daemon logs them at start and --doctor lists them: Load must
        // hand over what Parse found.
        File.WriteAllText(_path, """{"threads": 1000, "feedback": "Both"}""");

        AppSettings settings = AppSettings.Load(_path, out IReadOnlyList<string> notes);

        Assert.Equal(32, settings.Threads);
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void A_file_that_cannot_be_read_gives_the_defaults_and_says_why()
    {
        // A file another process holds locked (here, this test, through an
        // exclusive lock): dictation must still start, on the defaults, and
        // the log must say the file was not read rather than stay silent.
        File.WriteAllText(_path, """{"threads": 8}""");

        using (new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            AppSettings settings = AppSettings.Load(_path, out IReadOnlyList<string> notes);

            Assert.Equal(Defaults, settings.ToJson());
            string note = Assert.Single(notes);
            Assert.StartsWith(_path, note, StringComparison.Ordinal);
            Assert.EndsWith("every setting is at its default", note, StringComparison.Ordinal);
        }
    }

    // --- Rewriting one value: the tray's switches ---------------------------------

    private void Write(string content) => File.WriteAllText(_path, content);

    private string Read() => File.ReadAllText(_path);

    private static KeyValuePair<string, string>[] Values(params (string Key, string Json)[] values) =>
        [.. values.Select(value => KeyValuePair.Create(value.Key, value.Json))];

    [Fact]
    public void Rewriting_a_value_keeps_the_comments()
    {
        Write("""
            {
              // What tells you that the recording started and stopped.
              "feedback": "Sound",

              // Logs each dictation.
              "logEnabled": true
            }
            """);

        Assert.True(AppSettings.TryRewriteValue(_path, "feedback", "\"None\""));

        string after = Read();
        Assert.Contains("// What tells you that the recording started and stopped.", after, StringComparison.Ordinal);
        Assert.Contains("// Logs each dictation.", after, StringComparison.Ordinal);
        Assert.Contains("\"feedback\": \"None\",", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewriting_a_value_touches_no_other_line()
    {
        // Unticking "Play tones" must change the one feedback line: any other
        // difference would be a setting the user did not touch.
        Write("""
            {
              "hotkey": ["RightAlt"],
              "feedback": "Sound",
              "threads": 8
            }

            """);

        string[] before = File.ReadAllLines(_path);

        Assert.True(AppSettings.TryRewriteValue(_path, "feedback", "\"None\""));

        string[] after = File.ReadAllLines(_path);
        Assert.Equal(before.Length, after.Length);

        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(i == 2 ? "  \"feedback\": \"None\"," : before[i], after[i]);
        }
    }

    [Fact]
    public void The_trailing_comma_is_preserved()
    {
        // Dropping it would leave the file unparseable, and the next start
        // would fall back to the defaults — losing every other setting.
        Write("""
            {
              "feedback": "Sound",
              "logEnabled": true
            }
            """);

        AppSettings.TryRewriteValue(_path, "feedback", "\"None\"");

        Assert.Contains("\"feedback\": \"None\",", Read(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_without_a_trailing_comma_stays_without_one()
    {
        Write("""
            {
              "logEnabled": true,
              "feedback": "Sound"
            }
            """);

        AppSettings.TryRewriteValue(_path, "feedback", "\"None\"");

        string after = Read();
        Assert.Contains("\"feedback\": \"None\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("\"feedback\": \"None\",", after, StringComparison.Ordinal);
    }

    [Fact]
    public void The_spacing_around_the_colon_is_kept()
    {
        // The user's own layout is theirs: only the value changes.
        Write("""
            {
              "feedback":"Sound",
              "logEnabled"  :  true
            }
            """);

        AppSettings.TryRewriteValue(_path, "feedback", "\"None\"");
        AppSettings.TryRewriteValue(_path, "logEnabled", "false");

        string after = Read();
        Assert.Contains("  \"feedback\":\"None\",", after, StringComparison.Ordinal);
        Assert.Contains("  \"logEnabled\"  :  false", after, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rewritten_file_still_parses_to_the_new_value()
    {
        Write("""
            {
              // A comment the serializer could not have written back.
              "feedback": "Sound",
              "hotkey": ["RightAlt"]
            }
            """);

        AppSettings.TryRewriteValue(_path, "feedback", "\"None\"");

        AppSettings reloaded = AppSettings.Load(_path, out IReadOnlyList<string> notes);
        Assert.Equal(FeedbackMode.None, reloaded.Feedback);
        Assert.Equal(["RightAlt"], reloaded.Hotkey);
        Assert.Empty(notes);
    }

    [Fact]
    public void An_unknown_key_changes_nothing()
    {
        Write("""
            {
              "feedback": "Sound"
            }
            """);

        string before = Read();

        Assert.False(AppSettings.TryRewriteValue(_path, "nosuchkey", "\"x\""));
        Assert.Equal(before, Read());
    }

    [Fact]
    public void A_rewrite_that_changes_nothing_does_not_touch_the_file()
    {
        // The file is written once or not at all. An editor holding
        // settings.json open would otherwise ask to reload a file whose
        // content did not change.
        Write("""
            {
              "feedback": "Sound"
            }
            """);

        var old = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_path, old);

        Assert.False(AppSettings.TryRewriteValue(_path, "nosuchkey", "\"x\""));
        Assert.Equal(old, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void A_missing_file_is_reported_rather_than_created()
    {
        // The caller has already applied the change in memory. Failing to
        // persist it is worth a log line, not an exception in the menu.
        string absent = Path.Combine(Path.GetTempPath(), $"hexlinux-absent-{Guid.NewGuid():N}.json");

        Assert.False(AppSettings.TryRewriteValue(absent, "feedback", "\"Sound\""));
        Assert.False(File.Exists(absent));
    }

    [Fact]
    public void A_value_followed_by_a_comment_on_its_line_is_left_alone()
    {
        // F05. The value and the comment cannot be told apart without parsing
        // the JSON: rewriting blindly would drop the comma along with the
        // comment, and one missing comma makes the whole file unreadable.
        Write("""
            {
              "feedback": "Sound", // I want the beeps
              "logEnabled": true
            }
            """);

        string before = Read();

        Assert.False(AppSettings.TryRewriteValue(_path, "feedback", "\"None\""));
        Assert.Equal(before, Read());
    }

    [Fact]
    public void A_value_followed_by_a_block_comment_is_left_alone()
    {
        Write("""
            {
              "feedback": "Sound" /* tones */,
              "logEnabled": true
            }
            """);

        string before = Read();

        Assert.False(AppSettings.TryRewriteValue(_path, "feedback", "\"None\""));
        Assert.Equal(before, Read());
    }

    [Fact]
    public void A_refused_line_is_not_duplicated_at_the_end_of_the_file()
    {
        // The tray persists its switch with appendMissing. A feedback line the
        // user annotated is refused; appending a second "feedback" instead
        // would make the file grow at every click, and the hidden copy at the
        // bottom would then silently override the line the user edits.
        Write("""
            {
              "feedback": "Sound", // I want the beeps
              "logEnabled": true
            }
            """);

        string before = Read();

        IReadOnlyList<string> failed = AppSettings.RewriteValues(_path, Values(("feedback", "\"None\"")), appendMissing: true);

        Assert.Equal(["feedback"], failed);
        Assert.Equal(before, Read());
    }

    [Fact]
    public void A_value_continued_on_the_next_line_is_refused_rather_than_broken()
    {
        // A user who put the value on its own line: replacing the key's line
        // alone would leave the old value dangling below, and the file would
        // no longer parse — every setting back to its default at next start.
        Write("""
            {
              "feedback":
                "Sound",
              "logEnabled": false
            }
            """);

        string before = Read();

        Assert.False(AppSettings.TryRewriteValue(_path, "feedback", "\"None\""));
        Assert.Equal(before, Read());
    }

    [Fact]
    public void A_list_spread_over_several_lines_is_refused_rather_than_broken()
    {
        // The same with a shortcut laid out one key per line, as JSON
        // formatters do.
        Write("""
            {
              "hotkey": [
                "RightAlt"
              ],
              "logEnabled": false
            }
            """);

        string before = Read();

        Assert.False(AppSettings.TryRewriteValue(_path, "hotkey", "[\"F13\"]"));
        Assert.Equal(before, Read());
        Assert.Equal(["RightAlt"], AppSettings.Load(_path).Hotkey);
    }

    [Fact]
    public void Two_settings_sharing_a_line_are_left_alone()
    {
        // Rewriting the first would erase the second along with the old value.
        Write("""
            {
              "feedback": "Sound", "logEnabled": false,
              "threads": 8
            }
            """);

        string before = Read();

        Assert.False(AppSettings.TryRewriteValue(_path, "feedback", "\"None\""));
        Assert.Equal(before, Read());
    }

    [Fact]
    public void A_key_appearing_inside_a_comment_is_not_mistaken_for_the_setting()
    {
        Write("""
            {
              // Set "feedback" to None to silence everything.
              "feedback": "Sound"
            }
            """);

        AppSettings.TryRewriteValue(_path, "feedback", "\"None\"");

        string after = Read();
        Assert.Contains("// Set \"feedback\" to None to silence everything.", after, StringComparison.Ordinal);
        Assert.Contains("\"feedback\": \"None\"", after, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_keeps_its_Unix_line_endings()
    {
        // Written on Linux, read by Linux editors: a rewrite must not bring
        // Windows line endings into the file.
        Write("{\n  \"feedback\": \"Sound\",\n  \"logEnabled\": true\n}\n");

        AppSettings.TryRewriteValue(_path, "feedback", "\"None\"");

        Assert.Equal("{\n  \"feedback\": \"None\",\n  \"logEnabled\": true\n}\n", Read());
    }

    [Fact]
    public void A_file_that_cannot_be_written_reports_every_key_and_is_left_as_it_was()
    {
        // A file another process holds locked: the switch keeps its effect
        // for this session, the failure is reported, nothing is half-written.
        Write("""
            {
              "feedback": "Sound"
            }
            """);

        string before = Read();

        using (new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(
                ["feedback", "logEnabled"],
                AppSettings.RewriteValues(_path, Values(("feedback", "\"None\""), ("logEnabled", "false")), appendMissing: true));
            Assert.False(AppSettings.TryRewriteValue(_path, "feedback", "\"None\""));
        }

        Assert.Equal(before, Read());
    }

    [Fact]
    public void A_null_list_of_values_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => AppSettings.RewriteValues(_path, null!, appendMissing: false));
    }

    // --- Several values at once, and keys an older file lacks -------------------

    [Fact]
    public void Several_values_are_rewritten_in_one_pass_and_the_comments_kept()
    {
        Write("""
            {
              // The shortcut.
              "hotkey": ["RightCtrl"],
              // How the text is handed over.
              "insertion": "Paste",
              "threads": 4
            }
            """);

        IReadOnlyList<string> failed = AppSettings.RewriteValues(
            _path,
            Values(("hotkey", "[\"LeftCtrl\", \"LeftSuper\"]"), ("insertion", "\"Type\"")),
            appendMissing: false);

        Assert.Empty(failed);

        string after = Read();
        Assert.Contains("// The shortcut.", after, StringComparison.Ordinal);
        Assert.Contains("// How the text is handed over.", after, StringComparison.Ordinal);
        Assert.Contains("\"hotkey\": [\"LeftCtrl\", \"LeftSuper\"],", after, StringComparison.Ordinal);
        Assert.Contains("\"insertion\": \"Type\",", after, StringComparison.Ordinal);
        Assert.Contains("\"threads\": 4", after, StringComparison.Ordinal);

        AppSettings reloaded = AppSettings.Load(_path);
        Assert.Equal(["LeftCtrl", "LeftSuper"], reloaded.Hotkey);
        Assert.Equal(InsertionMode.Type, reloaded.Insertion);
    }

    [Fact]
    public void Without_appending_a_missing_key_is_reported_and_the_rest_still_written()
    {
        Write("""
            {
              "feedback": "Sound"
            }
            """);

        IReadOnlyList<string> failed = AppSettings.RewriteValues(
            _path,
            Values(("feedback", "\"None\""), ("frenchSpacing", "false")),
            appendMissing: false);

        Assert.Equal(["frenchSpacing"], failed);
        Assert.Contains("\"feedback\": \"None\"", Read(), StringComparison.Ordinal);
        Assert.DoesNotContain("frenchSpacing", Read(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_missing_from_an_older_file_is_added_before_the_closing_brace()
    {
        // A settings.json kept from an earlier version lacks the settings
        // added since — frenchSpacing, clipboardFallback. Saving a choice must
        // not skip exactly those.
        Write("""
            {
              // What tells you that the recording started and stopped.
              "feedback": "Sound"
            }
            """);

        IReadOnlyList<string> failed = AppSettings.RewriteValues(
            _path,
            Values(("feedback", "\"None\""), ("frenchSpacing", "false"), ("clipboardFallback", "true")),
            appendMissing: true);

        Assert.Empty(failed);

        Assert.Equal(
            """
            {
              // What tells you that the recording started and stopped.
              "feedback": "None",
              "frenchSpacing": false,
              "clipboardFallback": true
            }

            """,
            Read());

        AppSettings reloaded = AppSettings.Load(_path, out IReadOnlyList<string> notes);
        Assert.Equal(FeedbackMode.None, reloaded.Feedback);
        Assert.False(reloaded.FrenchSpacing);
        Assert.True(reloaded.ClipboardFallback);
        Assert.Empty(notes);
    }

    [Fact]
    public void Appending_skips_the_comments_and_blank_lines_before_the_brace()
    {
        Write("""
            {
              "feedback": "Sound"

              // A closing remark.
            }
            """);

        AppSettings.RewriteValues(_path, Values(("clipboardFallback", "true")), appendMissing: true);

        string after = Read();
        Assert.Contains("\"feedback\": \"Sound\",", after, StringComparison.Ordinal);
        Assert.Contains("// A closing remark.", after, StringComparison.Ordinal);
        Assert.True(AppSettings.Load(_path).ClipboardFallback);
    }

    [Fact]
    public void Appending_after_a_value_that_already_ends_with_a_comma_adds_none()
    {
        // A trailing comma is accepted by the reader: a second one would not be.
        Write("""
            {
              "feedback": "Sound",
            }
            """);

        Assert.Empty(AppSettings.RewriteValues(_path, Values(("threads", "8")), appendMissing: true));
        Assert.DoesNotContain(",,", Read(), StringComparison.Ordinal);
        Assert.Equal(8, AppSettings.Load(_path).Threads);
    }

    [Fact]
    public void Appending_into_an_empty_object_needs_no_comma()
    {
        Write("""
            {
            }
            """);

        Assert.Empty(AppSettings.RewriteValues(_path, Values(("threads", "8")), appendMissing: true));
        Assert.Equal("{\n  \"threads\": 8\n}\n", Read());
        Assert.Equal(8, AppSettings.Load(_path).Threads);
    }

    [Fact]
    public void Appending_is_refused_after_a_trailing_comment_rather_than_breaking_the_file()
    {
        // The comma the last value needs would land inside the comment.
        Write("""
            {
              "feedback": "Sound" // what marks a recording
            }
            """);

        string before = Read();

        IReadOnlyList<string> failed = AppSettings.RewriteValues(_path, Values(("threads", "8")), appendMissing: true);

        Assert.Equal(["threads"], failed);
        Assert.Equal(before, Read());
    }

    [Fact]
    public void Appending_is_refused_when_the_closing_brace_shares_a_line()
    {
        // A file written on one line has no line to insert before: refused,
        // and reported, rather than guessed at.
        Write("""{"feedback": "Sound"}""");

        string before = Read();

        Assert.Equal(["threads"], AppSettings.RewriteValues(_path, Values(("threads", "8")), appendMissing: true));
        Assert.Equal(before, Read());
    }

    [Fact]
    public void Appending_is_refused_when_nothing_but_comments_precede_the_brace()
    {
        // No opening line to anchor on: a file mangled down to comments.
        Write("""
            // settings
            }
            """);

        string before = Read();

        Assert.Equal(["threads"], AppSettings.RewriteValues(_path, Values(("threads", "8")), appendMissing: true));
        Assert.Equal(before, Read());
    }

    [Fact]
    public void Several_values_in_a_missing_file_are_all_reported()
    {
        string absent = Path.Combine(Path.GetTempPath(), $"hexlinux-absent-{Guid.NewGuid():N}.json");

        IReadOnlyList<string> failed = AppSettings.RewriteValues(
            absent,
            Values(("feedback", "\"Sound\""), ("threads", "2")),
            appendMissing: true);

        Assert.Equal(["feedback", "threads"], failed);
        Assert.False(File.Exists(absent));
    }

    // --- The file that ships ------------------------------------------------------

    private static string ShippedFile => Path.Combine(AppContext.BaseDirectory, AppSettings.FileName);

    [Fact]
    public void The_shipped_file_gives_exactly_the_defaults_of_the_code()
    {
        // HexWin's disagreed — Ctrl+Win in the code, RightShift in the file —
        // so the behaviour changed the moment a user removed a key, with
        // nothing to say why.
        Assert.True(File.Exists(ShippedFile), $"settings.json not found next to the tests: {ShippedFile}");

        AppSettings file = AppSettings.Load(ShippedFile, out IReadOnlyList<string> notes);

        Assert.Empty(notes);
        Assert.Equal(new AppSettings().ToJson(), file.ToJson());
    }

    [Fact]
    public void The_embedded_file_gives_exactly_the_defaults_of_the_code_with_no_note()
    {
        // What the first start copies into ~/.config/hexlinux: the user's
        // first --doctor must not list a single rejected setting.
        string? embedded = SettingsStore.Shipped();

        Assert.NotNull(embedded);

        AppSettings settings = AppSettings.Parse(embedded, out IReadOnlyList<string> notes);

        Assert.Empty(notes);
        Assert.Equal(new AppSettings().ToJson(), settings.ToJson());
    }

    [Fact]
    public void The_shipped_file_documents_every_setting_and_nothing_else()
    {
        // A setting missing from the file is undocumented; a key in the file
        // that the code does not read is a dead promise (HexWin's circle keys).
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(ShippedFile),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        string[] keys = [.. document.RootElement.EnumerateObject().Select(property => property.Name)];

        Assert.Equal(AppSettings.SettingNames.Order(StringComparer.Ordinal), keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_embedded_copy_is_the_shipped_file()
    {
        // The first start copies the embedded one: it must be the same
        // commented file, not a stale one.
        Assert.Equal(File.ReadAllText(ShippedFile), SettingsStore.Shipped());
    }

    [Fact]
    public void Unticking_play_tones_in_the_shipped_file_changes_exactly_one_line()
    {
        // F05, the tray's own call: the commented file the user got at first
        // start, one switch flipped, and a diff of one line.
        File.Copy(ShippedFile, _path);
        string[] before = File.ReadAllLines(_path);

        Assert.Empty(AppSettings.RewriteValues(_path, Values(("feedback", "\"None\"")), appendMissing: true));

        string[] after = File.ReadAllLines(_path);
        Assert.Equal(before.Length, after.Length);

        int[] changed = [.. Enumerable.Range(0, before.Length).Where(i => before[i] != after[i])];
        Assert.Equal("  \"feedback\": \"None\",", after[Assert.Single(changed)]);

        AppSettings reloaded = AppSettings.Load(_path, out IReadOnlyList<string> notes);
        Assert.Equal(FeedbackMode.None, reloaded.Feedback);
        Assert.Empty(notes);
    }

    [Fact]
    public void Every_setting_of_the_shipped_file_can_be_rewritten_in_place()
    {
        // A comment added after a value in the shipped file would make the
        // tray, and any later settings window, refuse to save that setting.
        File.Copy(ShippedFile, _path);

        using JsonDocument defaults = JsonDocument.Parse(new AppSettings().ToJson());

        foreach (JsonProperty setting in defaults.RootElement.EnumerateObject())
        {
            // Compact, as the tray writes a value: on one line.
            string value = JsonSerializer.Serialize(setting.Value);

            Assert.True(
                AppSettings.TryRewriteValue(_path, setting.Name, value),
                $"\"{setting.Name}\" cannot be rewritten in the shipped settings.json");
        }

        AppSettings.Load(_path, out IReadOnlyList<string> notes);
        Assert.Empty(notes);
    }
}
