using System.Diagnostics.CodeAnalysis;
using HexLinux.Diagnostics;
using HexLinux.Interop;

namespace HexLinux.Input;

/// <summary>
/// Reads the keyboards in <c>/dev/input</c>, passively, and hands every event
/// on — the reading half of the hold-to-talk shortcut.
///
/// <para><b>Observed, never grabbed.</b> Devices are opened read-only and
/// never with <c>EVIOCGRAB</c>: grabbing would take the keyboard away from the
/// desktop, which would then have to be fed every keystroke through a virtual
/// keyboard — putting HexLinux on the path of everything the user types, for
/// the sake of one key. The keys of the shortcut therefore still reach the
/// desktop (see <see cref="ChordDetector"/>).</para>
///
/// <para><b>One thread per keyboard, and it only reads.</b> Each thread waits
/// on its device with <c>poll</c> — with a short timeout, so that it notices
/// when it is asked to stop — reads whole events and passes them on. Every
/// decision is taken by the caller, on the daemon's loop
/// (<see cref="HotkeyTracker"/>), in the order the events were posted. Nothing
/// read is ever logged: the letters typed pass through here too.</para>
///
/// <para><b>Hot-plug.</b> The device list is read again every few seconds:
/// a keyboard plugged in is picked up, one that disappears makes its thread
/// end and report it, so that the keys it held are forgotten. A keyboard that
/// cannot be opened — no permission — is reported once, never fatal: the
/// control socket still works without it. While the session is inactive (the
/// screen belongs to another user), every keyboard is closed, and opened
/// again when the session comes back.</para>
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Reads /dev/input, which WSL does not have (CONFIG_INPUT_EVDEV unset): verified by --watch-hotkey on a real desktop.")]
public sealed class EvdevKeyboard : IDisposable
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(3);
    private const int PollTimeoutMilliseconds = 250;
    private const int EventsPerRead = 64;

    private readonly IReadOnlySet<int> _codes;
    private readonly Action<string, InputEvent> _onEvent;
    private readonly Action<string> _onDeviceGone;
    private readonly Action<string> _log;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, Reader> _readers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    private Timer? _rescan;
    private bool _suspended;
    private bool _disposed;
    private bool _noKeyboardReported;

    /// <param name="codes">A device is listened to when it can send one of these.</param>
    /// <param name="onEvent">Every key event and resynchronisation marker, on the reader thread.</param>
    /// <param name="onDeviceGone">A keyboard disappeared, on its reader thread.</param>
    /// <param name="log">One line per problem worth knowing, never per key.</param>
    public EvdevKeyboard(IReadOnlySet<int> codes, Action<string, InputEvent> onEvent, Action<string> onDeviceGone, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(onEvent);
        ArgumentNullException.ThrowIfNull(onDeviceGone);
        ArgumentNullException.ThrowIfNull(log);

        _codes = codes;
        _onEvent = onEvent;
        _onDeviceGone = onDeviceGone;
        _log = log;
    }

    /// <summary>The keyboards currently open, as "name (path)".</summary>
    public IReadOnlyList<string> OpenKeyboards
    {
        get
        {
            lock (_sync)
            {
                return [.. _readers.Values.Select(reader => $"{reader.Name} ({reader.Path})")];
            }
        }
    }

    /// <summary>Opens what can be opened now, then keeps looking every few seconds.</summary>
    public void Start()
    {
        Rescan();
        _rescan = new Timer(_ => Rescan(), null, RescanInterval, RescanInterval);
    }

    /// <summary>Closes every keyboard: the session is no longer the one in front of the screen.</summary>
    public void Suspend()
    {
        Reader[] readers;

        lock (_sync)
        {
            if (_suspended)
            {
                return;
            }

            _suspended = true;
            readers = [.. _readers.Values];
            _readers.Clear();
        }

        foreach (Reader reader in readers)
        {
            reader.Stop();
        }

        _log("session inactive: keyboards closed until it is back in front");
    }

    /// <summary>Opens the keyboards again.</summary>
    public void Resume()
    {
        lock (_sync)
        {
            if (!_suspended)
            {
                return;
            }

            _suspended = false;
        }

        _log("session active again: keyboards reopened");
        Rescan();
    }

    private void Rescan()
    {
        string text;

        try
        {
            text = File.ReadAllText(InputDeviceCatalog.SourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportOnce(InputDeviceCatalog.SourcePath, $"the input devices cannot be listed ({ex.Message}): the shortcut is unavailable");
            return;
        }

        IReadOnlyList<InputDevice> keyboards = InputDeviceCatalog.Keyboards(InputDeviceCatalog.Parse(text), _codes);

        lock (_sync)
        {
            if (_suspended || _disposed)
            {
                return;
            }

            if (keyboards.Count == 0 && _readers.Count == 0 && !_noKeyboardReported)
            {
                _noKeyboardReported = true;
                _log("no keyboard able to send the shortcut was found: hold-to-talk is unavailable, hexlinux --toggle still works");
            }

            foreach (InputDevice keyboard in keyboards)
            {
                string path = keyboard.DevicePath!;

                if (_readers.ContainsKey(path))
                {
                    continue;
                }

                (int descriptor, DeviceAccess access, int error) = DeviceFile.Open(path, Libc.ReadOnly | Libc.NonBlocking);

                if (descriptor < 0)
                {
                    if (access == DeviceAccess.PermissionDenied)
                    {
                        ReportOnce(path, $"{keyboard.Name} ({path}) cannot be read: permission denied. Run scripts/install-udev-rules.sh, or use hexlinux --toggle");
                    }
                    else if (access == DeviceAccess.Failed)
                    {
                        ReportOnce(path, $"{keyboard.Name} ({path}) cannot be opened: {Libc.Describe(error)}");
                    }

                    continue;
                }

                var reader = new Reader(this, path, keyboard.Name, descriptor);
                _readers[path] = reader;
                _reported.Remove(path);
                reader.Start();
                _log($"listening to {keyboard.Name} ({path})");
            }
        }
    }

    private void ReportOnce(string key, string message)
    {
        lock (_sync)
        {
            if (!_reported.Add(key))
            {
                return;
            }
        }

        _log(message);
    }

    /// <summary>A reader ended on its own: the device is gone.</summary>
    private void OnReaderEnded(Reader reader)
    {
        lock (_sync)
        {
            if (!_readers.TryGetValue(reader.Path, out Reader? current) || !ReferenceEquals(current, reader))
            {
                return;
            }

            _readers.Remove(reader.Path);
        }

        _log($"{reader.Name} ({reader.Path}) disappeared");
        _onDeviceGone(reader.Path);
    }

    public void Dispose()
    {
        Reader[] readers;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            readers = [.. _readers.Values];
            _readers.Clear();
        }

        _rescan?.Dispose();

        foreach (Reader reader in readers)
        {
            reader.Stop();
        }
    }

    /// <summary>One keyboard, one thread.</summary>
    private sealed class Reader(EvdevKeyboard owner, string path, string name, int descriptor)
    {
        private volatile bool _stopping;

        public string Path { get; } = path;

        public string Name { get; } = name;

        public void Start()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "hexlinux-evdev" };
            thread.Start();
        }

        /// <summary>Asks the thread to stop; it closes the device within one poll timeout.</summary>
        public void Stop() => _stopping = true;

        private unsafe void Run()
        {
            byte[] buffer = new byte[InputEvent.Size * EventsPerRead];
            bool resynchronising = false;
            bool gone = false;

            try
            {
                while (!_stopping)
                {
                    var poll = new Libc.PollDescriptor { Descriptor = descriptor, Events = Libc.PollIn };
                    int ready = Libc.Poll(ref poll, 1, PollTimeoutMilliseconds);

                    if (ready < 0)
                    {
                        if (Libc.LastError == Libc.EINTR)
                        {
                            continue;
                        }

                        gone = true;
                        break;
                    }

                    if (ready == 0)
                    {
                        continue;
                    }

                    if ((poll.ReturnedEvents & (Libc.PollError | Libc.PollHangUp | Libc.PollInvalid)) != 0)
                    {
                        gone = true;
                        break;
                    }

                    nint read;

                    fixed (byte* data = buffer)
                    {
                        read = Libc.Read(descriptor, data, (nuint)buffer.Length);
                    }

                    if (read < 0)
                    {
                        int error = Libc.LastError;

                        if (error is Libc.EAGAIN or Libc.EINTR)
                        {
                            continue;
                        }

                        // ENODEV: the keyboard was unplugged.
                        gone = true;
                        break;
                    }

                    if (read == 0)
                    {
                        gone = true;
                        break;
                    }

                    for (int offset = 0; offset + InputEvent.Size <= read; offset += InputEvent.Size)
                    {
                        if (!InputEvent.TryRead(buffer.AsSpan(offset, InputEvent.Size), out InputEvent inputEvent))
                        {
                            continue;
                        }

                        // Only what the tracker decides on is passed on: key
                        // events, a drop, and the report that ends a drop.
                        if (inputEvent.IsSyncDropped)
                        {
                            resynchronising = true;
                        }
                        else if (inputEvent.IsSyncReport && resynchronising)
                        {
                            resynchronising = false;
                        }
                        else if (!inputEvent.IsKey)
                        {
                            continue;
                        }

                        owner._onEvent(Path, inputEvent);
                    }
                }
            }
            finally
            {
                Libc.Close(descriptor);
            }

            if (gone && !_stopping)
            {
                owner.OnReaderEnded(this);
            }
        }
    }
}
