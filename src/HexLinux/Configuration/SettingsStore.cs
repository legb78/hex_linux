using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace HexLinux.Configuration;

/// <summary>
/// Where settings.json comes from on the first run, and how it is read.
///
/// <para>The commented settings.json is <b>copied</b> into
/// <c>~/.config/hexlinux</c> the first time any mode reads the settings, so
/// the user gets the comments — the documentation of every value — in the
/// file they will edit. The copy comes from inside the executable (an
/// embedded resource), not from beside it: a binary copied alone to
/// <c>~/.local/bin</c> would otherwise leave nothing to copy. An existing file
/// is never overwritten.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Reads and writes the user's configuration folder; the parsing is AppSettings.Parse, which is tested, and the embedded copy is checked by a test.")]
public static class SettingsStore
{
    /// <summary>The name the commented file is embedded under (see HexLinux.csproj).</summary>
    public const string ResourceName = "HexLinux.settings.json";

    /// <summary>The commented settings.json built into the executable, or null.</summary>
    public static string? Shipped()
    {
        using Stream? stream = typeof(SettingsStore).Assembly.GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Copies the commented file into place if there is none yet, then reads
    /// it. <paramref name="notes"/> lists what could not be used as written.
    /// </summary>
    public static AppSettings Load(AppPaths paths, Action<string> log, out IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        if (!File.Exists(paths.SettingsFile))
        {
            CreateFromShipped(paths, log);
        }

        return AppSettings.Load(paths.SettingsFile, out notes);
    }

    private static void CreateFromShipped(AppPaths paths, Action<string> log)
    {
        string? shipped = Shipped();

        if (shipped is null)
        {
            log("no commented settings.json is built into this executable: defaults in use");
            return;
        }

        try
        {
            Directory.CreateDirectory(paths.ConfigDirectory);

            // CreateNew: a file that appeared meanwhile is left alone.
            using var stream = new FileStream(paths.SettingsFile, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream);
            writer.Write(shipped);

            log($"settings.json created: {paths.SettingsFile} (every setting is explained inside)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"settings.json could not be created in {paths.ConfigDirectory} ({ex.Message}): defaults in use");
        }
    }
}
