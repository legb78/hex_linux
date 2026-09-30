using HexLinux.Input;
using Xunit;

namespace HexLinux.Tests.Qa;

/// <summary>
/// Random raw evdev traffic from two keyboards, with dropped events and
/// unplugged devices, thrown at the tracker the daemon decides with.
///
/// <para>The tracker's unit tests cover each of <c>SYN_DROPPED</c>, a
/// vanished keyboard and a repeat never pressed, one at a time. What they
/// cannot cover is an arbitrary mix of them on two keyboards at once — the
/// laptop's own and a Bluetooth one falling asleep — where a forgotten
/// release would leave the shortcut dead, or a modifier "held" for ever that
/// delays every paste by the modifier guard's full second. The seed is fixed,
/// so a failure replays identically.</para>
/// </summary>
public class HotkeyTrackerPropertyTests
{
    private const int Seed = 20260929;
    private const int Runs = 3000;

    private const string Laptop = "/dev/input/event3";
    private const string Bluetooth = "/dev/input/event7";

    private static readonly string[] Devices = [Laptop, Bluetooth];

    // 30 and 46 (KEY_A, KEY_C) are foreign keys.
    private static readonly int[] Alphabet =
    [
        LinuxKeys.LeftCtrl, LinuxKeys.RightCtrl, LinuxKeys.RightShift, LinuxKeys.RightAlt, 30, 46,
    ];

    private static readonly InputEvent Report = new(InputEvent.Synchronization, InputEvent.SynReport, 0);

    private static readonly InputEvent Dropped = new(InputEvent.Synchronization, InputEvent.SynDropped, 0);

    public static TheoryData<string[]> Shortcuts => new()
    {
        new[] { "RightCtrl" },
        new[] { "Ctrl" },
        new[] { "RightCtrl", "RightShift" },
    };

    [Theory]
    [MemberData(nameof(Shortcuts))]
    public void After_drops_and_unplugs_the_shortcut_starts_again_and_no_modifier_stays_held(string[] shortcut)
    {
        // The situation: an evening of typing on two keyboards, the reader
        // falling behind now and then, the Bluetooth keyboard going to sleep.
        // Once every key is really up, the next press of the shortcut must
        // dictate, and nothing may be believed held.
        var random = new Random(Seed);

        for (int run = 0; run < Runs; run++)
        {
            var tracker = new HotkeyTracker(new ChordDetector(shortcut));
            Dictionary<string, HashSet<int>> down = Devices.ToDictionary(device => device, _ => new HashSet<int>());
            List<string> trace = [];

            Drive(tracker, random, down, trace);

            // Every key really goes up, and each keyboard reports once more:
            // whatever drop was in progress is over.
            foreach (string device in Devices)
            {
                foreach (int code in down[device])
                {
                    Record(trace, $"{Short(device)} up {code}", tracker.OnEvent(device, Release(code)));
                }

                Record(trace, $"{Short(device)} report", tracker.OnEvent(device, Report));
            }

            Assert.False(tracker.Detector.IsActive, $"still active: {string.Join(", ", trace)}");
            Assert.True(tracker.HeldModifiers().Count == 0, $"a modifier is still believed held: {string.Join(", ", trace)}");

            ChordAction started = ChordAction.None;

            foreach (string name in shortcut)
            {
                started = tracker.OnEvent(Laptop, Press(LinuxKeys.Resolve(name)[0]));
            }

            Assert.True(started == ChordAction.Start, $"the shortcut no longer starts after: {string.Join(", ", trace)}");
        }
    }

    [Theory]
    [MemberData(nameof(Shortcuts))]
    public void Starts_and_ends_alternate_whatever_the_keyboards_do(string[] shortcut)
    {
        // A second Start while a dictation runs would open a second recording
        // over it; an end with no Start would transcribe nothing. Neither may
        // come out of drops or unplugs.
        var random = new Random(Seed + 1);

        for (int run = 0; run < Runs; run++)
        {
            var tracker = new HotkeyTracker(new ChordDetector(shortcut));
            Dictionary<string, HashSet<int>> down = Devices.ToDictionary(device => device, _ => new HashSet<int>());
            List<string> trace = [];
            bool open = false;

            Drive(tracker, random, down, trace, action =>
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
            });
        }
    }

    /// <summary>
    /// Plays 1 to 60 random events: presses, auto-repeats, releases (on
    /// either keyboard), drops, reports, and a keyboard unplugged.
    /// <paramref name="down"/> tracks what is physically down.
    /// </summary>
    private static void Drive(
        HotkeyTracker tracker,
        Random random,
        Dictionary<string, HashSet<int>> down,
        List<string> trace,
        Action<ChordAction>? observe = null)
    {
        int steps = random.Next(1, 61);

        for (int step = 0; step < steps; step++)
        {
            string device = Devices[random.Next(Devices.Length)];
            int code = Alphabet[random.Next(Alphabet.Length)];
            int roll = random.Next(30);
            ChordAction action;
            string what;

            if (roll < 12)
            {
                // A press, or the kernel's auto-repeat of a key already down.
                bool repeat = down[device].Contains(code);
                action = tracker.OnEvent(device, repeat ? Repeat(code) : Press(code));
                down[device].Add(code);
                what = $"{Short(device)} {(repeat ? "repeat" : "down")} {code}";
            }
            else if (roll < 24)
            {
                action = tracker.OnEvent(device, Release(code));
                down[device].Remove(code);
                what = $"{Short(device)} up {code}";
            }
            else if (roll < 26)
            {
                action = tracker.OnEvent(device, Dropped);
                what = $"{Short(device)} DROPPED";
            }
            else if (roll < 29)
            {
                action = tracker.OnEvent(device, Report);
                what = $"{Short(device)} report";
            }
            else
            {
                // Unplugged: its keys go with it, and it may come back later
                // with nothing held.
                action = tracker.OnDeviceRemoved(device);
                down[device].Clear();
                what = $"{Short(device)} UNPLUGGED";
            }

            Record(trace, what, action);
            observe?.Invoke(action);
        }
    }

    private static InputEvent Press(int code) => new(InputEvent.Key, (ushort)code, 1);

    private static InputEvent Repeat(int code) => new(InputEvent.Key, (ushort)code, 2);

    private static InputEvent Release(int code) => new(InputEvent.Key, (ushort)code, 0);

    private static string Short(string device) => device == Laptop ? "L" : "B";

    private static void Record(List<string> trace, string what, ChordAction action) =>
        trace.Add(action == ChordAction.None ? what : $"{what}->{action}");
}
