using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TrayQr.Services;

/// <summary>Moves QR pixels between <see cref="QrCodec"/>'s BGRA arrays and WinRT images, files, and the clipboard.</summary>
internal static class QrImageService
{
    /// <summary>Images larger than this are scaled down before decoding; QR detection doesn't need more.</summary>
    private const uint MaxDecodeSize = 2048;

    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"];

    public static WriteableBitmap ToBitmap(byte[] bgra, int size)
    {
        WriteableBitmap bitmap = new(size, size);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, bgra.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    public static async Task<InMemoryRandomAccessStream> EncodePngAsync(byte[] bgra, int size)
    {
        InMemoryRandomAccessStream stream = new();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)size, (uint)size, 96, 96, bgra);
        await encoder.FlushAsync();
        stream.Seek(0);
        return stream;
    }

    public static async Task SavePngAsync(byte[] bgra, int size, StorageFile file)
    {
        using InMemoryRandomAccessStream png = await EncodePngAsync(bgra, size);
        using IRandomAccessStream output = await file.OpenAsync(FileAccessMode.ReadWrite);
        output.Size = 0;
        await RandomAccessStream.CopyAndCloseAsync(png.GetInputStreamAt(0), output.GetOutputStreamAt(0));
    }

    /// <summary>
    /// Writes the PNG to a fresh file in the app's temp folder for drag-and-drop, which needs a real
    /// file for Explorer. Earlier drag files are cleared first, so at most one drag's worth is kept.
    /// </summary>
    public static async Task<StorageFile> SaveDragPngAsync(byte[] bgra, int size, string fileName)
    {
        StorageFolder temp = ApplicationData.Current.TemporaryFolder;
        StorageFolder folder;
        try
        {
            folder = await temp.CreateFolderAsync("Drag", CreationCollisionOption.ReplaceExisting);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A drop target may still hold the previous file open; reuse the folder then.
            folder = await temp.CreateFolderAsync("Drag", CreationCollisionOption.OpenIfExists);
        }

        StorageFile file = await folder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
        await SavePngAsync(bgra, size, file);
        return file;
    }

    public static async Task CopyPngAsync(byte[] bgra, int size)
    {
        InMemoryRandomAccessStream png = await EncodePngAsync(bgra, size);
        DataPackage package = new() { RequestedOperation = DataPackageOperation.Copy };
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(png));
        Clipboard.SetContent(package);
    }

    /// <summary>Decodes an image stream to BGRA8, scaled down to <see cref="MaxDecodeSize"/> if needed.</summary>
    public static async Task<(byte[] Pixels, int Width, int Height)> LoadBgraAsync(IRandomAccessStream stream)
    {
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        uint width = decoder.OrientedPixelWidth;
        uint height = decoder.OrientedPixelHeight;
        double factor = Math.Min(1.0, (double)MaxDecodeSize / Math.Max(width, height));

        BitmapTransform transform = new()
        {
            ScaledWidth = Math.Max(1, (uint)(width * factor)),
            ScaledHeight = Math.Max(1, (uint)(height * factor)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        PixelDataProvider data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        byte[] pixels = data.DetachPixelData();
        FlattenOntoWhite(pixels);
        return (pixels, (int)transform.ScaledWidth, (int)transform.ScaledHeight);
    }

    public static bool IsImageFile(IStorageItem item) =>
        item is StorageFile file && ImageExtensions.Contains(file.FileType, StringComparer.OrdinalIgnoreCase);

    // A transparent PNG of a black QR code decodes as all-black otherwise.
    private static void FlattenOntoWhite(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int alpha = bgra[i + 3];
            if (alpha == 255)
            {
                continue;
            }

            for (int c = 0; c < 3; c++)
            {
                bgra[i + c] = (byte)(((bgra[i + c] * alpha) + (255 * (255 - alpha))) / 255);
            }

            bgra[i + 3] = 255;
        }
    }
}
