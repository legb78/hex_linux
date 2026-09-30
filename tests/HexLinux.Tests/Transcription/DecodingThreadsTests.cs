using HexLinux.Transcription;
using Xunit;

namespace HexLinux.Tests.Transcription;

/// <summary>
/// "threads": 0 asks for one decoding thread per physical core. HexWin's
/// cases for the decision, then the Linux part: telling cores apart from what
/// sysfs and /proc/cpuinfo say, on the machines they describe.
/// </summary>
public class DecodingThreadsTests
{
    [Theory]
    [InlineData(2, 2)]
    [InlineData(6, 6)]
    [InlineData(24, 8)]
    public void Zero_picks_one_thread_per_physical_core_up_to_a_ceiling(int physicalCores, int expected)
    {
        Assert.Equal(expected, DecodingThreads.Resolve(0, physicalCores));
    }

    [Fact]
    public void A_machine_that_reports_no_core_still_gets_one_thread()
    {
        Assert.Equal(1, DecodingThreads.Resolve(0, 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void A_count_chosen_by_the_user_is_kept_as_is(int chosen)
    {
        // Even past the automatic ceiling: whoever set it may have measured.
        Assert.Equal(chosen, DecodingThreads.Resolve(chosen, 4));
    }

    [Fact]
    public void This_machine_resolves_to_a_sensible_count()
    {
        // Reads the real sysfs: whatever the machine, a count the process may
        // actually run on.
        int threads = DecodingThreads.Resolve(0);

        Assert.InRange(threads, 1, DecodingThreads.AutomaticCeiling);
        Assert.True(threads <= Environment.ProcessorCount);
    }

    // --- sysfs CPU lists ------------------------------------------------------------

    [Theory]
    [InlineData("0-3\n", new[] { 0, 1, 2, 3 })]
    [InlineData("0", new[] { 0 })]
    [InlineData("0-1,4,6-7", new[] { 0, 1, 4, 6, 7 })]
    [InlineData(" 2-3 \n", new[] { 2, 3 })]
    public void The_online_list_names_each_processor(string text, int[] expected)
    {
        // /sys/devices/system/cpu/online: a laptop with processors taken
        // offline reads "0-1,4,6-7", not a count.
        Assert.Equal(expected, DecodingThreads.ParseCpuList(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("zero")]
    [InlineData("3-1")]
    [InlineData("1-2-3")]
    [InlineData("0,,1")]
    [InlineData("-1")]
    public void A_list_that_is_not_one_reads_as_no_processor(string? text)
    {
        // A half-read list would give a count taken for the truth: none at all
        // makes the caller fall back to /proc/cpuinfo.
        Assert.Empty(DecodingThreads.ParseCpuList(text));
    }

    // --- Topology ---------------------------------------------------------------------

    [Fact]
    public void Two_threads_of_one_core_count_once()
    {
        // A 4-core, 8-thread laptop: processors 0-3 and 4-7 share core ids.
        (string?, string?, string?)[] processors =
        [
            ("0", "0", "0"), ("0", "0", "1"), ("0", "0", "2"), ("0", "0", "3"),
            ("0", "0", "0"), ("0", "0", "1"), ("0", "0", "2"), ("0", "0", "3"),
        ];

        Assert.Equal(4, DecodingThreads.CountCores(processors));
    }

    [Fact]
    public void The_same_core_id_on_two_packages_is_two_cores()
    {
        // A dual-socket workstation numbers its cores from 0 on each socket.
        (string?, string?, string?)[] processors = [("0", "0", "0"), ("0", "0", "1"), ("1", "0", "0"), ("1", "0", "1")];

        Assert.Equal(4, DecodingThreads.CountCores(processors));
    }

    [Fact]
    public void The_same_core_id_on_two_dies_is_two_cores()
    {
        // A package made of several dies may number the cores of each from 0.
        (string?, string?, string?)[] processors = [("0", "0", "0"), ("0", "1", "0")];

        Assert.Equal(2, DecodingThreads.CountCores(processors));
    }

    [Fact]
    public void A_kernel_without_die_ids_is_counted_on_packages_and_cores()
    {
        (string?, string?, string?)[] processors = [("0\n", null, "0\n"), ("0\n", null, "1\n"), ("0\n", null, "1\n")];

        Assert.Equal(2, DecodingThreads.CountCores(processors));
    }

    [Theory]
    [InlineData(null, "0", "0")]
    [InlineData("0", "0", null)]
    [InlineData("0", "0", "-1")]
    [InlineData("0", "?", "0")]
    [InlineData("x", "0", "0")]
    public void One_unreadable_processor_makes_the_whole_count_unknown(string? package, string? die, string? core)
    {
        // Some platforms write -1 for an id they do not know: every processor
        // would then collapse onto one core, and decoding onto one thread.
        (string?, string?, string?)[] processors = [("0", "0", "1"), (package, die, core)];

        Assert.Equal(0, DecodingThreads.CountCores(processors));
    }

    [Fact]
    public void No_processor_is_no_core()
    {
        Assert.Equal(0, DecodingThreads.CountCores([]));
    }

    // --- /proc/cpuinfo ---------------------------------------------------------------

    [Fact]
    public void Cpuinfo_counts_distinct_package_and_core_pairs()
    {
        // x86 with hyper-threading: two processors per core id.
        const string cpuinfo = """
            processor	: 0
            physical id	: 0
            core id		: 0

            processor	: 1
            physical id	: 0
            core id		: 1

            processor	: 2
            physical id	: 0
            core id		: 0

            processor	: 3
            physical id	: 0
            core id		: 1

            """;

        Assert.Equal(2, DecodingThreads.CountCoresFromCpuInfo(cpuinfo));
    }

    [Fact]
    public void Cpuinfo_tells_packages_apart()
    {
        const string cpuinfo = "processor : 0\nphysical id : 0\ncore id : 0\nprocessor : 1\nphysical id : 1\ncore id : 0\n";

        Assert.Equal(2, DecodingThreads.CountCoresFromCpuInfo(cpuinfo));
    }

    [Fact]
    public void An_arm_cpuinfo_without_core_ids_counts_nothing()
    {
        // ARM's /proc/cpuinfo has no "core id": the logical count is the
        // caller's last resort.
        const string cpuinfo = "processor\t: 0\nBogoMIPS\t: 48.00\nFeatures\t: fp asimd\n\nprocessor\t: 1\nBogoMIPS\t: 48.00\n";

        Assert.Equal(0, DecodingThreads.CountCoresFromCpuInfo(cpuinfo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_empty_cpuinfo_counts_nothing(string? cpuinfo)
    {
        Assert.Equal(0, DecodingThreads.CountCoresFromCpuInfo(cpuinfo));
    }
}
