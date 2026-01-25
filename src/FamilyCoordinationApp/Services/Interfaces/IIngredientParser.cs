namespace FamilyCoordinationApp.Services.Interfaces;

public interface IIngredientParser
{
    ParsedIngredient ParseIngredient(string input);
}
