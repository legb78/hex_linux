using System.Text;
using Xunit;

namespace HexLinux.Tests;

/// <summary>
/// What the repository's own text files may hold.
///
/// <para>The code that strips bidirectional controls from dictations
/// (<c>InsertionText</c>) once held them literally, in its regular expression
/// and in its tests (RV-12): GitHub then flags the file ("This file contains
/// bidirectional Unicode text"), review tools warn, and a reader cannot see
/// what a line really does — the very trick, "Trojan Source"
/// (CVE-2021-42574), the code defends against. They are to be written as
/// <c>\u202E</c>-style escapes.</para>
/// </summary>
public class SourceTextTests
{
    private static readonly string[] TextExtensions = [".cs", ".csproj", ".props", ".sln", ".sh", ".md", ".json", ".yml", ".yaml", ".rules", ".conf", ".html", ".txt"];

    private static readonly string[] Folders = ["src", "tests", "scripts", "packaging", "docs", ".github"];

    [Fact]
    public void No_text_file_holds_a_literal_bidirectional_control()
    {
        string? root = RepositoryRoot();

        // Run from the clone, as every test run is: the sources sit a few
        // folders above the test build. Found nowhere, there is nothing to
        // check — a copied test build, not a checkout.
        if (root is null)
        {
            return;
        }

        List<string> offending = [];

        foreach (string folder in Folders.Select(name => Path.Combine(root, name)).Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(root, file) || !TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] lines = File.ReadAllLines(file, Encoding.UTF8);

                for (int index = 0; index < lines.Length; index++)
                {
                    if (lines[index].Any(IsBidiControl))
                    {
                        offending.Add($"{Path.GetRelativePath(root, file)}:{index + 1}");
                    }
                }
            }
        }

        Assert.Empty(offending);
    }

    /// <summary>The embeddings and overrides U+202A–U+202E, the isolates U+2066–U+2069.</summary>
    private static bool IsBidiControl(char character) => character is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    private static bool IsBuildOutput(string root, string file)
    {
        string[] parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar);

        return parts.Any(part => part is "bin" or "obj" or "TestResults");
    }

    private static string? RepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HexLinux.sln")))
            {
                return folder.FullName;
            }
        }

        return null;
    }
}
