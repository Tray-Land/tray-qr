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

/// <summary>An opaque RGB color for rendering QR codes, independent of WinRT so it can be tested.</summary>
public readonly record struct QrColor(byte R, byte G, byte B)
{
    public static readonly QrColor Black = new(0, 0, 0);
    public static readonly QrColor White = new(255, 255, 255);

    /// <summary>Formats as <c>#RRGGBB</c>.</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Parses <c>#RRGGBB</c> (the <c>#</c> is optional), or returns <paramref name="fallback"/>.</summary>
    public static QrColor FromHex(string? hex, QrColor fallback)
    {
        string digits = hex?.Trim().TrimStart('#') ?? string.Empty;
        return digits.Length == 6 && uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out uint rgb)
            ? new QrColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : fallback;
    }

    /// <summary>WCAG contrast ratio between two colors, from 1 (identical) to 21 (black on white).</summary>
    public static double ContrastRatio(QrColor a, QrColor b)
    {
        double la = a.RelativeLuminance();
        double lb = b.RelativeLuminance();
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private double RelativeLuminance() => (0.2126 * Linear(R)) + (0.7152 * Linear(G)) + (0.0722 * Linear(B));

    private static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
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

    /// <summary>Codes below this contrast ratio are hard for many scanners to read.</summary>
    public const double MinReadableContrast = 3.0;

    /// <summary>Renders the matrix as black-on-white BGRA8 pixels, <paramref name="scale"/> pixels per module.</summary>
    public static byte[] RenderBgra(QrMatrix matrix, int scale) => RenderBgra(matrix, scale, QrColor.Black, QrColor.White);

    /// <summary>Renders the matrix as BGRA8 pixels, <paramref name="scale"/> pixels per module.</summary>
    public static byte[] RenderBgra(QrMatrix matrix, int scale, QrColor foreground, QrColor background)
    {
        int width = matrix.Size * scale;
        byte[] pixels = new byte[width * width * 4];
        for (int y = 0; y < width; y++)
        {
            int row = y / scale;
            for (int x = 0; x < width; x++)
            {
                QrColor color = matrix.IsDark(x / scale, row) ? foreground : background;
                int i = ((y * width) + x) * 4;
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
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
