using HexLinux.Input;
using Xunit;

namespace HexLinux.Tests.Qa;

/// <summary>
/// Random key sequences thrown at the hold-to-talk detector.
///
/// <para>The unit tests describe the known races one by one. These exist for
/// the failure nobody wrote down: a shortcut that stops answering until the
/// daemon restarts, because some ordering of presses, auto-repeats,
/// out-of-order releases and device resets left the detector convinced a key
/// is still held. The seed is fixed, so a failure replays identically.</para>
/// </summary>
public class ChordDetectorPropertyTests
{
    private const int Seed = 20260927;
    private const int Runs = 3000;

    // 30 and 46 (KEY_A, KEY_C) are foreign keys; the modifiers overlap the
    // shortcuts below.
    private static readonly int[] Alphabet =
    [
        LinuxKeys.LeftCtrl, LinuxKeys.RightCtrl, LinuxKeys.LeftShift, LinuxKeys.RightShift,
        LinuxKeys.LeftAlt, LinuxKeys.RightAlt, 30, 46,
    ];

    public static TheoryData<string[]> Shortcuts => new()
    {
        new[] { "RightCtrl" },
        new[] { "Ctrl" },
        new[] { "Ctrl", "Shift" },
        new[] { "LeftCtrl", "LeftAlt" },
        new[] { "RightCtrl", "RightShift", "RightAlt" },
    };

    [Theory]
    [MemberData(nameof(Shortcuts))]
    public void Once_every_key_is_up_the_detector_is_idle_and_the_shortcut_starts_again(string[] shortcut)
    {
        // The situation: whatever the user did with the keyboard — mashing
        // keys, releasing in any order, a keyboard unplugged mid-dictation —
        // the next deliberate press of the shortcut must start a dictation.
        var random = new Random(Seed);

        for (int run = 0; run < Runs; run++)
        {
            var detector = new ChordDetector(shortcut);
            var held = new HashSet<int>();
            List<string> trace = [];

            Drive(detector, random, held, trace);

            foreach (int code in held)
            {
                Record(trace, $"up {code}", detector.OnKeyUp(code));
            }

            Assert.False(detector.IsActive, $"still active after releasing everything: {string.Join(", ", trace)}");

            ChordAction started = ChordAction.None;

            foreach (string name in shortcut)
            {
                started = detector.OnKeyDown(LinuxKeys.Resolve(name)[0]);
            }

            Assert.True(
                started == ChordAction.Start,
                $"the shortcut no longer starts after: {string.Join(", ", trace)}");
        }
    }

    [Theory]
    [MemberData(nameof(Shortcuts))]
    public void A_start_and_its_end_always_alternate(string[] shortcut)
    {
        // The daemon opens the microphone on Start and closes it on Stop or
        // Cancel. Two Starts in a row would open a second recording over the
        // first; an end without a Start would transcribe nothing, or the wrong
        // thing.
        var random = new Random(Seed + 1);

        for (int run = 0; run < Runs; run++)
        {
            var detector = new ChordDetector(shortcut);
            var held = new HashSet<int>();
            List<string> trace = [];
            bool open = false;

            Drive(detector, random, held, trace, action =>
            {
                switch (action)
                {
                    case ChordAction.Start:
                        Assert.False(open, $"second Start without an end: {string.Join(", ", trace)}");
                        open = true;
                        break;
                    case ChordAction.Stop:
                    case ChordAction.Cancel:
                        Assert.True(open, $"{action} without a Start: {string.Join(", ", trace)}");
                        open = false;
                        break;
                    default:
                        break;
                }

                Assert.True(open == detector.IsActive, $"IsActive disagrees with the actions: {string.Join(", ", trace)}");
            });
        }
    }

    [Theory]
    [InlineData(LinuxKeys.LeftCtrl, LinuxKeys.RightCtrl)]
    [InlineData(LinuxKeys.RightCtrl, LinuxKeys.LeftCtrl)]
    public void A_shortcut_whose_keys_overlap_starts_whatever_the_order(int first, int second)
    {
        // ["Ctrl", "RightCtrl"] passes the settings validation (two known
        // names) and reads as "both Control keys". Either order of pressing
        // them must complete it. QA-07: slots are filled greedily, so
        // RightCtrl pressed first takes the generic "Ctrl" slot and LeftCtrl
        // then fits nowhere — the RightCtrl-first case fails.
        var detector = new ChordDetector(["Ctrl", "RightCtrl"]);

        detector.OnKeyDown(first);

        Assert.Equal(ChordAction.Start, detector.OnKeyDown(second));
    }

    /// <summary>
    /// Plays 1 to 40 random events: presses (auto-repeat when the key is
    /// already down), releases (spurious when it is not), and resets.
    /// <paramref name="observe"/> sees each action right after its event.
    /// </summary>
    private static void Drive(
        ChordDetector detector,
        Random random,
        HashSet<int> held,
        List<string> trace,
        Action<ChordAction>? observe = null)
    {
        int steps = random.Next(1, 41);

        for (int step = 0; step < steps; step++)
        {
            int code = Alphabet[random.Next(Alphabet.Length)];
            int roll = random.Next(20);
            ChordAction action;

            if (roll < 10)
            {
                action = detector.OnKeyDown(code);
                held.Add(code);
                Record(trace, $"down {code}", action);
            }
            else if (roll < 19)
            {
                action = detector.OnKeyUp(code);
                held.Remove(code);
                Record(trace, $"up {code}", action);
            }
            else
            {
                // A keyboard vanished. Its keys stay "held" in this model
                // only if they were on another keyboard; either way their
                // releases, if they come, must be harmless.
                action = detector.Reset();
                Record(trace, "reset", action);
            }

            observe?.Invoke(action);
        }
    }

    private static void Record(List<string> trace, string what, ChordAction action) =>
        trace.Add(action == ChordAction.None ? what : $"{what}->{action}");
}
