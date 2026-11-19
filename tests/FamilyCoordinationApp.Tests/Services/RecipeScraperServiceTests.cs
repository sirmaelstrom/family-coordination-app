using System.Net;
using System.Text;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// The browser import's scrape reports the address the page was finally served from (quest c001677c), because that,
/// not the URL the member typed, is the base its relative image resolves against.
/// </summary>
public class RecipeScraperServiceTests
{
    private const string Page = """
        <html><head><script type="application/ld+json">
        {"@context":"https://schema.org","@type":"Recipe","name":"Apple pie","image":"apple.png",
         "recipeIngredient":["6 apples"],"recipeInstructions":"Bake it."}
        </script></head><body></body></html>
        """;

    /// <summary>Answers every request with the page, as if the client had followed redirects to <paramref name="servedFrom"/>.</summary>
    private sealed class RedirectedHandler(Uri? servedFrom) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Page, Encoding.UTF8, "text/html"),
                // HttpClient's redirect handling leaves the LAST hop's request on the response.
                RequestMessage = servedFrom is null ? request : new HttpRequestMessage(HttpMethod.Get, servedFrom),
            });
    }

    private static RecipeScraperService Scraper(Uri? servedFrom)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("RecipeScraper")).Returns(() => new HttpClient(new RedirectedHandler(servedFrom)));
        return new RecipeScraperService(factory.Object, NullLogger<RecipeScraperService>.Instance);
    }

    [Fact]
    public async Task ScrapeRecipeAsync_reports_the_address_the_page_was_served_from_after_a_redirect()
    {
        var scraped = await Scraper(new Uri("https://www.example.com/desserts/fall/apple-pie"))
            .ScrapeRecipeAsync("https://example.com/recipes/apple-pie");

        scraped.Schema!.Name.Should().Be("Apple pie");
        scraped.PageUrl.Should().Be(new Uri("https://www.example.com/desserts/fall/apple-pie"));
    }

    [Fact]
    public async Task ScrapeRecipeAsync_reports_the_url_asked_for_when_nothing_redirected()
    {
        var scraped = await Scraper(servedFrom: null).ScrapeRecipeAsync("https://example.com/recipes/apple-pie");

        scraped.PageUrl.Should().Be(new Uri("https://example.com/recipes/apple-pie"));
    }
}
