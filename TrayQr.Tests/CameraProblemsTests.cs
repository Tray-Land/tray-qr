using System.Runtime.InteropServices;
using TrayQr.Services;

namespace TrayQr.Tests;

public class CameraProblemsTests
{
    [Theory]
    [InlineData(0x80070005u, CameraProblem.AccessDenied)]
    [InlineData(0xC00D3EA2u, CameraProblem.Disconnected)]
    [InlineData(0xC00DABE0u, CameraProblem.Disconnected)]
    [InlineData(0x8007048Fu, CameraProblem.Disconnected)]
    [InlineData(0xC00D3EA3u, CameraProblem.InUse)]
    [InlineData(0xC00D3704u, CameraProblem.InUse)]
    [InlineData(0x80004005u, CameraProblem.Unknown)]
    public void FromHResult_KnownCodes_MapToProblem(uint hresult, CameraProblem expected)
    {
        Assert.Equal(expected, CameraProblems.FromHResult(unchecked((int)hresult)));
    }

    [Fact]
    public void FromException_ClassifiedCameraException_KeepsItsProblem()
    {
        Assert.Equal(CameraProblem.Unsupported, CameraProblems.FromException(new CameraException(CameraProblem.Unsupported, "detail")));
    }

    [Fact]
    public void FromException_UnauthorizedAccess_IsAccessDenied()
    {
        Assert.Equal(CameraProblem.AccessDenied, CameraProblems.FromException(new UnauthorizedAccessException()));
    }

    [Fact]
    public void FromException_ComExceptionFromCamera_UsesItsHResult()
    {
        COMException preempted = new("platform text", unchecked((int)0xC00D3EA3));

        Assert.Equal(CameraProblem.InUse, CameraProblems.FromException(preempted));
    }

    [Fact]
    public void MessageFor_EveryProblem_HasItsOwnMessage()
    {
        string[] messages = Enum.GetValues<CameraProblem>().Select(CameraProblems.MessageFor).ToArray();

        Assert.All(messages, m => Assert.False(string.IsNullOrWhiteSpace(m)));
        Assert.Equal(messages.Length, messages.Distinct().Count());
    }
}
