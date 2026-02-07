using System.ComponentModel.DataAnnotations;

namespace FamilyCoordinationApp.Data.Entities;

public class Recipe
{
    public int HouseholdId { get; set; }
    public int RecipeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Instructions { get; set; }
    public string? ImagePath { get; set; }
    public string? SourceUrl { get; set; }
    public int? Servings { get; set; }
    public int? PrepTimeMinutes { get; set; }
    public int? CookTimeMinutes { get; set; }
    public RecipeType RecipeType { get; set; } = RecipeType.Main;
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedByUserId { get; set; }
    public bool IsDeleted { get; set; }

    // Attribution — set when recipe was copied from a connected household
    public int? SharedFromHouseholdId { get; set; }
    public string? SharedFromHouseholdName { get; set; } // Denormalized — survives disconnect
    public int? SharedFromRecipeId { get; set; } // Reference only, no FK (source may be in different household)

    // Concurrency token (maps to PostgreSQL xmin)
    [Timestamp]
    public uint Version { get; set; }

    // Navigation
    public Household Household { get; set; } = default!;
    public Household? SharedFromHousehold { get; set; }
    public User? CreatedBy { get; set; }
    public User? UpdatedBy { get; set; }
    public ICollection<RecipeIngredient> Ingredients { get; set; } = new List<RecipeIngredient>();
}
