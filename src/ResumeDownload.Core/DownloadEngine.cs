using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ResumeDownload.Core;

public sealed class DownloadEngine : IDisposable
{
    private const int BufferSize = 1024 * 1024;
    private const int FlushEvery = 8 * 1024 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(45);

    private readonly HttpClient _http;

    public DownloadEngine()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        _http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ResumeDownload/1.0");
    }

    public async Task RunAsync(
        DownloadRequest request,
        IProgress<DownloadUpdate> progress,
        CancellationToken pauseToken)
    {
        if (!Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            Report(progress, DownloadPhase.Failed, 0, 0, 0, "Enter an http or https link.");
            return;
        }

        string filePath;
        try
        {
            filePath = Path.GetFullPath(request.FilePath.Trim());
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory))
            {
                Report(progress, DownloadPhase.Failed, 0, 0, 0, "Choose a save path.");
                return;
            }

            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            Report(progress, DownloadPhase.Failed, 0, 0, 0, ex.Message);
            return;
        }

        var expectedHash = NormalizeHash(request.ExpectedSha256);
        long total = ReadSavedTotal(filePath);
        var delay = TimeSpan.FromSeconds(2);
        var speedStart = DateTime.UtcNow;
        long speedBytes = ExistingLength(filePath);

        while (!pauseToken.IsCancellationRequested)
        {
            var local = ExistingLength(filePath);
            if (total > 0 && local > total)
            {
                Report(progress, DownloadPhase.Failed, local, total, 0,
                    "The file is larger than the server's copy. It was left unchanged.");
                return;
            }

            if (total > 0 && local == total)
            {
                await FinishAsync(filePath, local, total, expectedHash, progress, pauseToken);
                return;
            }

            try
            {
                var done = await TransferOnceAsync(
                    uri,
                    filePath,
                    local,
                    total,
                    progress,
                    pauseToken,
                    (newTotal, received) =>
                    {
                        total = newTotal;
                        var elapsed = (DateTime.UtcNow - speedStart).TotalSeconds;
                        var speed = elapsed >= 0.4 ? (received - speedBytes) / elapsed : 0;
                        if (elapsed >= 1)
                        {
                            speedStart = DateTime.UtcNow;
                            speedBytes = received;
                        }

                        return speed;
                    });

                if (done)
                {
                    await FinishAsync(filePath, ExistingLength(filePath), total, expectedHash, progress, pauseToken);
                    return;
                }

                delay = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (pauseToken.IsCancellationRequested)
            {
                var received = ExistingLength(filePath);
                Report(progress, DownloadPhase.Paused, received, total, 0, "Paused. Start continues from this file.");
                return;
            }
            catch (DownloadRejectedException ex)
            {
                var received = ExistingLength(filePath);
                Report(progress, DownloadPhase.Failed, received, total, 0, ex.Message);
                return;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or TaskCanceledException)
            {
                var received = ExistingLength(filePath);
                Report(progress, DownloadPhase.Running, received, total, 0,
                    $"Connection dropped ({ex.Message}). Retrying in {delay.TotalSeconds:0}s.");
                try
                {
                    await Task.Delay(delay, pauseToken);
                }
                catch (OperationCanceledException) when (pauseToken.IsCancellationRequested)
                {
                    Report(progress, DownloadPhase.Paused, received, total, 0, "Paused. Start continues from this file.");
                    return;
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }
    }

    private async Task<bool> TransferOnceAsync(
        Uri uri,
        string filePath,
        long local,
        long knownTotal,
        IProgress<DownloadUpdate> progress,
        CancellationToken pauseToken,
        Func<long, long, double> measureSpeed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (local > 0)
        {
            request.Headers.Range = new RangeHeaderValue(local, null);
        }

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            pauseToken);

        var status = (int)response.StatusCode;
        var range = response.Content.Headers.ContentRange;
        RangePolicy.EnsureAccepted(local, status, range?.From);

        var total = range?.Length
            ?? (local == 0 ? response.Content.Headers.ContentLength : null)
            ?? (knownTotal > 0 ? knownTotal : (long?)null)
            ?? 0;
        if (total > 0)
        {
            SaveTotal(filePath, uri, total);
        }

        await using var network = await response.Content.ReadAsStreamAsync(pauseToken);
        await using var file = new FileStream(
            filePath,
            local == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous);

        var buffer = new byte[BufferSize];
        long received = local;
        var sinceFlush = 0;
        var nextReport = DateTime.UtcNow;

        Report(progress, DownloadPhase.Running, received, total, 0,
            local > 0 ? $"Resuming from {ByteText.Format(local)}." : "Starting download.");

        while (true)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(pauseToken);
            readCts.CancelAfter(ReadTimeout);
            int read;
            try
            {
                read = await network.ReadAsync(buffer, readCts.Token);
            }
            catch (OperationCanceledException) when (!pauseToken.IsCancellationRequested)
            {
                throw new TimeoutException("No data for 45 seconds.");
            }

            if (read == 0)
            {
                break;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), pauseToken);
            received += read;
            sinceFlush += read;
            if (sinceFlush >= FlushEvery)
            {
                await file.FlushAsync(pauseToken);
                sinceFlush = 0;
            }

            if (DateTime.UtcNow >= nextReport)
            {
                var speed = measureSpeed(total, received);
                Report(progress, DownloadPhase.Running, received, total, speed, "Downloading.");
                nextReport = DateTime.UtcNow.AddMilliseconds(250);
            }

            if (total > 0 && received >= total)
            {
                await file.FlushAsync(pauseToken);
                return true;
            }
        }

        await file.FlushAsync(pauseToken);
        return total > 0 && received >= total;
    }

    private static async Task FinishAsync(
        string filePath,
        long received,
        long total,
        string? expectedHash,
        IProgress<DownloadUpdate> progress,
        CancellationToken pauseToken)
    {
        if (!string.IsNullOrEmpty(expectedHash))
        {
            Report(progress, DownloadPhase.Running, received, total, 0, "Checking SHA256.");
            var actual = await HashFileAsync(filePath, pauseToken);
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Report(progress, DownloadPhase.Failed, received, total, 0,
                    $"SHA256 does not match. File hash is {actual}.");
                return;
            }
        }

        var finalPath = filePath;
        var rename = CompletedPath.RenameTarget(filePath);
        if (rename is not null && !File.Exists(rename))
        {
            File.Move(filePath, rename);
            finalPath = rename;
            var sidecar = filePath + ".resume.json";
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }

        Report(progress, DownloadPhase.Completed, received, total, 0, $"Finished. {finalPath}");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static long ExistingLength(string path)
    {
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    private static long ReadSavedTotal(string path)
    {
        var sidecar = path + ".resume.json";
        if (!File.Exists(sidecar))
        {
            return 0;
        }

        try
        {
            var state = JsonSerializer.Deserialize<SavedTotal>(File.ReadAllText(sidecar));
            return state?.TotalBytes ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static void SaveTotal(string path, Uri uri, long total)
    {
        var json = JsonSerializer.Serialize(new SavedTotal(uri.AbsoluteUri, total));
        File.WriteAllText(path + ".resume.json", json);
    }

    private static string? NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant();
    }

    private static void Report(
        IProgress<DownloadUpdate> progress,
        DownloadPhase phase,
        long received,
        long total,
        double bytesPerSecond,
        string message)
    {
        progress.Report(new DownloadUpdate(phase, received, total, bytesPerSecond, message));
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private sealed record SavedTotal(string Url, long TotalBytes);
}
