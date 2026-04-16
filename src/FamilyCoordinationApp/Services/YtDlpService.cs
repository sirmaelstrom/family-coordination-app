using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FamilyCoordinationApp.Models.YouTube;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

public class YtDlpService(ILogger<YtDlpService> logger) : IYtDlpService
{
    private const int TimeoutSeconds = 30;
    private const string YtDlpExecutable = "yt-dlp";

    public async Task<YouTubeVideoData?> ExtractVideoDataAsync(string youtubeUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(youtubeUrl))
            return null;

        var tempDir = Path.Combine(Path.GetTempPath(), $"ytdlp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var outputTemplate = Path.Combine(tempDir, "%(id)s");
            var args = BuildArguments(youtubeUrl, outputTemplate);

            var (exitCode, _, stderr) = await RunProcessAsync(args, timeoutCts.Token);

            // yt-dlp returns non-zero when any requested artifact fails (e.g. subtitle download
            // errors with "Did not get any data blocks" even on videos with available captions).
            // The .info.json is usually written regardless. Fall through and check for it before
            // giving up — the description path alone can still produce a recipe.
            if (exitCode != 0)
            {
                logger.LogWarning("yt-dlp exited with code {ExitCode}. stderr: {Stderr}", exitCode, Truncate(stderr, 500));
            }

            var metadata = ReadMetadata(tempDir);
            if (metadata?.Id == null)
            {
                logger.LogWarning("yt-dlp output missing video id — no .info.json found in {TempDir}", tempDir);
                return null;
            }

            var transcript = ReadTranscript(tempDir, metadata.Id);

            return new YouTubeVideoData
            {
                VideoId = metadata.Id,
                Title = metadata.Title ?? string.Empty,
                Description = metadata.Description,
                DurationSeconds = metadata.Duration,
                Channel = metadata.Channel,
                Transcript = transcript
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("yt-dlp extraction timed out after {Timeout}s for URL", TimeoutSeconds);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected error during yt-dlp extraction");
            return null;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    internal static string[] BuildArguments(string url, string outputTemplate)
    {
        // --write-info-json writes metadata to {id}.info.json on disk.
        // --dump-json would print JSON to stdout but suppresses subtitle downloads.
        return
        [
            "--write-info-json",
            "--write-sub",
            "--write-auto-sub",
            "--sub-lang", "en",
            "--sub-format", "srv1",
            "--skip-download",
            "--no-warnings",
            "-o", outputTemplate,
            url
        ];
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(string[] args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = YtDlpExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort kill.
        }
    }

    private string? ReadTranscript(string tempDir, string videoId)
    {
        // yt-dlp writes subtitles as {id}.en.srv1 (or sometimes {id}.en-*.srv1)
        var candidates = Directory.GetFiles(tempDir, $"{videoId}*.srv1");
        if (candidates.Length == 0)
            return null;

        try
        {
            var xml = File.ReadAllText(candidates[0]);
            return ParseSrv1(xml);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse subtitle file {File}", candidates[0]);
            return null;
        }
    }

    private YtDlpMetadata? ReadMetadata(string tempDir)
    {
        var candidates = Directory.GetFiles(tempDir, "*.info.json");
        if (candidates.Length == 0)
            return null;

        try
        {
            var json = File.ReadAllText(candidates[0]);
            return JsonSerializer.Deserialize<YtDlpMetadata>(json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize yt-dlp metadata from {File}", candidates[0]);
            return null;
        }
    }

    internal static string? ParseSrv1(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var root = doc.Root;
        if (root == null)
            return null;

        var texts = root.Elements("text")
            .Select(e => StripHtml(e.Value))
            .Where(s => !string.IsNullOrWhiteSpace(s));

        var joined = string.Join(" ", texts);
        joined = Regex.Replace(joined, @"\s+", " ").Trim();
        return joined.Length == 0 ? null : joined;
    }

    private static string StripHtml(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var decoded = System.Net.WebUtility.HtmlDecode(input);
        return Regex.Replace(decoded, @"<[^>]+>", string.Empty);
    }

    private static string Truncate(string input, int max)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;
        return input.Length <= max ? input : input[..max];
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
