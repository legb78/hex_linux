using System.Text.Json;
using HexLinux.Configuration;
using Xunit;

namespace HexLinux.Tests.Configuration;

/// <summary>
/// The rule checked end to end here: no input, however damaged, may stop the
/// application from starting with a usable configuration — and, unlike
/// HexWin, one bad value costs only its own setting.
/// </summary>
public class AppSettingsTests
{
    private const string DefaultModel = "models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";

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
    public void A_number_where_an_enum_belongs_falls_back_to_its_default()
    {
        // The serializer accepts any number for an enum: 42 has to be caught
        // after the reading.
        Assert.Equal(InsertionMode.Paste, AppSettings.Parse("""{"insertion": 42}""").Insertion);
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
        AppSettings settings = AppSettings.Parse($$"""{"provider": "{{provider}}"}""");

        Assert.Equal("cpu", settings.Provider);
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
        AppSettings settings = AppSettings.Parse("""{"hotkey": ["Ctrl", "Fn"]}""");

        Assert.Equal(["RightCtrl"], settings.Hotkey);
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
    [InlineData("\"Ydotool\"", KeySender.Auto)]
    [InlineData("9", KeySender.Auto)]
    public void The_key_sender_is_read_or_falls_back(string value, KeySender expected)
    {
        // ydotool is not supported: a file naming it falls back to Auto.
        Assert.Equal(expected, AppSettings.Parse($$"""{"keySender": {{value}}}""").KeySender);
    }

    [Theory]
    [InlineData("\"Both\"", FeedbackMode.Sound)]
    [InlineData("\"Visual\"", FeedbackMode.None)]
    [InlineData("\"visual\"", FeedbackMode.None)]
    public void HexWin_feedback_modes_keep_what_they_asked_for_that_Linux_can_do(string value, FeedbackMode expected)
    {
        // "Visual" asked for no sound at all: it must not start beeping
        // because the circle does not exist here.
        Assert.Equal(expected, AppSettings.Parse($$"""{"feedback": {{value}}}""").Feedback);
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
        AppSettings settings = AppSettings.Parse(json);

        Assert.Equal(DefaultModel, settings.ModelPath);
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

        AppSettings reread = AppSettings.Parse(original.ToJson());

        Assert.Equal(original.ToJson(), reread.ToJson());
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
}
