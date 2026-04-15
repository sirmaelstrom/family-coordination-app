using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using FamilyCoordinationApp.Models.YouTube;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class YtDlpServiceTests
{
    // ─── Fixture helpers ──────────────────────────────────────────────────────

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "YtDlp", fileName);

    private static string ReadFixture(string fileName) =>
        File.ReadAllText(FixturePath(fileName));

    // ─── Hand-written test double for IProcessRunner ──────────────────────────

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public string? CapturedExecutable { get; private set; }
        public string[]? CapturedArgs { get; private set; }
        public string? CapturedWorkingDirectory { get; private set; }

        public Func<string, string[], string, CancellationToken, Task<(int, string, string)>>? Handler { get; set; }

        public Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
            string executable,
            string[] args,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            CapturedExecutable = executable;
            CapturedArgs = args;
            CapturedWorkingDirectory = workingDirectory;
            return Handler?.Invoke(executable, args, workingDirectory, cancellationToken)
                ?? Task.FromResult<(int, string, string)>((0, "", ""));
        }
    }

    private static YtDlpService CreateService(FakeProcessRunner runner, TimeSpan? timeout = null) =>
        new(NullLogger<YtDlpService>.Instance, runner, timeout ?? TimeSpan.FromSeconds(10));

    // ─── Argument construction ────────────────────────────────────────────────

    [Fact]
    public void BuildArguments_ContainsDumpJsonFlag()
    {
        var args = YtDlpService.BuildArguments("https://www.youtube.com/watch?v=test123", "/tmp/output/%(id)s");
        args.Should().Contain("--dump-json");
    }

    [Fact]
    public void BuildArguments_ContainsSubtitleExtractionFlags()
    {
        var args = YtDlpService.BuildArguments("https://www.youtube.com/watch?v=test123", "/tmp/output/%(id)s");

        args.Should().Contain("--write-sub");
        args.Should().Contain("--write-auto-sub");
        args.Should().Contain("--sub-lang");
        args.Should().Contain("en");
        args.Should().Contain("--sub-format");
        args.Should().Contain("srv1");
        args.Should().Contain("--skip-download");
        args.Should().Contain("-o");
    }

    [Fact]
    public void BuildArguments_UrlIsLastArgument()
    {
        const string url = "https://www.youtube.com/watch?v=test123";
        var args = YtDlpService.BuildArguments(url, "/tmp/output/%(id)s");
        args.Last().Should().Be(url);
    }

    // ─── JSON deserialization (fixture contract tests) ────────────────────────

    [Fact]
    public void DeserializeMetadata_SampleVideoFixture_PopulatesAllFields()
    {
        var json = ReadFixture("sample-video.meta.json");

        var metadata = JsonSerializer.Deserialize<YtDlpMetadata>(json);

        metadata.Should().NotBeNull();
        metadata!.Id.Should().NotBeNullOrEmpty();
        metadata.Title.Should().NotBeNullOrEmpty();
        metadata.Description.Should().NotBeNullOrEmpty();
        metadata.Duration.Should().BeGreaterThan(0);
        metadata.Channel.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void DeserializeMetadata_NoCaptionsFixture_ParsesSuccessfully()
    {
        var json = ReadFixture("no-captions.meta.json");

        var metadata = JsonSerializer.Deserialize<YtDlpMetadata>(json);

        metadata.Should().NotBeNull();
        metadata!.Id.Should().NotBeNullOrEmpty();
        metadata.Title.Should().NotBeNullOrEmpty();
    }

    // ─── srv1 transcript parsing ──────────────────────────────────────────────

    [Fact]
    public void ParseSrv1Transcript_SampleFixture_ReturnsNonEmptyText()
    {
        var xmlContent = ReadFixture("sample-video.en.srv1");

        var transcript = YtDlpService.ParseSrv1Transcript(xmlContent);

        transcript.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ParseSrv1Transcript_StripsFontColorTags()
    {
        // yt-dlp sometimes encodes font tags as HTML entities inside text nodes
        const string xml = """
            <?xml version="1.0" encoding="utf-8" ?>
            <transcript>
              <text start="0.00" dur="3.00">Hello &lt;font color=&quot;#E5E5E5&quot;&gt;world&lt;/font&gt; today.</text>
            </transcript>
            """;

        var result = YtDlpService.ParseSrv1Transcript(xml);

        result.Should().NotBeNull();
        result.Should().NotContain("<font");
        result.Should().Contain("world");
    }

    [Fact]
    public void ParseSrv1Transcript_CollapsesDuplicateWhitespace()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8" ?>
            <transcript>
              <text start="0.00" dur="2.00">Stir   until   smooth.</text>
            </transcript>
            """;

        var result = YtDlpService.ParseSrv1Transcript(xml);

        result.Should().Be("Stir until smooth.");
    }

    [Fact]
    public void ParseSrv1Transcript_JoinsMultipleSegments()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8" ?>
            <transcript>
              <text start="0.00" dur="2.00">Add the garlic.</text>
              <text start="2.00" dur="2.00">Cook until golden.</text>
            </transcript>
            """;

        var result = YtDlpService.ParseSrv1Transcript(xml);

        result.Should().Be("Add the garlic. Cook until golden.");
    }

    // ─── Full service pipeline (via FakeProcessRunner) ────────────────────────

    [Fact]
    public async Task ExtractVideoDataAsync_WithCaptions_ReturnsPopulatedResult()
    {
        var metaJson = ReadFixture("sample-video.meta.json");
        var subtitleXml = ReadFixture("sample-video.en.srv1");
        var videoId = JsonSerializer.Deserialize<YtDlpMetadata>(metaJson)!.Id!;

        var runner = new FakeProcessRunner
        {
            Handler = (_, _, workDir, _) =>
            {
                // Simulate yt-dlp writing the subtitle file to the temp output dir
                File.WriteAllText(Path.Combine(workDir, $"{videoId}.en.srv1"), subtitleXml);
                return Task.FromResult<(int, string, string)>((0, metaJson, ""));
            }
        };

        var service = CreateService(runner);
        var result = await service.ExtractVideoDataAsync("https://www.youtube.com/watch?v=xvJ8TKjWKZY");

        result.Should().NotBeNull();
        result!.VideoId.Should().Be(videoId);
        result.Title.Should().Be("Perfect Pasta Aglio e Olio");
        result.Channel.Should().Be("Binging with Babish");
        result.DurationSeconds.Should().Be(487.0);
        result.Transcript.Should().NotBeNullOrWhiteSpace();
        result.Transcript.Should().NotContain("<font");
    }

    [Fact]
    public async Task ExtractVideoDataAsync_NoCaptions_ReturnsNullTranscript()
    {
        var metaJson = ReadFixture("no-captions.meta.json");

        var runner = new FakeProcessRunner
        {
            // Return the metadata JSON but do NOT write any subtitle file
            Handler = (_, _, _, _) => Task.FromResult<(int, string, string)>((0, metaJson, ""))
        };

        var service = CreateService(runner);
        var result = await service.ExtractVideoDataAsync("https://www.youtube.com/watch?v=noCpsVid1234");

        result.Should().NotBeNull();
        result!.Transcript.Should().BeNull();
        result.Title.Should().Be("Grandma's Secret Apple Pie Recipe");
    }

    [Fact]
    public async Task ExtractVideoDataAsync_ProcessExitsNonZero_ReturnsNull()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (_, _, _, _) => Task.FromResult<(int, string, string)>((1, "", "ERROR: Unable to extract"))
        };

        var service = CreateService(runner);
        var result = await service.ExtractVideoDataAsync("https://www.youtube.com/watch?v=invalid");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExtractVideoDataAsync_ProcessTimeout_ReturnsNull()
    {
        var runner = new FakeProcessRunner
        {
            // Simulate a process that never exits — task only completes on cancellation
            Handler = (_, _, _, ct) =>
            {
                var tcs = new TaskCompletionSource<(int, string, string)>();
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            }
        };

        // Use a very short timeout so the test completes quickly
        var service = CreateService(runner, TimeSpan.FromMilliseconds(150));

        var result = await service.ExtractVideoDataAsync("https://www.youtube.com/watch?v=hangs");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExtractVideoDataAsync_PassesYtDlpExecutableToRunner()
    {
        var metaJson = ReadFixture("sample-video.meta.json");
        var runner = new FakeProcessRunner
        {
            Handler = (_, _, _, _) => Task.FromResult<(int, string, string)>((0, metaJson, ""))
        };

        var service = CreateService(runner);
        await service.ExtractVideoDataAsync("https://www.youtube.com/watch?v=xvJ8TKjWKZY");

        runner.CapturedExecutable.Should().Be("yt-dlp");
    }
}
