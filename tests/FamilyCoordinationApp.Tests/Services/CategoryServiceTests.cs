using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class CategoryServiceTests : IDisposable
{
    private readonly ApplicationDbContext _context;
    private readonly Mock<IDbContextFactory<ApplicationDbContext>> _dbFactoryMock;
    private readonly Mock<ILogger<CategoryService>> _loggerMock;
    private readonly CategoryService _service;

    public CategoryServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options);
        _dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        _dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));
        _loggerMock = new Mock<ILogger<CategoryService>>();

        _service = new CategoryService(_dbFactoryMock.Object, _loggerMock.Object);

        // Seed test data
        SeedTestData();
    }

    private void SeedTestData()
    {
        // Create two households
        _context.Households.AddRange(
            new Household { Id = 1, Name = "Smith Family" },
            new Household { Id = 2, Name = "Jones Family" }
        );

        // Categories for household 1
        _context.Categories.AddRange(
            new Category { HouseholdId = 1, CategoryId = 1, Name = "Produce", SortOrder = 1 },
            new Category { HouseholdId = 1, CategoryId = 2, Name = "Dairy", SortOrder = 2 },
            new Category { HouseholdId = 1, CategoryId = 3, Name = "Deleted Category", SortOrder = 3, IsDeleted = true, DeletedAt = DateTime.UtcNow }
        );

        // Categories for household 2
        _context.Categories.AddRange(
            new Category { HouseholdId = 2, CategoryId = 1, Name = "Meat", SortOrder = 1 },
            new Category { HouseholdId = 2, CategoryId = 2, Name = "Bakery", SortOrder = 2 }
        );

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    [Fact]
    public async Task CreateCategoryAsync_CreatesWithCorrectHouseholdId()
    {
        // Arrange
        var newCategory = new Category
        {
            HouseholdId = 1,
            Name = "Frozen Foods",
            IconEmoji = "snowflake",
            Color = "#00BFFF"
        };

        // Act
        var result = await _service.CreateCategoryAsync(newCategory);

        // Assert
        result.HouseholdId.Should().Be(1);
        result.Name.Should().Be("Frozen Foods");
        result.CategoryId.Should().BeGreaterThan(0);
        result.SortOrder.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CreateCategoryAsync_AssignsNextCategoryIdForHousehold()
    {
        // Arrange
        var newCategory = new Category
        {
            HouseholdId = 1,
            Name = "Snacks"
        };

        // Act
        var result = await _service.CreateCategoryAsync(newCategory);

        // Assert
        // Household 1 has categories 1, 2, 3 (including deleted), so next should be 4
        result.CategoryId.Should().Be(4);
    }

    [Fact]
    public async Task GetCategoriesAsync_ReturnsOnlyCurrentHouseholdsCategories()
    {
        // Act
        var household1Categories = await _service.GetCategoriesAsync(householdId: 1);
        var household2Categories = await _service.GetCategoriesAsync(householdId: 2);

        // Assert
        household1Categories.Should().HaveCount(2); // Excludes deleted by default
        household1Categories.Should().OnlyContain(c => c.HouseholdId == 1);
        household1Categories.Select(c => c.Name).Should().Contain("Produce", "Dairy");
        household1Categories.Select(c => c.Name).Should().NotContain("Meat", "Bakery");

        household2Categories.Should().HaveCount(2);
        household2Categories.Should().OnlyContain(c => c.HouseholdId == 2);
        household2Categories.Select(c => c.Name).Should().Contain("Meat", "Bakery");
    }

    [Fact]
    public async Task GetCategoriesAsync_ExcludesDeletedByDefault()
    {
        // Act
        var categories = await _service.GetCategoriesAsync(householdId: 1, includeDeleted: false);

        // Assert
        categories.Should().NotContain(c => c.Name == "Deleted Category");
        categories.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetCategoriesAsync_IncludesDeletedWhenRequested()
    {
        // Act
        var categories = await _service.GetCategoriesAsync(householdId: 1, includeDeleted: true);

        // Assert
        categories.Should().Contain(c => c.Name == "Deleted Category");
        categories.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetCategoriesAsync_ReturnsOrderedBySortOrder()
    {
        // Act
        var categories = await _service.GetCategoriesAsync(householdId: 1);

        // Assert
        categories.Should().BeInAscendingOrder(c => c.SortOrder);
    }

    [Fact]
    public async Task UpdateCategoryAsync_UpdatesTheRightCategory()
    {
        // Arrange
        var categoryToUpdate = new Category
        {
            HouseholdId = 1,
            CategoryId = 1,
            Name = "Fresh Produce",
            IconEmoji = "leafy_green",
            Color = "#32CD32",
            SortOrder = 1
        };

        // Act
        var result = await _service.UpdateCategoryAsync(categoryToUpdate);

        // Assert
        result.Name.Should().Be("Fresh Produce");
        result.IconEmoji.Should().Be("leafy_green");
        result.Color.Should().Be("#32CD32");

        // Verify household 2's category with same ID is unchanged
        var household2Category = await _service.GetCategoryAsync(householdId: 2, categoryId: 1);
        household2Category!.Name.Should().Be("Meat"); // Original name unchanged
    }

    [Fact]
    public async Task UpdateCategoryAsync_ThrowsWhenCategoryNotFound()
    {
        // Arrange
        var nonExistentCategory = new Category
        {
            HouseholdId = 1,
            CategoryId = 999,
            Name = "Does Not Exist"
        };

        // Act
        var act = async () => await _service.UpdateCategoryAsync(nonExistentCategory);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*999*not found*");
    }

    [Fact]
    public async Task DeleteCategoryAsync_SoftDeletesCategory()
    {
        // Act
        await _service.DeleteCategoryAsync(householdId: 1, categoryId: 2);

        // Assert - Category should be soft deleted
        var deletedCategory = await _service.GetCategoryAsync(householdId: 1, categoryId: 2);
        deletedCategory.Should().NotBeNull();
        deletedCategory!.IsDeleted.Should().BeTrue();
        deletedCategory.DeletedAt.Should().NotBeNull();
        deletedCategory.DeletedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DeleteCategoryAsync_DoesNotAffectOtherHouseholds()
    {
        // Arrange - Both households have category ID 2
        var household2CategoryBefore = await _service.GetCategoryAsync(householdId: 2, categoryId: 2);
        household2CategoryBefore!.Name.Should().Be("Bakery");

        // Act - Delete from household 1
        await _service.DeleteCategoryAsync(householdId: 1, categoryId: 2);

        // Assert - Household 2's category should be unaffected
        var household2CategoryAfter = await _service.GetCategoryAsync(householdId: 2, categoryId: 2);
        household2CategoryAfter.Should().NotBeNull();
        household2CategoryAfter!.IsDeleted.Should().BeFalse();
        household2CategoryAfter.Name.Should().Be("Bakery");
    }

    [Fact]
    public async Task DeleteCategoryAsync_ThrowsWhenCategoryNotFound()
    {
        // Act
        var act = async () => await _service.DeleteCategoryAsync(householdId: 1, categoryId: 999);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*999*not found*");
    }

    [Fact]
    public async Task RestoreCategoryAsync_RestoresSoftDeletedCategory()
    {
        // Arrange - Category 3 is already soft deleted
        var deletedCategory = await _service.GetCategoryAsync(householdId: 1, categoryId: 3);
        deletedCategory!.IsDeleted.Should().BeTrue();

        // Act
        await _service.RestoreCategoryAsync(householdId: 1, categoryId: 3);

        // Assert
        var restoredCategory = await _service.GetCategoryAsync(householdId: 1, categoryId: 3);
        restoredCategory.Should().NotBeNull();
        restoredCategory!.IsDeleted.Should().BeFalse();
        restoredCategory.DeletedAt.Should().BeNull();
    }
}
