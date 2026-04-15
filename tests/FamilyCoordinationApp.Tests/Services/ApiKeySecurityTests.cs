using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class ApiKeySecurityTests
{
    // Helper: walk up from test assembly to find repo root (directory containing .git file or directory)
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var gitPath = Path.Combine(dir, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not find repo root (.git file or directory)");
    }

    // Helper: test logger that captures log entries
    private class CapturingLogger<T> : ILogger<T>
    {
        public List<string> LogEntries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LogEntries.Add(formatter(state, exception));
        }
    }

    private class MockHttpMessageHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(content) });
    }

    [Fact]
    public async Task GeminiService_DoesNotLogApiKey()
    {
        // Arrange
        const string testApiKey = "TEST_KEY_SECRET_12345";
        var logger = new CapturingLogger<GeminiRecipeExtractor>();

        var mockHandler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError, "Server Error");
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/")
        };

        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("Gemini")).Returns(httpClient);

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["GEMINI_API_KEY"]).Returns(testApiKey);

        var extractor = new GeminiRecipeExtractor(factoryMock.Object, configMock.Object, logger);

        // Act
        await extractor.ExtractFromTranscriptAsync("Some transcript text about cooking pasta");

        // Assert — the API key must NOT appear in any log entry
        logger.LogEntries.Should().NotBeEmpty("the service should log the error");
        logger.LogEntries.Should().NotContain(entry => entry.Contains(testApiKey),
            "API key must never appear in log output");
    }

    [Fact]
    public void GeminiUrl_QueryParametersStripped_InLogOutput()
    {
        var rawUrl = "https://generativelanguage.googleapis.com/v1beta/models/flash:gen?key=SECRET123&alt=json";

        var sanitized = GeminiRecipeExtractor.SanitizeUrl(rawUrl);

        sanitized.Should().NotContain("SECRET123", "API key must be redacted from logged URLs");
        sanitized.Should().NotContain("key=", "key parameter must be stripped or redacted");
        sanitized.Should().StartWith("https://generativelanguage.googleapis.com/",
            "base URL should be preserved for debugging context");
    }

    [Fact]
    public void Gitignore_ContainsEnvPattern()
    {
        var gitignore = File.ReadAllText(Path.Combine(GetRepoRoot(), ".gitignore"));
        gitignore.Should().Contain(".env");
    }

    [Fact]
    public void Dockerfile_DoesNotBakeApiKey()
    {
        var dockerfile = File.ReadAllText(Path.Combine(GetRepoRoot(), "Dockerfile"));
        dockerfile.Should().NotContain("ENV GEMINI");
        dockerfile.Should().NotContain("ARG GEMINI_API_KEY="); // Also catch ARG with default value
    }
}
