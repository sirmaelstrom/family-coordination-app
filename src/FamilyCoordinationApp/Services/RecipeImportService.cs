using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Models.SchemaOrg;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Result of a recipe import operation.
/// </summary>
public class RecipeImportResult
{
    public bool Success { get; private set; }
    public Recipe? Recipe { get; private set; }
    public string? ErrorMessage { get; private set; }
    public RecipeImportErrorType ErrorType { get; private set; }

    /// <summary>
    /// Partially extracted data for manual completion when full extraction fails.
    /// </summary>
    public PartialRecipeData? PartialData { get; private set; }

    public static RecipeImportResult Succeeded(Recipe recipe) => new()
    {
        Success = true,
        Recipe = recipe
    };

    public static RecipeImportResult Failed(string errorMessage, RecipeImportErrorType errorType, PartialRecipeData? partialData = null) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        ErrorType = errorType,
        PartialData = partialData
    };
}

public enum RecipeImportErrorType
{
    InvalidUrl,
    FetchFailed,
    ParsingFailed,
    ValidationFailed
}

/// <summary>
/// Partially extracted recipe data for manual completion.
/// </summary>
public class PartialRecipeData
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Instructions { get; set; }
    public List<string>? IngredientStrings { get; set; }
    public string? ImageUrl { get; set; }
    public int? PrepTimeMinutes { get; set; }
    public int? CookTimeMinutes { get; set; }
    public int? Servings { get; set; }
}

