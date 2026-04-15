using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FamilyCoordinationApp.Models.YouTube;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Executes yt-dlp as a subprocess to extract video metadata and transcripts from YouTube URLs.
/// </summary>
public sealed class YtDlpService : IYtDlpService
{
    private readonly IProcessExecutor _processExecutor;
    private readonly ILogger<YtDlpService> _logger;

    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(30);

    public YtDlpService(ILogger<YtDlpService> logger)
        : this(new DefaultProcessExecutor(), logger) { }

    internal YtDlpService(IProcessExecutor processExecutor, ILogger<YtDlpService> logger)
    {
        _processExecutor = processExecutor;
        _logger = logger;
    }

    public async Task<YouTubeVideoData?> ExtractVideoDataAsync(string youtubeUrl, CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ytdlp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using var timeoutCts = new CancellationTokenSource(ExtractionTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var outputTemplate = Path.Combine(tempDir, "%(id)s");
            var args = BuildArguments(youtubeUrl, outputTemplate);

            var (exitCode, stdOut, stdErr) = await _processExecutor.ExecuteAsync("yt-dlp", args, linkedCts.Token);

            if (exitCode != 0)
            {
                _logger.LogWarning("yt-dlp exited with code {ExitCode} for {Url}: {StdErr}", exitCode, youtubeUrl, stdErr);
                return null;
            }

            YtDlpMetadata? metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<YtDlpMetadata>(stdOut);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse yt-dlp JSON output for {Url}", youtubeUrl);
                return null;
            }

            if (metadata?.Id is null || metadata.Title is null)
            {
                _logger.LogWarning("yt-dlp returned incomplete metadata for {Url}", youtubeUrl);
                return null;
            }

            string? transcript = null;
            var subtitlePath = Path.Combine(tempDir, $"{metadata.Id}.en.srv1");
            if (File.Exists(subtitlePath))
            {
                try
                {
                    var xmlContent = await File.ReadAllTextAsync(subtitlePath, Encoding.UTF8, linkedCts.Token);
                    transcript = ParseSrv1Content(xmlContent);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to parse subtitle file for {Url}", youtubeUrl);
                }
            }

            return new YouTubeVideoData
            {
                VideoId = metadata.Id,
                Title = metadata.Title,
                Description = metadata.Description,
                DurationSeconds = metadata.Duration,
                Channel = metadata.Channel,
                Transcript = transcript
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("yt-dlp extraction timed out or was cancelled for {Url}", youtubeUrl);
            return null;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    internal static string[] BuildArguments(string youtubeUrl, string outputTemplate) =>
    [
        "--dump-json",
        "--write-sub",
        "--write-auto-sub",
        "--sub-lang", "en",
        "--sub-format", "srv1",
        "--skip-download",
        "-o", outputTemplate,
        youtubeUrl
    ];

    /// <summary>
    /// Parses srv1 XML subtitle content and returns concatenated transcript text.
    /// XDocument.Value strips child XML tags (e.g. &lt;font&gt;) automatically.
    /// </summary>
    internal static string ParseSrv1Content(string xmlContent)
    {
        var doc = XDocument.Parse(xmlContent);
        var segments = doc.Descendants("text")
            .Select(e => e.Value.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t));

        var combined = string.Join(" ", segments);
        return Regex.Replace(combined, @"\s+", " ").Trim();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Ignore cleanup failures — OS will reclaim temp files eventually
        }
    }
}

/// <summary>
/// Abstraction over subprocess execution, injectable for testing.
/// </summary>
internal interface IProcessExecutor
{
    Task<(int ExitCode, string StdOut, string StdErr)> ExecuteAsync(
        string executable,
        string[] args,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default process executor using System.Diagnostics.Process.
/// </summary>
internal sealed class DefaultProcessExecutor : IProcessExecutor
{
    public async Task<(int ExitCode, string StdOut, string StdErr)> ExecuteAsync(
        string executable,
        string[] args,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {executable}");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;
        return (process.ExitCode, stdOut, stdErr);
    }
}
