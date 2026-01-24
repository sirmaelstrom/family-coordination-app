using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class IngredientParserTests
{
    private readonly IIngredientParser _parser;

    public IngredientParserTests()
    {
        _parser = new IngredientParser();
    }

    [Fact]
    public void ParseIngredient_SimpleQuantityAndUnit_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("2 cups flour");

        Assert.Equal(2m, result.Quantity);
        Assert.Equal("cups", result.Unit);
        Assert.Equal("flour", result.Name);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void ParseIngredient_SimpleFraction_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("1/2 cup milk");

        Assert.Equal(0.5m, result.Quantity);
        Assert.Equal("cup", result.Unit);
        Assert.Equal("milk", result.Name);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void ParseIngredient_MixedFraction_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("1 1/2 cups flour");

        Assert.Equal(1.5m, result.Quantity);
        Assert.Equal("cups", result.Unit);
        Assert.Equal("flour", result.Name);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void ParseIngredient_UnitlessQuantity_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("3 eggs");

        Assert.Equal(3m, result.Quantity);
        Assert.Null(result.Unit);
        Assert.Equal("eggs", result.Name);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void ParseIngredient_NoQuantity_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("salt to taste");

        Assert.Null(result.Quantity);
        Assert.Null(result.Unit);
        Assert.Equal("salt to taste", result.Name);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void ParseIngredient_WithCommaNote_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("2 lbs chicken breast, boneless");

        Assert.Equal(2m, result.Quantity);
        Assert.Equal("lbs", result.Unit);
        Assert.Equal("chicken breast", result.Name);
        Assert.Equal("boneless", result.Notes);
    }

    [Fact]
    public void ParseIngredient_WithParenthesesNote_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("1 (14 oz) can diced tomatoes");

        Assert.Equal(1m, result.Quantity);
        Assert.Equal("can", result.Unit);
        Assert.Equal("diced tomatoes", result.Name);
        Assert.Equal("14 oz", result.Notes);
    }

    [Fact]
    public void ParseIngredient_RangeQuantity_UsesAverageMidpoint()
    {
        var result = _parser.ParseIngredient("2-3 cloves garlic, minced");

        Assert.Equal(2.5m, result.Quantity);
        Assert.Equal("cloves", result.Unit);
        Assert.Equal("garlic", result.Name);
        Assert.Equal("minced", result.Notes);
    }

    [Fact]
    public void ParseIngredient_UnicodeFraction_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("½ cup sugar");

        Assert.Equal(0.5m, result.Quantity);
        Assert.Equal("cup", result.Unit);
        Assert.Equal("sugar", result.Name);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void ParseIngredient_UnicodeMixedFraction_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("1½ cups flour");

        Assert.Equal(1.5m, result.Quantity);
        Assert.Equal("cups", result.Unit);
        Assert.Equal("flour", result.Name);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void ParseIngredient_TablespoonAbbreviation_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("2 tbsp butter");

        Assert.Equal(2m, result.Quantity);
        Assert.Equal("tbsp", result.Unit);
        Assert.Equal("butter", result.Name);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void ParseIngredient_TeaspoonFull_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("1 teaspoon vanilla extract");

        Assert.Equal(1m, result.Quantity);
        Assert.Equal("teaspoon", result.Unit);
        Assert.Equal("vanilla extract", result.Name);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void ParseIngredient_MetricWeight_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("500 g ground beef");

        Assert.Equal(500m, result.Quantity);
        Assert.Equal("g", result.Unit);
        Assert.Equal("ground beef", result.Name);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void ParseIngredient_MetricVolume_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("250 ml milk");

        Assert.Equal(250m, result.Quantity);
        Assert.Equal("ml", result.Unit);
        Assert.Equal("milk", result.Name);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void ParseIngredient_FluidOunces_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("8 fl oz water");

        Assert.Equal(8m, result.Quantity);
        Assert.Equal("fl oz", result.Unit);
        Assert.Equal("water", result.Name);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void ParseIngredient_MultipleParenthesesNotes_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("2 (15 oz) cans black beans, drained and rinsed");

        Assert.Equal(2m, result.Quantity);
        Assert.Equal("cans", result.Unit);
        Assert.Equal("black beans", result.Name);
        Assert.Equal("15 oz, drained and rinsed", result.Notes);
    }

    [Fact]
    public void ParseIngredient_DecimalQuantity_ParsesCorrectly()
    {
        var result = _parser.ParseIngredient("0.5 cup olive oil");

        Assert.Equal(0.5m, result.Quantity);
        Assert.Equal("cup", result.Unit);
        Assert.Equal("olive oil", result.Name);
        Assert.Null(result.Notes);
    }

    [Fact]
    public void ParseIngredient_PluralUnit_ParsesAsIs()
    {
        var result = _parser.ParseIngredient("3 tablespoons honey");

        Assert.Equal(3m, result.Quantity);
        Assert.Equal("tablespoons", result.Unit);
        Assert.Equal("honey", result.Name);
        Assert.Null(result.Notes);
    }
}
