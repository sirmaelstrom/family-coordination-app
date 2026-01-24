using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

public interface ICategoryService
{
    Task<List<Category>> GetCategoriesAsync(int householdId, CancellationToken cancellationToken = default);
}

public class CategoryService : ICategoryService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public CategoryService(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<Category>> GetCategoriesAsync(int householdId, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.Categories
            .Where(c => c.HouseholdId == householdId)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(cancellationToken);
    }
}
