using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Models.YouTube;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class YtDlpServiceTests
{
    private readonly string _fixturesDir;

    public YtDlpServiceTests()
    {
        _fixturesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "YtDlp");
    }

    [Fact]
    public async Task ExtractVideoDataAsync_PassesCorrectCliArguments_ForMetadataAndSubtitleExtraction()
    {
        // Arrange
        string[]? capturedArgs = null;
        var mockExecutor = new Mock<IProcessExecutor>();
        mockExecutor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string[], CancellationToken>((_, args, _) => capturedArgs = args)
            .ReturnsAsync((1, "", "error"));

        var service = new YtDlpService(mockExecutor.Object, Mock.Of<ILogger<YtDlpService>>());
        const string url = "https://www.youtube.com/watch?v=test123";

        // Act
        await service.ExtractVideoDataAsync(url);

        // Assert — metadata extraction
        capturedArgs.Should().Contain("--dump-json");
        capturedArgs.Should().Contain("--skip-download");

        // Assert — subtitle extraction
        capturedArgs.Should().Contain("--write-sub");
        capturedArgs.Should().Contain("--write-auto-sub");
        capturedArgs.Should().Contain("--sub-lang");
        capturedArgs.Should().Contain("en");
        capturedArgs.Should().Contain("--sub-format");
        capturedArgs.Should().Contain("srv1");

        // Assert — URL is included
        capturedArgs.Should().Contain(url);
    }

    [Fact]
    public void SampleMetadataFixture_DeserializesToYtDlpMetadata_WithRequiredFields()
    {
        // Arrange
        var json = File.ReadAllText(Path.Combine(_fixturesDir, "sample-video.meta.json"));

        // Act
        var metadata = JsonSerializer.Deserialize<YtDlpMetadata>(json);

        // Assert
        metadata.Should().NotBeNull();
        metadata!.Id.Should().NotBeNullOrEmpty();
        metadata.Title.Should().NotBeNullOrEmpty();
        metadata.Duration.Should().BeGreaterThan(0);
        metadata.Channel.Should().NotBeNullOrEmpty();
        metadata.Description.Should().NotBeNullOrEmpty();
        metadata.Subtitles.Should().NotBeNull();
        metadata.AutomaticCaptions.Should().NotBeNull();
    }

    [Fact]
    public void ParseSrv1Content_ExtractsTranscriptText_WithoutHtmlTags()
    {
        // Arrange
        var xmlContent = File.ReadAllText(Path.Combine(_fixturesDir, "sample-video.en.srv1"));

        // Act
        var transcript = YtDlpService.ParseSrv1Content(xmlContent);

        // Assert
        transcript.Should().NotBeNullOrEmpty();
        transcript.Should().Contain("scrambled eggs");
        transcript.Should().NotContain("<font");
        transcript.Should().NotContain("</font>");
        transcript.Should().NotMatchRegex(@"\s{2,}"); // no double spaces
    }

    [Fact]
    public async Task ExtractVideoDataAsync_NoCaptionsVideo_ReturnsNullTranscript()
    {
        // Arrange
        var metaJson = File.ReadAllText(Path.Combine(_fixturesDir, "no-captions.meta.json"));

        var mockExecutor = new Mock<IProcessExecutor>();
        mockExecutor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, metaJson, ""));

        var service = new YtDlpService(mockExecutor.Object, Mock.Of<ILogger<YtDlpService>>());

        // Act
        var result = await service.ExtractVideoDataAsync("https://www.youtube.com/watch?v=xyz789abc00");

        // Assert
        result.Should().NotBeNull();
        result!.Transcript.Should().BeNull();
        result.VideoId.Should().Be("xyz789abc00");
        result.Title.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExtractVideoDataAsync_ProcessTimesOut_ReturnsNull()
    {
        // Arrange
        var mockExecutor = new Mock<IProcessExecutor>();
        mockExecutor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Process timed out"));

        var service = new YtDlpService(mockExecutor.Object, Mock.Of<ILogger<YtDlpService>>());

        // Act
        var result = await service.ExtractVideoDataAsync("https://www.youtube.com/watch?v=test");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ExtractVideoDataAsync_WithSubtitleFile_ReturnsTranscriptWithoutHtmlTags()
    {
        // Arrange — mock executor writes the srv1 fixture to the temp dir the service creates
        var metaJson = File.ReadAllText(Path.Combine(_fixturesDir, "sample-video.meta.json"));
        var videoId = JsonSerializer.Deserialize<YtDlpMetadata>(metaJson)!.Id!;
        var srvContent = File.ReadAllText(Path.Combine(_fixturesDir, "sample-video.en.srv1"));

        var mockExecutor = new Mock<IProcessExecutor>();
        mockExecutor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string[], CancellationToken>((_, args, _) =>
            {
                // Extract temp dir from the -o argument and write the subtitle fixture there
                var oIndex = Array.IndexOf(args, "-o");
                if (oIndex >= 0 && oIndex + 1 < args.Length)
                {
                    var outputDir = Path.GetDirectoryName(args[oIndex + 1]);
                    if (outputDir != null && Directory.Exists(outputDir))
                        File.WriteAllText(Path.Combine(outputDir, $"{videoId}.en.srv1"), srvContent);
                }
            })
            .ReturnsAsync((0, metaJson, ""));

        var service = new YtDlpService(mockExecutor.Object, Mock.Of<ILogger<YtDlpService>>());

        // Act
        var result = await service.ExtractVideoDataAsync($"https://www.youtube.com/watch?v={videoId}");

        // Assert
        result.Should().NotBeNull();
        result!.VideoId.Should().Be(videoId);
        result.Title.Should().Be("Perfect Scrambled Eggs | Basics with Babish");
        result.Transcript.Should().NotBeNullOrEmpty();
        result.Transcript.Should().NotContain("<font");
        result.Transcript.Should().Contain("scrambled eggs");
    }
}
