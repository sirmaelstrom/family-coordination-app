using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using FamilyCoordinationApp.Models.SchemaOrg;
using FamilyCoordinationApp.Models.YouTube;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Tests.Services;

public class YouTubeRecipeExtractorTests
{
    // ── YouTubeUrlHelper URL detection ────────────────────────────────────────

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("https://youtube.com/watch?v=abc123", true)]
    [InlineData("https://m.youtube.com/watch?v=abc123", true)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", true)]
    [InlineData("https://www.youtube.com/shorts/abc123", true)]
    [InlineData("https://www.youtube.com/embed/abc123", true)]
    public void IsYouTubeUrl_ValidYouTubeUrls_ReturnsTrue(string url, bool expected)
    {
        YouTubeUrlHelper.IsYouTubeUrl(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://www.allrecipes.com/recipe/123", false)]
    [InlineData("https://example.com/watch?v=abc", false)]
    [InlineData("https://notyoutube.com/watch?v=abc", false)]
    [InlineData("https://www.youtube.com/channel/UCabc123", false)]
    [InlineData("https://www.youtube.com/user/SomeUser", false)]
    [InlineData("https://www.youtube.com/", false)]
    [InlineData("https://youtu.be/", false)]
    [InlineData("not-a-url", false)]
    public void IsYouTubeUrl_NonVideoUrls_ReturnsFalse(string url, bool expected)
    {
        YouTubeUrlHelper.IsYouTubeUrl(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123&t=120", true)]
    [InlineData("https://www.youtube.com/watch?v=abc123&utm_source=newsletter&utm_medium=email", true)]
    [InlineData("https://youtu.be/abc123?t=45", true)]
    [InlineData("https://www.youtube.com/shorts/abc123?feature=share", true)]
    public void IsYouTubeUrl_WithQueryParams_ReturnsTrue(string url, bool expected)
    {
        YouTubeUrlHelper.IsYouTubeUrl(url).Should().Be(expected);
    }

    // ── Coordination logic ────────────────────────────────────────────────────

    private readonly Mock<IYtDlpService> _ytDlpMock = new();
    private readonly Mock<IDescriptionRecipeExtractor> _descriptionMock = new();
    private readonly Mock<IGeminiRecipeExtractor> _geminiMock = new();
    private readonly YouTubeRecipeExtractor _extractor;

    public YouTubeRecipeExtractorTests()
    {
        _extractor = new YouTubeRecipeExtractor(
            _ytDlpMock.Object,
            _descriptionMock.Object,
            _geminiMock.Object,
            NullLogger<YouTubeRecipeExtractor>.Instance);
    }

    [Fact]
    public async Task ExtractRecipeAsync_YtDlpReturnsNull_ReturnsNull()
    {
        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((YouTubeVideoData?)null);

        var result = await _extractor.ExtractRecipeAsync("https://youtu.be/abc");

        result.Should().BeNull();
        _descriptionMock.Verify(d => d.ExtractFromDescription(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _geminiMock.Verify(g => g.ExtractFromTranscriptAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExtractRecipeAsync_DescriptionHasRecipe_ReturnsSchemaWithoutCallingGemini()
    {
        var videoData = new YouTubeVideoData
        {
            VideoId = "abc",
            Title = "Test Recipe Video",
            Description = "Some description with recipe",
            Transcript = "transcript text"
        };
        var expectedSchema = new RecipeSchema { Name = "Test Recipe" };

        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(videoData);
        _descriptionMock.Setup(d => d.ExtractFromDescription(videoData.Description, videoData.Title))
            .Returns(expectedSchema);

        var result = await _extractor.ExtractRecipeAsync("https://youtu.be/abc");

        result.Should().BeSameAs(expectedSchema);
        _geminiMock.Verify(g => g.ExtractFromTranscriptAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExtractRecipeAsync_DescriptionReturnsNull_CallsGeminiWithTranscript()
    {
        var videoData = new YouTubeVideoData
        {
            VideoId = "abc",
            Title = "Test Video",
            Description = "No recipe here",
            Transcript = "the transcript"
        };
        var expectedSchema = new RecipeSchema { Name = "Gemini Recipe" };

        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(videoData);
        _descriptionMock.Setup(d => d.ExtractFromDescription(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((RecipeSchema?)null);
        _geminiMock.Setup(g => g.ExtractFromTranscriptAsync(
            videoData.Transcript, videoData.Title, videoData.Description, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedSchema);

        var result = await _extractor.ExtractRecipeAsync("https://youtu.be/abc");

        result.Should().BeSameAs(expectedSchema);
        _geminiMock.Verify(g => g.ExtractFromTranscriptAsync(
            videoData.Transcript, videoData.Title, videoData.Description, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExtractRecipeAsync_NoDescriptionAndNoTranscript_ReturnsNull()
    {
        var videoData = new YouTubeVideoData
        {
            VideoId = "abc",
            Title = "Test Video",
            Description = null,
            Transcript = null
        };

        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(videoData);

        var result = await _extractor.ExtractRecipeAsync("https://youtu.be/abc");

        result.Should().BeNull();
        _descriptionMock.Verify(d => d.ExtractFromDescription(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _geminiMock.Verify(g => g.ExtractFromTranscriptAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExtractRecipeAsync_DescriptionNullTranscriptAvailable_GeminiFallsBackToNull_ReturnsNull()
    {
        var videoData = new YouTubeVideoData
        {
            VideoId = "abc",
            Title = "Test Video",
            Description = null,
            Transcript = "some transcript"
        };

        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(videoData);
        _geminiMock.Setup(g => g.ExtractFromTranscriptAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RecipeSchema?)null);

        var result = await _extractor.ExtractRecipeAsync("https://youtu.be/abc");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExtractRecipeAsync_DescriptionFailsTranscriptMissing_GeminiNotCalled()
    {
        var videoData = new YouTubeVideoData
        {
            VideoId = "abc",
            Title = "Test Video",
            Description = "Some description",
            Transcript = null
        };

        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(videoData);
        _descriptionMock.Setup(d => d.ExtractFromDescription(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((RecipeSchema?)null);

        var result = await _extractor.ExtractRecipeAsync("https://youtu.be/abc");

        result.Should().BeNull();
        _geminiMock.Verify(g => g.ExtractFromTranscriptAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
