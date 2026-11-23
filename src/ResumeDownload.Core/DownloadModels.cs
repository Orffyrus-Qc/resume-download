namespace ResumeDownload.Core;

public enum DownloadPhase
{
    Idle,
    Running,
    Paused,
    Completed,
    Failed
}

public sealed record DownloadUpdate(
    DownloadPhase Phase,
    long ReceivedBytes,
    long TotalBytes,
    double BytesPerSecond,
    string Message);

public sealed class DownloadRequest
{
    public required string Url { get; init; }
    public required string FilePath { get; init; }
    public string? ExpectedSha256 { get; init; }
}

public sealed class DownloadRejectedException : Exception
{
    public DownloadRejectedException(string message) : base(message)
    {
    }
}
