using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Tests.Services;

public class DescriptionExtractorTests
{
    private readonly IDescriptionRecipeExtractor _extractor;
    private readonly IIngredientParser _ingredientParser;

    public DescriptionExtractorTests()
    {
        _extractor = new DescriptionRecipeExtractor();
        _ingredientParser = new IngredientParser();
    }

    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Descriptions", fileName));

    // ──────────────────────────────────────────────────────────────────
    // Formatted recipe fixture
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_ReturnsSchema()
    {
        var description = ReadFixture("formatted-recipe.txt");

        var result = _extractor.ExtractFromDescription(description, "Classic Chicken Stir-Fry");

        Assert.NotNull(result);
    }

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_UsesVideoTitleAsName()
    {
        var description = ReadFixture("formatted-recipe.txt");

        var result = _extractor.ExtractFromDescription(description, "Classic Chicken Stir-Fry");

        Assert.Equal("Classic Chicken Stir-Fry", result!.Name);
    }

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_ExtractsAtLeastTwoIngredients()
    {
        var description = ReadFixture("formatted-recipe.txt");

        var result = _extractor.ExtractFromDescription(description, "Classic Chicken Stir-Fry");

        Assert.NotNull(result!.RecipeIngredient);
        Assert.True(result.RecipeIngredient!.Length >= 2);
    }

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_ExtractsInstructions()
    {
        var description = ReadFixture("formatted-recipe.txt");

        var result = _extractor.ExtractFromDescription(description, "Classic Chicken Stir-Fry");

        Assert.NotNull(result!.RecipeInstructions);
        Assert.IsType<string>(result.RecipeInstructions);
        Assert.False(string.IsNullOrWhiteSpace((string)result.RecipeInstructions!));
    }

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_ExtractsPrepTime()
    {
        var description = ReadFixture("formatted-recipe.txt");

        var result = _extractor.ExtractFromDescription(description, "Classic Chicken Stir-Fry");

        Assert.Equal("PT15M", result!.PrepTime);
    }

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_ExtractsCookTime()
    {
        var description = ReadFixture("formatted-recipe.txt");

        var result = _extractor.ExtractFromDescription(description, "Classic Chicken Stir-Fry");

        Assert.Equal("PT20M", result!.CookTime);
    }

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_ExtractsServings()
    {
        var description = ReadFixture("formatted-recipe.txt");

        var result = _extractor.ExtractFromDescription(description, "Classic Chicken Stir-Fry");

        Assert.Equal("4", result!.RecipeYield);
    }

    // ──────────────────────────────────────────────────────────────────
    // No-recipe fixture
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_NoRecipe_ReturnsNull()
    {
        var description = ReadFixture("no-recipe.txt");

        var result = _extractor.ExtractFromDescription(description, "What I Ate Today");

        Assert.Null(result);
    }

    // ──────────────────────────────────────────────────────────────────
    // Mixed-content fixture
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_MixedContent_ReturnsSchema()
    {
        var description = ReadFixture("mixed-content.txt");

        var result = _extractor.ExtractFromDescription(description, "Creamy Tomato Pasta");

        Assert.NotNull(result);
    }

    [Fact]
    public void ExtractFromDescription_MixedContent_ExtractsIngredientsIgnoringCommentary()
    {
        var description = ReadFixture("mixed-content.txt");

        var result = _extractor.ExtractFromDescription(description, "Creamy Tomato Pasta");

        Assert.NotNull(result!.RecipeIngredient);
        Assert.True(result.RecipeIngredient!.Length >= 2);
        // Commentary lines before the recipe must not appear as ingredients
        Assert.DoesNotContain(result.RecipeIngredient, i => i.Contains("grandmother"));
        Assert.DoesNotContain(result.RecipeIngredient, i => i.Contains("subscribe"));
    }

