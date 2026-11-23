using ResumeDownload.Core;

namespace ResumeDownload.Tests;

public class CompletedPathTests
{
    [Fact]
    public void OperaPartialBecomesTheRealGgufName()
    {
        var path = @"C:\Users\Orffyrus\Downloads\mistralai_Devstral-Small-2-24B-Instruct-2512-Q4_K_M.gguf (1).opdownload";
        var target = CompletedPath.RenameTarget(path);
        Assert.Equal(
            @"C:\Users\Orffyrus\Downloads\mistralai_Devstral-Small-2-24B-Instruct-2512-Q4_K_M.gguf",
            target);
    }

    [Fact]
    public void PlainGgufStaysInPlace()
    {
        Assert.Null(CompletedPath.RenameTarget(@"C:\Downloads\model.gguf"));
    }
}
