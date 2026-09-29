using System.Diagnostics.CodeAnalysis;
using HexLinux.Diagnostics;

namespace HexLinux.Interop;

/// <summary>
/// Opens a device node and says, in the doctor's terms, what happened.
///
/// <para>The one reliable permission check is the open itself: udev's rules,
/// ACLs and groups all end up there, and a check that reasoned about them
/// instead would get some combination wrong. So <c>--doctor</c> opens every
/// keyboard it reports on, and closes it at once.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Opens real device nodes, verified by --doctor.")]
internal static class DeviceFile
{
    /// <summary>Opens <paramref name="path"/>; the descriptor is -1 unless access is <see cref="DeviceAccess.Accessible"/>.</summary>
    public static (int Descriptor, DeviceAccess Access, int Error) Open(string path, int flags)
    {
        int descriptor = Libc.Open(path, flags | Libc.CloseOnExec, 0);

        if (descriptor >= 0)
        {
            return (descriptor, DeviceAccess.Accessible, 0);
        }

        int error = Libc.LastError;

        DeviceAccess access = error switch
        {
            Libc.EACCES or Libc.EPERM => DeviceAccess.PermissionDenied,
            Libc.ENOENT or Libc.ENODEV => DeviceAccess.Missing,
            _ => DeviceAccess.Failed,
        };

        return (-1, access, error);
    }

    /// <summary>Opens and closes at once: only the permission is of interest.</summary>
    public static DeviceAccess Probe(string path, int flags)
    {
        (int descriptor, DeviceAccess access, _) = Open(path, flags);

        if (descriptor >= 0)
        {
            Libc.Close(descriptor);
        }

        return access;
    }
}
