using ResumeDownload.Core;

namespace ResumeDownload.Tests;

public class RangePolicyTests
{
    [Fact]
    public void FreshDownloadAccepts200()
    {
        RangePolicy.EnsureAccepted(0, 200, null);
    }

    [Fact]
    public void ResumeRequires206AtTheSameByte()
    {
        RangePolicy.EnsureAccepted(6_069_487_980, 206, 6_069_487_980);
    }

    [Fact]
    public void ResumeRejectsAFullRestart()
    {
        var error = Assert.Throws<DownloadRejectedException>(
            () => RangePolicy.EnsureAccepted(100, 200, null));
        Assert.Contains("did not resume", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResumeRejectsADifferentStart()
    {
        Assert.Throws<DownloadRejectedException>(
            () => RangePolicy.EnsureAccepted(100, 206, 0));
    }

    [Fact]
    public void ResumeRejectsAMissingStart()
    {
        Assert.Throws<DownloadRejectedException>(
            () => RangePolicy.EnsureAccepted(100, 206, null));
    }

    [Fact]
    public void RangeNotSatisfiableIsRejected()
    {
        Assert.Throws<DownloadRejectedException>(
            () => RangePolicy.EnsureAccepted(100, 416, null));
    }
}
