using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;
using FamilyCoordinationApp.Models.SchemaOrg;

namespace FamilyCoordinationApp.Tests.Services;

public class RecipeImportServiceTests
{
    private readonly Mock<IUrlValidator> _urlValidatorMock;
    private readonly Mock<IRecipeScraperService> _scraperMock;
    private readonly Mock<IIngredientParser> _ingredientParserMock;
    private readonly Mock<ILogger<RecipeImportService>> _loggerMock;
    private readonly RecipeImportService _service;

    public RecipeImportServiceTests()
    {
        _urlValidatorMock = new Mock<IUrlValidator>();
        _scraperMock = new Mock<IRecipeScraperService>();
        _ingredientParserMock = new Mock<IIngredientParser>();
        _loggerMock = new Mock<ILogger<RecipeImportService>>();

        _service = new RecipeImportService(
            _urlValidatorMock.Object,
            _scraperMock.Object,
            _ingredientParserMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task ImportFromUrlAsync_ValidUrl_ExtractsRecipe()
    {
        // Arrange
        const string url = "https://www.allrecipes.com/recipe/21014/good-old-fashioned-pancakes/";
        const int householdId = 1;
        const int userId = 1;

        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));

        var schema = new RecipeSchema
        {
            Name = "Good Old-Fashioned Pancakes",
            Description = "Fluffy pancakes",
            RecipeIngredient = new[] { "1 cup flour", "1 egg", "1 cup milk" },
            RecipeInstructions = JsonSerializer.Deserialize<JsonElement>("[{\"@type\": \"HowToStep\", \"text\": \"Mix ingredients\"}, {\"@type\": \"HowToStep\", \"text\": \"Cook pancakes\"}]"),
            RecipeYield = JsonSerializer.Deserialize<JsonElement>("\"4 servings\""),
            PrepTime = "PT10M",
            CookTime = "PT15M",
            Image = JsonSerializer.Deserialize<JsonElement>("\"https://example.com/pancakes.jpg\"")
        };

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(schema);

        _ingredientParserMock.Setup(p => p.ParseIngredient(It.IsAny<string>()))
            .Returns<string>(s => new ParsedIngredient { Name = s });

        // Act
        var result = await _service.ImportFromUrlAsync(url, householdId, userId);

        // Assert
        result.Success.Should().BeTrue();
        result.Recipe.Should().NotBeNull();
        result.Recipe!.Name.Should().Be("Good Old-Fashioned Pancakes");
        result.Recipe.Description.Should().Be("Fluffy pancakes");
        result.Recipe.SourceUrl.Should().Be(url);
        result.Recipe.ImagePath.Should().Be("https://example.com/pancakes.jpg");
        result.Recipe.Servings.Should().Be(4);
        result.Recipe.PrepTimeMinutes.Should().Be(10);
        result.Recipe.CookTimeMinutes.Should().Be(15);
        result.Recipe.Ingredients.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("https://example.com/image.jpg", "https://example.com/image.jpg")]
    [InlineData(null, null)]
    public async Task ImportFromUrlAsync_ExtractsImageUrl_FromStringFormat(string? imageUrl, string? expectedImagePath)
    {
        // Arrange
        const string url = "https://example.com/recipe";
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));

        var schema = new RecipeSchema
        {
            Name = "Test Recipe",
            RecipeIngredient = new[] { "1 cup flour" },
            Image = imageUrl != null ? JsonSerializer.Deserialize<JsonElement>($"\"{imageUrl}\"") : null
        };

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(schema);
        _ingredientParserMock.Setup(p => p.ParseIngredient(It.IsAny<string>()))
            .Returns<string>(s => new ParsedIngredient { Name = s });

        // Act
        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        // Assert
        result.Success.Should().BeTrue();
        result.Recipe!.ImagePath.Should().Be(expectedImagePath);
    }

    [Fact]
    public async Task ImportFromUrlAsync_ExtractsImageUrl_FromArrayFormat()
    {
        // Arrange
        const string url = "https://example.com/recipe";
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));

        var schema = new RecipeSchema
        {
            Name = "Test Recipe",
            RecipeIngredient = new[] { "1 cup flour" },
            Image = JsonSerializer.Deserialize<JsonElement>("[\"https://example.com/first.jpg\", \"https://example.com/second.jpg\"]")
        };

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(schema);
        _ingredientParserMock.Setup(p => p.ParseIngredient(It.IsAny<string>()))
            .Returns<string>(s => new ParsedIngredient { Name = s });

        // Act
        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        // Assert
        result.Success.Should().BeTrue();
        result.Recipe!.ImagePath.Should().Be("https://example.com/first.jpg");
    }

    [Fact]
    public async Task ImportFromUrlAsync_ExtractsImageUrl_FromImageObjectFormat()
    {
        // Arrange
        const string url = "https://example.com/recipe";
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));

        var schema = new RecipeSchema
        {
            Name = "Test Recipe",
            RecipeIngredient = new[] { "1 cup flour" },
            Image = JsonSerializer.Deserialize<JsonElement>("{\"@type\": \"ImageObject\", \"url\": \"https://example.com/imageobj.jpg\"}")
        };

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(schema);
        _ingredientParserMock.Setup(p => p.ParseIngredient(It.IsAny<string>()))
            .Returns<string>(s => new ParsedIngredient { Name = s });

        // Act
        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        // Assert
        result.Success.Should().BeTrue();
        result.Recipe!.ImagePath.Should().Be("https://example.com/imageobj.jpg");
    }

    [Fact]
    public async Task ImportFromUrlAsync_InvalidUrl_ReturnsError()
    {
        // Arrange
        const string url = "http://internal.server/recipe";
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((false, "URL not allowed"));

        // Act
        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorType.Should().Be(RecipeImportErrorType.InvalidUrl);
        result.ErrorMessage.Should().Contain("URL not allowed");
    }

    [Fact]
    public async Task ImportFromUrlAsync_NoRecipeFound_ReturnsParsingError()
    {
        // Arrange
        const string url = "https://example.com/not-a-recipe";
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));
        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync((RecipeSchema?)null);

        // Act
        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorType.Should().Be(RecipeImportErrorType.ParsingFailed);
    }

    [Fact]
    public async Task ImportFromUrlAsync_MissingName_ReturnsValidationError()
    {
        // Arrange
        const string url = "https://example.com/recipe";
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));

        var schema = new RecipeSchema
        {
            Name = null,
            RecipeIngredient = new[] { "1 cup flour" }
        };

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(schema);

        // Act
        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorType.Should().Be(RecipeImportErrorType.ValidationFailed);
        result.PartialData.Should().NotBeNull();
    }

    [Theory]
    [InlineData("PT15M", 15)]
    [InlineData("PT1H", 60)]
    [InlineData("PT1H30M", 90)]
    [InlineData("PT2H15M", 135)]
    [InlineData("pt45m", 45)]
    [InlineData("invalid", null)]
    [InlineData(null, null)]
    public async Task ImportFromUrlAsync_ParsesIsoDuration_Correctly(string? duration, int? expectedMinutes)
    {
        // Arrange
        const string url = "https://example.com/recipe";
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));

        var schema = new RecipeSchema
        {
            Name = "Test Recipe",
            RecipeIngredient = new[] { "1 cup flour" },
            PrepTime = duration
        };

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(schema);
        _ingredientParserMock.Setup(p => p.ParseIngredient(It.IsAny<string>()))
            .Returns<string>(s => new ParsedIngredient { Name = s });

        // Act
        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        // Assert
        result.Success.Should().BeTrue();
        result.Recipe!.PrepTimeMinutes.Should().Be(expectedMinutes);
    }

    [Theory]
    [InlineData("4 servings", 4)]
    [InlineData("Makes 6", 6)]
    [InlineData("12", 12)]
    [InlineData("Serves 8 people", 8)]
    [InlineData(null, null)]
    public async Task ImportFromUrlAsync_ParsesServings_Correctly(string? yieldStr, int? expectedServings)
    {
        // Arrange
        const string url = "https://example.com/recipe";
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));

        var schema = new RecipeSchema
        {
            Name = "Test Recipe",
            RecipeIngredient = new[] { "1 cup flour" },
            RecipeYield = yieldStr != null ? JsonSerializer.Deserialize<JsonElement>($"\"{yieldStr}\"") : null
        };

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(schema);
        _ingredientParserMock.Setup(p => p.ParseIngredient(It.IsAny<string>()))
            .Returns<string>(s => new ParsedIngredient { Name = s });

        // Act
        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        // Assert
        result.Success.Should().BeTrue();
        result.Recipe!.Servings.Should().Be(expectedServings);
    }
}
