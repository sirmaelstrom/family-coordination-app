using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class GeminiRecipeExtractorTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock = new();
    private readonly Mock<IConfiguration> _configMock = new();
    private readonly Mock<ILogger<GeminiRecipeExtractor>> _loggerMock = new();
    private const string TestApiKey = "unit-test-gemini-credential";

    public GeminiRecipeExtractorTests()
    {
        _configMock.Setup(c => c["GEMINI_API_KEY"]).Returns(TestApiKey);
    }

    private GeminiRecipeExtractor CreateService() =>
        new(_httpClientFactoryMock.Object, _configMock.Object, _loggerMock.Object);

    private void SetupHttpClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") };
        _httpClientFactoryMock.Setup(f => f.CreateClient("Gemini")).Returns(client);
    }

    private static string LoadFixture(string filename) =>
        File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "Gemini", filename));

    // --- Fixture contract tests ---

    [Fact]
    public async Task ExtractFromTranscriptAsync_SuccessfulFixture_ReturnsRecipeWithNameAndIngredients()
    {
        var fixtureJson = LoadFixture("successful-extraction.json");
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(fixtureJson, Encoding.UTF8, "application/json")
            }));
        SetupHttpClient(handler);

        var result = await CreateService().ExtractFromTranscriptAsync("some transcript");

        result.Should().NotBeNull();
        result!.Name.Should().NotBeNullOrWhiteSpace();
        result.RecipeIngredient.Should().NotBeNullOrEmpty();
        result.RecipeIngredient!.Length.Should().BeGreaterThanOrEqualTo(2);
        result.RecipeInstructions.Should().NotBeNull();
        result.RecipeInstructions.Should().BeOfType<JsonElement>();
        var instructions = (JsonElement)result.RecipeInstructions!;
        instructions.ValueKind.Should().Be(JsonValueKind.Array);
        instructions.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ExtractFromTranscriptAsync_NoRecipeFixture_ReturnsNull()
    {
        var fixtureJson = LoadFixture("no-recipe-found.json");
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(fixtureJson, Encoding.UTF8, "application/json")
            }));
        SetupHttpClient(handler);

        var result = await CreateService().ExtractFromTranscriptAsync("a vlog with no recipe");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExtractFromTranscriptAsync_MalformedJsonFixture_ReturnsNullWithoutException()
    {
        var fixtureJson = LoadFixture("malformed-response.json");
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(fixtureJson, Encoding.UTF8, "application/json")
            }));
        SetupHttpClient(handler);

        var act = () => CreateService().ExtractFromTranscriptAsync("some transcript");

        await act.Should().NotThrowAsync();
        var result = await CreateService().ExtractFromTranscriptAsync("some transcript");
        result.Should().BeNull();
    }

    // --- Fixture envelope structure test ---

    [Fact]
    public void SuccessfulFixture_EnvelopeStructure_IsValid()
    {
        var fixtureJson = LoadFixture("successful-extraction.json");
        using var doc = JsonDocument.Parse(fixtureJson);

        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        text.Should().NotBeNullOrWhiteSpace();

        var recipe = JsonSerializer.Deserialize<JsonElement>(text!);
        recipe.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();
        recipe.GetProperty("recipeIngredient").ValueKind.Should().Be(JsonValueKind.Array);
        recipe.GetProperty("recipeInstructions").ValueKind.Should().Be(JsonValueKind.Array);
    }

    // --- Configuration tests ---

    [Fact]
    public async Task ExtractFromTranscriptAsync_MissingApiKey_ReturnsNullWithoutHttpCall()
    {
        _configMock.Setup(c => c["GEMINI_API_KEY"]).Returns((string?)null);

        var result = await CreateService().ExtractFromTranscriptAsync("some transcript");

        result.Should().BeNull();
        _httpClientFactoryMock.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExtractFromTranscriptAsync_ApiKeyIncludedInRequestUrl()
    {
        string? capturedUri = null;
        var handler = new MockHttpMessageHandler(async (req, _) =>
        {
            capturedUri = req.RequestUri?.ToString();
            await Task.CompletedTask;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(LoadFixture("successful-extraction.json"), Encoding.UTF8, "application/json")
            };
        });
        SetupHttpClient(handler);

        await CreateService().ExtractFromTranscriptAsync("transcript");

        capturedUri.Should().Contain($"key={TestApiKey}");
    }

    // --- HTTP error handling tests ---

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task ExtractFromTranscriptAsync_HttpError_ReturnsNull(int statusCode)
    {
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage((HttpStatusCode)statusCode)));
        SetupHttpClient(handler);

        var result = await CreateService().ExtractFromTranscriptAsync("some transcript");

        result.Should().BeNull();
    }

    // --- Prompt construction tests ---

    [Fact]
    public async Task ExtractFromTranscriptAsync_PromptContainsTranscriptAndTitle()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(async (req, _) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(LoadFixture("successful-extraction.json"), Encoding.UTF8, "application/json")
            };
        });
        SetupHttpClient(handler);

        const string transcript = "Today we are making spaghetti carbonara";
        const string title = "Best Carbonara Recipe Ever";

        await CreateService().ExtractFromTranscriptAsync(transcript, videoTitle: title);

        capturedBody.Should().NotBeNull();
        var body = JsonSerializer.Deserialize<JsonElement>(capturedBody!);
        var promptText = body
            .GetProperty("contents")[0]
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        promptText.Should().Contain(transcript);
        promptText.Should().Contain(title);
    }

    [Fact]
    public async Task ExtractFromTranscriptAsync_PromptTruncatesLongDescription()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(async (req, _) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(LoadFixture("successful-extraction.json"), Encoding.UTF8, "application/json")
            };
        });
        SetupHttpClient(handler);

        var longDescription = new string('x', 1000);

        await CreateService().ExtractFromTranscriptAsync("transcript", videoDescription: longDescription);

        capturedBody.Should().NotBeNull();
        var body = JsonSerializer.Deserialize<JsonElement>(capturedBody!);
        var promptText = body
            .GetProperty("contents")[0]
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        // Description should be truncated to 500 chars
        promptText.Should().Contain(new string('x', 500));
        promptText.Should().NotContain(new string('x', 501));
    }

    // --- URL sanitization tests ---

    [Theory]
    [InlineData(
        "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key=secret123",
        "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent")]
    [InlineData(
        "https://example.com/path?query=value&key=secret",
        "https://example.com/path")]
    [InlineData(
        "https://example.com/path#fragment",
        "https://example.com/path")]
    public void SanitizeUrl_StripsQueryStringAndFragment(string input, string expected)
    {
        GeminiRecipeExtractor.SanitizeUrl(input).Should().Be(expected);
    }

    [Fact]
    public void SanitizeUrl_RelativeUrl_ReturnsOriginal()
    {
        const string relative = "not-a-url";
        GeminiRecipeExtractor.SanitizeUrl(relative).Should().Be(relative);
    }

    // --- Markdown fence stripping ---

    [Fact]
    public async Task ExtractFromTranscriptAsync_MarkdownFencedJson_ParsedCorrectly()
    {
        // Simulates Gemini ignoring responseMimeType and wrapping output in fences
        const string recipeJson = """{"name":"Pasta","recipeIngredient":["1 cup pasta"],"recipeInstructions":["Cook pasta."]}""";
        var fencedText = $"```json\n{recipeJson}\n```";

        var envelope = $$"""
            {
              "candidates": [{
                "content": {
                  "parts": [{"text": {{JsonSerializer.Serialize(fencedText)}} }],
                  "role": "model"
                },
                "finishReason": "STOP"
              }]
            }
            """;

        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json")
            }));
        SetupHttpClient(handler);

        var result = await CreateService().ExtractFromTranscriptAsync("some transcript");

        result.Should().NotBeNull();
        result!.Name.Should().Be("Pasta");
    }
}

file class MockHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => handler(request, cancellationToken);
}