public class RecipeImportService(
    IUrlValidator urlValidator,
    IRecipeScraperService scraperService,
    IIngredientParser ingredientParser,
    ICategoryInferenceService categoryInference,
    IYouTubeRecipeExtractor youtubeExtractor,
    ILogger<RecipeImportService> logger,
    IHouseholdClock clock) : IRecipeImportService
{
    public async Task<RecipeImportResult> ImportFromUrlAsync(string url, int householdId, int userId, CancellationToken cancellationToken = default)
    {
        // Step 0: YouTube URL detection — route to YouTube pipeline before DNS validation.
        // yt-dlp handles the network request, so SSRF checks against YouTube domains are unnecessary.
        if (YouTubeUrlHelper.IsYouTubeUrl(url))
        {
            return await ImportFromYouTubeAsync(url, householdId, userId, cancellationToken);
        }

        // Step 1: Validate URL (SSRF protection)
        var (isValid, validationError) = urlValidator.ValidateUrl(url);
        if (!isValid)
        {
            logger.LogWarning("URL validation failed for {Url}: {Error}", url, validationError);
            return RecipeImportResult.Failed(validationError!, RecipeImportErrorType.InvalidUrl);
        }

        // Step 2: Fetch HTML and extract JSON-LD
        ScrapedRecipe scraped;
        try
        {
            scraped = await scraperService.ScrapeRecipeAsync(url, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to fetch recipe from {Url}", url);
            return RecipeImportResult.Failed(
                $"Could not fetch the recipe page. The site may be unavailable or blocking requests. Error: {ex.Message}",
                RecipeImportErrorType.FetchFailed);
        }

        // The page's image resolves against the address it was finally served from, not the app's own origin.
        return FromSchema(scraped.Schema, url, householdId, userId, scraped.PageUrl);
    }

    /// <summary>
    /// Steps 3–5 of the website import: validate the extracted schema and convert it to an unsaved recipe. Shared with
    /// the agents' import (quest 8bfa42cf), which fetches the page through its own hardened client.
    /// <para>The recipe's image comes back as an absolute http(s) URL the recipe save accepts, or not at all
    /// (<see cref="BrowserImageUrl"/>): a relative schema.org <c>image</c> is resolved against
    /// <paramref name="pageUrl"/>, the address the page was finally served from, or against <paramref name="url"/>
    /// when the caller has none.</para>
    /// </summary>
    public RecipeImportResult FromSchema(RecipeSchema? schema, string url, int householdId, int userId, Uri? pageUrl = null)
    {
        var imageBase = pageUrl?.AbsoluteUri ?? url;
        // Step 3: Check if we got any data
        if (schema == null)
        {
            logger.LogWarning("No JSON-LD Recipe found at {Url}", url);
            return RecipeImportResult.Failed(
                "Could not find recipe data on this page. The site may not use standard recipe markup.",
                RecipeImportErrorType.ParsingFailed);
        }

        // Step 4: Validate required fields
        var partialData = ExtractPartialData(schema, imageBase, householdId);

        if (string.IsNullOrWhiteSpace(schema.Name))
        {
            return RecipeImportResult.Failed(
                "Could not extract recipe name from the page.",
                RecipeImportErrorType.ValidationFailed,
                partialData);
        }

        if (schema.RecipeIngredient == null || schema.RecipeIngredient.Length == 0)
        {
            return RecipeImportResult.Failed(
                "Could not extract ingredients from the page. Please add ingredients manually.",
                RecipeImportErrorType.ValidationFailed,
                partialData);
        }

        // Step 5: Convert to Recipe entity
        var recipe = ConvertToRecipeEntity(schema, url, householdId, userId, imageBase);

        logger.LogInformation("Successfully imported recipe '{Name}' from {Url}", recipe.Name, url);

        return RecipeImportResult.Succeeded(recipe);
    }

    private async Task<RecipeImportResult> ImportFromYouTubeAsync(string url, int householdId, int userId, CancellationToken cancellationToken)
    {
        RecipeSchema? schema;
        try
        {
            schema = await youtubeExtractor.ExtractRecipeAsync(url, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "YouTube extraction threw for {Url}", url);
            return RecipeImportResult.Failed(
                "Could not extract recipe from this video.",
                RecipeImportErrorType.ParsingFailed);
        }

        if (schema == null)
        {
            return RecipeImportResult.Failed(
                "Could not extract recipe from this video.",
                RecipeImportErrorType.ParsingFailed);
        }

        var partialData = ExtractPartialData(schema, url, householdId);

        if (string.IsNullOrWhiteSpace(schema.Name))
        {
            return RecipeImportResult.Failed(
                "Could not extract recipe name from the video.",
                RecipeImportErrorType.ValidationFailed,
                partialData);
        }

        if (schema.RecipeIngredient == null || schema.RecipeIngredient.Length == 0)
        {
            return RecipeImportResult.Failed(
                "Could not extract ingredients from the video. Please add ingredients manually.",
                RecipeImportErrorType.ValidationFailed,
                partialData);
        }

        var recipe = ConvertToRecipeEntity(schema, url, householdId, userId, url);
        logger.LogInformation("Successfully imported recipe '{Name}' from YouTube {Url}", recipe.Name, url);
        return RecipeImportResult.Succeeded(recipe);
    }

    private Recipe ConvertToRecipeEntity(RecipeSchema schema, string sourceUrl, int householdId, int userId, string imageBase)
    {
        var recipe = new Recipe
        {
            HouseholdId = householdId,
            Name = schema.Name!.Trim(),
            Description = schema.Description?.Trim(),
            Instructions = ExtractInstructions(schema.RecipeInstructions),
            SourceUrl = sourceUrl,
            ImagePath = BrowserImageUrl(schema.Image, imageBase, householdId), // an external image the save accepts, or none
            Servings = ExtractServings(schema.RecipeYield),
            PrepTimeMinutes = ParseIsoDuration(schema.PrepTime),
            CookTimeMinutes = ParseIsoDuration(schema.CookTime),
            CreatedByUserId = userId,
            CreatedAt = clock.UtcNow,
            Ingredients = new List<RecipeIngredient>()
        };

        // Parse ingredients using existing IngredientParser
        var sortOrder = 0;
        foreach (var ingredientString in schema.RecipeIngredient!)
        {
            if (string.IsNullOrWhiteSpace(ingredientString))
                continue;

            try
            {
                var parsed = ingredientParser.ParseIngredient(ingredientString);
                var inferredCategory = categoryInference.InferCategory(parsed.Name);

                recipe.Ingredients.Add(new RecipeIngredient
                {
                    HouseholdId = householdId,
                    Name = parsed.Name,
                    Quantity = parsed.Quantity,
                    Unit = parsed.Unit,
                    Notes = parsed.Notes,
                    Category = inferredCategory,
                    SortOrder = sortOrder++
                });
            }
            catch (ArgumentException)
            {
                // If parsing fails, add as raw text with no quantity/unit
                var inferredCategory = categoryInference.InferCategory(ingredientString.Trim());
                recipe.Ingredients.Add(new RecipeIngredient
                {
                    HouseholdId = householdId,
                    Name = ingredientString.Trim(),
                    Category = inferredCategory,
                    SortOrder = sortOrder++
                });
            }
        }

        return recipe;
    }

    private static string? ExtractInstructions(object? recipeInstructions)
    {
        if (recipeInstructions == null)
            return null;

        // Handle string format
        if (recipeInstructions is string str)
            return str.Trim();

        // Handle JsonElement (from deserialization)
        if (recipeInstructions is JsonElement element)
        {
            // String
            if (element.ValueKind == JsonValueKind.String)
                return element.GetString()?.Trim();

            // Array of HowToStep or strings
            if (element.ValueKind == JsonValueKind.Array)
            {
                var steps = new List<string>();
                var stepNumber = 1;

                foreach (var item in element.EnumerateArray())
                {
                    string? stepText = null;

                    if (item.ValueKind == JsonValueKind.String)
                    {
                        stepText = item.GetString();
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        // HowToStep format
                        if (item.TryGetProperty("text", out var textProp))
                            stepText = textProp.GetString();
                        else if (item.TryGetProperty("name", out var nameProp))
                            stepText = nameProp.GetString();
                    }

                    if (!string.IsNullOrWhiteSpace(stepText))
                    {
                        steps.Add($"{stepNumber}. {stepText.Trim()}");
                        stepNumber++;
                    }
                }

                return steps.Count > 0 ? string.Join("\n\n", steps) : null;
            }
        }

        return recipeInstructions.ToString()?.Trim();
    }

    private static int? ExtractServings(object? recipeYield)
    {
        if (recipeYield == null)
            return null;

        // Handle JsonElement
        if (recipeYield is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number)
            {
                // Use TryGetInt32 to safely handle overflow
                if (element.TryGetInt32(out var intValue))
                    return intValue;
                // If it's a decimal or too large, try to get the raw text and parse
                return ParseServingsString(element.GetRawText());
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                var str = element.GetString();
                return ParseServingsString(str);
            }

            // Handle array format (some sites use ["4 servings"])
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var result = ParseServingsString(item.GetString());
                        if (result.HasValue)
                            return result;
                    }
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var num))
                        return num;
                }
            }
        }

        // Handle direct types
        if (recipeYield is int intVal)
            return intVal;

        if (recipeYield is string strVal)
            return ParseServingsString(strVal);

        return null;
    }

    private static int? ParseServingsString(string? str)
    {
        if (string.IsNullOrWhiteSpace(str))
            return null;

        // Extract first number from string like "4 servings" or "Makes 6"
        var match = Regex.Match(str, @"\d+");
        if (match.Success && int.TryParse(match.Value, out var servings))
            return servings;

        return null;
    }

    private static int? ParseIsoDuration(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
            return null;

        // ISO 8601 duration: PT15M (15 minutes), PT1H30M (90 minutes)
        var match = Regex.Match(duration, @"PT(?:(\d+)H)?(?:(\d+)M)?", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        // Use TryParse to safely handle overflow from malformed duration strings
        int hours = 0, minutes = 0;
        if (match.Groups[1].Success && !int.TryParse(match.Groups[1].Value, out hours))
            return null; // Hours value too large
        if (match.Groups[2].Success && !int.TryParse(match.Groups[2].Value, out minutes))
            return null; // Minutes value too large

        // Guard against overflow in multiplication
        if (hours > 35791) // hours * 60 would overflow Int32.MaxValue
            return null;

        var totalMinutes = hours * 60 + minutes;
        return totalMinutes > 0 ? totalMinutes : null;
    }

    private static PartialRecipeData ExtractPartialData(RecipeSchema schema, string imageBase, int householdId)
    {
        return new PartialRecipeData
        {
            Name = schema.Name,
            Description = schema.Description,
            Instructions = ExtractInstructions(schema.RecipeInstructions),
            IngredientStrings = schema.RecipeIngredient?.ToList(),
            ImageUrl = BrowserImageUrl(schema.Image, imageBase, householdId),
            PrepTimeMinutes = ParseIsoDuration(schema.PrepTime),
            CookTimeMinutes = ParseIsoDuration(schema.CookTime),
            Servings = ExtractServings(schema.RecipeYield)
        };
    }

    /// <summary>
    /// The browser's image for an import: the first of the page's image candidates that resolves to a value the
    /// recipe save accepts (<see cref="ImagePathPolicy"/>, the check POST /api/recipes makes), or null. The image is
    /// dropped, never the recipe (PR #177 class fix: the import must not hand the browser an image its save refuses).
    /// </summary>
    private static string? BrowserImageUrl(object? image, string imageBase, int householdId) =>
        PickImageUrl(image, imageBase, url => ImagePathPolicy.TryNormalize(url, householdId, out _));

    /// <summary>
    /// The first of the page's schema.org <c>image</c> candidates (<see cref="ImageCandidates"/>) whose resolved form
    /// (<see cref="ResolveImageUrl"/>) the consumer's own save check <paramref name="accepts"/>, or null.
    /// </summary>
    internal static string? PickImageUrl(object? image, string pageUrl, Func<string, bool> accepts)
    {
        foreach (var candidate in ImageCandidates(image))
        {
            if (ResolveImageUrl(pageUrl, candidate) is { } url && accepts(url))
                return url;
        }
        return null;
    }

    /// <summary>
    /// The page's schema.org <c>image</c> as the page wrote it, every candidate in order: a URL string; an
    /// ImageObject's <c>url</c>, then its <c>contentUrl</c> (the media file itself; some sites give only that); each
    /// item of an array. A value that is not a non-empty string is skipped. Nothing here judges a candidate: the
    /// caller takes the first one its save accepts, so an unusable first entry never hides a usable later one.
    /// </summary>
    private static readonly string[] ImageObjectUrlProperties = ["url", "contentUrl"];

    private static IEnumerable<string> ImageCandidates(object? image)
    {
        if (image is string str)
        {
            if (!string.IsNullOrWhiteSpace(str)) yield return str;
            yield break;
        }
        if (image is not JsonElement element) yield break;

        var items = element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().ToList() : [element];
        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                if (!string.IsNullOrWhiteSpace(item.GetString())) yield return item.GetString()!;
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in ImageObjectUrlProperties)
            {
                if (item.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    yield return value.GetString()!;
                }
            }
        }
    }

    /// <summary>
    /// A page's image reference as an absolute http(s) URL in the form the page wrote it, or null. A relative value
    /// ("/images/pie.jpg", "pie.jpg", "//cdn.example/pie.jpg") is resolved against the page URL, as a browser would;
    /// any other scheme, or a reference that cannot be resolved ("//", a bad port), is no image, never an exception.
    /// Length is not judged here: each consumer's save check does that (<see cref="PickImageUrl"/>).
    /// <para>Neither form is re-serialised. <see cref="Uri.AbsoluteUri"/> percent-encodes a non-ASCII filename, which
    /// turned a saveable 431-character URL into a refused 551-character one, first for an absolute URL (PR #177 review
    /// 5984747882) and then for its relative twin (review 5987541661). An absolute URL comes back trimmed, as written;
    /// a relative one as the combined URI's <see cref="Uri.OriginalString"/>: the page's own reference text after the
    /// base, with dot-segments removed and every escape the page chose (a signed query's <c>%2B</c>) kept.</para>
    /// </summary>
    internal static string? ResolveImageUrl(string pageUrl, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        // Absolute first, and only http(s): on Unix "/images/pie.jpg" parses as an absolute file: URI, so a relative
        // reference is parsed explicitly as Relative and combined with the page URL.
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && IsHttp(absolute))
            return value;
        return Uri.TryCreate(value, UriKind.Relative, out var relative)
            && Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)
            && Uri.TryCreate(page, relative, out var resolved)
            && IsHttp(resolved)
            ? resolved.OriginalString
            : null;

        static bool IsHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;
    }
}
