using System.Text.RegularExpressions;

namespace ResumeDownload.Core;

public static partial class CompletedPath
{
    public static string? RenameTarget(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var name = Path.GetFileName(path);
        if (name.EndsWith(".opdownload", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".opdownload".Length];
        }

        var match = DuplicateSuffix().Match(name);
        if (match.Success)
        {
            name = match.Groups["base"].Value;
        }

        var destination = Path.Combine(directory, name);
        if (string.Equals(
                Path.GetFullPath(destination),
                Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return destination;
    }

    [GeneratedRegex(@"^(?<base>.+?) \(\d+\)$")]
    private static partial Regex DuplicateSuffix();
}
