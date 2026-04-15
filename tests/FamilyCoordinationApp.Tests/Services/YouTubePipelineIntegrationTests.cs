using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using FamilyCoordinationApp.Models.SchemaOrg;
using FamilyCoordinationApp.Models.YouTube;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Full pipeline seam tests. External boundaries (yt-dlp, Gemini, scraper) are mocked;
/// internal services (DescriptionRecipeExtractor, IngredientParser, CategoryInferenceService)
/// are real.
///
/// Topology:
///   [YouTube URL] → IsYouTubeUrl → [IYtDlpService mock]
///     → DescriptionRecipeExtractor (REAL) → (if null) → [IGeminiRecipeExtractor mock]
///     → RecipeSchema → IngredientParser (REAL) → RecipeEntity
/// </summary>
public class YouTubePipelineIntegrationTests
{
    private readonly Mock<IYtDlpService> _ytDlpMock = new();
    private readonly Mock<IGeminiRecipeExtractor> _geminiMock = new();
    private readonly Mock<IRecipeScraperService> _scraperMock = new();
    private readonly Mock<IUrlValidator> _urlValidatorMock = new();

    // Real internal services
    private readonly IDescriptionRecipeExtractor _descriptionExtractor = new DescriptionRecipeExtractor();
    private readonly IIngredientParser _ingredientParser = new IngredientParser();
    private readonly ICategoryInferenceService _categoryInference = new CategoryInferenceService();

    private static string ReadFixture(string subDir, string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", subDir, fileName));

    private RecipeImportService CreateService()
    {
        var youtubeExtractor = new YouTubeRecipeExtractor(
            _ytDlpMock.Object,
            _descriptionExtractor,
            _geminiMock.Object,
            NullLogger<YouTubeRecipeExtractor>.Instance);

        return new RecipeImportService(
            _urlValidatorMock.Object,
            _scraperMock.Object,
            _ingredientParser,
            _categoryInference,
            youtubeExtractor,
            NullLogger<RecipeImportService>.Instance);
    }

    // ── Scenario 1: Description has recipe → entity created, Gemini not called ──

    [Fact]
    public async Task Pipeline_YouTubeUrl_DescriptionHasRecipe_CreatesEntityWithoutGemini()
    {
        var description = ReadFixture("Descriptions", "formatted-recipe.txt");
        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new YouTubeVideoData
            {
                VideoId = "abc123",
                Title = "Classic Chicken Stir-Fry",
                Description = description
            });

        var service = CreateService();
        var result = await service.ImportFromUrlAsync("https://www.youtube.com/watch?v=abc123", 1, 1);

        result.Success.Should().BeTrue();
        result.Recipe.Should().NotBeNull();
        result.Recipe!.Name.Should().NotBeNullOrEmpty();
        result.Recipe.Ingredients.Should().HaveCountGreaterThan(1);

        _geminiMock.Verify(g => g.ExtractFromTranscriptAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Pipeline_YouTubeUrl_DescriptionHasRecipe_IngredientsParsedWithRealParser()
    {
        var description = ReadFixture("Descriptions", "formatted-recipe.txt");
        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new YouTubeVideoData
            {
                VideoId = "abc123",
                Title = "Classic Chicken Stir-Fry",
                Description = description
            });

        var service = CreateService();
        var result = await service.ImportFromUrlAsync("https://www.youtube.com/watch?v=abc123", 1, 1);

        result.Success.Should().BeTrue();
        // Real IngredientParser should parse quantities from the fixture ingredients
        result.Recipe!.Ingredients.Should().AllSatisfy(i => i.Name.Should().NotBeNullOrWhiteSpace());
    }

    // ── Scenario 2: No recipe in description → Gemini called → entity created ──

    [Fact]
    public async Task Pipeline_YouTubeUrl_NoRecipeInDescription_CallsGeminiAndCreatesEntity()
    {
        var noRecipeDescription = ReadFixture("Descriptions", "no-recipe.txt");
        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new YouTubeVideoData
            {
                VideoId = "xyz789",
                Title = "Pasta Video",
                Description = noRecipeDescription,
                Transcript = "Today we make simple pasta. You need two cups of flour and two eggs. Mix them together and knead the dough."
            });

        _geminiMock.Setup(g => g.ExtractFromTranscriptAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecipeSchema
            {
                Name = "Simple Pasta",
                RecipeIngredient = ["2 cups flour", "2 eggs", "1 tsp salt"],
                RecipeInstructions = "Mix flour and eggs. Knead dough. Rest for 30 minutes."
            });

        var service = CreateService();
        var result = await service.ImportFromUrlAsync("https://youtu.be/xyz789", 1, 1);

        result.Success.Should().BeTrue();
        result.Recipe.Should().NotBeNull();
        result.Recipe!.Name.Should().Be("Simple Pasta");
        result.Recipe.Ingredients.Should().HaveCount(3);

        _geminiMock.Verify(g => g.ExtractFromTranscriptAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Scenario 3: Both paths fail → RecipeImportResult.Failed ──────────────

    [Fact]
    public async Task Pipeline_YouTubeUrl_BothPathsFail_ReturnsParsingFailed()
    {
        var noRecipeDescription = ReadFixture("Descriptions", "no-recipe.txt");
        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new YouTubeVideoData
            {
                VideoId = "fail123",
                Title = "Non-recipe video",
                Description = noRecipeDescription,
                Transcript = "Just talking about random travel stories with no food content."
            });

        _geminiMock.Setup(g => g.ExtractFromTranscriptAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RecipeSchema?)null);

        var service = CreateService();
        var result = await service.ImportFromUrlAsync("https://www.youtube.com/watch?v=fail123", 1, 1);

        result.Success.Should().BeFalse();
        result.ErrorType.Should().Be(RecipeImportErrorType.ParsingFailed);
        result.ErrorMessage.Should().Contain("Could not extract recipe");
    }

