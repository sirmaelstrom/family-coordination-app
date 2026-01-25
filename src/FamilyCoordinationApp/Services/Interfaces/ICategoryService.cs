using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface ICategoryService
{
    Task<List<Category>> GetCategoriesAsync(int householdId, bool includeDeleted = false, CancellationToken cancellationToken = default);
    Task<Category?> GetCategoryAsync(int householdId, int categoryId, CancellationToken cancellationToken = default);
    Task<Category> CreateCategoryAsync(Category category, CancellationToken cancellationToken = default);
    Task<Category> UpdateCategoryAsync(Category category, CancellationToken cancellationToken = default);
    Task DeleteCategoryAsync(int householdId, int categoryId, CancellationToken cancellationToken = default);
    Task RestoreCategoryAsync(int householdId, int categoryId, CancellationToken cancellationToken = default);
    Task UpdateSortOrderAsync(int householdId, List<(int CategoryId, int SortOrder)> sortOrders, CancellationToken cancellationToken = default);
    Task<int> GetNextCategoryIdAsync(int householdId, CancellationToken cancellationToken = default);
    Task<bool> HasIngredientsAsync(int householdId, string categoryName, CancellationToken cancellationToken = default);
}
