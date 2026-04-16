using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyCoordinationApp.Models.SchemaOrg;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

public class GeminiRecipeExtractor(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<GeminiRecipeExtractor> logger) : IGeminiRecipeExtractor
{
    // gemini-2.0-flash was deprecated for new API key users (returns 404 on generateContent
    // even though it appears in ListModels). gemini-2.5-flash is the current stable flash tier.
    private const string DefaultModel = "gemini-2.5-flash";
    private const int DescriptionContextChars = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<RecipeSchema?> ExtractFromTranscriptAsync(
        string transcriptText,
        string? videoTitle = null,
        string? videoDescription = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcriptText))
            return null;

        var apiKey = configuration["GEMINI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("GEMINI_API_KEY is not configured; skipping Gemini extraction.");
            return null;
        }

        var prompt = BuildPrompt(transcriptText, videoTitle, videoDescription);
        var requestBody = BuildRequestBody(prompt);

        var model = configuration["GEMINI_MODEL"];
        if (string.IsNullOrWhiteSpace(model))
            model = DefaultModel;

        var endpointPath = $"v1beta/models/{model}:generateContent";
        var client = httpClientFactory.CreateClient("Gemini");
        var requestUri = $"{endpointPath}?key={apiKey}";

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Gemini API request failed: {Message} (endpoint: {Endpoint})",
                ex.Message, SanitizeUrl(endpointPath));
            return null;
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("Gemini API request timed out (endpoint: {Endpoint})", SanitizeUrl(endpointPath));
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Gemini API returned {StatusCode} for endpoint {Endpoint}",
                    (int)response.StatusCode, SanitizeUrl(endpointPath));
                return null;
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read Gemini response body");
                return null;
            }

            return ParseResponse(body);
        }
    }

    internal static string BuildPrompt(string transcript, string? title, string? description)
    {
        var descriptionContext = string.IsNullOrWhiteSpace(description)
            ? "(not provided)"
            : description.Length > DescriptionContextChars
                ? description[..DescriptionContextChars]
                : description;

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

        Video title: {{title ?? "(unknown)"}}
        Video description (partial context): {{descriptionContext}}

        Transcript:
        {{transcript}}
        """;
    }

    internal static string BuildRequestBody(string prompt)
    {
        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = prompt } }
                }
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                temperature = 0.1
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    private RecipeSchema? ParseResponse(string body)
    {
        string? extractedText;
        try
        {
            using var envelope = JsonDocument.Parse(body);
            if (!envelope.RootElement.TryGetProperty("candidates", out var candidates)
                || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() == 0)
            {
                logger.LogWarning("Gemini response had no candidates");
                return null;
            }

            var first = candidates[0];
            if (!first.TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)
                || parts.ValueKind != JsonValueKind.Array
                || parts.GetArrayLength() == 0
                || !parts[0].TryGetProperty("text", out var textElement))
            {
                logger.LogWarning("Gemini response missing content.parts[0].text");
                return null;
            }

            extractedText = textElement.GetString();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse Gemini response envelope");
            return null;
        }

        if (string.IsNullOrWhiteSpace(extractedText))
            return null;

        var cleanedText = StripMarkdownFences(extractedText);

        RecipeSchema? schema;
        try
        {
            schema = JsonSerializer.Deserialize<RecipeSchema>(cleanedText, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Gemini produced invalid JSON for recipe extraction");
            return null;
        }

        if (schema == null || string.IsNullOrWhiteSpace(schema.Name))
        {
            logger.LogInformation("Gemini reported no recipe in transcript");
            return null;
        }

        return schema;
    }

    private static string StripMarkdownFences(string input)
    {
        var trimmed = input.Trim();
        // Match ```json ... ``` or ``` ... ``` wrappers
        var match = Regex.Match(trimmed, @"^```(?:json)?\s*(.*?)\s*```$",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value.Trim();
        return trimmed;
    }

    /// <summary>
    /// Returns scheme + host + path only — no query string or fragment.
    /// Use before logging any URL that may contain the API key.
    /// </summary>
    internal static string SanitizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return $"{absolute.Scheme}://{absolute.Authority}{absolute.AbsolutePath}";

        // Relative URL — strip query/fragment by hand.
        var queryIndex = url.IndexOf('?');
        if (queryIndex >= 0)
            url = url[..queryIndex];
        var fragmentIndex = url.IndexOf('#');
        if (fragmentIndex >= 0)
            url = url[..fragmentIndex];
        return url;
    }
}
