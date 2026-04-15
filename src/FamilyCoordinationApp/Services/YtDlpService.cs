using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FamilyCoordinationApp.Models.YouTube;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FamilyCoordinationApp.Services;

public class YtDlpService : IYtDlpService
{
    private const int TimeoutSeconds = 30;

    private readonly ILogger<YtDlpService> _logger;

    public YtDlpService(ILogger<YtDlpService> logger) => _logger = logger;

    public async Task<YouTubeVideoData?> ExtractVideoDataAsync(
        string youtubeUrl, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var tempDir = Path.Combine(Path.GetTempPath(), $"ytdlp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var arguments = BuildArguments(youtubeUrl, tempDir);
            var (exitCode, stdout) = await ExecuteYtDlpAsync(arguments, linkedCts.Token);

            if (exitCode != 0)
            {
                _logger.LogWarning("yt-dlp exited with code {ExitCode} for {Url}", exitCode, youtubeUrl);
                return null;
            }

            var metadata = ParseMetadata(stdout);
            if (metadata?.Id is null || metadata.Title is null)
            {
                _logger.LogWarning("yt-dlp returned invalid metadata for {Url}", youtubeUrl);
                return null;
            }

            string? transcript = null;
            var subtitlePath = Path.Combine(tempDir, $"{metadata.Id}.en.srv1");
            if (File.Exists(subtitlePath))
            {
                var srv1Content = await File.ReadAllTextAsync(subtitlePath, linkedCts.Token);
                transcript = ParseSrv1Transcript(srv1Content);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp extraction failed for {Url}", youtubeUrl);
            return null;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Overridable for testing — runs yt-dlp and returns (exitCode, stdout).</summary>
    protected virtual async Task<(int ExitCode, string Stdout)> ExecuteYtDlpAsync(
        string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start yt-dlp process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        return (process.ExitCode, await stdoutTask);
    }

    /// <summary>
    /// Builds the yt-dlp argument string for a single call that extracts metadata
    /// (stdout JSON) and subtitle files (written to tempDir).
    /// </summary>
    internal static string BuildArguments(string youtubeUrl, string tempDir)
    {
        // Normalize path separator — yt-dlp output templates use forward slashes on all platforms.
        var outputTemplate = Path.Combine(tempDir, "%(id)s").Replace('\\', '/');
        return $"--dump-json --write-sub --write-auto-sub --sub-lang en --sub-format srv1 --skip-download -o \"{outputTemplate}\" \"{youtubeUrl}\"";
    }

    /// <summary>Parses yt-dlp --dump-json stdout into a YtDlpMetadata POCO.</summary>
    internal static YtDlpMetadata? ParseMetadata(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<YtDlpMetadata>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses an srv1 XML subtitle file into a flat transcript string.
    /// Strips HTML tags (e.g. font elements) and collapses whitespace.
    /// Returns null if the XML is empty or unparseable.
    /// </summary>
    internal static string? ParseSrv1Transcript(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try
        {
            var doc = XDocument.Parse(xml);
            var segments = doc.Descendants("text")
                .Select(el => StripHtmlTags(el.Value))
                .Where(s => !string.IsNullOrWhiteSpace(s));

            var joined = string.Join(" ", segments);
            return string.IsNullOrWhiteSpace(joined) ? null : CollapseWhitespace(joined);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string StripHtmlTags(string text) =>
        Regex.Replace(text, "<[^>]+>", string.Empty);

    private static string CollapseWhitespace(string text) =>
        Regex.Replace(text.Trim(), @"\s+", " ");
}
