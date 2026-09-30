using System.Diagnostics.CodeAnalysis;
using HexLinux.Diagnostics;
using HexLinux.Input;
using HexLinux.Interop;

namespace HexLinux.Output;

/// <summary>
/// HexLinux's own virtual keyboard, made through <c>/dev/uinput</c>, to press
/// the paste shortcut where no desktop tool can.
///
/// <para><b>Why a virtual keyboard.</b> GNOME and KDE under Wayland let no
/// application send keys to another, on purpose: xdotool only reaches X11
/// windows, and wtype needs a protocol neither implements. A keyboard made
/// through uinput sits below the desktop, next to the real ones, and every
/// compositor listens to it. The price is a permission — write access to
/// <c>/dev/uinput</c>, given by the optional udev rule — and physical keys
/// only: it can press Ctrl+V, not type text.</para>
///
/// <para><b>Created once, when the daemon starts, and kept.</b> The
/// compositor picks a new input device up through udev, which takes a moment;
/// a keyboard created for each paste would send its keys before anyone
/// listens. It declares only the keys it needs (see
/// <see cref="UinputAbi.DeclaredKeys"/>), is named so that HexLinux's own
/// reader ignores it (<see cref="InputDeviceCatalog.UinputDeviceName"/>), and
/// is destroyed with the daemon.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Needs /dev/uinput, which WSL does not have: verified on a real desktop (docs/testing.md). The bytes it writes are UinputAbi and KeySequences, which are tested.")]
public sealed class UinputKeyboard : IDisposable
{
    private const int EventKey = 1;
    private const int EventSync = 0;

    /// <summary>Between two key changes, so that the compositor sees each one on its own.</summary>
    private static readonly TimeSpan KeyGap = TimeSpan.FromMilliseconds(8);

    private readonly Lock _sync = new();
    private int _descriptor;

    private UinputKeyboard(int descriptor) => _descriptor = descriptor;

    /// <summary>Whether <c>/dev/uinput</c> could be opened for writing, without creating anything.</summary>
    public static DeviceAccess Probe() => DeviceFile.Probe(UinputAbi.DevicePath, Libc.WriteOnly | Libc.NonBlocking);

    /// <summary>Creates the keyboard, or returns null and says why.</summary>
    public static unsafe UinputKeyboard? TryCreate(out DeviceAccess access, out string? problem)
    {
        (int descriptor, access, int error) = DeviceFile.Open(UinputAbi.DevicePath, Libc.WriteOnly | Libc.NonBlocking);

        if (descriptor < 0)
        {
            problem = access switch
            {
                DeviceAccess.PermissionDenied => "/dev/uinput is not writable (scripts/install-udev-rules.sh --with-uinput)",
                DeviceAccess.Missing => "/dev/uinput does not exist (the uinput module is not loaded)",
                _ => $"/dev/uinput cannot be opened: {Libc.Describe(error)}",
            };

            return null;
        }

        byte[] setup = UinputAbi.BuildSetup(InputDeviceCatalog.UinputDeviceName, UinputAbi.BusVirtual, 0, 0, 1);
        bool created = Libc.IoctlInt(descriptor, UinputAbi.SetEventBit, EventKey) >= 0
            && Libc.IoctlInt(descriptor, UinputAbi.SetEventBit, EventSync) >= 0
            && UinputAbi.DeclaredKeys.All(key => Libc.IoctlInt(descriptor, UinputAbi.SetKeyBit, key) >= 0);

        if (created)
        {
            fixed (byte* data = setup)
            {
                created = Libc.IoctlPointer(descriptor, UinputAbi.DeviceSetup, data) >= 0
                    && Libc.IoctlNone(descriptor, UinputAbi.DeviceCreate) >= 0;
            }
        }

        if (!created)
        {
            int failure = Libc.LastError;
            Libc.Close(descriptor);
            access = DeviceAccess.Failed;
            problem = $"the virtual keyboard could not be created: {Libc.Describe(failure)}";
            return null;
        }

        problem = null;
        return new UinputKeyboard(descriptor);
    }

    /// <summary>
    /// Writes each group of events, a short pause between groups. Returns
    /// false when the kernel took less than was written.
    /// </summary>
    public unsafe bool Send(IReadOnlyList<InputEvent[]> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        lock (_sync)
        {
            if (_descriptor < 0)
            {
                return false;
            }

            foreach (InputEvent[] group in groups)
            {
                byte[] buffer = new byte[group.Length * InputEvent.Size];

                for (int index = 0; index < group.Length; index++)
                {
                    group[index].WriteTo(buffer.AsSpan(index * InputEvent.Size));
                }

                nint written;

                fixed (byte* data = buffer)
                {
                    written = Libc.Write(_descriptor, data, (nuint)buffer.Length);
                }

                if (written != buffer.Length)
                {
                    return false;
                }

                Thread.Sleep(KeyGap);
            }

            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_descriptor < 0)
            {
                return;
            }

            Libc.IoctlNone(_descriptor, UinputAbi.DeviceDestroy);
            Libc.Close(_descriptor);
            _descriptor = -1;
        }
    }
}