    [Fact]
    public async Task Pipeline_YouTubeUrl_YtDlpFails_ReturnsParsingFailed()
    {
        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((YouTubeVideoData?)null);

        var service = CreateService();
        var result = await service.ImportFromUrlAsync("https://www.youtube.com/watch?v=broken", 1, 1);

        result.Success.Should().BeFalse();
        result.ErrorType.Should().Be(RecipeImportErrorType.ParsingFailed);
    }

    // ── Scenario 4: Non-YouTube URL → existing JSON-LD pipeline (regression) ──

    [Fact]
    public async Task Pipeline_NonYouTubeUrl_RoutesToScraperPipeline_NotYouTubePipeline()
    {
        const string url = "https://www.allrecipes.com/recipe/test/";
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));
        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecipeSchema
            {
                Name = "Allrecipes Test Recipe",
                RecipeIngredient = ["1 cup flour", "1 egg", "1 cup milk"]
            });

        var service = CreateService();
        var result = await service.ImportFromUrlAsync(url, 1, 1);

        result.Success.Should().BeTrue();
        result.Recipe!.Name.Should().Be("Allrecipes Test Recipe");
        result.Recipe.Ingredients.Should().HaveCount(3);

        // YouTube services must NOT be called for non-YouTube URLs
        _ytDlpMock.Verify(y => y.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _geminiMock.Verify(g => g.ExtractFromTranscriptAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Pipeline_NonYouTubeUrl_StillPerformsSsrfValidation()
    {
        const string url = "http://internal.server/recipe";
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((false, "Private IP not allowed"));

        var service = CreateService();
        var result = await service.ImportFromUrlAsync(url, 1, 1);

        result.Success.Should().BeFalse();
        result.ErrorType.Should().Be(RecipeImportErrorType.InvalidUrl);

        _ytDlpMock.Verify(y => y.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── YouTube URL detection edge cases ──────────────────────────────────────

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123&t=120")]
    [InlineData("https://youtu.be/abc123?t=45")]
    [InlineData("https://m.youtube.com/watch?v=abc123")]
    [InlineData("https://www.youtube.com/shorts/abc123")]
    [InlineData("https://www.youtube.com/embed/abc123")]
    public async Task Pipeline_VariousYouTubeUrlFormats_RouteToYouTubePipeline(string url)
    {
        _ytDlpMock.Setup(s => s.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((YouTubeVideoData?)null);

        var service = CreateService();
        await service.ImportFromUrlAsync(url, 1, 1);

        // If it routed to YouTube pipeline, yt-dlp was called (even if it returned null)
        _ytDlpMock.Verify(y => y.ExtractVideoDataAsync(url, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("https://www.youtube.com/channel/UCabc")]
    [InlineData("https://www.youtube.com/user/SomeUser")]
    [InlineData("https://www.youtube.com/")]
    public async Task Pipeline_InvalidYouTubeUrls_DoNotRouteToYouTubePipeline(string url)
    {
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));
        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RecipeSchema?)null);

        var service = CreateService();
        await service.ImportFromUrlAsync(url, 1, 1);

        // Should NOT route to YouTube pipeline — yt-dlp never called
        _ytDlpMock.Verify(y => y.ExtractVideoDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
