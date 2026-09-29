using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace HexLinux.Transcription;

/// <summary>
/// Turns the <c>threads</c> setting into the count handed to the engine.
///
/// <para><b>Zero means automatic: one thread per physical core</b>, not per
/// logical processor. The two threads of a hyper-threaded core share the same
/// arithmetic units, which the matrix products of the model already keep busy;
/// a second thread on it only adds synchronisation. ONNX Runtime picks the
/// same count when left to itself (HexWin's reasoning, kept).</para>
///
/// <para>A fixed four used to be the default: too many on a dual-core laptop,
/// where decoding then fights the rest of the machine, too few on a desktop
/// with eight cores or more.</para>
///
/// <para><b>How Linux tells cores apart.</b> Every online processor has a
/// <c>topology</c> folder in <c>/sys/devices/system/cpu/cpuN</c>, whose
/// <c>physical_package_id</c>, <c>die_id</c> and <c>core_id</c> together name
/// its core: two hardware threads of one core share all three. A core id is
/// only unique within its package (and die), hence the three together.
/// <c>/proc/cpuinfo</c>'s "physical id" and "core id" say the same on x86 and
/// serve when sysfs cannot be read; ARM's cpuinfo has neither, and the count
/// of logical processors is the last resort. The count never exceeds
/// <see cref="Environment.ProcessorCount"/>, which already honours the CPU
/// affinity and a container's CPU limit: more threads than the process may run
/// on would only queue.</para>
///
/// <para>The parsing is pure and tested; only the reading of the files is not.</para>
/// </summary>
public static class DecodingThreads
{
    /// <summary>
    /// Ceiling of the automatic count. The model is small: past a handful of
    /// threads, splitting the work costs more than it brings.
    /// </summary>
    internal const int AutomaticCeiling = 8;

    private const string CpuFolder = "/sys/devices/system/cpu";

    public static int Resolve(int setting) => Resolve(setting, PhysicalCores());

    public static int Resolve(int setting, int physicalCores) =>
        setting > 0 ? setting : Math.Clamp(physicalCores, 1, AutomaticCeiling);

    /// <summary>
    /// The processors listed by a sysfs CPU list such as
    /// <c>/sys/devices/system/cpu/online</c>: "0-3,6,8-9". Empty when the text
    /// is not such a list.
    /// </summary>
    public static IReadOnlyList<int> ParseCpuList(string? text)
    {
        List<int> processors = [];

        if (string.IsNullOrWhiteSpace(text))
        {
            return processors;
        }

        foreach (string part in text.Trim().Split(','))
        {
            string[] bounds = part.Split('-');

            if (bounds.Length is < 1 or > 2
                || !TryParseId(bounds[0], out int first)
                || !TryParseId(bounds[^1], out int last)
                || last < first)
            {
                return [];
            }

            for (int processor = first; processor <= last; processor++)
            {
                processors.Add(processor);
            }
        }

        return processors;
    }

    /// <summary>
    /// The number of distinct cores among processors described by the
    /// contents of their <c>physical_package_id</c>, <c>die_id</c> (null when
    /// the kernel has no such file, as older ones do not) and <c>core_id</c>
    /// files. Zero when any of them cannot be read or is not a plain number —
    /// some platforms write -1 for an unknown id: a partial or collapsed count
    /// would be taken for the truth.
    /// </summary>
    public static int CountCores(IEnumerable<(string? Package, string? Die, string? Core)> processors)
    {
        ArgumentNullException.ThrowIfNull(processors);

        HashSet<(int, int, int)> cores = [];

        foreach ((string? package, string? die, string? core) in processors)
        {
            int dieId = 0;

            if (!TryParseId(package, out int packageId)
                || (die is not null && !TryParseId(die, out dieId))
                || !TryParseId(core, out int coreId))
            {
                return 0;
            }

            cores.Add((packageId, dieId, coreId));
        }

        return cores.Count;
    }

    /// <summary>
    /// The number of distinct ("physical id", "core id") pairs in
    /// <c>/proc/cpuinfo</c>. Zero when no processor gives its core id, as on
    /// ARM.
    /// </summary>
    public static int CountCoresFromCpuInfo(string? cpuinfo)
    {
        if (string.IsNullOrEmpty(cpuinfo))
        {
            return 0;
        }

        HashSet<(int, int)> cores = [];
        int package = 0;
        int? core = null;

        foreach (string line in cpuinfo.Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);

            if (line.Trim().Length == 0)
            {
                // A blank line ends one processor's block.
                Close();
                continue;
            }

            if (colon < 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..];

            if (key == "processor")
            {
                // A new block without a blank line before it.
                Close();
            }
            else if (key == "physical id" && TryParseId(value, out int id))
            {
                package = id;
            }
            else if (key == "core id" && TryParseId(value, out id))
            {
                core = id;
            }
        }

        Close();
        return cores.Count;

        void Close()
        {
            if (core is { } found)
            {
                cores.Add((package, found));
            }

            package = 0;
            core = null;
        }
    }

    private static bool TryParseId(string? text, out int id) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id);

    [ExcludeFromCodeCoverage(Justification = "Reads sysfs and /proc; the counting is CountCores and CountCoresFromCpuInfo, which are tested.")]
    private static int PhysicalCores()
    {
        int logical = Environment.ProcessorCount;
        int cores = CoresFromSysfs();

        if (cores <= 0)
        {
            cores = CountCoresFromCpuInfo(ReadOrNull("/proc/cpuinfo"));
        }

        return cores > 0 ? Math.Min(cores, logical) : logical;
    }

    [ExcludeFromCodeCoverage(Justification = "Reads sysfs; the counting is CountCores, which is tested.")]
    private static int CoresFromSysfs()
    {
        IReadOnlyList<int> online = ParseCpuList(ReadOrNull(Path.Combine(CpuFolder, "online")));

        return online.Count == 0
            ? 0
            : CountCores(online.Select(processor =>
            {
                string topology = Path.Combine(CpuFolder, $"cpu{processor}", "topology");

                // A die_id that exists but cannot be read becomes "?", which
                // CountCores refuses, rather than null, which means "no dies".
                return (
                    ReadOrNull(Path.Combine(topology, "physical_package_id")),
                    File.Exists(Path.Combine(topology, "die_id")) ? ReadOrNull(Path.Combine(topology, "die_id")) ?? "?" : null,
                    ReadOrNull(Path.Combine(topology, "core_id")));
            }));
    }

    [ExcludeFromCodeCoverage(Justification = "File access; a missing or unreadable file reads as null, which the tested parsers turn into zero.")]
    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
