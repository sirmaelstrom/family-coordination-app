using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Models.SchemaOrg;

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

public interface IRecipeImportService
{
    /// <summary>
    /// Imports recipe from URL, returning Recipe entity or error with partial data.
    /// </summary>
    Task<RecipeImportResult> ImportFromUrlAsync(string url, int householdId, int userId, CancellationToken cancellationToken = default);
}

public class RecipeImportService : IRecipeImportService
{
    private readonly IUrlValidator _urlValidator;
    private readonly IRecipeScraperService _scraperService;
    private readonly IIngredientParser _ingredientParser;
    private readonly ICategoryInferenceService _categoryInference;
    private readonly ILogger<RecipeImportService> _logger;

    public RecipeImportService(
        IUrlValidator urlValidator,
        IRecipeScraperService scraperService,
        IIngredientParser ingredientParser,
        ICategoryInferenceService categoryInference,
        ILogger<RecipeImportService> logger)
    {
        _urlValidator = urlValidator;
        _scraperService = scraperService;
        _ingredientParser = ingredientParser;
        _categoryInference = categoryInference;
        _logger = logger;
    }

    public async Task<RecipeImportResult> ImportFromUrlAsync(string url, int householdId, int userId, CancellationToken cancellationToken = default)
    {
        // Step 1: Validate URL (SSRF protection)
        var (isValid, validationError) = _urlValidator.ValidateUrl(url);
        if (!isValid)
        {
            _logger.LogWarning("URL validation failed for {Url}: {Error}", url, validationError);
            return RecipeImportResult.Failed(validationError!, RecipeImportErrorType.InvalidUrl);
        }

        // Step 2: Fetch HTML and extract JSON-LD
        RecipeSchema? schema;
        try
        {
            schema = await _scraperService.ScrapeRecipeAsync(url, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to fetch recipe from {Url}", url);
            return RecipeImportResult.Failed(
                $"Could not fetch the recipe page. The site may be unavailable or blocking requests. Error: {ex.Message}",
                RecipeImportErrorType.FetchFailed);
        }

        // Step 3: Check if we got any data
        if (schema == null)
        {
            _logger.LogWarning("No JSON-LD Recipe found at {Url}", url);
            return RecipeImportResult.Failed(
                "Could not find recipe data on this page. The site may not use standard recipe markup.",
                RecipeImportErrorType.ParsingFailed);
        }

        // Step 4: Validate required fields
        var partialData = ExtractPartialData(schema);

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
        var recipe = ConvertToRecipeEntity(schema, url, householdId, userId);

        _logger.LogInformation("Successfully imported recipe '{Name}' from {Url}", recipe.Name, url);

        return RecipeImportResult.Succeeded(recipe);
    }

    private Recipe ConvertToRecipeEntity(RecipeSchema schema, string sourceUrl, int householdId, int userId)
    {
        var recipe = new Recipe
        {
            HouseholdId = householdId,
            Name = schema.Name!.Trim(),
            Description = schema.Description?.Trim(),
            Instructions = ExtractInstructions(schema.RecipeInstructions),
            SourceUrl = sourceUrl,
            ImagePath = ExtractImageUrl(schema.Image), // Store external image URL directly
            Servings = ExtractServings(schema.RecipeYield),
            PrepTimeMinutes = ParseIsoDuration(schema.PrepTime),
            CookTimeMinutes = ParseIsoDuration(schema.CookTime),
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
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
                var parsed = _ingredientParser.ParseIngredient(ingredientString);
                var inferredCategory = _categoryInference.InferCategory(parsed.Name);

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
                var inferredCategory = _categoryInference.InferCategory(ingredientString.Trim());
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

    private static PartialRecipeData ExtractPartialData(RecipeSchema schema)
    {
        return new PartialRecipeData
        {
            Name = schema.Name,
            Description = schema.Description,
            Instructions = ExtractInstructions(schema.RecipeInstructions),
            IngredientStrings = schema.RecipeIngredient?.ToList(),
            ImageUrl = ExtractImageUrl(schema.Image),
            PrepTimeMinutes = ParseIsoDuration(schema.PrepTime),
            CookTimeMinutes = ParseIsoDuration(schema.CookTime),
            Servings = ExtractServings(schema.RecipeYield)
        };
    }

    private static string? ExtractImageUrl(object? image)
    {
        if (image == null)
            return null;

        if (image is string str)
            return str;

        if (image is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
                return element.GetString();

            // Array of images - take first
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        return item.GetString();
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("url", out var urlProp))
                        return urlProp.GetString();
                }
            }

            // ImageObject
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("url", out var url))
                return url.GetString();
        }

        return null;
    }
}
