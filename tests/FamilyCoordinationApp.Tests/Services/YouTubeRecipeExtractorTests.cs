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
    private readonly Mock<IYtDlpService> _ytDlpMock = new();
    private readonly Mock<IDescriptionRecipeExtractor> _descriptionMock = new();
    private readonly Mock<IGeminiRecipeExtractor> _geminiMock = new();

    private YouTubeRecipeExtractor BuildSut() => new(
        _ytDlpMock.Object,
        _descriptionMock.Object,
        _geminiMock.Object,
        NullLogger<YouTubeRecipeExtractor>.Instance);

    [Fact]
    public async Task ExtractRecipeAsync_YtDlpFails_ReturnsNull()
    {
        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((YouTubeVideoData?)null);

        var result = await BuildSut().ExtractRecipeAsync("https://youtu.be/abc");

        result.Should().BeNull();
        _descriptionMock.VerifyNoOtherCalls();
        _geminiMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExtractRecipeAsync_DescriptionHasRecipe_SkipsGemini()
    {
        var videoData = new YouTubeVideoData
        {
            VideoId = "abc",
            Title = "Cake",
            Description = "Ingredients:\n- 1 cup flour\nInstructions:\n1. Bake",
            Transcript = "transcript text"
        };
        var descriptionSchema = new RecipeSchema { Name = "Cake", RecipeIngredient = new[] { "1 cup flour" } };

        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(videoData);
        _descriptionMock.Setup(d => d.ExtractFromDescription(videoData.Description, "Cake"))
            .Returns(descriptionSchema);

        var result = await BuildSut().ExtractRecipeAsync("https://youtu.be/abc");

        result.Should().BeSameAs(descriptionSchema);
        _geminiMock.Verify(
            g => g.ExtractFromTranscriptAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExtractRecipeAsync_DescriptionMisses_FallsBackToGemini()
    {
        var videoData = new YouTubeVideoData
        {
            VideoId = "abc",
            Title = "Cake",
            Description = "just a description",
            Transcript = "transcript text"
        };
        var geminiSchema = new RecipeSchema { Name = "Cake", RecipeIngredient = new[] { "flour" } };

        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(videoData);
        _descriptionMock.Setup(d => d.ExtractFromDescription(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((RecipeSchema?)null);
        _geminiMock.Setup(g => g.ExtractFromTranscriptAsync(
                "transcript text", "Cake", "just a description", It.IsAny<CancellationToken>()))
            .ReturnsAsync(geminiSchema);

        var result = await BuildSut().ExtractRecipeAsync("https://youtu.be/abc");

        result.Should().BeSameAs(geminiSchema);
    }

    [Fact]
    public async Task ExtractRecipeAsync_NoTranscriptButDescriptionPresent_FallsBackToLlmWithDescription()
    {
        var videoData = new YouTubeVideoData
        {
            VideoId = "abc",
            Title = "Cake",
            Description = "ingredients list without parseable instructions header",
            Transcript = null
        };
        var geminiSchema = new RecipeSchema { Name = "Cake", RecipeIngredient = new[] { "flour" } };

        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(videoData);
        _descriptionMock.Setup(d => d.ExtractFromDescription(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((RecipeSchema?)null);
        _geminiMock.Setup(g => g.ExtractFromTranscriptAsync(
                videoData.Description!, "Cake", videoData.Description, It.IsAny<CancellationToken>()))
            .ReturnsAsync(geminiSchema);

        var result = await BuildSut().ExtractRecipeAsync("https://youtu.be/abc");

        result.Should().BeSameAs(geminiSchema);
    }

    [Fact]
    public async Task ExtractRecipeAsync_NoTranscriptAndNoDescription_ReturnsNull()
    {
        var videoData = new YouTubeVideoData
        {
            VideoId = "abc",
            Title = "Cake",
            Description = null,
            Transcript = null
        };
        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(videoData);

        var result = await BuildSut().ExtractRecipeAsync("https://youtu.be/abc");

        result.Should().BeNull();
        _geminiMock.Verify(
            g => g.ExtractFromTranscriptAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
