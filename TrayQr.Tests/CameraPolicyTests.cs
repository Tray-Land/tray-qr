using TrayQr.Services;

namespace TrayQr.Tests;

public class CameraPolicyTests
{
    [Fact]
    public void ChooseFormat_MixedSizes_Prefers720p()
    {
        VideoFormat[] formats = [new(3840, 2160, 30), new(640, 480, 30), new(1280, 720, 30), new(1920, 1080, 30)];

        Assert.Equal(2, CameraPolicy.ChooseFormat(formats));
    }

    [Fact]
    public void ChooseFormat_SameSize_PrefersHigherFrameRate()
    {
        VideoFormat[] formats = [new(1280, 720, 15), new(1280, 720, 60), new(1280, 720, 30)];

        Assert.Equal(1, CameraPolicy.ChooseFormat(formats));
    }

    [Fact]
    public void ChooseFormat_SameWidthAndFrameRate_Prefers720High()
    {
        VideoFormat[] formats = [new(1280, 2160, 30), new(1280, 960, 30), new(1280, 720, 30)];

        Assert.Equal(2, CameraPolicy.ChooseFormat(formats));
    }

    [Fact]
    public void ChooseFormat_SmallerExists_NeverPicksAbove1080p()
    {
        VideoFormat[] formats = [new(2560, 1440, 30), new(640, 360, 30)];

        Assert.Equal(1, CameraPolicy.ChooseFormat(formats));
    }

    [Fact]
    public void ChooseFormat_SlowCloserFormat_PrefersSmoothOne()
    {
        VideoFormat[] formats = [new(1280, 720, 5), new(640, 480, 30)];

        Assert.Equal(1, CameraPolicy.ChooseFormat(formats));
    }

    [Fact]
    public void ChooseFormat_NoFormats_ReturnsNull()
    {
        Assert.Null(CameraPolicy.ChooseFormat([]));
    }

    [Fact]
    public void PickCamera_SavedStillConnected_ReturnsSaved()
    {
        Assert.Equal("usb", CameraPolicy.PickCamera(["built-in", "usb"], "usb"));
    }

    [Theory]
    [InlineData("unplugged")]
    [InlineData(null)]
    public void PickCamera_SavedMissing_ReturnsFirst(string? savedId)
    {
        Assert.Equal("built-in", CameraPolicy.PickCamera(["built-in", "usb"], savedId));
    }

    [Fact]
    public void PickCamera_NoCameras_ReturnsNull()
    {
        Assert.Null(CameraPolicy.PickCamera([], "usb"));
    }
}
