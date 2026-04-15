using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class YtDlpServiceTests
{
    private static string FixturePath(string filename) =>
        Path.Combine("Fixtures", "YtDlp", filename);

    // Fake: overrides process execution to return canned JSON stdout.
    // Does NOT write subtitle files to disk, so transcript will be null.
    private sealed class FakeYtDlpService : YtDlpService
    {
        private readonly string _stdout;

        public FakeYtDlpService(string stdout, ILogger<YtDlpService> logger)
            : base(logger) => _stdout = stdout;

        protected override Task<(int ExitCode, string Stdout)> ExecuteYtDlpAsync(
            string arguments, CancellationToken cancellationToken) =>
            Task.FromResult((0, _stdout));
    }

    // Fake: overrides process execution to block until cancellation.
    private sealed class HangingYtDlpService : YtDlpService
    {
        public HangingYtDlpService(ILogger<YtDlpService> logger) : base(logger) { }

        protected override async Task<(int ExitCode, string Stdout)> ExecuteYtDlpAsync(
            string arguments, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return (0, string.Empty); // never reached
        }
    }

    // --- Argument construction ---

    [Fact]
    public void BuildArguments_ContainsMetadataExtractionFlag()
    {
        var args = YtDlpService.BuildArguments("https://www.youtube.com/watch?v=test", "/tmp/ytdlp-test");

        args.Should().Contain("--dump-json");
    }

    [Fact]
    public void BuildArguments_ContainsSubtitleExtractionFlags()
    {
        var args = YtDlpService.BuildArguments("https://www.youtube.com/watch?v=test", "/tmp/ytdlp-test");

        args.Should().Contain("--write-sub");
        args.Should().Contain("--write-auto-sub");
        args.Should().Contain("--sub-lang en");
        args.Should().Contain("--sub-format srv1");
        args.Should().Contain("--skip-download");
    }

    // --- JSON deserialization ---

    [Fact]
    public void ParseMetadata_WithSampleFixture_DeserializesAllFields()
    {
        var json = File.ReadAllText(FixturePath("sample-video.meta.json"));

        var metadata = YtDlpService.ParseMetadata(json);

        metadata.Should().NotBeNull();
        metadata!.Id.Should().Be("4aZr5hZXP_s");
        metadata.Title.Should().Be("Chicken Teriyaki Casserole");
        metadata.Duration.Should().BeApproximately(189, 1);
        metadata.Channel.Should().Be("TheCooknShare");
        metadata.Description.Should().Contain("teriyaki");
    }

    // --- srv1 XML parsing ---

    [Fact]
    public void ParseSrv1Transcript_WithSampleFixture_ReturnsPlainText()
    {
        var xml = File.ReadAllText(FixturePath("sample-video.en.srv1"));

        var transcript = YtDlpService.ParseSrv1Transcript(xml);

        transcript.Should().NotBeNullOrEmpty();
        transcript.Should().Contain("teriyaki");
    }

    [Fact]
    public void ParseSrv1Transcript_WithSampleFixture_StripsHtmlTags()
    {
        var xml = File.ReadAllText(FixturePath("sample-video.en.srv1"));

        var transcript = YtDlpService.ParseSrv1Transcript(xml);

        transcript.Should().NotContain("<font");
        transcript.Should().NotContain("</font>");
        transcript.Should().NotContain("<");
        transcript.Should().NotContain(">");
    }

    // --- No-captions fixture ---

    [Fact]
    public void ParseMetadata_WithNoCaptionsFixture_DeserializesSuccessfully()
    {
        var json = File.ReadAllText(FixturePath("no-captions.meta.json"));

        var metadata = YtDlpService.ParseMetadata(json);

        metadata.Should().NotBeNull();
        metadata!.Id.Should().Be("noCaptionsVid1");
        metadata.Title.Should().Be("Simple Roasted Vegetables");
    }

    [Fact]
    public async Task ExtractVideoDataAsync_NoCaptionsVideo_ReturnsNullTranscript()
    {
        var json = File.ReadAllText(FixturePath("no-captions.meta.json"));
        var service = new FakeYtDlpService(json, Mock.Of<ILogger<YtDlpService>>());

        // FakeYtDlpService returns metadata JSON but writes no subtitle file,
        // so the service should produce a result with Transcript = null.
        var result = await service.ExtractVideoDataAsync("https://www.youtube.com/watch?v=noCaptionsVid1");

        result.Should().NotBeNull();
        result!.VideoId.Should().Be("noCaptionsVid1");
        result.Transcript.Should().BeNull();
    }

    // --- Timeout enforcement ---

    [Fact]
    public async Task ExtractVideoDataAsync_CancellationRequested_ReturnsNull()
    {
        var service = new HangingYtDlpService(Mock.Of<ILogger<YtDlpService>>());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var result = await service.ExtractVideoDataAsync(
            "https://www.youtube.com/watch?v=test", cts.Token);

        result.Should().BeNull();
    }
}
