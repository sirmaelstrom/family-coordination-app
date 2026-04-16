using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using FamilyCoordinationApp.Models.SchemaOrg;
using FamilyCoordinationApp.Models.YouTube;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Seam tests: real DescriptionRecipeExtractor + YouTubeRecipeExtractor +
/// RecipeImportService, with yt-dlp and Gemini mocked at the external boundary.
/// </summary>
public class YouTubePipelineIntegrationTests
{
    private readonly Mock<IYtDlpService> _ytDlpMock = new();
    private readonly Mock<IGeminiRecipeExtractor> _geminiMock = new();
    private readonly Mock<IUrlValidator> _urlValidatorMock = new();
    private readonly Mock<IRecipeScraperService> _scraperMock = new();
    private readonly IIngredientParser _ingredientParser = new IngredientParser();
    private readonly Mock<ICategoryInferenceService> _categoryInferenceMock = new();

    private RecipeImportService BuildSut()
    {
        _categoryInferenceMock
            .Setup(c => c.InferCategory(It.IsAny<string>()))
            .Returns("Other");

        var descriptionExtractor = new DescriptionRecipeExtractor(
            NullLogger<DescriptionRecipeExtractor>.Instance);

        var youtubeExtractor = new YouTubeRecipeExtractor(
            _ytDlpMock.Object,
            descriptionExtractor,
            _geminiMock.Object,
            NullLogger<YouTubeRecipeExtractor>.Instance);

        return new RecipeImportService(
            _urlValidatorMock.Object,
            _scraperMock.Object,
            _ingredientParser,
            _categoryInferenceMock.Object,
            youtubeExtractor,
            NullLogger<RecipeImportService>.Instance);
    }

    [Fact]
    public async Task YouTubeUrl_DescriptionHasRecipe_SkipsGemini()
    {
        var videoData = new YouTubeVideoData
        {
            VideoId = "abc",
            Title = "Chocolate Chip Cookies",
            Description = "Ingredients:\n- 2 cups flour\n- 1 cup sugar\n- 1 cup butter\n\n" +
                          "Instructions:\n1. Mix wet and dry.\n2. Bake at 375F for 12 minutes.\n",
            Transcript = "some transcript"
        };
        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(videoData);

        var result = await BuildSut().ImportFromUrlAsync("https://youtu.be/abc", householdId: 1, userId: 1);

        result.Success.Should().BeTrue();
        result.Recipe.Should().NotBeNull();
        result.Recipe!.Name.Should().Be("Chocolate Chip Cookies");
        result.Recipe.Ingredients.Should().NotBeEmpty();
        result.Recipe.SourceUrl.Should().Be("https://youtu.be/abc");

        _geminiMock.Verify(
            g => g.ExtractFromTranscriptAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task YouTubeUrl_DescriptionLacksRecipe_UsesGemini()
    {
        var videoData = new YouTubeVideoData
        {
            VideoId = "abc",
            Title = "Silly Video",
            Description = "Follow me on Instagram @chef",
            Transcript = "salt and pepper and garlic"
        };
        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(videoData);

        var geminiSchema = new RecipeSchema
        {
            Name = "Silly Stew",
            RecipeIngredient = new[] { "1 tsp salt", "1 tsp pepper" },
            RecipeInstructions = "1. Mix things"
        };
        _geminiMock.Setup(g => g.ExtractFromTranscriptAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(geminiSchema);

        var result = await BuildSut().ImportFromUrlAsync("https://www.youtube.com/watch?v=abc", householdId: 1, userId: 1);

        result.Success.Should().BeTrue();
        result.Recipe!.Name.Should().Be("Silly Stew");
        result.Recipe.Ingredients.Should().HaveCount(2);
    }

    [Fact]
    public async Task YouTubeUrl_BothPathsFail_ReturnsFailure()
    {
        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((YouTubeVideoData?)null);

        var result = await BuildSut().ImportFromUrlAsync("https://youtu.be/abc", householdId: 1, userId: 1);

        result.Success.Should().BeFalse();
        result.ErrorType.Should().Be(RecipeImportErrorType.ParsingFailed);
    }

    [Fact]
    public async Task NonYouTubeUrl_RoutesToScraperPipeline()
    {
        const string url = "https://www.allrecipes.com/recipe/1/";

        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));
        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecipeSchema
            {
                Name = "Test Recipe",
                RecipeIngredient = new[] { "1 cup flour" },
                RecipeInstructions = "Mix"
            });

        var result = await BuildSut().ImportFromUrlAsync(url, householdId: 1, userId: 1);

        result.Success.Should().BeTrue();
        _ytDlpMock.Verify(
            s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
