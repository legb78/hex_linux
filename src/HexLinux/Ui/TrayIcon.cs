using HexLinux.Daemon;

namespace HexLinux.Ui;

/// <summary>
/// One image of the tray icon, in the shape the StatusNotifierItem protocol
/// carries it: a width, a height and the pixels.
/// </summary>
/// <param name="Width">In pixels.</param>
/// <param name="Height">In pixels.</param>
/// <param name="Argb32">
/// Four bytes per pixel, row after row from the top left: alpha, red, green,
/// blue. See <see cref="TrayIcon"/> for why in that order.
/// </param>
public sealed record IconPixmap(int Width, int Height, byte[] Argb32);

/// <summary>
/// Draws the tray icon of each state: a coloured disc, crossed in white when
/// the model could not be loaded — HexWin's icons, pixel for pixel in spirit.
///
/// <para>Drawn by the program rather than shipped as image files, as HexWin
/// does: they are coloured dots, and the protocol wants raw pixels anyway. The
/// drawing is plain arithmetic, so the tests can read single pixels back
/// without a graphics library.</para>
///
/// <para><b>Byte order.</b> The StatusNotifierItem specification (its "Icons"
/// section) transfers icons as <c>a(iiay)</c> — width, height, image data —
/// with the data "represented in ARGB32 format and in the network byte
/// order". Network order is big-endian, so every pixel is the four bytes
/// alpha, red, green, blue, in that order. It is what KDE's own implementation
/// sends: it converts each <c>QImage::Format_ARGB32</c> pixel to big-endian
/// (kstatusnotifieritem.cpp, <c>imageToStruct</c>). That Qt format is
/// straight alpha, so the colour is not premultiplied here either: an edge
/// pixel keeps the full colour and only its alpha falls.</para>
///
/// <para><b>Sizes.</b> Several resolutions travel in the one property and the
/// host picks the closest: 16, 22 and 24 pixels for ordinary panels, 32, 48
/// and 64 for scaled or large ones. A host that must resize a disc blurs it;
/// one that finds its size exact does not.</para>
/// </summary>
public static class TrayIcon
{
    /// <summary>Every size produced, smallest first.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [16, 22, 24, 32, 48, 64];

    /// <summary>
    /// Samples per pixel along each axis. Sixteen samples per pixel give the
    /// edge seventeen levels of transparency, plenty for a disc this small.
    /// </summary>
    private const int Subsamples = 4;

    private const int SamplesPerPixel = Subsamples * Subsamples;

    /// <summary>The icon of <paramref name="state"/> at every size of <see cref="Sizes"/>.</summary>
    public static IReadOnlyList<IconPixmap> RenderAll(DictationState state) =>
        [.. Sizes.Select(size => Render(state, size))];

    /// <summary>
    /// The icon of <paramref name="state"/>, <paramref name="size"/> pixels
    /// square.
    ///
    /// <para>Proportions taken from HexWin's 32-pixel drawing: a 2-pixel
    /// margin around the disc, a cross whose arms reach 4.5 pixels from the
    /// centre along each axis, drawn with a 4-pixel pen. They are scaled with
    /// the size, so that every resolution shows the same picture.</para>
    /// </summary>
    public static IconPixmap Render(DictationState state, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        Rgb fill = StatePalette.For(state);
        Rgb cross = StatePalette.Cross;
        bool crossed = state == DictationState.Failed;

        double centre = size / 2.0;
        double radius = centre - size / 16.0;
        double arm = size * 4.5 / 32.0;
        double halfPen = size / 16.0;

        byte[] pixels = new byte[size * size * 4];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int inDisc = 0;
                int inCross = 0;

                for (int sy = 0; sy < Subsamples; sy++)
                {
                    for (int sx = 0; sx < Subsamples; sx++)
                    {
                        double dx = x + (sx + 0.5) / Subsamples - centre;
                        double dy = y + (sy + 0.5) / Subsamples - centre;

                        if (dx * dx + dy * dy > radius * radius)
                        {
                            continue;
                        }

                        inDisc++;

                        if (crossed && (OnStroke(dx, dy, arm, arm, halfPen) || OnStroke(dx, dy, arm, -arm, halfPen)))
                        {
                            inCross++;
                        }
                    }
                }

                if (inDisc == 0)
                {
                    // Outside the disc: fully transparent, which the array
                    // already is.
                    continue;
                }

                int plain = inDisc - inCross;
                int offset = (y * size + x) * 4;

                pixels[offset] = (byte)((255 * inDisc + SamplesPerPixel / 2) / SamplesPerPixel);
                pixels[offset + 1] = Mix(fill.R, cross.R, plain, inCross);
                pixels[offset + 2] = Mix(fill.G, cross.G, plain, inCross);
                pixels[offset + 3] = Mix(fill.B, cross.B, plain, inCross);
            }
        }

        return new IconPixmap(size, size, pixels);
    }

    /// <summary>
    /// True when the point lies on the stroke from (-endX, -endY) to (endX,
    /// endY), a segment through the centre drawn with a flat-ended pen, as
    /// GDI+ draws HexWin's cross.
    /// </summary>
    private static bool OnStroke(double dx, double dy, double endX, double endY, double halfPen)
    {
        // Position along the segment, from -1 at one end to +1 at the other,
        // then distance from its line.
        double lengthSquared = endX * endX + endY * endY;
        double along = (dx * endX + dy * endY) / lengthSquared;

        if (along is < -1 or > 1)
        {
            return false;
        }

        double across = Math.Abs(dx * endY - dy * endX) / Math.Sqrt(lengthSquared);

        return across <= halfPen;
    }

    /// <summary>
    /// Colour of a pixel covered partly by the disc and partly by the cross,
    /// weighted by the samples of each; never premultiplied by the alpha.
    /// </summary>
    private static byte Mix(byte fill, byte cross, int fillSamples, int crossSamples)
    {
        int total = fillSamples + crossSamples;

        return (byte)((fill * fillSamples + cross * crossSamples + total / 2) / total);
    }
}
