using System.Text;
using HexLinux.Configuration;
using Xunit;

namespace HexLinux.Tests.Qa;

/// <summary>
/// settings.json damaged at random.
///
/// <para>The rule the class states is that a damaged file never stops the
/// application from starting. A person edits this file by hand, with comments,
/// in whatever editor: a missing quote, a stray comma, a pasted character.
/// These tests damage a realistic file thousands of ways and check the two
/// promises — no exception, and every value inside its bounds — instead of
/// the handful of mistakes someone thought of.</para>
/// </summary>
public class AppSettingsFuzzTests
{
    private const string Realistic = """
        {
          // Folder of the model.
          "modelPath": "models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
          "hotkey": ["RightCtrl"],
          "minRecordingMilliseconds": 250,
          "maxRecordingSeconds": 120,
          "segmentation": false,
          "pauseMilliseconds": 700,
          "unloadAfterMinutes": 5,
          "provider": "cpu",
          "threads": 4,
          "frenchSpacing": true,
          "insertion": "Paste",
          "pasteShortcut": "CtrlV",
          "keySender": "Auto",
          "clipboardFallback": false,
          "feedback": "Sound",
          "logEnabled": true,
        }
        """;

    // No raw surrogate here: the file is read with File.ReadAllText, which
    // turns invalid UTF-8 into U+FFFD, so a lone surrogate can only reach
    // Parse as a \u escape, the case the theory below covers.
    private const string Noise = "{}[]\":,0123456789-+.eE truefalsenul/*\\u\n\té€�";

    [Fact]
    public void A_file_damaged_at_random_never_throws_and_stays_in_bounds()
    {
        // One to seven slips of the keyboard in a realistic file: the daemon
        // must still start, with every value usable.
        var random = new Random(20260927);

        for (int run = 0; run < 4000; run++)
        {
            string json = Mutate(Realistic, random, random.Next(1, 8));

            AppSettings settings = AppSettings.Parse(json);

            AssertInBounds(settings, json);
        }
    }

    [Theory]
    [InlineData("{\"\\ud800\": 1}")]
    [InlineData("{\"\\udc00\": true, \"threads\": 2}")]
    [InlineData("{\"modelPath\": \"\\ud800\"}")]
    [InlineData("{\"hotkey\": [\"\\ud83d\"]}")]
    public void A_lone_surrogate_escape_does_not_stop_the_start(string json)
    {
        // "\ud83d" is half of an emoji escape: what is left when a person
        // deletes the second half by mistake. System.Text.Json refuses to turn
        // it into a string with InvalidOperationException — not a
        // JsonException — so it has to be caught like one, key names
        // included. QA-02: in a key name it escapes Parse, and the daemon,
        // --doctor and every other mode die at start (verified with the
        // binary: "Unhandled exception", no crash.log).
        AppSettings settings = AppSettings.Parse(json);

        AssertInBounds(settings, json);
    }

    [Fact]
    public void An_untouched_file_keeps_the_privacy_defaults()
    {
        // frenchSpacing on and clipboardFallback off are the documented
        // defaults: the second one is what keeps a dictation out of the
        // clipboard when nothing can paste it.
        AppSettings settings = AppSettings.Parse("{}");

        Assert.True(settings.FrenchSpacing);
        Assert.False(settings.ClipboardFallback);
    }

    private static void AssertInBounds(AppSettings settings, string json)
    {
        string context = $"after parsing: {json}";

        Assert.False(string.IsNullOrWhiteSpace(settings.ModelPath), context);
        Assert.NotEmpty(settings.Hotkey);
        Assert.DoesNotContain("Space", settings.Hotkey, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("CapsLock", settings.Hotkey, StringComparer.OrdinalIgnoreCase);
        Assert.InRange(settings.MinRecordingMilliseconds, 0, 5_000);
        Assert.InRange(settings.MaxRecordingSeconds, 5, 600);
        Assert.InRange(settings.PauseMilliseconds, 0, 5_000);
        Assert.InRange(settings.Threads, 1, 32);
        Assert.InRange(settings.UnloadAfterMinutes, 0, 1_440);
        Assert.Equal("cpu", settings.Provider);
        Assert.True(Enum.IsDefined(settings.Insertion), context);
        Assert.True(Enum.IsDefined(settings.PasteShortcut), context);
        Assert.True(Enum.IsDefined(settings.KeySender), context);
        Assert.True(Enum.IsDefined(settings.Feedback), context);
    }

    private static string Mutate(string text, Random random, int edits)
    {
        var builder = new StringBuilder(text);

        for (int i = 0; i < edits; i++)
        {
            int at = random.Next(builder.Length + 1);
            char noise = Noise[random.Next(Noise.Length)];

            switch (random.Next(3))
            {
                case 0:
                    builder.Insert(at, noise);
                    break;
                case 1 when at < builder.Length:
                    builder.Remove(at, 1);
                    break;
                default:
                    if (at < builder.Length)
                    {
                        builder[at] = noise;
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
