using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FamilyCoordinationApp.Models.SchemaOrg;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

public class GeminiRecipeExtractor(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<GeminiRecipeExtractor> logger) : IGeminiRecipeExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public async Task<RecipeSchema?> ExtractFromTranscriptAsync(
        string transcriptText,
        string? videoTitle = null,
        string? videoDescription = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = configuration["GEMINI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("GEMINI_API_KEY is not configured — skipping Gemini extraction");
            return null;
        }

        var requestUrl = $"v1beta/models/gemini-2.0-flash:generateContent?key={apiKey}";
        var prompt = BuildPrompt(transcriptText, videoTitle, videoDescription);

        var requestBody = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                temperature = 0.1
            }
        };

        var client = httpClientFactory.CreateClient("Gemini");

        try
        {
            var jsonContent = JsonSerializer.Serialize(requestBody);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(requestUrl, httpContent, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Gemini API returned {StatusCode}", (int)response.StatusCode);
                return null;
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseGeminiResponse(responseBody);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Gemini API call failed");
            return null;
        }
    }

    private RecipeSchema? ParseGeminiResponse(string responseBody)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<GeminiResponse>(responseBody, JsonOptions);
            var text = envelope?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("Gemini returned empty or missing response text");
                return null;
            }

            text = StripMarkdownFences(text);

            var recipe = JsonSerializer.Deserialize<RecipeSchema>(text, JsonOptions);

            if (recipe?.Name is null)
            {
                logger.LogInformation("Gemini found no recipe in transcript");
                return null;
            }

            return recipe;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse Gemini response JSON");
            return null;
        }
    }

    /// <summary>
    /// Returns scheme + host + path only, stripping query string and fragment.
    /// Used to sanitize URLs before logging to prevent API key leakage.
    /// </summary>
    internal static string SanitizeUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        return url;
    }

    private static string StripMarkdownFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```"))
            return text;

        var newlineIndex = trimmed.IndexOf('\n');
        var closingFence = trimmed.LastIndexOf("```");
        if (newlineIndex > 0 && closingFence > newlineIndex)
            return trimmed[(newlineIndex + 1)..closingFence].Trim();

        return text;
    }

    private static string BuildPrompt(string transcriptText, string? videoTitle, string? videoDescription)
    {
        var descriptionContext = videoDescription is { Length: > 500 }
            ? videoDescription[..500]
            : videoDescription ?? string.Empty;

        return $$"""
            You are a recipe extraction assistant. Extract the recipe from the following cooking video transcript.

            Return a JSON object with these fields:
            - "name": recipe name (string)
            - "recipeIngredient": array of ingredient strings, each in "quantity unit ingredient" format (e.g., "2 cups all-purpose flour")
            - "recipeInstructions": array of step strings, each a complete instruction
            - "prepTime": ISO 8601 duration (e.g., "PT15M") or null
            - "cookTime": ISO 8601 duration (e.g., "PT30M") or null
            - "recipeYield": servings as string (e.g., "4 servings") or null

            If the transcript does not contain a recipe, return: {"name": null}

            Ignore non-recipe content (stories, sponsors, commentary). Extract only recipe information.

            Video title: {{videoTitle ?? "Unknown"}}
            Video description (partial context): {{descriptionContext}}

            Transcript:
            {{transcriptText}}
            """;
    }

    private class GeminiResponse
    {
        [JsonPropertyName("candidates")]
        public GeminiCandidate[]? Candidates { get; set; }
    }

    private class GeminiCandidate
    {
        [JsonPropertyName("content")]
        public GeminiContent? Content { get; set; }
    }

    private class GeminiContent
    {
        [JsonPropertyName("parts")]
        public GeminiPart[]? Parts { get; set; }
    }

    private class GeminiPart
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
