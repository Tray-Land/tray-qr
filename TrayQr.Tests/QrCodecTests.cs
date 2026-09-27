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
}
