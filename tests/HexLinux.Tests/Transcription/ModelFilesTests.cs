using HexLinux.Transcription;
using Xunit;

namespace HexLinux.Tests.Transcription;

/// <summary>
/// A model folder that exists is not necessarily a model: a download cut
/// short leaves empty or truncated files, and ONNX Runtime fails on them inside
/// native code, where the failure may take the daemon down instead of raising
/// an exception. These checks turn that into a sentence naming the file. The
/// sizes are injected, so a whole folder is described in one line.
/// </summary>
public class ModelFilesTests
{
    private const string Folder = "/home/ada/.local/share/hexlinux/models/parakeet";

    // The real sizes of the v3 int8 files, from the archive get-model.sh
    // installs (checked with stat after a download).
    private static readonly Dictionary<string, long> RealV3Sizes = new(StringComparer.Ordinal)
    {
        [ModelFiles.Encoder] = 652_184_281,
        [ModelFiles.Decoder] = 11_845_275,
        [ModelFiles.Joiner] = 6_355_277,
        [ModelFiles.Tokens] = 93_939,
    };

    private static Func<string, long?> Sizes(IReadOnlyDictionary<string, long> byName) =>
        path => byName.TryGetValue(Path.GetFileName(path), out long size) ? size : null;

    private static Func<string, long?> AllAtMinimum() =>
        Sizes(ModelFiles.Required.ToDictionary(file => file.Name, file => file.MinimumBytes, StringComparer.Ordinal));

    [Fact]
    public void The_four_files_of_the_archive_are_required()
    {
        // The names sherpa-onnx's Parakeet archive contains: the engine is
        // configured with exactly these, so a renamed file is a missing one.
        Assert.Equal(
            ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"],
            ModelFiles.Required.Select(file => file.Name));
    }

    [Fact]
    public void A_complete_v3_model_has_no_problem()
    {
        // The floors must accept the genuine model: a floor above a real size
        // would refuse a download that is perfectly fine.
        Assert.Empty(ModelFiles.Problems(Folder, Sizes(RealV3Sizes)));
    }

    [Fact]
    public void Every_floor_is_far_below_the_real_size()
    {
        // Far below, so that every published variant passes: the check is
        // for an empty or plainly cut-off file, not for corruption, which is
        // get-model.sh's SHA-256 job.
        foreach ((string name, long minimum) in ModelFiles.Required)
        {
            Assert.True(minimum * 4 < RealV3Sizes[name], $"{name}: floor {minimum} is too close to {RealV3Sizes[name]}");
            Assert.True(minimum > 0, $"{name}: an empty file would pass");
        }
    }

    [Fact]
    public void Files_exactly_at_their_floor_are_accepted()
    {
        Assert.Empty(ModelFiles.Problems(Folder, AllAtMinimum()));
    }

    [Theory]
    [InlineData(ModelFiles.Encoder)]
    [InlineData(ModelFiles.Decoder)]
    [InlineData(ModelFiles.Joiner)]
    [InlineData(ModelFiles.Tokens)]
    public void A_missing_file_is_named_with_its_full_path(string missing)
    {
        // The message --doctor and the engine show: the user must see which
        // file of which folder to look at.
        Dictionary<string, long> sizes = new(RealV3Sizes, StringComparer.Ordinal);
        sizes.Remove(missing);

        IReadOnlyList<string> problems = ModelFiles.Problems(Folder, Sizes(sizes));

        Assert.Equal($"{Folder}/{missing} is missing", Assert.Single(problems));
    }

    [Theory]
    [InlineData(ModelFiles.Encoder)]
    [InlineData(ModelFiles.Decoder)]
    [InlineData(ModelFiles.Joiner)]
    [InlineData(ModelFiles.Tokens)]
    public void A_file_one_byte_under_its_floor_is_too_small(string truncated)
    {
        // The boundary: the floor itself passes, one byte less does not, and
        // the message gives the size found so the user sees how short it is.
        long floor = ModelFiles.Required.Single(file => file.Name == truncated).MinimumBytes;
        Dictionary<string, long> sizes = new(RealV3Sizes, StringComparer.Ordinal) { [truncated] = floor - 1 };

        IReadOnlyList<string> problems = ModelFiles.Problems(Folder, Sizes(sizes));

        Assert.Equal($"{Folder}/{truncated} is only {floor - 1} bytes, too small to be a model file", Assert.Single(problems));
    }

    [Fact]
    public void An_empty_file_is_reported_as_too_small_rather_than_missing()
    {
        // The difference tells the user it was there and broken — re-run
        // get-model.sh — rather than never downloaded.
        Dictionary<string, long> sizes = new(RealV3Sizes, StringComparer.Ordinal) { [ModelFiles.Encoder] = 0 };

        Assert.Equal(
            $"{Folder}/encoder.int8.onnx is only 0 bytes, too small to be a model file",
            Assert.Single(ModelFiles.Problems(Folder, Sizes(sizes))));
    }

    [Fact]
    public void An_empty_folder_lists_every_file_in_the_order_of_the_archive()
    {
        // All at once, not the first one only: one look is enough to see the
        // download never happened.
        IReadOnlyList<string> problems = ModelFiles.Problems(Folder, _ => null);

        Assert.Equal(
            [
                $"{Folder}/encoder.int8.onnx is missing",
                $"{Folder}/decoder.int8.onnx is missing",
                $"{Folder}/joiner.int8.onnx is missing",
                $"{Folder}/tokens.txt is missing",
            ],
            problems);
    }

    [Fact]
    public void Each_file_is_looked_up_inside_the_given_folder()
    {
        List<string> asked = [];

        ModelFiles.Problems(Folder, path =>
        {
            asked.Add(path);
            return null;
        });

        Assert.All(asked, path => Assert.Equal(Folder, Path.GetDirectoryName(path)));
        Assert.Equal(4, asked.Count);
    }

    [Fact]
    public void The_arguments_are_required()
    {
        Assert.Throws<ArgumentNullException>(() => ModelFiles.Problems(null!, _ => 0));
        Assert.Throws<ArgumentNullException>(() => ModelFiles.Problems(Folder, null!));
    }
}
