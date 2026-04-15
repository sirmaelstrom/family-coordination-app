using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FamilyCoordinationApp.Models.YouTube;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FamilyCoordinationApp.Services;

public sealed class YtDlpService : IYtDlpService
{
    private readonly ILogger<YtDlpService> _logger;
    private readonly IProcessRunner _processRunner;
    private readonly TimeSpan _timeout;

    // Primary constructor for DI — uses the real process runner with the spec-mandated 30s timeout.
    public YtDlpService(ILogger<YtDlpService> logger)
        : this(logger, new DefaultProcessRunner(), TimeSpan.FromSeconds(30)) { }

    // Internal constructor for testing — accepts a custom runner and configurable timeout.
    internal YtDlpService(ILogger<YtDlpService> logger, IProcessRunner processRunner, TimeSpan timeout)
    {
        _logger = logger;
        _processRunner = processRunner;
        _timeout = timeout;
    }

    public async Task<YouTubeVideoData?> ExtractVideoDataAsync(
        string youtubeUrl,
        CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ytdlp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using var timeoutCts = new CancellationTokenSource(_timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var outputTemplate = Path.Combine(tempDir, "%(id)s");
            var args = BuildArguments(youtubeUrl, outputTemplate);

            var (exitCode, stdout, _) = await _processRunner.RunAsync(
                "yt-dlp", args, tempDir, linkedCts.Token);

            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                _logger.LogWarning("yt-dlp failed for {Url}. Exit code: {ExitCode}", youtubeUrl, exitCode);
                return null;
            }

            var metadata = JsonSerializer.Deserialize<YtDlpMetadata>(stdout);
            if (metadata?.Id is null || metadata.Title is null)
            {
                _logger.LogWarning("yt-dlp returned incomplete metadata for {Url}", youtubeUrl);
                return null;
            }

            string? transcript = null;
            var subtitlePath = Path.Combine(tempDir, $"{metadata.Id}.en.srv1");
            if (File.Exists(subtitlePath))
            {
                var xmlContent = await File.ReadAllTextAsync(subtitlePath, linkedCts.Token);
                transcript = ParseSrv1Transcript(xmlContent);
            }

            return new YouTubeVideoData
            {
                VideoId = metadata.Id,
                Title = metadata.Title,
                Description = metadata.Description,
                DurationSeconds = metadata.Duration,
                Channel = metadata.Channel,
                Transcript = transcript,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Internal timeout fired — not an external cancellation request.
            _logger.LogWarning("yt-dlp timed out after {Seconds}s for {Url}", _timeout.TotalSeconds, youtubeUrl);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp extraction failed for {Url}", youtubeUrl);
            return null;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Builds the yt-dlp argument list for a combined metadata + subtitle extraction call.
    /// Single invocation: --dump-json writes metadata to stdout; --write-sub/--write-auto-sub
    /// writes the subtitle file to the output directory.
    /// </summary>
    internal static string[] BuildArguments(string url, string outputTemplate) =>
    [
        "--dump-json",
        "--write-sub",
        "--write-auto-sub",
        "--sub-lang", "en",
        "--sub-format", "srv1",
        "--skip-download",
        "-o", outputTemplate,
        url,
    ];

    /// <summary>
    /// Parses an srv1 XML subtitle file into a single transcript string.
    /// Strips HTML tags from segment text (yt-dlp occasionally includes font elements).
    /// Returns null if parsing fails.
    /// </summary>
    internal static string? ParseSrv1Transcript(string xmlContent)
    {
        try
        {
            var doc = XDocument.Parse(xmlContent);
            var segments = doc.Descendants("text")
                .Select(t => StripHtmlTags(t.Value).Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t));

            var transcript = string.Join(' ', segments);
            return Regex.Replace(transcript, @"\s{2,}", " ").Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string StripHtmlTags(string input) =>
        Regex.Replace(input, @"<[^>]+>", string.Empty);
}

/// <summary>
/// Abstraction over Process.Start for testability. Internal — not part of the public API.
/// </summary>
internal interface IProcessRunner
{
    Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string executable,
        string[] args,
        string workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed class DefaultProcessRunner : IProcessRunner
{
    public async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string executable,
        string[] args,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