    [Fact]
    public void ExtractFromDescription_MixedContent_ExtractsCookTime()
    {
        var description = ReadFixture("mixed-content.txt");

        var result = _extractor.ExtractFromDescription(description, "Creamy Tomato Pasta");

        Assert.Equal("PT25M", result!.CookTime);
    }

    [Fact]
    public void ExtractFromDescription_MixedContent_ExtractsServings()
    {
        var description = ReadFixture("mixed-content.txt");

        var result = _extractor.ExtractFromDescription(description, "Creamy Tomato Pasta");

        Assert.Equal("4", result!.RecipeYield);
    }

    // ──────────────────────────────────────────────────────────────────
    // Edge cases
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_NullDescription_ReturnsNull()
    {
        var result = _extractor.ExtractFromDescription(null!);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractFromDescription_EmptyDescription_ReturnsNull()
    {
        var result = _extractor.ExtractFromDescription(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractFromDescription_WhitespaceDescription_ReturnsNull()
    {
        var result = _extractor.ExtractFromDescription("   \n\n   ");

        Assert.Null(result);
    }

    [Fact]
    public void ExtractFromDescription_OnlyIngredients_ReturnsNull()
    {
        // Only ingredients — no instructions — should return null
        var description = """
            Ingredients:
            - 2 cups flour
            - 1 cup sugar
            - 2 eggs
            """;

        var result = _extractor.ExtractFromDescription(description);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractFromDescription_OnlyOneIngredient_ReturnsNull()
    {
        // Only 1 ingredient line found — below threshold — should return null
        var description = """
            Ingredients:
            - 2 cups flour

            Instructions:
            1. Mix everything together and bake at 350F for 30 minutes.
            """;

        var result = _extractor.ExtractFromDescription(description);

        Assert.Null(result);
    }

    // ──────────────────────────────────────────────────────────────────
    // Ingredient line detection
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_QuantityLineIsIngredient()
    {
        var description = """
            Ingredients:
            2 cups flour
            1 cup sugar
            2 eggs

            Instructions:
            1. Mix and bake at 350F for 30 minutes.
            """;

        var result = _extractor.ExtractFromDescription(description);

        Assert.NotNull(result);
        Assert.Contains(result!.RecipeIngredient!, i => i.StartsWith("2 cups flour"));
    }

    [Fact]
    public void ExtractFromDescription_SocialMediaLineIsNotIngredient()
    {
        var description = """
            Ingredients:
            - Follow me on Instagram @chef
            - 2 cups flour
            - 1 cup sugar
            - 3 eggs

            Instructions:
            1. Mix ingredients together.
            2. Bake at 350F for 30 minutes.
            """;

        var result = _extractor.ExtractFromDescription(description);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.RecipeIngredient!, i => i.Contains("Instagram"));
        Assert.Contains(result.RecipeIngredient!, i => i.Contains("flour"));
    }

    // ──────────────────────────────────────────────────────────────────
    // RecipeInstructions is a string (not array) — field type contract
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_InstructionsFieldIsString()
    {
        var description = ReadFixture("formatted-recipe.txt");

        var result = _extractor.ExtractFromDescription(description, "Test Recipe");

        // RecipeInstructions must be a string, not a collection.
        // A boxed string[] would silently produce "System.String[]" via .ToString().
        Assert.IsType<string>(result!.RecipeInstructions);
        Assert.DoesNotContain("System.String[]", (string)result.RecipeInstructions!);
    }

    // ──────────────────────────────────────────────────────────────────
    // Fallback name when videoTitle is null
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_NoVideoTitle_FallsBackToDescriptionTitle()
    {
        var description = ReadFixture("formatted-recipe.txt");

        // Pass null for videoTitle — should extract name from description
        var result = _extractor.ExtractFromDescription(description, null);

        Assert.NotNull(result);
        // Name should be set to something (the line before "Ingredients:")
        Assert.False(string.IsNullOrWhiteSpace(result!.Name));
    }

    // ──────────────────────────────────────────────────────────────────
    // Seam test: extracted ingredients → IngredientParser handoff
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_SeamTest_IngredientsParseCorrectly()
    {
        var description = ReadFixture("formatted-recipe.txt");
        var schema = _extractor.ExtractFromDescription(description, "Classic Chicken Stir-Fry");

        Assert.NotNull(schema);
        Assert.NotNull(schema!.RecipeIngredient);

        // Every ingredient string must parse to a complete result (non-empty Name)
        var failedIngredients = new List<string>();
        foreach (var ingredientStr in schema.RecipeIngredient!)
        {
            if (string.IsNullOrWhiteSpace(ingredientStr))
                continue;

            try
            {
                var parsed = _ingredientParser.ParseIngredient(ingredientStr);
                if (!parsed.IsComplete)
                    failedIngredients.Add(ingredientStr);
            }
            catch
            {
                failedIngredients.Add(ingredientStr);
            }
        }

        Assert.Empty(failedIngredients);
    }

    [Fact]
    public void ExtractFromDescription_SeamTest_QuantityAndUnitSurviveHandoff()
    {
        // "2 cups broccoli florets" should parse with quantity=2, unit=cups
        var description = """
            Ingredients:
            - 2 cups broccoli florets
            - 1 tbsp soy sauce
            - 1/2 cup chicken broth

            Instructions:
            1. Heat oil in a wok.
            2. Add broccoli and stir-fry for 3 minutes.
            3. Add soy sauce and broth, toss to coat.
            """;

        var schema = _extractor.ExtractFromDescription(description, "Stir-Fry");

        Assert.NotNull(schema);
        var broccoliLine = schema!.RecipeIngredient!.FirstOrDefault(i => i.Contains("broccoli"));
        Assert.NotNull(broccoliLine);

        var parsed = _ingredientParser.ParseIngredient(broccoliLine!);
        Assert.Equal(2m, parsed.Quantity);
        Assert.Equal("cups", parsed.Unit);
        Assert.Contains("broccoli", parsed.Name);
    }

    // ──────────────────────────────────────────────────────────────────
    // Time extraction edge cases
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_ServesPattern_ExtractsYield()
    {
        var description = """
            Ingredients:
            - 2 cups flour
            - 1 cup sugar
            - 2 eggs

            Instructions:
            1. Mix ingredients.
            2. Bake at 350F.

            Serves 4
            """;

        var result = _extractor.ExtractFromDescription(description);

        Assert.Equal("4", result!.RecipeYield);
    }

    [Fact]
    public void ExtractFromDescription_PrepTimeMinutes_ProducesISO8601()
    {
        var description = """
            Prep: 15 min

            Ingredients:
            - 2 cups flour
            - 1 cup sugar
            - 2 eggs

            Instructions:
            1. Mix ingredients.
            2. Bake at 350F.
            """;

        var result = _extractor.ExtractFromDescription(description);

        Assert.Equal("PT15M", result!.PrepTime);
    }

    [Fact]
    public void ExtractFromDescription_CookTimeMinutes_ProducesISO8601()
    {
        var description = """
            Cook time: 30 min

            Ingredients:
            - 2 cups flour
            - 1 cup sugar
            - 2 eggs

            Instructions:
            1. Mix ingredients.
            2. Bake at 350F for 30 minutes.
            """;

        var result = _extractor.ExtractFromDescription(description);

        Assert.Equal("PT30M", result!.CookTime);
    }

    [Fact]
    public void ExtractFromDescription_TimeInHoursAndMinutes_ProducesISO8601()
    {
        var description = """
            Cook time: 1 hour 30 min

            Ingredients:
            - 2 cups beef chuck
            - 1 cup broth
            - 3 cloves garlic

            Instructions:
            1. Brown the beef in a pot.
            2. Add broth and garlic, simmer for 1 hour 30 min.
            """;

        var result = _extractor.ExtractFromDescription(description);

        Assert.Equal("PT1H30M", result!.CookTime);
    }
}
