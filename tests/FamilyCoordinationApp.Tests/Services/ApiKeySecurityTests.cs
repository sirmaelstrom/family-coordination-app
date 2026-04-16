using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class ApiKeySecurityTests
{
    [Fact]
    public async Task GeminiService_DoesNotLogApiKey()
    {
        const string testApiKey = "TEST_KEY_SECRET_12345";
        var logger = new CapturingLogger<GeminiRecipeExtractor>();

        var handler = new StubHandler(HttpStatusCode.InternalServerError, "Server Error");
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/")
        };

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Gemini")).Returns(httpClient);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GEMINI_API_KEY"] = testApiKey
            })
            .Build();

        var extractor = new GeminiRecipeExtractor(factory.Object, config, logger);

        await extractor.ExtractFromTranscriptAsync("Some transcript text about cooking pasta");

        logger.Entries.Should().NotBeEmpty("the service should log the error");
        logger.Entries.Should().NotContain(e => e.Contains(testApiKey),
            "API key must never appear in log output");
    }

    [Fact]
    public void GeminiUrl_QueryParametersStripped_InLogOutput()
    {
        var raw = "https://generativelanguage.googleapis.com/v1beta/models/flash:gen?key=SECRET123&alt=json";

        var sanitized = GeminiRecipeExtractor.SanitizeUrl(raw);

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
        dockerfile.Should().NotContain("ARG GEMINI_API_KEY=");
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("Could not find repo root (.git directory)");
    }

    private class StubHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/plain")
            });
    }

    private class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
            if (exception != null)
                Entries.Add(exception.ToString());
        }
    }
}
