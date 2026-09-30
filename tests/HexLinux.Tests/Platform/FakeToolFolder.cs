namespace HexLinux.Tests.Platform;

/// <summary>
/// A temporary folder of fake tools: small <c>/bin/sh</c> scripts standing
/// for xclip, xdotool and the like, which record what they were given in
/// files of the folder.
///
/// <para>Why fakes rather than the real tools: the guarantees that matter in
/// the process shells — no deadlock on a tool that forks, a time limit that
/// kills, an output cap, the text on standard input and never in argv — hold
/// whatever the tool, and a CI runner has no desktop for the real ones. Every
/// Linux machine has <c>/bin/sh</c>.</para>
/// </summary>
internal sealed class FakeToolFolder : IDisposable
{
    public FakeToolFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hexlinux-fake-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>
    /// Writes an executable script; <c>$DIR</c> in <paramref name="body"/>
    /// stands for this folder. Returns its path.
    /// </summary>
    public string Add(string name, string body)
    {
        string path = File(name);

        System.IO.File.WriteAllText(path, "#!/bin/sh\nDIR='" + Path + "'\n" + body.ReplaceLineEndings("\n") + "\n");
        System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Read(string name) => System.IO.File.Exists(File(name)) ? System.IO.File.ReadAllText(File(name)) : string.Empty;

    public bool Has(string name) => System.IO.File.Exists(File(name));

    public void Write(string name, string content) => System.IO.File.WriteAllText(File(name), content);

    /// <summary>Everything every tool recorded about its arguments.</summary>
    public string AllArguments() =>
        string.Concat(Directory.EnumerateFiles(Path, "*.args").Select(System.IO.File.ReadAllText));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A child still writing: the temporary folder is cleaned later.
        }
    }
}
