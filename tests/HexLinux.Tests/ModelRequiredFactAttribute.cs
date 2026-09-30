using HexLinux.Configuration;
using HexLinux.Transcription;
using Xunit;

namespace HexLinux.Tests;

/// <summary>
/// A test that needs the real recognition model on disk, and skips itself with
/// a usable message when it is absent.
///
/// <para>Kept from HexWin, for the same first impression: cloning the
/// repository and running <c>dotnet test</c> must not fail a handful of tests
/// only because the 480 MB model has not been downloaded yet. A skip keeps the
/// run green, the count says how many were skipped, and each one says why. The
/// <c>Category=Integration</c> trait stays alongside, so that CI excludes them
/// up front.</para>
///
/// <para>The model is looked up exactly where the application looks: the XDG
/// data folder first (<c>~/.local/share/hexlinux/models</c>, where
/// <c>scripts/get-model.sh</c> puts it), then up the tree from the test
/// build folder (a <c>models/</c> folder at the root of the clone).</para>
/// </summary>
public sealed class ModelRequiredFactAttribute : FactAttribute
{
    public ModelRequiredFactAttribute()
    {
        if (ModelDirectory is null)
        {
            Skip = "Recognition model not present. Run scripts/get-model.sh to download it.";
        }
    }

    /// <summary>The model folder the application itself would use, or null.</summary>
    public static string? ModelDirectory =>
        ModelLocator.Resolve(
            new AppSettings().ModelPath,
            AppContext.BaseDirectory,
            AppPaths.FromEnvironment().DataDirectory);
}
