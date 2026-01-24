using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class UnitConverterTests
{
    private readonly UnitConverter _converter = new();

    [Fact]
    public void Convert_SameUnit_ReturnsUnchangedQuantity()
    {
        // Arrange
        var quantity = 2.5m;
        var unit = "cup";

        // Act
        var result = _converter.Convert(quantity, unit, unit);

        // Assert
        Assert.Equal(quantity, result);
    }

    [Fact]
    public void Convert_TablespoonToTeaspoon_ConvertsCorrectly()
    {
        // Arrange
        var quantity = 1m;
        var fromUnit = "tbsp";
        var toUnit = "tsp";

        // Act
        var result = _converter.Convert(quantity, fromUnit, toUnit);

        // Assert
        Assert.Equal(3m, result);
    }

    [Fact]
    public void Convert_CupToTablespoon_ConvertsCorrectly()
    {
        // Arrange
        var quantity = 1m;
        var fromUnit = "cup";
        var toUnit = "tbsp";

        // Act
        var result = _converter.Convert(quantity, fromUnit, toUnit);

        // Assert
        Assert.Equal(16m, result);
    }

    [Fact]
    public void Convert_FluidOunceToCup_ConvertsCorrectly()
    {
        // Arrange
        var quantity = 8m;
        var fromUnit = "fl oz";
        var toUnit = "cup";

        // Act
        var result = _converter.Convert(quantity, fromUnit, toUnit);

        // Assert
        Assert.Equal(1m, result);
    }

    [Fact]
    public void Convert_OunceToPound_ConvertsCorrectly()
    {
        // Arrange
        var quantity = 16m;
        var fromUnit = "oz";
        var toUnit = "lb";

        // Act
        var result = _converter.Convert(quantity, fromUnit, toUnit);

        // Assert
        Assert.Equal(1m, result);
    }

    [Fact]
    public void Convert_GramToKilogram_ConvertsCorrectly()
    {
        // Arrange
        var quantity = 1000m;
        var fromUnit = "g";
        var toUnit = "kg";

        // Act
        var result = _converter.Convert(quantity, fromUnit, toUnit);

        // Assert
        Assert.Equal(1m, result);
    }

    [Fact]
    public void Convert_CupToMilliliter_ConvertsCorrectly()
    {
        // Arrange
        var quantity = 1m;
        var fromUnit = "cup";
        var toUnit = "ml";

        // Act
        var result = _converter.Convert(quantity, fromUnit, toUnit);

        // Assert
        Assert.Equal(236.588m, result, precision: 3);
    }

    [Fact]
    public void Convert_PoundToGram_ConvertsCorrectly()
    {
        // Arrange
        var quantity = 1m;
        var fromUnit = "lb";
        var toUnit = "g";

        // Act
        var result = _converter.Convert(quantity, fromUnit, toUnit);

        // Assert
        Assert.Equal(453.592m, result, precision: 3);
    }

    [Fact]
    public void Convert_MixedFamilies_ThrowsInvalidOperationException()
    {
        // Arrange
        var quantity = 1m;
        var fromUnit = "cup"; // Volume
        var toUnit = "oz"; // Weight

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            _converter.Convert(quantity, fromUnit, toUnit));
    }

    [Fact]
    public void Convert_UnknownFromUnit_ThrowsInvalidOperationException()
    {
        // Arrange
        var quantity = 1m;
        var fromUnit = "unknown";
        var toUnit = "cup";

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            _converter.Convert(quantity, fromUnit, toUnit));
    }

    [Fact]
    public void Convert_UnknownToUnit_ThrowsInvalidOperationException()
    {
        // Arrange
        var quantity = 1m;
        var fromUnit = "cup";
        var toUnit = "unknown";

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            _converter.Convert(quantity, fromUnit, toUnit));
    }

    [Fact]
    public void Convert_NullFromUnit_ReturnsUnchangedQuantity()
    {
        // Arrange
        var quantity = 2.5m;
        string? fromUnit = null;
        var toUnit = "cup";

        // Act
        var result = _converter.Convert(quantity, fromUnit, toUnit);

        // Assert
        Assert.Equal(quantity, result);
    }

    [Fact]
    public void Convert_NullToUnit_ReturnsUnchangedQuantity()
    {
        // Arrange
        var quantity = 2.5m;
        var fromUnit = "cup";
        string? toUnit = null;

        // Act
        var result = _converter.Convert(quantity, fromUnit, toUnit);

        // Assert
        Assert.Equal(quantity, result);
    }

    [Fact]
    public void Convert_EmptyFromUnit_ReturnsUnchangedQuantity()
    {
        // Arrange
        var quantity = 2.5m;
        var fromUnit = "";
        var toUnit = "cup";

        // Act
        var result = _converter.Convert(quantity, fromUnit, toUnit);

        // Assert
        Assert.Equal(quantity, result);
    }

    [Fact]
    public void Convert_CaseInsensitive_ConvertsCorrectly()
    {
        // Arrange
        var quantity = 1m;
        var fromUnit = "Cup"; // Uppercase
        var toUnit = "TBSP"; // Uppercase

        // Act
        var result = _converter.Convert(quantity, fromUnit, toUnit);

        // Assert
        Assert.Equal(16m, result);
    }

    [Fact]
    public void Convert_PluralHandling_ConvertsCorrectly()
    {
        // Arrange
        var quantity = 2m;
        var fromUnit = "cups"; // Plural
        var toUnit = "tablespoons"; // Plural

        // Act
        var result = _converter.Convert(quantity, fromUnit, toUnit);

        // Assert
        Assert.Equal(32m, result);
    }

    [Fact]
    public void FindCommonUnit_AllSameVolume_ReturnsVolumeUnit()
    {
        // Arrange
        var units = new List<string?> { "cup", "tbsp", "tsp" };

        // Act
        var result = _converter.FindCommonUnit(units);

        // Assert
        Assert.NotNull(result);
        Assert.True(_converter.CanConvert("cup", result), "Result should be convertible from cup");
    }

    [Fact]
    public void FindCommonUnit_AllSameWeight_ReturnsWeightUnit()
    {
        // Arrange
        var units = new List<string?> { "oz", "lb", "g" };

        // Act
        var result = _converter.FindCommonUnit(units);

        // Assert
        Assert.NotNull(result);
        Assert.True(_converter.CanConvert("oz", result), "Result should be convertible from oz");
    }

    [Fact]
    public void FindCommonUnit_MixedFamilies_ReturnsNull()
    {
        // Arrange
        var units = new List<string?> { "cup", "oz" };

        // Act
        var result = _converter.FindCommonUnit(units);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindCommonUnit_EmptyList_ReturnsNull()
    {
        // Arrange
        var units = new List<string?>();

        // Act
        var result = _converter.FindCommonUnit(units);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindCommonUnit_AllNull_ReturnsNull()
    {
        // Arrange
        var units = new List<string?> { null, null, null };

        // Act
        var result = _converter.FindCommonUnit(units);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindCommonUnit_MostCommonUnit_IsPreferred()
    {
        // Arrange
        var units = new List<string?> { "cup", "cup", "cup", "tbsp" };

        // Act
        var result = _converter.FindCommonUnit(units);

        // Assert
        Assert.Equal("cup", result);
    }

    [Fact]
    public void CanConvert_SameFamilyVolume_ReturnsTrue()
    {
        // Arrange
        var fromUnit = "cup";
        var toUnit = "tbsp";

        // Act
        var result = _converter.CanConvert(fromUnit, toUnit);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void CanConvert_SameFamilyWeight_ReturnsTrue()
    {
        // Arrange
        var fromUnit = "oz";
        var toUnit = "lb";

        // Act
        var result = _converter.CanConvert(fromUnit, toUnit);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void CanConvert_DifferentFamilies_ReturnsFalse()
    {
        // Arrange
        var fromUnit = "cup";
        var toUnit = "oz";

        // Act
        var result = _converter.CanConvert(fromUnit, toUnit);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void CanConvert_UnknownUnit_ReturnsFalse()
    {
        // Arrange
        var fromUnit = "unknown";
        var toUnit = "cup";

        // Act
        var result = _converter.CanConvert(fromUnit, toUnit);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void CanConvert_CountUnit_DoesNotConvertToOthers()
    {
        // Arrange
        var fromUnit = "piece";
        var toUnit = "cup";

        // Act
        var result = _converter.CanConvert(fromUnit, toUnit);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Convert_WithWhitespace_HandlesCorrectly()
    {
        // Arrange
        var quantity = 1m;
        var fromUnit = " cup ";
        var toUnit = " tbsp ";

        // Act
        var result = _converter.Convert(quantity, fromUnit, toUnit);

        // Assert
        Assert.Equal(16m, result);
    }
}
