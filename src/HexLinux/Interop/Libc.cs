using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace HexLinux.Interop;

/// <summary>
/// The few C library calls .NET does not wrap: raw file descriptors for
/// <c>/dev/input</c> and <c>/dev/uinput</c>, <c>ioctl</c>, <c>poll</c> and
/// <c>flock</c>.
///
/// <para>Every constant was checked by compiling a program against the headers
/// of the build machine (glibc and Linux UAPI headers of Ubuntu 24.04,
/// x86-64): <c>O_RDONLY=0 O_WRONLY=1 O_RDWR=2 O_CREAT=64 O_NONBLOCK=2048
/// O_CLOEXEC=524288</c>; <c>POLLIN=1 POLLERR=8 POLLHUP=16 POLLNVAL=32</c>,
/// <c>sizeof(struct pollfd)=8</c>; <c>LOCK_EX=2 LOCK_NB=4</c>;
/// <c>EINTR=4 EAGAIN=11 EACCES=13 ENOENT=2 ENODEV=19 EPERM=1</c>.</para>
///
/// <para><b>Every descriptor is opened with <c>O_CLOEXEC</c>.</b> The
/// clipboard tools HexLinux starts stay alive after it, to serve the
/// clipboard; a descriptor they inherited would outlive the daemon — the lock
/// file among them, and the next daemon would then believe one is still
/// running.</para>
///
/// <para><b>No string ever comes back as <c>string</c>.</b> The generated
/// marshaller frees a returned string, and <c>strerror</c>'s is static: the
/// first error would abort the process (verified: exit code 134). A pointer
/// comes back, copied by <see cref="Marshal.PtrToStringUTF8(nint)"/>.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "P/Invoke declarations: only callable against real devices, verified by --doctor and the smoke tests.")]
internal static partial class Libc
{
    private const string Library = "libc";

    public const int ReadOnly = 0;
    public const int WriteOnly = 1;
    public const int ReadWrite = 2;
    public const int Create = 64;
    public const int NonBlocking = 2048;
    public const int CloseOnExec = 524288;

    public const short PollIn = 1;
    public const short PollError = 8;
    public const short PollHangUp = 16;
    public const short PollInvalid = 32;

    public const int LockExclusive = 2;
    public const int LockNonBlocking = 4;

    public const int EPERM = 1;
    public const int ENOENT = 2;
    public const int EINTR = 4;
    public const int EAGAIN = 11;
    public const int EACCES = 13;
    public const int ENODEV = 19;

    /// <summary><c>struct pollfd</c>: 8 bytes, fd at 0, events at 4, revents at 6.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PollDescriptor
    {
        public int Descriptor;
        public short Events;
        public short ReturnedEvents;
    }

    /// <summary>
    /// <c>open</c> is variadic; its third argument, the mode, is passed on
    /// every call — read only with <see cref="Create"/> — which is how the
    /// x86-64 calling convention passes a variadic integer anyway.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags, uint mode);

    [LibraryImport(Library, EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int descriptor);

    [LibraryImport(Library, EntryPoint = "read", SetLastError = true)]
    public static unsafe partial nint Read(int descriptor, byte* buffer, nuint count);

    [LibraryImport(Library, EntryPoint = "write", SetLastError = true)]
    public static unsafe partial nint Write(int descriptor, byte* buffer, nuint count);

    /// <summary><c>ioctl</c> with an integer argument; the request is C's <c>unsigned long</c>.</summary>
    [LibraryImport(Library, EntryPoint = "ioctl", SetLastError = true)]
    public static partial int IoctlInt(int descriptor, nuint request, int argument);

    /// <summary><c>ioctl</c> with a pointer to a structure.</summary>
    [LibraryImport(Library, EntryPoint = "ioctl", SetLastError = true)]
    public static unsafe partial int IoctlPointer(int descriptor, nuint request, byte* argument);

    /// <summary><c>ioctl</c> with no argument.</summary>
    [LibraryImport(Library, EntryPoint = "ioctl", SetLastError = true)]
    public static partial int IoctlNone(int descriptor, nuint request);

    [LibraryImport(Library, EntryPoint = "poll", SetLastError = true)]
    public static partial int Poll(ref PollDescriptor descriptor, nuint count, int timeoutMilliseconds);

    [LibraryImport(Library, EntryPoint = "flock", SetLastError = true)]
    public static partial int Flock(int descriptor, int operation);

    [LibraryImport(Library, EntryPoint = "getuid")]
    public static partial uint GetUid();

    [LibraryImport(Library, EntryPoint = "geteuid")]
    public static partial uint GetEffectiveUid();

    /// <summary><c>AT_FDCWD</c>: a relative path is taken from the working directory.</summary>
    private const int AtCurrentDirectory = -100;

    /// <summary><c>STATX_UID</c>, in both the request and the returned mask.</summary>
    private const uint StatxUid = 8;

    /// <summary>
    /// <c>sizeof(struct statx)</c>, and where its fields are: <c>stx_mask</c>
    /// at 0, <c>stx_uid</c> at 20 (verified by compiling against the headers
    /// of glibc 2.39, x86-64). <c>statx</c> rather than <c>stat</c>: its layout
    /// is fixed by the kernel's UAPI, where <c>struct stat</c> differs between
    /// architectures, and glibc has had it since 2.28.
    /// </summary>
    private const int StatxSize = 256;
    private const int StatxUidOffset = 20;

    [LibraryImport(Library, EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int Statx(int directory, string path, int flags, uint mask, byte* buffer);

    /// <summary>The uid owning <paramref name="path"/>, or null when it cannot be read.</summary>
    public static unsafe uint? OwnerOf(string path)
    {
        byte* buffer = stackalloc byte[StatxSize];

        if (Statx(AtCurrentDirectory, path, 0, StatxUid, buffer) != 0 || (*(uint*)buffer & StatxUid) == 0)
        {
            return null;
        }

        return *(uint*)(buffer + StatxUidOffset);
    }

    [LibraryImport(Library, EntryPoint = "strerror")]
    private static partial nint StrErrorPointer(int error);

    /// <summary>The error of the last call made with <c>SetLastError</c>.</summary>
    public static int LastError => Marshal.GetLastPInvokeError();

    /// <summary>The C library's message for an error number.</summary>
    public static string Describe(int error) => Marshal.PtrToStringUTF8(StrErrorPointer(error)) ?? $"error {error}";
}
