using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class DescriptionRecipeExtractorTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Descriptions", name));

    private readonly DescriptionRecipeExtractor _extractor = new(NullLogger<DescriptionRecipeExtractor>.Instance);

    [Fact]
    public void ExtractFromDescription_FormattedRecipe_ReturnsSchema()
    {
        var text = Fixture("formatted-recipe.txt");

        var result = _extractor.ExtractFromDescription(text, "Perfect Chocolate Chip Cookies");

        result.Should().NotBeNull();
        result!.Name.Should().Be("Perfect Chocolate Chip Cookies");
        result.RecipeIngredient.Should().NotBeNull();
        result.RecipeIngredient!.Length.Should().BeGreaterThan(5);
        result.RecipeIngredient.Should().Contain(s => s.Contains("flour"));
        result.RecipeInstructions.Should().BeOfType<string>();
        ((string)result.RecipeInstructions!).Should().Contain("Preheat");
        result.RecipeYield.Should().Be("24");
        result.PrepTime.Should().Be("PT15M");
        result.CookTime.Should().Be("PT12M");
    }

    [Fact]
    public void ExtractFromDescription_NoRecipe_ReturnsNull()
    {
        var text = Fixture("no-recipe.txt");
        _extractor.ExtractFromDescription(text, "Some Video").Should().BeNull();
    }

    [Fact]
    public void ExtractFromDescription_MixedContent_ExtractsRecipeSection()
    {
        var text = Fixture("mixed-content.txt");

        var result = _extractor.ExtractFromDescription(text, "Tomato Soup");

        result.Should().NotBeNull();
        result!.RecipeIngredient.Should().Contain(s => s.Contains("tomatoes"));
        ((string)result.RecipeInstructions!).Should().Contain("Preheat");
    }

    [Fact]
    public void ExtractFromDescription_Null_ReturnsNull()
    {
        _extractor.ExtractFromDescription(null, "Title").Should().BeNull();
        _extractor.ExtractFromDescription("", "Title").Should().BeNull();
        _extractor.ExtractFromDescription("   ", "Title").Should().BeNull();
    }

    [Fact]
    public void ExtractFromDescription_SocialLinksOnly_ReturnsNull()
    {
        var text = "Ingredients:\nFollow me on Instagram @chef\nSubscribe!";
        _extractor.ExtractFromDescription(text, "Test").Should().BeNull();
    }

    [Fact]
    public void ExtractFromDescription_InstructionsStringType_IsString()
    {
        // M7: RecipeInstructions must be a plain string, not a typed array.
        var text = Fixture("formatted-recipe.txt");
        var result = _extractor.ExtractFromDescription(text, "Title");
        result!.RecipeInstructions.Should().BeOfType<string>();
    }
}
