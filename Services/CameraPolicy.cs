namespace TrayQr.Services;

/// <summary>One video format a camera offers, independent of WinRT so it can be tested.</summary>
public readonly record struct VideoFormat(int Width, int Height, double FramesPerSecond);

/// <summary>Decides which camera and which video format the Camera tab uses.</summary>
public static class CameraPolicy
{
    // QR detection doesn't need more than 720p, and smaller frames decode faster.
    private const int TargetWidth = 1280;
    private const int TargetHeight = 720;
    private const int MaxWidth = 1920;
    private const int MaxHeight = 1080;
    private const double MinFramesPerSecond = 15;

    /// <summary>
    /// Returns the index of the format closest to 720p, preferring smooth frame rates and never
    /// going above 1080p when something smaller exists; returns null when there are no formats.
    /// </summary>
    public static int? ChooseFormat(IReadOnlyList<VideoFormat> formats)
    {
        if (formats.Count == 0)
        {
            return null;
        }

        return Enumerable.Range(0, formats.Count)
            .OrderBy(i => formats[i].Width > MaxWidth || formats[i].Height > MaxHeight)
            .ThenBy(i => formats[i].FramesPerSecond < MinFramesPerSecond)
            .ThenBy(i => Math.Abs(formats[i].Width - TargetWidth) + Math.Abs(formats[i].Height - TargetHeight))
            .ThenByDescending(i => formats[i].FramesPerSecond)
            .First();
    }

    /// <summary>Returns the saved camera if it's still connected, otherwise the first one; returns null when there are none.</summary>
    public static string? PickCamera(IReadOnlyList<string> ids, string? savedId) =>
        savedId is not null && ids.Contains(savedId) ? savedId : ids.FirstOrDefault();
}
