using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

public class CategoryService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<CategoryService> logger,
    IHouseholdClock clock,
    IKeyAllocator keys) : ICategoryService
{

    public async Task<List<Category>> GetCategoriesAsync(int householdId, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var query = context.Categories.AsQueryable();

        if (includeDeleted)
        {
            query = query.IgnoreQueryFilters(["SoftDelete"]);
        }

        return await query
            .OrderBy(c => c.SortOrder)
            .ToListAsync(cancellationToken);
    }

    public async Task<Category?> GetCategoryAsync(int householdId, int categoryId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.Categories
            .IgnoreQueryFilters(["SoftDelete"])
            .FirstOrDefaultAsync(c => c.CategoryId == categoryId, cancellationToken);
    }

    public async Task<Category> CreateCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        return await keys.InsertAsync(
            "Category",
            async (attempt) =>
            {
                await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

                category.CategoryId = await keys.NextKeyAsync(context.Categories, c => c.CategoryId, cancellationToken);

                // Set sort order to end of list if not specified
                if (category.SortOrder == 0)
                {
                    var maxOrder = await context.Categories
                        .MaxAsync(c => (int?)c.SortOrder, cancellationToken) ?? 0;
                    category.SortOrder = maxOrder + 1;
                }

                context.Categories.Add(category);
                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Created category {CategoryId} for household {HouseholdId}", category.CategoryId, category.HouseholdId);

                return category;
            });
    }

    public async Task<Category> UpdateCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.Categories
            .IgnoreQueryFilters(["SoftDelete"])
            .FirstOrDefaultAsync(c => c.CategoryId == category.CategoryId, cancellationToken);

        if (existing == null)
        {
            throw new NotFoundException("Category not found.", $"Category {category.CategoryId} not found");
        }

        existing.Name = category.Name;
        existing.IconEmoji = category.IconEmoji;
        existing.Color = category.Color;
        existing.SortOrder = category.SortOrder;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Updated category {CategoryId} for household {HouseholdId}", category.CategoryId, category.HouseholdId);

        return existing;
    }

    public async Task DeleteCategoryAsync(int householdId, int categoryId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var category = await context.Categories
            .FirstOrDefaultAsync(c => c.CategoryId == categoryId, cancellationToken);

        if (category == null)
        {
            throw new NotFoundException("Category not found.", $"Category {categoryId} not found");
        }

        // Soft delete
        category.IsDeleted = true;
        category.DeletedAt = clock.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Soft deleted category {CategoryId} for household {HouseholdId}", categoryId, householdId);
    }

    public async Task RestoreCategoryAsync(int householdId, int categoryId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var category = await context.Categories
            .IgnoreQueryFilters(["SoftDelete"])
            .FirstOrDefaultAsync(c => c.CategoryId == categoryId && c.IsDeleted, cancellationToken);

        if (category == null)
        {
            throw new NotFoundException("Category not found.", $"Deleted category {categoryId} not found");
        }

        category.IsDeleted = false;
        category.DeletedAt = null;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Restored category {CategoryId} for household {HouseholdId}", categoryId, householdId);
    }

    public async Task UpdateSortOrderAsync(int householdId, List<(int CategoryId, int SortOrder)> sortOrders, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        foreach (var (categoryId, sortOrder) in sortOrders)
        {
            var category = await context.Categories
                .FirstOrDefaultAsync(c => c.CategoryId == categoryId, cancellationToken);

            if (category != null)
            {
                category.SortOrder = sortOrder;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> HasIngredientsAsync(int householdId, string categoryName, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.RecipeIngredients
            .AnyAsync(i => i.Category == categoryName, cancellationToken);
    }
}
