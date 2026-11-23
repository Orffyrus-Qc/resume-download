namespace ResumeDownload.Core;

public static class RangePolicy
{
    public static void EnsureAccepted(long localBytes, int statusCode, long? rangeStart)
    {
        if (statusCode == 416)
        {
            throw new DownloadRejectedException(
                "The server refused the resume range. The partial file was left unchanged.");
        }

        if (localBytes <= 0)
        {
            if (statusCode is 200 or 206)
            {
                return;
            }

            throw new DownloadRejectedException(
                $"The server returned status {statusCode}. Nothing was written.");
        }

        if (statusCode != 206)
        {
            throw new DownloadRejectedException(
                "The server did not resume. The partial file was left unchanged.");
        }

        if (rangeStart is null)
        {
            throw new DownloadRejectedException(
                "The server resumed without saying where. The partial file was left unchanged.");
        }

        if (rangeStart.Value != localBytes)
        {
            throw new DownloadRejectedException(
                $"The server resumed at byte {rangeStart.Value}, but the file is {localBytes} bytes. The partial file was left unchanged.");
        }
    }
}
