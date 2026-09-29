using HexLinux.Daemon;

namespace HexLinux.Ui;

/// <summary>
/// Everything the tray shows for one snapshot, computed once: the host reads
/// it piecemeal, property after property, and must never mix two moments.
/// </summary>
/// <param name="Snapshot">The daemon's snapshot this view was made from.</param>
/// <param name="ToolTip">The tooltip line, see <see cref="TrayText.ToolTip"/>.</param>
/// <param name="Status">The StatusNotifierItem status, see <see cref="TrayPresentation.StatusFor"/>.</param>
/// <param name="Menu">The menu entries, see <see cref="TrayMenu.Build"/>.</param>
public sealed record TrayView(
    StatusSnapshot Snapshot,
    string ToolTip,
    string Status,
    IReadOnlyList<TrayMenuItem> Menu)
{
    /// <summary>The state, which alone decides the icon.</summary>
    public DictationState State => Snapshot.State;
}

/// <summary>What moved between two views, hence which signals the host must receive.</summary>
[Flags]
public enum TrayChanges
{
    None = 0,

    /// <summary>The state, hence the icon: <c>NewIcon</c>.</summary>
    Icon = 1,

    /// <summary><c>NewToolTip</c>.</summary>
    ToolTip = 2,

    /// <summary><c>NewStatus</c>.</summary>
    Status = 4,

    /// <summary>At least one menu entry: <c>ItemsPropertiesUpdated</c>.</summary>
    Menu = 8,
}

/// <summary>
/// Turns the daemon's snapshots into what the tray shows, and tells which
/// parts changed.
///
/// <para>A StatusNotifierItem host does not poll: it reads the properties when
/// it first sees the item, then only when a signal says that one of them
/// changed. Missing a signal leaves a stale icon on screen; sending one for
/// nothing makes the host redraw and, on some panels, flicker. Deciding which
/// signals to send is therefore worth a pure, tested function.</para>
/// </summary>
public static class TrayPresentation
{
    /// <summary>The usual status: the item is shown.</summary>
    public const string Active = "Active";

    /// <summary>
    /// The status of the failed state. The specification reserves it for
    /// information that calls for the user's intervention, which a model that
    /// could not be loaded does: until <c>get-model.sh</c> has run, nothing
    /// can be dictated. Hosts that hide idle icons in an overflow area bring
    /// such an item back into view.
    /// </summary>
    public const string NeedsAttention = "NeedsAttention";

    /// <summary>
    /// What the tray shows before the daemon's first snapshot: loading, with
    /// nothing to open and nothing checked yet.
    /// </summary>
    public static StatusSnapshot InitialSnapshot { get; } =
        new(DictationState.Loading, string.Empty, TonesEnabled: false, AutoStartEnabled: false, string.Empty, string.Empty);

    public static TrayView From(StatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new TrayView(
            snapshot,
            TrayText.ToolTip(snapshot.State, snapshot.HotkeyDescription),
            StatusFor(snapshot.State),
            TrayMenu.Build(snapshot));
    }

    /// <summary>The StatusNotifierItem status of a state.</summary>
    public static string StatusFor(DictationState state) =>
        state == DictationState.Failed ? NeedsAttention : Active;

    /// <summary>The parts of the tray that differ between two views.</summary>
    public static TrayChanges Compare(TrayView before, TrayView after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        TrayChanges changes = TrayChanges.None;

        if (before.State != after.State)
        {
            changes |= TrayChanges.Icon;
        }

        if (before.ToolTip != after.ToolTip)
        {
            changes |= TrayChanges.ToolTip;
        }

        if (before.Status != after.Status)
        {
            changes |= TrayChanges.Status;
        }

        if (!before.Menu.SequenceEqual(after.Menu))
        {
            changes |= TrayChanges.Menu;
        }

        return changes;
    }
}
