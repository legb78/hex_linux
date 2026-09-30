using HexLinux.Configuration;
using HexLinux.Input;

namespace HexLinux.Daemon;

/// <summary>
/// Builds what the status surface shows from the daemon's own state.
///
/// <para>Pure, and in one place, so that the tray and <c>--status</c> can
/// never describe the same moment in two ways: the daemon builds a snapshot
/// after every change and pushes it only when it differs from the last one
/// (the record's equality).</para>
/// </summary>
public static class StatusSnapshots
{
    public static StatusSnapshot Build(DictationState state, AppSettings settings, bool tonesEnabled, bool autoStartEnabled, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);

        return new StatusSnapshot(
            state,
            HotkeyText.Describe(settings.Hotkey),
            tonesEnabled,
            autoStartEnabled,
            paths.SettingsFile,
            paths.StateDirectory);
    }
}
