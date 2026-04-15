using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Models.SchemaOrg;

namespace FamilyCoordinationApp.Tests.Services;

public class GeminiRecipeExtractorTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<IConfiguration> _configMock;
    private readonly Mock<ILogger<GeminiRecipeExtractor>> _loggerMock;
    private readonly GeminiRecipeExtractor _extractor;

    public GeminiRecipeExtractorTests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _configMock = new Mock<IConfiguration>();
        _loggerMock = new Mock<ILogger<GeminiRecipeExtractor>>();

        _configMock.Setup(c => c["GEMINI_API_KEY"]).Returns("test-api-key");

        _extractor = new GeminiRecipeExtractor(
            _httpClientFactoryMock.Object,
            _configMock.Object,
            _loggerMock.Object);
    }

    private FakeHttpMessageHandler SetupMockClient(HttpStatusCode statusCode, string? responseBody = null)
    {
        var handler = new FakeHttpMessageHandler(statusCode, responseBody);
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/")
        };
        _httpClientFactoryMock.Setup(f => f.CreateClient("Gemini")).Returns(client);
        return handler;
    }

    private static string LoadFixture(string filename) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gemini", filename));

    // ── Successful extraction ─────────────────────────────────────────────────

    [Fact]
    public async Task ExtractFromTranscriptAsync_SuccessfulFixture_ReturnsPopulatedSchema()
    {
        var fixture = LoadFixture("successful-extraction.json");
        SetupMockClient(HttpStatusCode.OK, fixture);

        var result = await _extractor.ExtractFromTranscriptAsync(
            transcriptText: "Today we're making chocolate chip cookies...",
            videoTitle: "Best Chocolate Chip Cookies");

        result.Should().NotBeNull();
        result!.Name.Should().NotBeNullOrEmpty();
        result.RecipeIngredient.Should().NotBeNullOrEmpty();
        result.RecipeIngredient!.Length.Should().BeGreaterThanOrEqualTo(2);
        result.RecipeInstructions.Should().NotBeNull();
    }

    // ── No-recipe response ────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractFromTranscriptAsync_NoRecipeFixture_ReturnsNull()
    {
        var fixture = LoadFixture("no-recipe-found.json");
        SetupMockClient(HttpStatusCode.OK, fixture);

        var result = await _extractor.ExtractFromTranscriptAsync(
            transcriptText: "Welcome to my travel vlog. Today we visited the Eiffel Tower...");

        result.Should().BeNull();
    }

    // ── Malformed JSON in response ────────────────────────────────────────────

    [Fact]
    public async Task ExtractFromTranscriptAsync_MalformedJsonFixture_ReturnsNullWithoutException()
    {
        var fixture = LoadFixture("malformed-response.json");
        SetupMockClient(HttpStatusCode.OK, fixture);

        var act = async () => await _extractor.ExtractFromTranscriptAsync(
            transcriptText: "Let me show you how to make chocolate cake...");

        await act.Should().NotThrowAsync();
        var result = await _extractor.ExtractFromTranscriptAsync(
            transcriptText: "Let me show you how to make chocolate cake...");
        result.Should().BeNull();
    }

    // ── API key configuration ─────────────────────────────────────────────────

    [Fact]
    public async Task ExtractFromTranscriptAsync_MissingApiKey_ReturnsNull()
    {
        _configMock.Setup(c => c["GEMINI_API_KEY"]).Returns((string?)null);

        var result = await _extractor.ExtractFromTranscriptAsync("some transcript");

        result.Should().BeNull();
        _httpClientFactoryMock.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExtractFromTranscriptAsync_ReadsApiKeyFromConfiguration()
    {
        var fixture = LoadFixture("successful-extraction.json");
        SetupMockClient(HttpStatusCode.OK, fixture);

        await _extractor.ExtractFromTranscriptAsync("some transcript");

        _configMock.Verify(c => c["GEMINI_API_KEY"], Times.Once);
    }

    // ── HTTP error handling ───────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ExtractFromTranscriptAsync_HttpError_ReturnsNull(HttpStatusCode statusCode)
    {
        SetupMockClient(statusCode);

        var result = await _extractor.ExtractFromTranscriptAsync("some transcript");

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ExtractFromTranscriptAsync_HttpError_LogsWarning(HttpStatusCode statusCode)
    {
        SetupMockClient(statusCode);

        await _extractor.ExtractFromTranscriptAsync("some transcript");

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(((int)statusCode).ToString())),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // ── Prompt construction ───────────────────────────────────────────────────

    [Fact]
    public async Task ExtractFromTranscriptAsync_IncludesTranscriptInPrompt()
    {
        const string transcript = "First add two cups of flour to the bowl.";
        var fixture = LoadFixture("successful-extraction.json");
        var handler = SetupMockClient(HttpStatusCode.OK, fixture);

        await _extractor.ExtractFromTranscriptAsync(transcriptText: transcript);

        handler.CapturedRequestBody.Should().NotBeNull();
        var body = JsonDocument.Parse(handler.CapturedRequestBody!);
        var text = body.RootElement
            .GetProperty("contents")[0]
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        text.Should().Contain(transcript);
    }

    [Fact]
    public async Task ExtractFromTranscriptAsync_IncludesVideoTitleInPrompt()
    {
        const string title = "Amazing Pasta Recipe";
        var fixture = LoadFixture("successful-extraction.json");
        var handler = SetupMockClient(HttpStatusCode.OK, fixture);

        await _extractor.ExtractFromTranscriptAsync(
            transcriptText: "some transcript",
            videoTitle: title);

        handler.CapturedRequestBody.Should().NotBeNull();
        var body = JsonDocument.Parse(handler.CapturedRequestBody!);
        var text = body.RootElement
            .GetProperty("contents")[0]
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        text.Should().Contain(title);
    }

    [Fact]
    public async Task ExtractFromTranscriptAsync_TruncatesDescriptionTo500Chars()
    {
        var longDescription = new string('x', 600);
        var fixture = LoadFixture("successful-extraction.json");
        var handler = SetupMockClient(HttpStatusCode.OK, fixture);

        await _extractor.ExtractFromTranscriptAsync(
            transcriptText: "some transcript",
            videoDescription: longDescription);

        handler.CapturedRequestBody.Should().NotBeNull();
        var body = JsonDocument.Parse(handler.CapturedRequestBody!);
        var text = body.RootElement
            .GetProperty("contents")[0]
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        // The description in the prompt should be truncated to 500 chars
        text.Should().Contain(new string('x', 500));
        text.Should().NotContain(new string('x', 501));
    }

    // ── URL sanitization ──────────────────────────────────────────────────────

    [Fact]
    public void SanitizeUrl_StripsQueryString()
    {
        const string url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key=secret-api-key";

        var sanitized = GeminiRecipeExtractor.SanitizeUrl(url);

        sanitized.Should().Be("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent");
        sanitized.Should().NotContain("key=");
        sanitized.Should().NotContain("secret-api-key");
    }

    [Fact]
    public void SanitizeUrl_StripsFragment()
    {
        const string url = "https://example.com/path?query=value#fragment";

        var sanitized = GeminiRecipeExtractor.SanitizeUrl(url);

        sanitized.Should().Be("https://example.com/path");
    }

    [Fact]
    public void SanitizeUrl_RelativeUrl_ReturnsOriginal()
    {
        const string url = "not-a-url";

        var sanitized = GeminiRecipeExtractor.SanitizeUrl(url);

        sanitized.Should().Be(url);
    }

    // ── Fixture contract tests ────────────────────────────────────────────────

    [Fact]
    public void SuccessfulFixture_ContractTest_EnvelopeDeserializesCorrectly()
    {
        var fixture = LoadFixture("successful-extraction.json");
        using var doc = JsonDocument.Parse(fixture);

        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SuccessfulFixture_ContractTest_TextParsesAsRecipeSchema()
    {
        var fixture = LoadFixture("successful-extraction.json");
        using var doc = JsonDocument.Parse(fixture);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString()!;

        var schema = JsonSerializer.Deserialize<RecipeSchema>(text, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip
        });

        schema.Should().NotBeNull();
        schema!.Name.Should().NotBeNullOrEmpty();
        schema.RecipeIngredient.Should().NotBeNullOrEmpty();
        schema.RecipeIngredient!.Length.Should().BeGreaterThanOrEqualTo(2);

        // RecipeInstructions is object? — System.Text.Json deserializes arrays as JsonElement
        schema.RecipeInstructions.Should().NotBeNull();
        schema.RecipeInstructions.Should().BeOfType<JsonElement>();
        var instructions = (JsonElement)schema.RecipeInstructions!;
        instructions.ValueKind.Should().Be(JsonValueKind.Array);
        instructions.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void NoRecipeFixture_ContractTest_TextHasNullName()
    {
        var fixture = LoadFixture("no-recipe-found.json");
        using var doc = JsonDocument.Parse(fixture);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString()!;

        var schema = JsonSerializer.Deserialize<RecipeSchema>(text, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip
        });

        schema.Should().NotBeNull();
        schema!.Name.Should().BeNull();
    }

    [Fact]
    public void MalformedFixture_ContractTest_TextIsInvalidJson()
    {
        var fixture = LoadFixture("malformed-response.json");
        using var doc = JsonDocument.Parse(fixture);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString()!;

        var act = () => JsonDocument.Parse(text);

        act.Should().Throw<JsonException>();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _responseContent;

        public string? CapturedRequestBody { get; private set; }

        public FakeHttpMessageHandler(HttpStatusCode statusCode, string? responseContent = null)
        {
            _statusCode = statusCode;
            _responseContent = responseContent;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content != null)
                CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            var response = new HttpResponseMessage(_statusCode);
            if (_responseContent != null)
                response.Content = new StringContent(_responseContent, Encoding.UTF8, "application/json");

            return response;
        }
    }
}
