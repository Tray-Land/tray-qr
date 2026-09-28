using TrayQr.Services;

namespace TrayQr.Tests;

public class QrCodecTests
{
    [Theory]
    [InlineData("https://github.com/TheJoeFin/Simple-QR-Code-Maker", QrErrorCorrection.Low)]
    [InlineData("Hello, world!", QrErrorCorrection.Medium)]
    [InlineData("Ünïcödé ✓ 二维码", QrErrorCorrection.Quartile)]
    [InlineData("WIFI:T:WPA;S:MyNetwork;P:secret;;", QrErrorCorrection.High)]
    public void RoundTripsThroughRenderedPixels(string text, QrErrorCorrection level)
    {
        QrMatrix matrix = QrCodec.Encode(text, level)!;
        int scale = QrCodec.ScaleFor(matrix, 400);

        byte[] pixels = QrCodec.RenderBgra(matrix, scale);
        IReadOnlyList<string> decoded = QrCodec.Decode(pixels, matrix.Size * scale, matrix.Size * scale);

        Assert.Equal([text], decoded);
    }

    [Fact]
    public void HigherErrorCorrectionMakesALargerCode()
    {
        const string text = "https://example.com/some/fairly/long/path";
        Assert.True(QrCodec.Encode(text, QrErrorCorrection.High)!.Size > QrCodec.Encode(text, QrErrorCorrection.Low)!.Size);
    }

    [Fact]
    public void EmptyOrTooLongTextHasNoCode()
    {
        Assert.Null(QrCodec.Encode("", QrErrorCorrection.Medium));
        Assert.Null(QrCodec.Encode(new string('x', 5000), QrErrorCorrection.Medium));
    }

    [Fact]
    public void BlankImageDecodesToNothing()
    {
        byte[] white = Enumerable.Repeat((byte)255, 100 * 100 * 4).ToArray();
        Assert.Empty(QrCodec.Decode(white, 100, 100));
    }

    [Fact]
    public void ScaleKeepsImageWithinLimit()
    {
        QrMatrix matrix = QrCodec.Encode("abc", QrErrorCorrection.Medium)!;
        int scale = QrCodec.ScaleFor(matrix, 512);
        Assert.InRange(matrix.Size * scale, 512 - matrix.Size, 512);
    }

    [Fact]
    public void RenderBgra_CustomColors_UsesThemAndStillDecodes()
    {
        const string text = "colored";
        QrColor navy = new(0x1F, 0x2A, 0x44);
        QrColor cream = new(0xFF, 0xF8, 0xE7);
        QrMatrix matrix = QrCodec.Encode(text, QrErrorCorrection.Medium)!;
        int scale = QrCodec.ScaleFor(matrix, 400);

        byte[] pixels = QrCodec.RenderBgra(matrix, scale, navy, cream);

        // The top-left pixel is in the quiet zone, so it's the background, stored as BGRA.
        Assert.Equal([cream.B, cream.G, cream.R, 255], pixels[..4]);
        Assert.Equal([text], QrCodec.Decode(pixels, matrix.Size * scale, matrix.Size * scale));
    }

    [Theory]
    [InlineData("#1F2A44", 0x1F, 0x2A, 0x44)]
    [InlineData("ff8000", 0xFF, 0x80, 0x00)]
    public void FromHex_ValidHex_ParsesAndRoundTrips(string hex, byte r, byte g, byte b)
    {
        QrColor color = QrColor.FromHex(hex, QrColor.White);

        Assert.Equal(new QrColor(r, g, b), color);
        Assert.Equal(color, QrColor.FromHex(color.ToHex(), QrColor.White));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void FromHex_Invalid_ReturnsFallback(string? hex)
    {
        Assert.Equal(QrColor.Black, QrColor.FromHex(hex, QrColor.Black));
    }

    [Fact]
    public void ContrastRatio_KnownPairs_MatchWcag()
    {
        Assert.Equal(21, QrColor.ContrastRatio(QrColor.Black, QrColor.White), 1);
        Assert.Equal(1, QrColor.ContrastRatio(QrColor.White, QrColor.White), 3);
        Assert.True(QrColor.ContrastRatio(new QrColor(200, 200, 200), QrColor.White) < QrCodec.MinReadableContrast);
    }
}
