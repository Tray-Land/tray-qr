namespace TrayQr.Services;

/// <summary>What went wrong with the camera, in terms the user can act on.</summary>
public enum CameraProblem
{
    Unknown,
    AccessDenied,
    Disconnected,
    InUse,
    Unsupported,
}

/// <summary>A camera failure the scanner already classified.</summary>
public sealed class CameraException(CameraProblem problem, string detail) : Exception(detail)
{
    public CameraProblem Problem { get; } = problem;
}

/// <summary>
/// Turns camera failures into stable messages for the Camera tab. The platform's own error text
/// is diagnostic detail and isn't shown; it goes to <c>Trace</c> instead.
/// </summary>
public static class CameraProblems
{
    // From winerror.h and mferror.h.
    private const uint AccessDenied = 0x80070005;              // E_ACCESSDENIED
    private const uint DeviceNotConnected = 0x8007048F;        // HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED)
    private const uint DeviceInvalidated = 0xC00D3EA2;         // MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED
    private const uint NoCaptureDevices = 0xC00DABE0;          // MF_E_NO_CAPTURE_DEVICES_AVAILABLE
    private const uint DevicePreempted = 0xC00D3EA3;           // MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED
    private const uint HardwareFailedToStream = 0xC00D3704;    // MF_E_HW_MFT_FAILED_START_STREAMING

    public static CameraProblem FromHResult(int hresult) => unchecked((uint)hresult) switch
    {
        AccessDenied => CameraProblem.AccessDenied,
        DeviceNotConnected or DeviceInvalidated or NoCaptureDevices => CameraProblem.Disconnected,
        DevicePreempted or HardwareFailedToStream => CameraProblem.InUse,
        _ => CameraProblem.Unknown,
    };

    public static CameraProblem FromException(Exception exception) => exception switch
    {
        CameraException camera => camera.Problem,
        UnauthorizedAccessException => CameraProblem.AccessDenied,
        _ => FromHResult(exception.HResult),
    };

    public static string MessageFor(CameraProblem problem) => problem switch
    {
        CameraProblem.AccessDenied => "Camera access is off for Tray QR. Turn it on in privacy settings, then try again.",
        CameraProblem.Disconnected => "The camera was disconnected. Reconnect it, then try again.",
        CameraProblem.InUse => "Another app is using the camera. Close it, then try again.",
        CameraProblem.Unsupported => "This camera doesn't offer video Tray QR can scan. Try another camera.",
        _ => "The camera stopped working. Try again, or choose another camera.",
    };
}
