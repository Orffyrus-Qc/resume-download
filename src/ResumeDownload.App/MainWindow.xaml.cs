using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using ResumeDownload.Core;

namespace ResumeDownload.App;

public partial class MainWindow : Window
{
    private const string DefaultUrl =
        "https://huggingface.co/bartowski/mistralai_Devstral-Small-2-24B-Instruct-2512-GGUF/resolve/main/mistralai_Devstral-Small-2-24B-Instruct-2512-Q4_K_M.gguf";

    private const string DefaultHash =
        "bfd11c8679c6b81eb43763505465d7dcfa72e460ab1c220ecc235a3efadd7f7f";

    private readonly DownloadEngine _engine = new();
    private CancellationTokenSource? _pause;
    private bool _running;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        ShowExistingPartial();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save download as",
            Filter = "All files|*.*",
            OverwritePrompt = false,
            CheckFileExists = false,
            FileName = string.IsNullOrWhiteSpace(PathBox.Text)
                ? "download.bin"
                : Path.GetFileName(PathBox.Text)
        };
        var directory = Path.GetDirectoryName(PathBox.Text);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = dialog.FileName;
            ShowExistingPartial();
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        SaveSettings();
        _pause = new CancellationTokenSource();
        _running = true;
        StartButton.IsEnabled = false;
        PauseButton.IsEnabled = true;
        var progress = new Progress<DownloadUpdate>(Apply);
        var request = new DownloadRequest
        {
            Url = UrlBox.Text,
            FilePath = PathBox.Text,
            ExpectedSha256 = HashBox.Text
        };

        try
        {
            await _engine.RunAsync(request, progress, _pause.Token);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _running = false;
            StartButton.IsEnabled = true;
            PauseButton.IsEnabled = false;
            _pause.Dispose();
            _pause = null;
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _pause?.Cancel();
    }

    private void Apply(DownloadUpdate update)
    {
        var totalKnown = update.TotalBytes > 0;
        Progress.IsIndeterminate = update.Phase == DownloadPhase.Running && !totalKnown;
        if (totalKnown)
        {
            Progress.Value = Math.Clamp(update.ReceivedBytes * 100d / update.TotalBytes, 0, 100);
        }
        else if (update.Phase != DownloadPhase.Running)
        {
            Progress.Value = 0;
        }

        StatusText.Text = update.Message;
        var received = ByteText.Format(update.ReceivedBytes);
        var total = totalKnown ? ByteText.Format(update.TotalBytes) : "unknown size";
        var speed = update.BytesPerSecond >= 1
            ? $"{ByteText.Format((long)update.BytesPerSecond)}/s"
            : "";
        var eta = "";
        if (update.BytesPerSecond > 1 && totalKnown && update.ReceivedBytes < update.TotalBytes)
        {
            var seconds = (update.TotalBytes - update.ReceivedBytes) / update.BytesPerSecond;
            eta = TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss");
        }

        DetailText.Text = string.Join("   ", new[] { $"{received} / {total}", speed, eta }
            .Where(part => part.Length > 0));

        if (update.Phase is DownloadPhase.Paused or DownloadPhase.Failed or DownloadPhase.Completed)
        {
            StartButton.Content = update.Phase == DownloadPhase.Completed ? "Start" : "Resume";
        }
    }

    private void ShowExistingPartial()
    {
        if (!File.Exists(PathBox.Text))
        {
            return;
        }

        var length = new FileInfo(PathBox.Text).Length;
        if (length > 0)
        {
            DetailText.Text = $"Partial file is {ByteText.Format(length)}. Start continues from there.";
        }
    }

    private string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ResumeDownload",
            "settings.json");

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(SettingsPath));
                if (saved is not null)
                {
                    UrlBox.Text = saved.Url;
                    PathBox.Text = saved.FilePath;
                    HashBox.Text = saved.Sha256;
                    return;
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        var partial = Path.Combine(
            downloads,
            "mistralai_Devstral-Small-2-24B-Instruct-2512-Q4_K_M.gguf (1).opdownload");
        var finished = Path.Combine(
            downloads,
            "mistralai_Devstral-Small-2-24B-Instruct-2512-Q4_K_M.gguf");
        UrlBox.Text = DefaultUrl;
        HashBox.Text = DefaultHash;
        PathBox.Text = File.Exists(partial) ? partial : finished;
    }

    private void SaveSettings()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new SavedSettings(UrlBox.Text, PathBox.Text, HashBox.Text));
        File.WriteAllText(SettingsPath, json);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pause?.Cancel();
        _engine.Dispose();
        base.OnClosed(e);
    }

    private sealed record SavedSettings(string Url, string FilePath, string Sha256);
}
