using FluentAssertions;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Tests.Services;

public class DescriptionRecipeExtractorTests
{
    private readonly IDescriptionRecipeExtractor _extractor;

    private static string FixturePath(string filename) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Descriptions", filename);

    public DescriptionRecipeExtractorTests()
    {
        _extractor = new DescriptionRecipeExtractor();
    }

    // ── Null / empty guard ────────────────────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_NullDescription_ReturnsNull()
    {
        var result = _extractor.ExtractFromDescription(null!);
        result.Should().BeNull();
    }

    [Fact]
    public void ExtractFromDescription_EmptyDescription_ReturnsNull()
    {
        var result = _extractor.ExtractFromDescription(string.Empty);
        result.Should().BeNull();
    }

    [Fact]
    public void ExtractFromDescription_WhitespaceDescription_ReturnsNull()
    {
        var result = _extractor.ExtractFromDescription("   \n\n  ");
        result.Should().BeNull();
    }

    // ── Formatted recipe fixture ──────────────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_ReturnsSchema()
    {
        var description = File.ReadAllText(FixturePath("formatted-recipe.txt"));

        var result = _extractor.ExtractFromDescription(description, "Classic Chocolate Chip Cookies");

        result.Should().NotBeNull();
        result!.Name.Should().Be("Classic Chocolate Chip Cookies");
        result.RecipeIngredient.Should().NotBeNull();
        result.RecipeIngredient!.Length.Should().BeGreaterThanOrEqualTo(2);
        result.RecipeInstructions.Should().NotBeNull();
        result.RecipeInstructions.Should().BeOfType<string>();
        ((string)result.RecipeInstructions!).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_ExtractsCorrectIngredientCount()
    {
        var description = File.ReadAllText(FixturePath("formatted-recipe.txt"));

        var result = _extractor.ExtractFromDescription(description);

        result.Should().NotBeNull();
        // formatted-recipe.txt has 9 ingredients
        result!.RecipeIngredient!.Length.Should().Be(9);
    }

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_ExtractsTimesAndServings()
    {
        var description = File.ReadAllText(FixturePath("formatted-recipe.txt"));

        var result = _extractor.ExtractFromDescription(description);

        result.Should().NotBeNull();
        result!.PrepTime.Should().Be("PT15M");
        result.CookTime.Should().Be("PT12M");
        result.RecipeYield.Should().Be("24");
    }

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_InstructionsContainSteps()
    {
        var description = File.ReadAllText(FixturePath("formatted-recipe.txt"));

        var result = _extractor.ExtractFromDescription(description);

        result.Should().NotBeNull();
        var instructions = (string)result!.RecipeInstructions!;
        // The fixture has 8 numbered steps
        instructions.Should().Contain("Preheat oven");
        instructions.Should().Contain("chocolate chips");
    }

    // ── No-recipe fixture ─────────────────────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_NoRecipe_ReturnsNull()
    {
        var description = File.ReadAllText(FixturePath("no-recipe.txt"));

        var result = _extractor.ExtractFromDescription(description, "My Vlog");

        result.Should().BeNull();
    }

    // ── Mixed-content fixture ─────────────────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_MixedContent_ExtractsRecipePortion()
    {
        var description = File.ReadAllText(FixturePath("mixed-content.txt"));

        var result = _extractor.ExtractFromDescription(description, "Summer Pasta");

        result.Should().NotBeNull();
        result!.RecipeIngredient.Should().NotBeNull();
        result.RecipeIngredient!.Length.Should().BeGreaterThanOrEqualTo(2);
        result.RecipeInstructions.Should().BeOfType<string>();
    }

    [Fact]
    public void ExtractFromDescription_MixedContent_IgnoresSponsorContent()
    {
        var description = File.ReadAllText(FixturePath("mixed-content.txt"));

        var result = _extractor.ExtractFromDescription(description);

        result.Should().NotBeNull();
        var instructions = (string)result!.RecipeInstructions!;
        instructions.Should().NotContain("sponsored");
        instructions.Should().NotContain("ExampleBrand");
        instructions.Should().NotContain("Instagram");
    }

    [Fact]
    public void ExtractFromDescription_MixedContent_IgnoresCommentaryAsIngredients()
    {
        var description = File.ReadAllText(FixturePath("mixed-content.txt"));

        var result = _extractor.ExtractFromDescription(description);

        result.Should().NotBeNull();
        // Commentary paragraphs should not appear as ingredients
        result!.RecipeIngredient!.Should().NotContain(x => x.Contains("crowd pleaser"));
        result.RecipeIngredient.Should().NotContain(x => x.Contains("family reunion"));
    }

    // ── Inline string tests for specific heuristics ───────────────────────────

    [Fact]
    public void ExtractFromDescription_IngredientLine_QuantityAndUnitDetected()
    {
        const string description = """
            Ingredients:
            - 2 cups flour
            - 1 tsp baking powder
            Instructions:
            1. Mix dry ingredients.
            2. Bake for 30 minutes.
            """;

        var result = _extractor.ExtractFromDescription(description);

        result.Should().NotBeNull();
        result!.RecipeIngredient.Should().Contain("2 cups flour");
        result.RecipeIngredient.Should().Contain("1 tsp baking powder");
    }

    [Fact]
    public void ExtractFromDescription_SocialMediaLine_NotTreatedAsIngredient()
    {
        const string description = """
            Ingredients:
            - 2 cups flour
            - 1 tsp salt
            Instructions:
            1. Mix and bake.
            Follow me on Instagram @chef
            """;

        var result = _extractor.ExtractFromDescription(description);

        result.Should().NotBeNull();
        // Social media line should not appear in ingredients
        result!.RecipeIngredient!.Should().NotContain(x => x.Contains("Instagram"));
    }

    [Theory]
    [InlineData("Serves 4", "4")]
    [InlineData("Yield: 6", "6")]
    [InlineData("Makes 12 cookies", "12")]
    [InlineData("Servings: 8", "8")]
    public void ExtractFromDescription_ServingsPatterns_ExtractCorrectly(string servingsLine, string expected)
    {
        var description = $"""
            Ingredients:
            - 2 cups flour
            - 1 tsp baking soda
            Instructions:
            1. Mix ingredients.
            2. Bake until done.
            {servingsLine}
            """;

        var result = _extractor.ExtractFromDescription(description);

        result.Should().NotBeNull();
        result!.RecipeYield.Should().Be(expected);
    }

    [Theory]
    [InlineData("Prep: 15 min", "PT15M")]
    [InlineData("Prep time: 30 minutes", "PT30M")]
    [InlineData("Prep: 1 hour", "PT1H")]
    public void ExtractFromDescription_PrepTimePatterns_ConvertToIso8601(string timeLine, string expected)
    {
        var description = $"""
            {timeLine}
            Ingredients:
            - 2 cups flour
            - 1 tsp baking soda
            Instructions:
            1. Mix ingredients.
            2. Bake until done.
            """;

        var result = _extractor.ExtractFromDescription(description);

        result.Should().NotBeNull();
        result!.PrepTime.Should().Be(expected);
    }

    [Theory]
    [InlineData("Cook time: 30 minutes", "PT30M")]
    [InlineData("Cook: 45 min", "PT45M")]
    [InlineData("Cook time: 1 hour", "PT1H")]
    public void ExtractFromDescription_CookTimePatterns_ConvertToIso8601(string timeLine, string expected)
    {
        var description = $"""
            {timeLine}
            Ingredients:
            - 2 cups flour
            - 1 tsp baking soda
            Instructions:
            1. Mix ingredients.
            2. Bake until done.
            """;

        var result = _extractor.ExtractFromDescription(description);

        result.Should().NotBeNull();
        result!.CookTime.Should().Be(expected);
    }

    [Fact]
    public void ExtractFromDescription_OnlyIngredients_NoInstructions_ReturnsNull()
    {
        const string description = """
            Ingredients:
            - 2 cups flour
            - 1 tsp salt
            - 3 eggs
            """;

        var result = _extractor.ExtractFromDescription(description);

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractFromDescription_OnlyOneIngredient_ReturnsNull()
    {
        const string description = """
            Ingredients:
            - 2 cups flour
            Instructions:
            1. Mix and bake.
            """;

        var result = _extractor.ExtractFromDescription(description);

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractFromDescription_NoIngredientHeader_ReturnsNull()
    {
        const string description = """
            Just bake everything together at 350°F for 30 minutes.
            It's that simple!
            """;

        var result = _extractor.ExtractFromDescription(description);

        result.Should().BeNull();
    }

    // ── RecipeInstructions type contract ──────────────────────────────────────

    [Fact]
    public void ExtractFromDescription_RecipeInstructions_IsPlainString_NotArray()
    {
        const string description = """
            Ingredients:
            - 2 cups flour
            - 1 tsp salt
            Instructions:
            1. Preheat oven to 375F.
            2. Mix the ingredients.
            3. Bake for 30 minutes.
            """;

        var result = _extractor.ExtractFromDescription(description);

        result.Should().NotBeNull();
        // CRITICAL: must be string, not string[] — downstream ExtractInstructions only handles string/JsonElement
        result!.RecipeInstructions.Should().BeOfType<string>();
        result.RecipeInstructions.ToString().Should().NotBe("System.String[]");
    }

    // ── Seam test: extractor output → IngredientParser ───────────────────────

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_IngredientsParseableByIngredientParser()
    {
        var description = File.ReadAllText(FixturePath("formatted-recipe.txt"));
        var parser = new IngredientParser();

        var result = _extractor.ExtractFromDescription(description);

        result.Should().NotBeNull();
        result!.RecipeIngredient.Should().NotBeNullOrEmpty();

        foreach (var ingredientLine in result.RecipeIngredient!)
        {
            var parsed = parser.ParseIngredient(ingredientLine);
            parsed.IsComplete.Should().BeTrue(
                because: $"ingredient line '{ingredientLine}' should parse to a complete ingredient");
        }
    }

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_QuantityAndUnitSurviveHandoff()
    {
        var description = File.ReadAllText(FixturePath("formatted-recipe.txt"));
        var parser = new IngredientParser();

        var result = _extractor.ExtractFromDescription(description);

        result.Should().NotBeNull();

        // "2 1/4 cups all-purpose flour" should survive the handoff
        var flourLine = result!.RecipeIngredient!.FirstOrDefault(x => x.Contains("flour") && x.Contains("cups"));
        flourLine.Should().NotBeNull(because: "flour ingredient should be in the extracted list");

        var parsed = parser.ParseIngredient(flourLine!);
        parsed.Quantity.Should().Be(2.25m);
        parsed.Unit.Should().Be("cups");
        parsed.Name.Should().Contain("flour");
    }
}
