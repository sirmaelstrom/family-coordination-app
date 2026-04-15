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
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    private const string ExtractionPromptTemplate =
        """
        You are a recipe extraction assistant. Extract the recipe from the following cooking video transcript.

        Return a JSON object with these fields:
        - "name": recipe name (string)
        - "recipeIngredient": array of ingredient strings, each in "quantity unit ingredient" format (e.g., "2 cups all-purpose flour")
        - "recipeInstructions": array of step strings, each a complete instruction
        - "prepTime": ISO 8601 duration (e.g., "PT15M") or null
        - "cookTime": ISO 8601 duration (e.g., "PT30M") or null
        - "recipeYield": servings as string (e.g., "4 servings") or null

        If the transcript does not contain a recipe, return: {{"name": null}}

        Ignore non-recipe content (stories, sponsors, commentary). Extract only recipe information.

        Video title: {0}
        Video description (partial context): {1}

        Transcript:
        {2}
        """;

    public async Task<RecipeSchema?> ExtractFromTranscriptAsync(
        string transcriptText,
        string? videoTitle = null,
        string? videoDescription = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = configuration["GEMINI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("GEMINI_API_KEY is not configured. Skipping Gemini extraction.");
            return null;
        }

        var descriptionContext = videoDescription != null
            ? videoDescription[..Math.Min(500, videoDescription.Length)]
            : string.Empty;

        var prompt = string.Format(ExtractionPromptTemplate,
            videoTitle ?? "(no title)",
            descriptionContext,
            transcriptText);

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

        var requestJson = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");
        var url = $"v1beta/models/gemini-2.0-flash:generateContent?key={apiKey}";

        var client = httpClientFactory.CreateClient("Gemini");

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(url, content, cancellationToken);
        }
        catch (Exception ex)
        {
            var sanitizedMessage = SanitizeUrl(ex.Message);
            logger.LogWarning("Gemini API request failed: {Message}", sanitizedMessage);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Gemini API returned {StatusCode}", (int)response.StatusCode);
            return null;
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseGeminiResponse(responseJson);
    }

    private RecipeSchema? ParseGeminiResponse(string responseJson)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<GeminiResponse>(responseJson, JsonOptions);
            var text = envelope?.Candidates?[0]?.Content?.Parts?[0]?.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("Gemini response contained no text");
                return null;
            }

            // Strip markdown code fences if present — fallback since responseMimeType should prevent this
            text = StripMarkdownFences(text);

            var schema = JsonSerializer.Deserialize<RecipeSchema>(text, JsonOptions);
            if (schema?.Name == null)
            {
                logger.LogInformation("Gemini indicated no recipe found in transcript");
                return null;
            }

            return schema;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse Gemini response as JSON");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected error parsing Gemini response");
            return null;
        }
    }

    private static string StripMarkdownFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```"))
            return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline >= 0)
            trimmed = trimmed[(firstNewline + 1)..];
        if (trimmed.EndsWith("```"))
            trimmed = trimmed[..^3].TrimEnd();

        return trimmed;
    }

    /// <summary>
    /// Returns the URL with query string and fragment stripped (scheme + host + path only).
    /// Used to sanitize URLs before logging to prevent API key leakage.
    /// </summary>
    internal static string SanitizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
    }

    private sealed class GeminiResponse
    {
        [JsonPropertyName("candidates")]
        public GeminiCandidate[]? Candidates { get; set; }
    }

    private sealed class GeminiCandidate
    {
        [JsonPropertyName("content")]
        public GeminiContent? Content { get; set; }
    }

    private sealed class GeminiContent
    {
        [JsonPropertyName("parts")]
        public GeminiPart[]? Parts { get; set; }
    }

    private sealed class GeminiPart
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
