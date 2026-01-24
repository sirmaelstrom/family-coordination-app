using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

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

public class CategoryService : ICategoryService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ILogger<CategoryService> _logger;

    public CategoryService(IDbContextFactory<ApplicationDbContext> dbFactory, ILogger<CategoryService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<List<Category>> GetCategoriesAsync(int householdId, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var query = context.Categories
            .Where(c => c.HouseholdId == householdId);

        if (includeDeleted)
        {
            query = query.IgnoreQueryFilters();
        }

        return await query
            .OrderBy(c => c.SortOrder)
            .ToListAsync(cancellationToken);
    }

    public async Task<Category?> GetCategoryAsync(int householdId, int categoryId, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.Categories
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.HouseholdId == householdId && c.CategoryId == categoryId, cancellationToken);
    }

    public async Task<Category> CreateCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        category.CategoryId = await GetNextCategoryIdInternalAsync(context, category.HouseholdId, cancellationToken);

        // Set sort order to end of list if not specified
        if (category.SortOrder == 0)
        {
            var maxOrder = await context.Categories
                .Where(c => c.HouseholdId == category.HouseholdId)
                .MaxAsync(c => (int?)c.SortOrder, cancellationToken) ?? 0;
            category.SortOrder = maxOrder + 1;
        }

        context.Categories.Add(category);
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created category {CategoryId} for household {HouseholdId}", category.CategoryId, category.HouseholdId);

        return category;
    }

    public async Task<Category> UpdateCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.Categories
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.HouseholdId == category.HouseholdId && c.CategoryId == category.CategoryId, cancellationToken);

        if (existing == null)
        {
            throw new InvalidOperationException($"Category {category.CategoryId} not found");
        }

        existing.Name = category.Name;
        existing.IconEmoji = category.IconEmoji;
        existing.Color = category.Color;
        existing.SortOrder = category.SortOrder;

        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Updated category {CategoryId} for household {HouseholdId}", category.CategoryId, category.HouseholdId);

        return existing;
    }

    public async Task DeleteCategoryAsync(int householdId, int categoryId, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var category = await context.Categories
            .FirstOrDefaultAsync(c => c.HouseholdId == householdId && c.CategoryId == categoryId, cancellationToken);

        if (category == null)
        {
            throw new InvalidOperationException($"Category {categoryId} not found");
        }

        // Soft delete
        category.IsDeleted = true;
        category.DeletedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Soft deleted category {CategoryId} for household {HouseholdId}", categoryId, householdId);
    }

    public async Task RestoreCategoryAsync(int householdId, int categoryId, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var category = await context.Categories
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.HouseholdId == householdId && c.CategoryId == categoryId && c.IsDeleted, cancellationToken);

        if (category == null)
        {
            throw new InvalidOperationException($"Deleted category {categoryId} not found");
        }

        category.IsDeleted = false;
        category.DeletedAt = null;

        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Restored category {CategoryId} for household {HouseholdId}", categoryId, householdId);
    }

    public async Task UpdateSortOrderAsync(int householdId, List<(int CategoryId, int SortOrder)> sortOrders, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        foreach (var (categoryId, sortOrder) in sortOrders)
        {
            var category = await context.Categories
                .FirstOrDefaultAsync(c => c.HouseholdId == householdId && c.CategoryId == categoryId, cancellationToken);

            if (category != null)
            {
                category.SortOrder = sortOrder;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> GetNextCategoryIdAsync(int householdId, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await GetNextCategoryIdInternalAsync(context, householdId, cancellationToken);
    }

    private static async Task<int> GetNextCategoryIdInternalAsync(ApplicationDbContext context, int householdId, CancellationToken cancellationToken)
    {
        var maxId = await context.Categories
            .IgnoreQueryFilters()
            .Where(c => c.HouseholdId == householdId)
            .MaxAsync(c => (int?)c.CategoryId, cancellationToken) ?? 0;

        return maxId + 1;
    }

    public async Task<bool> HasIngredientsAsync(int householdId, string categoryName, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.RecipeIngredients
            .AnyAsync(i => i.HouseholdId == householdId && i.Category == categoryName, cancellationToken);
    }
}
