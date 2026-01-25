using System.Text.Json;
using System.Text.Json.Serialization;
using AngleSharp.Html.Parser;
using FamilyCoordinationApp.Models.SchemaOrg;

namespace FamilyCoordinationApp.Services;

public interface IRecipeScraperService
{
    /// <summary>
    /// Fetches HTML content from URL with resilient HTTP client.
    /// </summary>
    Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts RecipeSchema from JSON-LD script tags in HTML.
    /// Returns null if no Recipe JSON-LD found.
    /// </summary>
    Task<RecipeSchema?> ExtractJsonLdAsync(string html, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches URL and extracts Recipe JSON-LD in one operation.
    /// </summary>
    Task<RecipeSchema?> ScrapeRecipeAsync(string url, CancellationToken cancellationToken = default);
}

public class RecipeScraperService : IRecipeScraperService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RecipeScraperService> _logger;
    private readonly HtmlParser _htmlParser;

    // Use lenient JSON options to handle edge cases from various recipe sites
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip
    };

    // Realistic User-Agent strings (rotate to avoid fingerprinting)
    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:123.0) Gecko/20100101 Firefox/123.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
    ];

    public RecipeScraperService(
        IHttpClientFactory httpClientFactory,
        ILogger<RecipeScraperService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _htmlParser = new HtmlParser();
    }

    public async Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient("RecipeScraper");

        // Set realistic User-Agent (critical for anti-bot)
        var userAgent = UserAgents[Random.Shared.Next(UserAgents.Length)];
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", userAgent);
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

        _logger.LogInformation("Fetching recipe from {Url}", url);

        var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<RecipeSchema?> ExtractJsonLdAsync(string html, CancellationToken cancellationToken = default)
    {
        var document = await _htmlParser.ParseDocumentAsync(html, cancellationToken);

        // Query for JSON-LD script tags
        var jsonLdScripts = document.QuerySelectorAll("script[type='application/ld+json']");

        foreach (var script in jsonLdScripts)
        {
            var jsonContent = script.TextContent;
            if (string.IsNullOrWhiteSpace(jsonContent))
                continue;

            try
            {
                var recipe = TryParseRecipeFromJson(jsonContent);
                if (recipe != null)
                {
                    _logger.LogInformation("Extracted recipe '{Name}' from JSON-LD", recipe.Name);
                    return recipe;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Failed to parse JSON-LD script: {Content}", jsonContent[..Math.Min(100, jsonContent.Length)]);
                // Try next script tag
            }
        }

        _logger.LogWarning("No Recipe JSON-LD found in HTML");
        return null;
    }

    public async Task<RecipeSchema?> ScrapeRecipeAsync(string url, CancellationToken cancellationToken = default)
    {
        var html = await FetchHtmlAsync(url, cancellationToken);
        return await ExtractJsonLdAsync(html, cancellationToken);
    }

    private static RecipeSchema? TryParseRecipeFromJson(string jsonContent)
    {
        var trimmed = jsonContent.Trim();

        // Handle array format: [{...}, {...}]
        if (trimmed.StartsWith('['))
        {
            var items = JsonSerializer.Deserialize<JsonElement[]>(trimmed);
            if (items != null)
            {
                foreach (var item in items)
                {
                    var recipe = TryParseRecipeElement(item);
                    if (recipe != null)
                        return recipe;
                }
            }
            return null;
        }

        // Handle single object format: {...}
        var element = JsonSerializer.Deserialize<JsonElement>(trimmed);
        return TryParseRecipeElement(element);
    }

    private static RecipeSchema? TryParseRecipeElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        // Check @type property
        if (element.TryGetProperty("@type", out var typeProperty))
        {
            // Handle @type as string or array (e.g., ["Recipe", "NewsArticle"])
            var isRecipe = false;
            if (typeProperty.ValueKind == JsonValueKind.String)
            {
                isRecipe = typeProperty.GetString() == "Recipe";
            }
            else if (typeProperty.ValueKind == JsonValueKind.Array)
            {
                foreach (var typeItem in typeProperty.EnumerateArray())
                {
                    if (typeItem.ValueKind == JsonValueKind.String && typeItem.GetString() == "Recipe")
                    {
                        isRecipe = true;
                        break;
                    }
                }
            }

            if (isRecipe)
            {
                return JsonSerializer.Deserialize<RecipeSchema>(element.GetRawText(), JsonOptions);
            }

            // Handle @graph structure: { "@graph": [{...Recipe...}] }
            if (element.TryGetProperty("@graph", out var graphProperty) && graphProperty.ValueKind == JsonValueKind.Array)
            {
                foreach (var graphItem in graphProperty.EnumerateArray())
                {
                    var recipe = TryParseRecipeElement(graphItem);
                    if (recipe != null)
                        return recipe;
                }
            }
        }

        // Check if @graph exists without @type at root
        if (element.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
        {
            foreach (var graphItem in graph.EnumerateArray())
            {
                var recipe = TryParseRecipeElement(graphItem);
                if (recipe != null)
                    return recipe;
            }
        }

        return null;
    }
}
