using HexLinux.Daemon;

namespace HexLinux.Ui;

/// <summary>A colour with no transparency, as the palette hands it out.</summary>
/// <param name="R">Red, 0 to 255.</param>
/// <param name="G">Green, 0 to 255.</param>
/// <param name="B">Blue, 0 to 255.</param>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// One colour per state, for the tray icon.
///
/// <para>The very values of HexWin's palette, so that someone who uses both
/// applications reads the same signal on either system: grey while the model
/// loads, blue when ready, red while recording, orange while transcribing, dark
/// grey crossed in white when the model could not be loaded.</para>
///
/// <para>The hues are deliberately distinct rather than shades of one another:
/// the icon has to stay legible at sixteen pixels across, over a panel that may
/// be light or dark.</para>
/// </summary>
public static class StatePalette
{
    public static Rgb For(DictationState state) => state switch
    {
        DictationState.Loading => new Rgb(134, 142, 150),
        DictationState.Idle => new Rgb(25, 113, 194),
        DictationState.Recording => new Rgb(224, 49, 49),
        DictationState.Transcribing => new Rgb(232, 89, 12),
        _ => new Rgb(64, 64, 64),
    };

    /// <summary>The cross drawn over the failed state: white, as HexWin's.</summary>
    public static Rgb Cross { get; } = new(255, 255, 255);
}
