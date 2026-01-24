namespace FamilyCoordinationApp.Services;

public class ParsedIngredient
{
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public interface IIngredientParser
{
    ParsedIngredient ParseIngredient(string input);
}

public class IngredientParser : IIngredientParser
{
    public ParsedIngredient ParseIngredient(string input)
    {
        throw new NotImplementedException();
    }
}
