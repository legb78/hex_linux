using System.Globalization;
using System.Text;
using HexLinux.Input;
using Xunit;

namespace HexLinux.Tests.Qa;

/// <summary>
/// <c>/proc/bus/input/devices</c> read under stress.
///
/// <para>The daemon parses that file at start and on every hot-plug rescan.
/// An exception there costs the whole keyboard layer; a misread bitmap makes
/// it listen to the wrong devices, or to none. The sample texts of the unit
/// tests are what one machine printed; these cover what the kernel can print
/// in general, and what a garbled read looks like.</para>
/// </summary>
public class InputDeviceCatalogPropertyTests
{
    /// <summary><c>KEY_MAX</c>: the key bitmap covers codes 0 to 767.</summary>
    private const int KeyMax = 767;

    private static readonly string[] Pieces =
    [
        "I: Bus=", "0003", "0011", "zz", " Vendor=046d", "N: Name=\"", "Logitech\"", "\"", "P: Phys=usb-0000",
        "H: Handlers=", "sysrq ", "kbd ", "event", "3", "12", "leds ", "B: KEY=", "B: EV=120013", "ffff", "0 ",
        "fffffffffffffffe ", "10000000000000000000", "-1", " ", "\n", "\n\n", "\r\n", "=", ":", "é", "\0",
    ];

    [Fact]
    public void Arbitrary_text_never_throws()
    {
        // A read that raced a device disappearing, a kernel with a format
        // change: the catalog must degrade to "fewer devices", never throw.
        var random = new Random(20260927);
        IReadOnlySet<int> codes = new HashSet<int> { LinuxKeys.RightCtrl, LinuxKeys.LeftCtrl };

        for (int run = 0; run < 5000; run++)
        {
            var text = new StringBuilder();

            for (int i = random.Next(0, 60); i > 0; i--)
            {
                text.Append(Pieces[random.Next(Pieces.Length)]);
            }

            IReadOnlyList<InputDevice> devices = InputDeviceCatalog.Parse(text.ToString());
            _ = InputDeviceCatalog.Keyboards(devices, codes);
        }
    }

    [Fact]
    public void A_bitmap_printed_the_way_the_kernel_prints_it_reads_back_as_the_same_codes()
    {
        // The kernel drops the leading zero words but prints the inner ones
        // as "0", most significant word first (drivers/input/input.c,
        // input_print_bitmap). Getting the word order or the zero words wrong
        // shifts every code by 64 and picks the wrong keyboards.
        var random = new Random(20260928);

        for (int run = 0; run < 3000; run++)
        {
            HashSet<int> expected = [];

            for (int i = random.Next(0, 12); i > 0; i--)
            {
                // Clustered like real keyboards, with outliers up to KEY_MAX.
                expected.Add(random.Next(3) == 0 ? random.Next(0, KeyMax + 1) : random.Next(1, 128));
            }

            string text = string.Join(
                '\n',
                "I: Bus=0011 Vendor=0001 Product=0001 Version=ab41",
                "N: Name=\"AT Translated Set 2 keyboard\"",
                "H: Handlers=sysrq kbd event3 leds",
                "B: KEY=" + KernelBitmap(expected),
                string.Empty);

            InputDevice device = Assert.Single(InputDeviceCatalog.Parse(text));

            Assert.True(
                expected.SetEquals(device.KeyCodes),
                $"KEY={KernelBitmap(expected)} read as [{string.Join(' ', device.KeyCodes.Order())}]");
        }
    }

    [Fact]
    public void Carriage_returns_do_not_hide_a_keyboard()
    {
        // The same text saved on Windows, or piped through a tool that adds
        // CR: the device and its event node must still be found.
        string text = "I: Bus=0003 Vendor=046d Product=c31c Version=0110\r\n"
            + "N: Name=\"Logitech USB Keyboard\"\r\n"
            + "H: Handlers=sysrq kbd event5 leds\r\n"
            + "B: KEY=" + KernelBitmap([LinuxKeys.RightCtrl, LinuxKeys.LeftCtrl]) + "\r\n\r\n";

        IReadOnlyList<InputDevice> keyboards = InputDeviceCatalog.Keyboards(
            InputDeviceCatalog.Parse(text),
            new HashSet<int> { LinuxKeys.RightCtrl });

        Assert.Equal("/dev/input/event5", Assert.Single(keyboards).DevicePath);
    }

    /// <summary>The kernel's own formatting of a key bitmap, 64-bit words.</summary>
    private static string KernelBitmap(IEnumerable<int> codes)
    {
        ulong[] words = new ulong[(KeyMax / 64) + 1];

        foreach (int code in codes)
        {
            words[code / 64] |= 1UL << (code % 64);
        }

        List<string> printed = [];
        bool skipEmpty = true;

        for (int i = words.Length - 1; i >= 0; i--)
        {
            if (skipEmpty && words[i] == 0)
            {
                continue;
            }

            skipEmpty = false;
            printed.Add(words[i].ToString("x", CultureInfo.InvariantCulture));
        }

        return printed.Count == 0 ? "0" : string.Join(' ', printed);
    }
}
