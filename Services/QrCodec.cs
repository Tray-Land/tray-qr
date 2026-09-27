using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace TrayQr.Services;

/// <summary>QR error-correction levels, lowest (densest data) to highest (most damage-tolerant).</summary>
public enum QrErrorCorrection
{
    Low,
    Medium,
    Quartile,
    High,
}

/// <summary>A QR code as a square grid of modules, including its quiet zone.</summary>
public sealed class QrMatrix(bool[] modules, int size)
{
    public int Size { get; } = size;

    public bool IsDark(int x, int y) => modules[(y * Size) + x];
}

/// <summary>
/// Encodes and decodes QR codes with ZXing. Works on plain BGRA byte arrays so it has no WinRT
/// dependency and can be tested on its own.
/// </summary>
public static class QrCodec
{
    private const int QuietZone = 2;

    /// <summary>Builds the module grid for <paramref name="text"/>, or null when it can't be encoded (too long).</summary>
    public static QrMatrix? Encode(string text, QrErrorCorrection level)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        Dictionary<EncodeHintType, object> hints = new()
        {
            [EncodeHintType.ERROR_CORRECTION] = ToZXing(level),
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
            [EncodeHintType.MARGIN] = QuietZone,
        };

        try
        {
            // Width/height 0 returns the minimum size: one pixel per module.
            BitMatrix bits = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, hints);
            int size = bits.Width;
            bool[] modules = new bool[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    modules[(y * size) + x] = bits[x, y];
                }
            }

            return new QrMatrix(modules, size);
        }
        catch (WriterException)
        {
            return null;
        }
    }

    /// <summary>Renders the matrix as BGRA8 pixels, <paramref name="scale"/> pixels per module.</summary>
    public static byte[] RenderBgra(QrMatrix matrix, int scale)
    {
        int width = matrix.Size * scale;
        byte[] pixels = new byte[width * width * 4];
        for (int y = 0; y < width; y++)
        {
            int row = y / scale;
            for (int x = 0; x < width; x++)
            {
                byte value = matrix.IsDark(x / scale, row) ? (byte)0 : (byte)255;
                int i = ((y * width) + x) * 4;
                pixels[i] = value;
                pixels[i + 1] = value;
                pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>The largest integer scale that keeps the rendered image at or under <paramref name="maxPixels"/> wide.</summary>
    public static int ScaleFor(QrMatrix matrix, int maxPixels) => Math.Max(1, maxPixels / matrix.Size);

    /// <summary>Finds every QR code (and other common barcodes) in a BGRA8 image.</summary>
    public static IReadOnlyList<string> Decode(byte[] bgra, int width, int height)
    {
        BarcodeReaderGeneric reader = new()
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats =
                [
                    BarcodeFormat.QR_CODE,
                    BarcodeFormat.DATA_MATRIX,
                    BarcodeFormat.AZTEC,
                    BarcodeFormat.PDF_417,
                    BarcodeFormat.EAN_13,
                    BarcodeFormat.UPC_A,
                    BarcodeFormat.CODE_128,
                ],
            },
        };

        RGBLuminanceSource source = new(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        Result[]? results = reader.DecodeMultiple(source);
        return results is null
            ? []
            : results.Select(r => r.Text).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
    }

    private static ErrorCorrectionLevel ToZXing(QrErrorCorrection level) => level switch
    {
        QrErrorCorrection.Low => ErrorCorrectionLevel.L,
        QrErrorCorrection.Quartile => ErrorCorrectionLevel.Q,
        QrErrorCorrection.High => ErrorCorrectionLevel.H,
        _ => ErrorCorrectionLevel.M,
    };
}
