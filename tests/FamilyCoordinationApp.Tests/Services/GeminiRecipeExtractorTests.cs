using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class GeminiRecipeExtractorTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gemini", name));

    private static GeminiRecipeExtractor BuildExtractor(
        HttpResponseMessage response,
        Action<HttpRequestMessage, string>? captureRequest = null,
        string apiKey = "test-key-xyz")
    {
        var handler = new CapturingHandler(response, captureRequest);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/")
        };

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Gemini")).Returns(httpClient);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GEMINI_API_KEY"] = apiKey
            })
            .Build();

        return new GeminiRecipeExtractor(factory.Object, config, NullLogger<GeminiRecipeExtractor>.Instance);
    }

    private static HttpResponseMessage MakeJsonResponse(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ExtractFromTranscriptAsync_SuccessFixture_ReturnsSchema()
    {
        var body = Fixture("successful-extraction.json");
        var extractor = BuildExtractor(MakeJsonResponse(HttpStatusCode.OK, body));

        var schema = await extractor.ExtractFromTranscriptAsync("a long transcript", "Tomato Soup", "description");

        schema.Should().NotBeNull();
        schema!.Name.Should().Be("Classic Tomato Soup");
        schema.RecipeIngredient.Should().NotBeNull();
        schema.RecipeIngredient!.Length.Should().BeGreaterThan(2);
        schema.RecipeYield.Should().NotBeNull();
    }

    [Fact]
    public async Task ExtractFromTranscriptAsync_NoRecipeFixture_ReturnsNull()
    {
        var body = Fixture("no-recipe-found.json");
        var extractor = BuildExtractor(MakeJsonResponse(HttpStatusCode.OK, body));

        var schema = await extractor.ExtractFromTranscriptAsync("transcript");
        schema.Should().BeNull();
    }

    [Fact]
    public async Task ExtractFromTranscriptAsync_MalformedFixture_ReturnsNull()
    {
        var body = Fixture("malformed-response.json");
        var extractor = BuildExtractor(MakeJsonResponse(HttpStatusCode.OK, body));

        var schema = await extractor.ExtractFromTranscriptAsync("transcript");
        schema.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ExtractFromTranscriptAsync_HttpError_ReturnsNull(HttpStatusCode status)
    {
        var extractor = BuildExtractor(MakeJsonResponse(status, "{\"error\":\"bad\"}"));
        var schema = await extractor.ExtractFromTranscriptAsync("transcript");
        schema.Should().BeNull();
    }

    [Fact]
    public async Task ExtractFromTranscriptAsync_MissingApiKey_ReturnsNull()
    {
        var extractor = BuildExtractor(MakeJsonResponse(HttpStatusCode.OK, "{}"), apiKey: "");
        var schema = await extractor.ExtractFromTranscriptAsync("transcript");
        schema.Should().BeNull();
    }

    [Fact]
    public async Task ExtractFromTranscriptAsync_EmptyTranscript_ReturnsNull()
    {
        var extractor = BuildExtractor(MakeJsonResponse(HttpStatusCode.OK, "{}"));
        (await extractor.ExtractFromTranscriptAsync("")).Should().BeNull();
        (await extractor.ExtractFromTranscriptAsync("   ")).Should().BeNull();
    }

    [Fact]
    public async Task ExtractFromTranscriptAsync_BuildsPromptWithTranscriptAndTitle()
    {
        string? capturedBody = null;
        var extractor = BuildExtractor(
            MakeJsonResponse(HttpStatusCode.OK, Fixture("successful-extraction.json")),
            (_, body) => capturedBody = body);

        await extractor.ExtractFromTranscriptAsync("salt and pepper to taste", "My Cool Recipe", "video description");

        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("salt and pepper to taste");
        capturedBody.Should().Contain("My Cool Recipe");
        capturedBody.Should().Contain("application/json");
    }

    [Fact]
    public void SanitizeUrl_StripsQueryParameters()
    {
        var raw = "https://generativelanguage.googleapis.com/v1beta/models/gemini:generate?key=SECRET123&alt=json";
        var sanitized = GeminiRecipeExtractor.SanitizeUrl(raw);

        sanitized.Should().NotContain("SECRET123");
        sanitized.Should().NotContain("key=");
        sanitized.Should().StartWith("https://generativelanguage.googleapis.com/");
        sanitized.Should().Contain("/v1beta/models/gemini:generate");
    }

    [Fact]
    public void BuildPrompt_IncludesTitleAndTranscript()
    {
        var prompt = GeminiRecipeExtractor.BuildPrompt("mix the flour", "Bread", "desc");
        prompt.Should().Contain("mix the flour");
        prompt.Should().Contain("Bread");
    }

    private class CapturingHandler(HttpResponseMessage response, Action<HttpRequestMessage, string>? capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
            capture?.Invoke(request, body);
            return response;
        }
    }
}
