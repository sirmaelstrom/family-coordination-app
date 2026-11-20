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
    private readonly Mock<ICategoryInferenceService> _categoryInferenceMock;
    private readonly Mock<IYouTubeRecipeExtractor> _youtubeExtractorMock;
    private readonly Mock<ILogger<RecipeImportService>> _loggerMock;
    private readonly RecipeImportService _service;

    public RecipeImportServiceTests()
    {
        _urlValidatorMock = new Mock<IUrlValidator>();
        _scraperMock = new Mock<IRecipeScraperService>();
        _ingredientParserMock = new Mock<IIngredientParser>();
        _categoryInferenceMock = new Mock<ICategoryInferenceService>();
        _youtubeExtractorMock = new Mock<IYouTubeRecipeExtractor>();
        _loggerMock = new Mock<ILogger<RecipeImportService>>();

        _service = new RecipeImportService(
            _urlValidatorMock.Object,
            _scraperMock.Object,
            _ingredientParserMock.Object,
            _categoryInferenceMock.Object,
            _youtubeExtractorMock.Object,
            _loggerMock.Object,
            TestClocks.System);
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

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(new ScrapedRecipe(schema, new Uri(url)));

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

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(new ScrapedRecipe(schema, new Uri(url)));
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

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(new ScrapedRecipe(schema, new Uri(url)));
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

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(new ScrapedRecipe(schema, new Uri(url)));
        _ingredientParserMock.Setup(p => p.ParseIngredient(It.IsAny<string>()))
            .Returns<string>(s => new ParsedIngredient { Name = s });

        // Act
        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        // Assert
        result.Success.Should().BeTrue();
        result.Recipe!.ImagePath.Should().Be("https://example.com/imageobj.jpg");
    }

    // PR #171 review (quest c001677c): an ImageObject that names its file only as contentUrl produced no image.
    [Theory]
    [InlineData("{\"@type\":\"ImageObject\",\"contentUrl\":\"https://example.com/content.jpg\"}", "https://example.com/content.jpg")]
    [InlineData("[{\"@type\":\"ImageObject\",\"contentUrl\":\"https://example.com/first.jpg\"},\"https://example.com/second.jpg\"]", "https://example.com/first.jpg")]
    [InlineData("{\"@type\":\"ImageObject\",\"url\":\"https://example.com/url.jpg\",\"contentUrl\":\"https://example.com/content.jpg\"}", "https://example.com/url.jpg")]
    [InlineData("{\"@type\":\"ImageObject\",\"url\":{\"@id\":\"#primary\"},\"contentUrl\":\"https://example.com/content.jpg\"}", "https://example.com/content.jpg")]
    [InlineData("[{\"@type\":\"ImageObject\",\"width\":800},{\"@type\":\"ImageObject\",\"contentUrl\":\"https://example.com/later.jpg\"}]", "https://example.com/later.jpg")]
    [InlineData("{\"@type\":\"ImageObject\",\"width\":800}", null)]
    public async Task ImportFromUrlAsync_ExtractsImageUrl_FromImageObjectContentUrl(string imageJson, string? expectedImagePath)
    {
        const string url = "https://example.com/recipe";
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));
        var schema = new RecipeSchema
        {
            Name = "Test Recipe",
            RecipeIngredient = new[] { "1 cup flour" },
            Image = JsonSerializer.Deserialize<JsonElement>(imageJson)
        };
        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(new ScrapedRecipe(schema, new Uri(url)));
        _ingredientParserMock.Setup(p => p.ParseIngredient(It.IsAny<string>()))
            .Returns<string>(s => new ParsedIngredient { Name = s });

        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        result.Success.Should().BeTrue();
        result.Recipe!.ImagePath.Should().Be(expectedImagePath);
    }

    // PR #171 review (quest c001677c): the browser import passed a relative image through, so its preview asked the
    // app's own origin for a recipe site's picture. It resolves against the address the page was served from.
    [Theory]
    [InlineData("/images/apple.jpg", "https://www.example.com/images/apple.jpg")]
    [InlineData("apple.jpg", "https://www.example.com/desserts/fall/apple.jpg")]
    [InlineData("//cdn.example.net/apple.jpg", "https://cdn.example.net/apple.jpg")]
    [InlineData("https://images.example.org/apple.jpg", "https://images.example.org/apple.jpg")]
    [InlineData("data:image/png;base64,AAAA", null)]
    public async Task ImportFromUrlAsync_ResolvesARelativeImage_AgainstThePageItWasServedFrom(string image, string? expectedImagePath)
    {
        const string url = "https://example.com/recipes/apple-pie";
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));
        var schema = new RecipeSchema
        {
            Name = "Apple pie",
            RecipeIngredient = new[] { "6 apples" },
            Image = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(image))
        };
        // The site redirected the page to another host and path.
        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScrapedRecipe(schema, new Uri("https://www.example.com/desserts/fall/apple-pie")));
        _ingredientParserMock.Setup(p => p.ParseIngredient(It.IsAny<string>()))
            .Returns<string>(s => new ParsedIngredient { Name = s });

        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        result.Success.Should().BeTrue();
        result.Recipe!.ImagePath.Should().Be(expectedImagePath);
        result.Recipe.SourceUrl.Should().Be(url, "the source stays the address the member gave");
    }

    // PR #177 review 5984747882 (quest c001677c): an already absolute image URL was re-serialised as Uri.AbsoluteUri,
    // which percent-encodes a non-ASCII filename. A 431-character CDN URL became 551 characters, over the 500 the
    // recipe save accepts (ImagePathPolicy), so the browser's confirm of its own preview answered 400. An absolute
    // http(s) URL comes back as the page wrote it; only a relative one is rebuilt.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportFromUrlAsync_KeepsAnAbsoluteImageUrl_AsWritten_SoTheImportCanBeSaved(bool complete)
    {
        const string url = "https://example.com/recipes/matcha-cheesecake";
        var image = AbsoluteCdnImageWithJapaneseFilename();
        image.Length.Should().Be(431);
        new Uri(image).AbsoluteUri.Length.Should().Be(551, "percent-encoding the filename would push it past the save cap");
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));
        var schema = new RecipeSchema
        {
            Name = "Matcha cheesecake",
            RecipeIngredient = complete ? new[] { "200 g cream cheese" } : null,
            Image = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(image))
        };
        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(new ScrapedRecipe(schema, new Uri(url)));
        _ingredientParserMock.Setup(p => p.ParseIngredient(It.IsAny<string>()))
            .Returns<string>(s => new ParsedIngredient { Name = s });

        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        var previewed = complete ? result.Recipe!.ImagePath : result.PartialData!.ImageUrl;
        previewed.Should().Be(image);
        // What the SPA sends back on confirm: POST /api/recipes refuses it with a 400 unless this accepts it.
        ImagePathPolicy.TryNormalize(previewed, 1, out var saved).Should().BeTrue();
        saved.Should().Be(image);
    }

    private static string AbsoluteCdnImageWithJapaneseFilename()
    {
        var prefix = "https://cdn.example.jp/images/2026/11/抹茶のチーズケーキと黒蜜きなこ.jpg?w=1200&h=800&fit=crop&auto=format&sig=";
        return prefix + new string('a', 431 - prefix.Length);
    }

    // PR #177 class fix (quest c001677c, design.md in fca-177-classfix-2026-11-20): every image the import hands the
    // browser is one the save accepts (ImagePathPolicy, POST /api/recipes), or none. The image is dropped, never the
    // recipe. One row per producer class: relative (review 5987541661), over-long absolute, malformed reference,
    // ImageObject url/contentUrl, and arrays whose first entry is unusable.
    private const string ImportPage = "https://cdn.example.jp/recipes/matcha-cheesecake";
    private static readonly string OverLongAbsolute = "https://cdn.example.jp/images/" + new string('b', 600) + ".jpg";

    public static TheoryData<string, string, string?> ImageProducers()
    {
        var unicode = AbsoluteCdnImageWithJapaneseFilename();
        var unicodeRelative = unicode["https://cdn.example.jp".Length..]; // "/images/2026/11/抹茶….jpg?…&sig=aaa…"
        string Json(object value) => JsonSerializer.Serialize(value);
        return new()
        {
            { "relative, Unicode filename, signed query", Json(unicodeRelative), unicode },
            { "absolute over the save's 500", Json(OverLongAbsolute), null },
            { "malformed: //", Json("//"), null },
            { "malformed: bad port", Json("//cdn.example.jp:99999/x.jpg"), null },
            { "ImageObject relative Unicode contentUrl", Json(new Dictionary<string, string> { ["@type"] = "ImageObject", ["contentUrl"] = unicodeRelative }), unicode },
            { "ImageObject unusable url, usable contentUrl", Json(new Dictionary<string, string> { ["@type"] = "ImageObject", ["url"] = "data:image/png;base64,AAAA", ["contentUrl"] = "/images/apple.jpg" }), "https://cdn.example.jp/images/apple.jpg" },
            { "array: data: placeholder first", Json(new[] { "data:image/png;base64,AAAA", "/images/apple.jpg" }), "https://cdn.example.jp/images/apple.jpg" },
            { "array: over-long first", Json(new[] { OverLongAbsolute, "https://cdn.example.jp/ok.jpg" }), "https://cdn.example.jp/ok.jpg" },
        };
    }

    [Theory]
    [MemberData(nameof(ImageProducers))]
    public async Task ImportFromUrlAsync_ReturnsOnlyAnImageTheSaveAccepts_OrNone(string producer, string imageJson, string? expected)
    {
        foreach (var complete in new[] { true, false })
        {
            _urlValidatorMock.Setup(v => v.ValidateUrl(ImportPage)).Returns((true, null));
            var schema = new RecipeSchema
            {
                Name = "Matcha cheesecake",
                RecipeIngredient = complete ? new[] { "200 g cream cheese" } : null,
                Image = JsonSerializer.Deserialize<JsonElement>(imageJson)
            };
            _scraperMock.Setup(s => s.ScrapeRecipeAsync(ImportPage, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ScrapedRecipe(schema, new Uri(ImportPage)));
            _ingredientParserMock.Setup(p => p.ParseIngredient(It.IsAny<string>()))
                .Returns<string>(s => new ParsedIngredient { Name = s });

            var result = await _service.ImportFromUrlAsync(ImportPage, 1, 1);

            var because = $"{producer}, {(complete ? "complete" : "partial")} preview";
            result.Success.Should().Be(complete, because + ": the image never decides whether the recipe imports");
            var previewed = complete ? result.Recipe!.ImagePath : result.PartialData!.ImageUrl;
            previewed.Should().Be(expected, because);
            // What the SPA posts back on confirm: POST /api/recipes answers 400 unless this accepts it.
            ImagePathPolicy.TryNormalize(previewed, 1, out _).Should().BeTrue(because);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportFromUrlAsync_YouTubeThumbnail_IsKeptOnlyWhenTheSaveAcceptsIt(bool overLong)
    {
        // Producer P6: yt-dlp's thumbnail is stamped into the schema's image (YouTubeRecipeExtractor.StampVideoMetadata).
        const string url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
        var thumbnail = overLong ? OverLongAbsolute : "https://i.ytimg.com/vi/dQw4w9WgXcQ/maxresdefault.jpg";
        _youtubeExtractorMock.Setup(y => y.ExtractRecipeAsync(url, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecipeSchema { Name = "Video pie", RecipeIngredient = new[] { "6 apples" }, Image = thumbnail });
        _ingredientParserMock.Setup(p => p.ParseIngredient(It.IsAny<string>()))
            .Returns<string>(s => new ParsedIngredient { Name = s });

        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        result.Success.Should().BeTrue();
        result.Recipe!.ImagePath.Should().Be(overLong ? null : thumbnail);
        ImagePathPolicy.TryNormalize(result.Recipe.ImagePath, 1, out _).Should().BeTrue();
    }

    [Fact]
    public async Task ImportFromUrlAsync_PartialData_CarriesTheResolvedImage()
    {
        const string url = "https://example.com/recipes/soup";
        _urlValidatorMock.Setup(v => v.ValidateUrl(url)).Returns((true, null));
        var schema = new RecipeSchema
        {
            Name = "Soup",
            RecipeIngredient = null,
            Image = JsonSerializer.Deserialize<JsonElement>("\"/images/soup.jpg\"")
        };
        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(new ScrapedRecipe(schema, new Uri(url)));

        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        result.Success.Should().BeFalse();
        result.PartialData!.ImageUrl.Should().Be("https://example.com/images/soup.jpg");
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
        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(new ScrapedRecipe(null, new Uri(url)));

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

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(new ScrapedRecipe(schema, new Uri(url)));

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

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(new ScrapedRecipe(schema, new Uri(url)));
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

        _scraperMock.Setup(s => s.ScrapeRecipeAsync(url, It.IsAny<CancellationToken>())).ReturnsAsync(new ScrapedRecipe(schema, new Uri(url)));
        _ingredientParserMock.Setup(p => p.ParseIngredient(It.IsAny<string>()))
            .Returns<string>(s => new ParsedIngredient { Name = s });

        // Act
        var result = await _service.ImportFromUrlAsync(url, 1, 1);

        // Assert
        result.Success.Should().BeTrue();
        result.Recipe!.Servings.Should().Be(expectedServings);
    }
}
