using System.Text;
using System.Text.RegularExpressions;
using HexLinux.Input;
using HexLinux.Output;
using HexLinux.Session;
using Xunit;

namespace HexLinux.Tests.Qa;

/// <summary>
/// The small parsers that read what other programs print — loginctl, the
/// clipboard tools, the control socket, the kernel's input events — fed
/// arbitrary input.
///
/// <para>Each of them sits on a path the daemon cannot afford to lose: an
/// exception while reading loginctl's answer costs the insertion, one on a
/// socket line costs the control channel, one on an input event costs the
/// keyboard reader thread. Whatever those programs print, the parsers must
/// answer something.</para>
/// </summary>
public class PureParsersFuzzTests
{
    private const string Alphabet = "ActiveLockedHint=yesno \t\r\n=:;togglestartstopcancelstatusTARGETSUTF8_STRINGimage/png text/plain;charset=utf-8\0é";

    [Fact]
    public void Loginctl_output_of_any_shape_parses_and_only_a_clear_no_or_yes_refuses()
    {
        // Unknown must allow (WSL, containers); only "Active=no" or
        // "LockedHint=yes" may refuse an insertion.
        var random = new Random(20260927);

        for (int run = 0; run < 5000; run++)
        {
            string output = RandomText(random, random.Next(0, 80));

            LogindState state = LogindState.Parse(output);

            if (state.Active != false && state.Locked != true)
            {
                Assert.True(state.AllowsInsertion, output);
            }
            else
            {
                Assert.False(state.AllowsInsertion, output);
            }
        }
    }

    [Theory]
    [InlineData("Active=yes\r\nLockedHint=no\r\n", true)]
    [InlineData("Active=no\nLockedHint=no\n", false)]
    [InlineData("Active=yes\nLockedHint=yes\n", false)]
    [InlineData("Failed to get session: No such file or directory\n", true)]
    [InlineData("", true)]
    public void Loginctl_answers_seen_in_practice(string output, bool allows)
    {
        // CRLF, an error message instead of properties, nothing at all: the
        // three shapes a failing or unusual loginctl produces.
        Assert.Equal(allows, LogindState.Parse(output).AllowsInsertion);
    }

    [Fact]
    public void Any_socket_line_is_a_command_or_Unknown()
    {
        // A local client can send anything; the vocabulary is closed.
        var random = new Random(20260928);

        for (int run = 0; run < 5000; run++)
        {
            string line = RandomText(random, random.Next(0, 30));
            ControlCommand command = ControlCommands.Parse(line);

            Assert.True(Enum.IsDefined(command), line);

            if (command != ControlCommand.Unknown)
            {
                Assert.Equal(ControlCommands.Name(command), line.Trim().ToLowerInvariant());
            }
        }
    }

    [Fact]
    public void Any_reply_line_is_either_refused_or_read_back_identically()
    {
        // The client prints what the daemon answered; a line off the
        // grammar must be refused, not half-read into a wrong exit code.
        // Runs of spaces between the two words are tolerated, and only that.
        var random = new Random(20260931);
        var spaces = new Regex(" +");

        for (int run = 0; run < 5000; run++)
        {
            string line = random.Next(3) switch
            {
                0 => RandomText(random, random.Next(0, 30)),
                1 => $"{new[] { "ok", "ignored", "error", "OK", "fine" }[random.Next(5)]} {RandomText(random, random.Next(0, 12))}",
                _ => ControlReply.Done((HexLinux.Daemon.DictationState)random.Next(0, 5)).Format(),
            };

            if (ControlReply.TryParse(line, out ControlReply reply))
            {
                Assert.Equal(spaces.Replace(line.Trim(), " "), reply.Format());
            }
        }
    }

    [Fact]
    public void The_preferred_clipboard_format_is_always_one_on_offer_and_never_bookkeeping()
    {
        // Saving "TARGETS" or "TIMESTAMP" instead of the content would
        // restore garbage; saving a format that is not on offer fails.
        string[] vocabulary =
        [
            "TARGETS", "MULTIPLE", "TIMESTAMP", "SAVE_TARGETS", "DELETE", "INCR", "UTF8_STRING", "STRING", "TEXT",
            "text/plain", "text/plain;charset=utf-8", "image/png", "image/jpeg", "text/uri-list", "text/html",
            "application/x-kde-cutselection", "  ", string.Empty, "x-special/gnome-copied-files",
        ];
        var random = new Random(20260929);

        for (int run = 0; run < 5000; run++)
        {
            string[] offered = [.. Enumerable.Range(0, random.Next(0, 8)).Select(_ => vocabulary[random.Next(vocabulary.Length)])];

            string? preferred = ClipboardFormats.Preferred(offered);

            if (preferred is not null)
            {
                Assert.Contains(preferred, offered.Select(format => format.Trim()));
                Assert.DoesNotContain(preferred, new[] { "TARGETS", "MULTIPLE", "TIMESTAMP", "SAVE_TARGETS", "DELETE", "INCR" });
            }
        }
    }

    [Fact]
    public void An_input_event_survives_a_round_trip_through_its_24_bytes()
    {
        // The reader parses what the kernel wrote and the uinput sender
        // writes what the kernel will parse: both directions must agree on
        // the layout, for every value, negative ones included.
        var random = new Random(20260930);
        byte[] buffer = new byte[InputEvent.Size];

        for (int run = 0; run < 5000; run++)
        {
            var original = new InputEvent((ushort)random.Next(0, 65536), (ushort)random.Next(0, 65536), random.Next(int.MinValue, int.MaxValue));

            random.NextBytes(buffer);
            original.WriteTo(buffer);

            Assert.True(InputEvent.TryRead(buffer, out InputEvent read));
            Assert.Equal(original, read);
            Assert.All(buffer[..16], b => Assert.Equal(0, b));
        }
    }

    [Fact]
    public void A_short_read_is_refused_rather_than_misread()
    {
        // read() on an evdev node can return less than a whole event when
        // the device vanishes mid-read.
        for (int length = 0; length < InputEvent.Size; length++)
        {
            Assert.False(InputEvent.TryRead(new byte[length], out _));
        }
    }

    private static string RandomText(Random random, int length)
    {
        var builder = new StringBuilder(length);

        for (int i = 0; i < length; i++)
        {
            builder.Append(Alphabet[random.Next(Alphabet.Length)]);
        }

        return builder.ToString();
    }
}
