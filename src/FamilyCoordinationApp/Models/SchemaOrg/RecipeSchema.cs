using System.Text.Json.Serialization;

namespace FamilyCoordinationApp.Models.SchemaOrg;

/// <summary>
/// POCO for schema.org Recipe type (https://schema.org/Recipe).
/// Used to deserialize JSON-LD from recipe websites.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class RecipeSchema
{
    [JsonPropertyName("@context")]
    public string? Context { get; set; }

    [JsonPropertyName("@type")]
    public object? Type { get; set; } // Can be string "Recipe" or array ["Recipe", "NewsArticle"]

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("image")]
    public object? Image { get; set; } // Can be string URL or ImageObject

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("recipeIngredient")]
    public string[]? RecipeIngredient { get; set; }

    [JsonPropertyName("recipeInstructions")]
    public object? RecipeInstructions { get; set; } // Can be string or HowToStep[]

    [JsonPropertyName("prepTime")]
    public string? PrepTime { get; set; } // ISO 8601 duration: "PT15M"

    [JsonPropertyName("cookTime")]
    public string? CookTime { get; set; } // ISO 8601 duration: "PT30M"

    [JsonPropertyName("recipeYield")]
    public object? RecipeYield { get; set; } // Can be string "4 servings" or number

    [JsonPropertyName("recipeCategory")]
    public object? RecipeCategory { get; set; } // Can be string or array

    [JsonPropertyName("recipeCuisine")]
    public object? RecipeCuisine { get; set; } // Can be string or array

    [JsonPropertyName("author")]
    public object? Author { get; set; } // Can be string or Person object
}

/// <summary>
/// HowToStep for structured recipe instructions.
/// </summary>
public class HowToStep
{
    [JsonPropertyName("@type")]
    public string Type { get; set; } = "HowToStep";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
