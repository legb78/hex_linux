using System.Text;
using HexLinux.Daemon;
using Xunit;

namespace HexLinux.Tests.Qa;

/// <summary>
/// The Exec line of the autostart entry, checked by reading it back.
///
/// <para>A wrong quote does not fail when the entry is written: it fails at
/// the next login, silently, and HexLinux simply does not start. So every
/// generated value is decoded here the way the Desktop Entry specification
/// says a reader must — string escapes first, then the Exec quoting, then the
/// field codes — and must give back exactly the path it was made from.
/// https://specifications.freedesktop.org/desktop-entry-spec/latest/exec-variables.html</para>
/// </summary>
public class DesktopEntryPropertyTests
{
    /// <summary>The characters the specification reserves in an Exec argument.</summary>
    private const string Reserved = " \t\n\"'\\><~|&;$*?#()`";

    /// <summary>
    /// What a folder name can hold, control characters aside: QuoteExec
    /// refuses those outright (no value of a key file may hold one), which
    /// the last test checks.
    /// </summary>
    private static readonly string Pool = Reserved.Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\t", string.Empty, StringComparison.Ordinal)
        + "%%/abcXYZ019._-=:,@+[]{}!^éü€漢";

    [Fact]
    public void Any_path_reads_back_as_itself()
    {
        // The situation: the binary lives in "~/Apps/Hex Linux (test)/" or a
        // folder with a $ or a % in its name. The session must start exactly
        // that file, as one argument.
        var random = new Random(20260927);

        for (int run = 0; run < 5000; run++)
        {
            string path = "/" + RandomText(random, random.Next(0, 24));

            string exec = DesktopEntry.QuoteExec(path);

            Assert.Equal([path], Decode(exec));
        }
    }

    [Theory]
    [InlineData("/home/ada/Hex Linux/hexlinux")]
    [InlineData("/opt/100%/hexlinux")]
    [InlineData("/home/ada/$HOME/hexlinux")]
    [InlineData("/home/ada/a\\b/hexlinux")]
    [InlineData("/home/ada/\"quoted\"/hexlinux")]
    [InlineData("/home/ada/`id`/hexlinux")]
    [InlineData("/home/ada/%f/hexlinux")]
    public void Awkward_real_paths_read_back_as_themselves(string path)
    {
        // The same property on the cases a person would try first; a failure
        // here is readable without replaying the random run.
        Assert.Equal([path], Decode(DesktopEntry.QuoteExec(path)));
    }

    [Fact]
    public void The_whole_entry_carries_the_quoted_path_on_its_Exec_line()
    {
        // Build must use QuoteExec, not the raw path.
        const string path = "/home/ada/Hex Linux/hexlinux";

        string entry = DesktopEntry.Build(path);
        string exec = entry.Split('\n').Single(line => line.StartsWith("Exec=", StringComparison.Ordinal))["Exec=".Length..];

        Assert.Equal([path], Decode(exec));
    }

    [Theory]
    [InlineData("/home/ada/tab\there/hexlinux")]
    [InlineData("/home/ada/line\nbreak/hexlinux")]
    [InlineData("/home/ada/bell\u0007/hexlinux")]
    [InlineData("/home/ada/del\u007f/hexlinux")]
    public void A_control_character_is_refused_rather_than_written_as_something_else(string path)
    {
        // A tab or a line break in a folder name cannot be written in an Exec
        // value: an entry that quietly started another path would be worse
        // than none.
        Assert.Throws<ArgumentException>(() => DesktopEntry.QuoteExec(path));
    }

    /// <summary>
    /// Reads an Exec value the way the specification says: key-file string
    /// escapes, then the quoting, then the field codes (<c>%%</c> is a
    /// literal percent sign; any other field code in a path is a failure).
    /// Anything the specification forbids throws, so a malformed value fails
    /// the test rather than being read generously.
    /// </summary>
    private static List<string> Decode(string value)
    {
        string unescaped = UnescapeString(value);
        List<string> arguments = [];
        var current = new StringBuilder();
        bool quoted = false;
        bool pending = false;

        for (int i = 0; i < unescaped.Length; i++)
        {
            char c = unescaped[i];

            if (quoted)
            {
                if (c == '"')
                {
                    quoted = false;
                }
                else if (c == '\\')
                {
                    char next = i + 1 < unescaped.Length ? unescaped[++i] : throw new FormatException("backslash at the end");

                    if (next is not ('"' or '`' or '$' or '\\'))
                    {
                        throw new FormatException($"\\{next} is not an escape inside quotes");
                    }

                    current.Append(next);
                }
                else if (c is '`' or '$')
                {
                    throw new FormatException($"{c} must be escaped inside quotes");
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
                pending = true;
            }
            else if (c == ' ')
            {
                if (pending || current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    pending = false;
                }
            }
            else if (Reserved.Contains(c, StringComparison.Ordinal))
            {
                throw new FormatException($"reserved character {c} outside quotes");
            }
            else
            {
                current.Append(c);
            }
        }

        if (quoted)
        {
            throw new FormatException("unterminated quote");
        }

        if (pending || current.Length > 0)
        {
            arguments.Add(current.ToString());
        }

        return [.. arguments.Select(ExpandFieldCodes)];
    }

    private static string UnescapeString(string value)
    {
        var builder = new StringBuilder();

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\')
            {
                builder.Append(value[i]);
                continue;
            }

            char next = i + 1 < value.Length ? value[++i] : throw new FormatException("backslash at the end of the value");

            builder.Append(next switch
            {
                's' => ' ',
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '\\' => '\\',
                _ => throw new FormatException($"\\{next} is not a string escape"),
            });
        }

        return builder.ToString();
    }

    private static string ExpandFieldCodes(string argument)
    {
        var builder = new StringBuilder();

        for (int i = 0; i < argument.Length; i++)
        {
            if (argument[i] != '%')
            {
                builder.Append(argument[i]);
            }
            else if (i + 1 < argument.Length && argument[i + 1] == '%')
            {
                builder.Append('%');
                i++;
            }
            else
            {
                throw new FormatException("a field code in a path");
            }
        }

        return builder.ToString();
    }

    private static string RandomText(Random random, int length)
    {
        var builder = new StringBuilder(length);

        for (int i = 0; i < length; i++)
        {
            builder.Append(Pool[random.Next(Pool.Length)]);
        }

        return builder.ToString();
    }
}
