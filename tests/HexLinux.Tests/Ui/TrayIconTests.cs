using HexLinux.Daemon;
using HexLinux.Ui;
using Xunit;

namespace HexLinux.Tests.Ui;

/// <summary>
/// The icons are read back pixel by pixel: the byte order, the transparency
/// and the cross are exactly what a tray host decodes, and a mistake in any of
/// them shows as a wrong colour on somebody's panel, not as an error.
/// </summary>
public class TrayIconTests
{
    private static (byte A, byte R, byte G, byte B) Pixel(IconPixmap icon, int x, int y)
    {
        int offset = (y * icon.Width + x) * 4;

        return (icon.Argb32[offset], icon.Argb32[offset + 1], icon.Argb32[offset + 2], icon.Argb32[offset + 3]);
    }

    [Fact]
    public void The_sizes_produced_are_fixed_smallest_first()
    {
        // Hosts pick the closest size: 16, 22 and 24 for ordinary panels,
        // 32 to 64 for scaled ones. A size missing here is a blurred icon there.
        Assert.Equal([16, 22, 24, 32, 48, 64], TrayIcon.Sizes);
        Assert.Equal(TrayIcon.Sizes, TrayIcon.RenderAll(DictationState.Idle).Select(icon => icon.Width));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(22)]
    [InlineData(32)]
    [InlineData(64)]
    public void The_buffer_holds_four_bytes_per_pixel(int size)
    {
        // a(iiay): the host reads width × height × 4 bytes. A shorter buffer is
        // refused by the host, or read past its end.
        IconPixmap icon = TrayIcon.Render(DictationState.Recording, size);

        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
        Assert.Equal(size * size * 4, icon.Argb32.Length);
    }

    [Theory]
    [InlineData(DictationState.Loading)]
    [InlineData(DictationState.Idle)]
    [InlineData(DictationState.Recording)]
    [InlineData(DictationState.Transcribing)]
    public void The_centre_is_the_state_colour_alpha_byte_first(DictationState state)
    {
        // Network byte order ARGB32: alpha, red, green, blue. A buffer written
        // in the machine's little-endian order would reach the host as BGRA,
        // and the red recording dot would show up blue.
        Rgb colour = StatePalette.For(state);

        Assert.Equal(((byte)255, colour.R, colour.G, colour.B), Pixel(TrayIcon.Render(state, 32), 16, 16));
    }

    [Theory]
    [InlineData(DictationState.Loading)]
    [InlineData(DictationState.Idle)]
    [InlineData(DictationState.Recording)]
    [InlineData(DictationState.Transcribing)]
    [InlineData(DictationState.Failed)]
    public void The_corners_are_transparent(DictationState state)
    {
        // A disc, not a square: the panel's own colour must show around it,
        // light or dark.
        foreach (IconPixmap icon in TrayIcon.RenderAll(state))
        {
            int last = icon.Width - 1;

            Assert.Equal(0, Pixel(icon, 0, 0).A);
            Assert.Equal(0, Pixel(icon, last, 0).A);
            Assert.Equal(0, Pixel(icon, 0, last).A);
            Assert.Equal(0, Pixel(icon, last, last).A);
        }
    }

    [Fact]
    public void The_failed_icon_is_crossed_in_white_on_both_diagonals()
    {
        // Dark grey alone could pass for "loading" at sixteen pixels; the
        // cross is what says the model is missing, as on HexWin's icon.
        IconPixmap icon = TrayIcon.Render(DictationState.Failed, 32);
        var white = ((byte)255, (byte)255, (byte)255, (byte)255);

        for (int step = 0; step < 4; step++)
        {
            Assert.Equal(white, Pixel(icon, 16 + step, 16 + step));
            Assert.Equal(white, Pixel(icon, 15 - step, 15 - step));
            Assert.Equal(white, Pixel(icon, 16 + step, 15 - step));
            Assert.Equal(white, Pixel(icon, 15 - step, 16 + step));
        }
    }

    [Fact]
    public void The_failed_icon_keeps_its_colour_off_the_cross()
    {
        // Above the centre, between the two arms: the disc's own grey.
        Rgb colour = StatePalette.For(DictationState.Failed);

        Assert.Equal(((byte)255, colour.R, colour.G, colour.B), Pixel(TrayIcon.Render(DictationState.Failed, 32), 16, 6));
    }

    [Fact]
    public void Only_the_failed_icon_is_crossed()
    {
        // The recording dot must stay a plain disc: a white mark on it would
        // read as a failure in the middle of a dictation.
        IconPixmap icon = TrayIcon.Render(DictationState.Recording, 32);
        Rgb colour = StatePalette.For(DictationState.Recording);

        Assert.Equal(((byte)255, colour.R, colour.G, colour.B), Pixel(icon, 18, 18));
        Assert.Equal(((byte)255, colour.R, colour.G, colour.B), Pixel(icon, 13, 18));
    }

    [Fact]
    public void The_edge_is_smoothed_with_straight_alpha()
    {
        // Straight alpha is what hosts expect (KDE sends QImage's ARGB32, not
        // its premultiplied variant): an edge pixel keeps the full colour and
        // only its alpha falls. Premultiplied, the edge would darken into a
        // dark ring around the disc.
        IconPixmap icon = TrayIcon.Render(DictationState.Idle, 32);
        Rgb colour = StatePalette.For(DictationState.Idle);

        var edge = new List<(byte A, byte R, byte G, byte B)>();

        for (int y = 0; y < icon.Height; y++)
        {
            for (int x = 0; x < icon.Width; x++)
            {
                var pixel = Pixel(icon, x, y);

                if (pixel.A is > 0 and < 255)
                {
                    edge.Add(pixel);
                }
            }
        }

        Assert.NotEmpty(edge);
        Assert.All(edge, pixel => Assert.Equal((colour.R, colour.G, colour.B), (pixel.R, pixel.G, pixel.B)));
    }

    [Fact]
    public void Every_state_looks_different()
    {
        // Loading, ready, recording, transcribing and failed are five
        // different signals; two equal images would merge two of them.
        IReadOnlyList<byte[]> images = [.. Enum.GetValues<DictationState>().Select(state => TrayIcon.Render(state, 22).Argb32)];

        for (int i = 0; i < images.Count; i++)
        {
            for (int j = i + 1; j < images.Count; j++)
            {
                Assert.NotEqual(images[i], images[j]);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-8)]
    public void A_size_below_one_pixel_is_refused(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayIcon.Render(DictationState.Idle, size));
    }

    // --- Palette --------------------------------------------------------------------

    [Fact]
    public void The_palette_is_the_one_of_hexwin()
    {
        // Someone who uses both applications must read the same colours.
        Assert.Equal(new Rgb(134, 142, 150), StatePalette.For(DictationState.Loading));
        Assert.Equal(new Rgb(25, 113, 194), StatePalette.For(DictationState.Idle));
        Assert.Equal(new Rgb(224, 49, 49), StatePalette.For(DictationState.Recording));
        Assert.Equal(new Rgb(232, 89, 12), StatePalette.For(DictationState.Transcribing));
        Assert.Equal(new Rgb(64, 64, 64), StatePalette.For(DictationState.Failed));
        Assert.Equal(new Rgb(255, 255, 255), StatePalette.Cross);
    }

    [Fact]
    public void A_state_without_a_colour_of_its_own_looks_failed()
    {
        // A state added later and forgotten here must not show up as "ready".
        Assert.Equal(StatePalette.For(DictationState.Failed), StatePalette.For((DictationState)42));
    }
}
