using System.Globalization;

namespace HexLinux.Transcription;

/// <summary>
/// The four files a Parakeet model is made of, and the smallest size each
/// may have.
///
/// <para>A file that exists is not necessarily a model. An empty or cut-off
/// file makes ONNX Runtime fail inside native code, where the failure may take
/// the process down rather than raise an exception; checking the size first
/// turns that into a message naming the file. The floors are far below the
/// real sizes — for v3, 652 MB, 11.8 MB, 6.4 MB and 94 KB — so as to accept
/// every published variant: they catch a file that is empty or plainly
/// truncated, not every corruption. The archive's integrity is
/// <c>get-model.sh</c>'s job: it checks its SHA-256 before extracting
/// anything.</para>
/// </summary>
public static class ModelFiles
{
    public const string Encoder = "encoder.int8.onnx";
    public const string Decoder = "decoder.int8.onnx";
    public const string Joiner = "joiner.int8.onnx";
    public const string Tokens = "tokens.txt";

    /// <summary>Each required file with its minimum size in bytes.</summary>
    public static IReadOnlyList<(string Name, long MinimumBytes)> Required { get; } =
    [
        (Encoder, 50L * 1024 * 1024),
        (Decoder, 100L * 1024),
        (Joiner, 100L * 1024),
        (Tokens, 1024),
    ];

    /// <summary>
    /// What is wrong with the model folder, one sentence per file, or an
    /// empty list when it is complete.
    /// </summary>
    /// <param name="directory">The model folder.</param>
    /// <param name="sizeOf">Size of a file in bytes, or null when it does not exist.</param>
    public static IReadOnlyList<string> Problems(string directory, Func<string, long?> sizeOf)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(sizeOf);

        List<string> problems = [];

        foreach ((string name, long minimum) in Required)
        {
            string path = Path.Combine(directory, name);
            long? size = sizeOf(path);

            if (size is null)
            {
                problems.Add($"{path} is missing");
            }
            else if (size < minimum)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{path} is only {size} bytes, too small to be a model file"));
            }
        }

        return problems;
    }
}
