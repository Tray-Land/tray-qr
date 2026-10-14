using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;

namespace TrayQr.Services;

/// <summary>
/// Streams one camera into a <see cref="MediaPlayerElement"/> and looks for codes in its frames.
/// Raises <see cref="CodesFound"/> once, on the first frame with a code, and then stops decoding.
/// The owner must dispose it as soon as the preview isn't visible, which also turns the camera off.
/// </summary>
internal sealed class CameraScanner : IAsyncDisposable
{
    // Decoding runs off the UI thread; frames that arrive while one is decoding are dropped.
    private const long DecodeIntervalMs = 150;

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private MediaSource? _mediaSource;
    private MediaPlayer? _player;
    private MediaPlayerElement? _preview;
    private int _isDecoding;
    private long _lastDecodeMs;

    /// <summary>Raised off the UI thread with the decoded texts and the frame they were found in.</summary>
    public event Action<IReadOnlyList<string>, SoftwareBitmap>? CodesFound;

    /// <summary>Raised off the UI thread when the camera stops working, e.g. it was unplugged.</summary>
    public event Action<string>? Failed;

    public static async Task<IReadOnlyList<DeviceInformation>> FindCamerasAsync() =>
        await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

    /// <summary>Starts the camera. Throws <see cref="UnauthorizedAccessException"/> when camera access is off.</summary>
    public async Task StartAsync(string deviceId, MediaPlayerElement preview)
    {
        // Exclusive control lets us pick a decode-friendly format; if another app has the camera, share its stream.
        bool isExclusive = true;
        try
        {
            _capture = await InitializeAsync(deviceId, MediaCaptureSharingMode.ExclusiveControl);
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException)
        {
            isExclusive = false;
            _capture = await InitializeAsync(deviceId, MediaCaptureSharingMode.SharedReadOnly);
        }

        _capture.Failed += Capture_Failed;

        MediaFrameSource source = FindColorSource(_capture)
            ?? throw new InvalidOperationException("This camera doesn't offer a color video stream.");
        if (isExclusive)
        {
            await TrySetFormatAsync(source);
        }

        _mediaSource = MediaSource.CreateFromMediaFrameSource(source);
        _player = new MediaPlayer { RealTimePlayback = true, Source = _mediaSource };
        _preview = preview;
        preview.SetMediaPlayer(_player);
        _player.Play();

        _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += Reader_FrameArrived;
        MediaFrameReaderStartStatus status = await _reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            throw new InvalidOperationException($"The camera didn't start ({status}).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_reader is not null)
        {
            _reader.FrameArrived -= Reader_FrameArrived;
            try
            {
                await _reader.StopAsync();
            }
            catch (Exception)
            {
                // Already stopped, e.g. the camera was unplugged.
            }

            _reader.Dispose();
            _reader = null;
        }

        if (_preview is not null && _preview.MediaPlayer == _player)
        {
            _preview.SetMediaPlayer(null);
        }

        _preview = null;
        _player?.Dispose();
        _player = null;
        _mediaSource?.Dispose();
        _mediaSource = null;

        if (_capture is not null)
        {
            _capture.Failed -= Capture_Failed;
            _capture.Dispose();
            _capture = null;
        }
    }

    private static async Task<MediaCapture> InitializeAsync(string deviceId, MediaCaptureSharingMode sharingMode)
    {
        MediaCapture capture = new();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = sharingMode,
            });
            return capture;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    // Some cameras have a separate preview stream; otherwise the record stream works too.
    private static MediaFrameSource? FindColorSource(MediaCapture capture)
    {
        List<MediaFrameSource> color = capture.FrameSources.Values.Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color).ToList();
        return color.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
            ?? color.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord);
    }

    private static async Task TrySetFormatAsync(MediaFrameSource source)
    {
        List<MediaFrameFormat> formats = source.SupportedFormats.Where(f => f.VideoFormat is not null).ToList();
        int? index = CameraPolicy.ChooseFormat(formats.Select(f => new VideoFormat(
            (int)f.VideoFormat.Width,
            (int)f.VideoFormat.Height,
            f.FrameRate.Denominator == 0 ? 0 : (double)f.FrameRate.Numerator / f.FrameRate.Denominator)).ToList());
        if (index is not int i)
        {
            return;
        }

        try
        {
            await source.SetFormatAsync(formats[i]);
        }
        catch (Exception)
        {
            // The camera's current format still works, just with slower decoding.
        }
    }

    private void Capture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs args) => Failed?.Invoke(args.Message);

    private void Reader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastDecodeMs) < DecodeIntervalMs || Interlocked.Exchange(ref _isDecoding, 1) == 1)
        {
            return;
        }

        bool isFound = false;
        try
        {
            using MediaFrameReference? frame = sender.TryAcquireLatestFrame();
            if (frame?.VideoMediaFrame?.SoftwareBitmap is not { } bitmap)
            {
                return;
            }

            using SoftwareBitmap bgra = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8);
            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;
            byte[] pixels = new byte[width * height * 4];
            bgra.CopyToBuffer(pixels.AsBuffer());

            // Camera frames often leave alpha at 0, which the decoder would read as all white.
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            IReadOnlyList<string> codes = QrCodec.Decode(pixels, width, height);
            if (codes.Count > 0)
            {
                isFound = true;
                SoftwareBitmap snapshot = SoftwareBitmap.CreateCopyFromBuffer(
                    pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
                CodesFound?.Invoke(codes, snapshot);
            }
        }
        catch (Exception)
        {
            // A frame that can't be read is skipped; the next one gets its turn.
        }
        finally
        {
            Interlocked.Exchange(ref _lastDecodeMs, Environment.TickCount64);

            // After a hit the flag stays set, so no further frames are decoded.
            if (!isFound)
            {
                Interlocked.Exchange(ref _isDecoding, 0);
            }
        }
    }
}
