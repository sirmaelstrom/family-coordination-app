using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// The shopping breakdown joins are bounded to their columns (quest b942b943, the fca#129 AG binding): a join at
/// the limit is kept whole, a join past it keeps the parts that fit plus a visible "… (+N more)" marker, and no
/// input yields a string longer than the column.
/// </summary>
public class BoundedJoinTests
{
    [Fact]
    public void A_join_exactly_at_the_limit_is_kept_whole()
    {
        var parts = new[] { "aaaa", "bbbb" }; // "aaaa + bbbb" = 11
        BoundedJoin.Join(parts, " + ", 11).Should().Be("aaaa + bbbb");
    }

    [Fact]
    public void A_join_one_past_the_limit_keeps_the_parts_that_fit_and_marks_the_rest()
    {
        var parts = new[] { "aaaaaaaaaaaa", "bbbbbbbbbbbb" }; // whole join = 27
        // "aaaaaaaaaaaa + … (+1 more)" is 26: one under the whole join keeps the first part and marks the second.
        BoundedJoin.Join(parts, " + ", 26).Should().Be("aaaaaaaaaaaa + … (+1 more)");
        BoundedJoin.Join(parts, " + ", 25).Should().Be("… (+2 more)");
    }

    [Fact]
    public void Without_a_marker_whole_parts_past_the_limit_are_dropped()
    {
        BoundedJoin.Join(["1:2:3", "1:2:4", "1:2:5"], ",", 11, marker: false).Should().Be("1:2:3,1:2:4");
    }

    [Fact]
    public void A_limit_shorter_than_the_bare_marker_still_fits()
    {
        BoundedJoin.Join(["abcdef"], ", ", 3).Should().HaveLength(3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(40)]
    [InlineData(500)]
    public void No_input_exceeds_the_limit(int partCount)
    {
        var parts = Enumerable.Range(0, partCount).Select(i => new string('x', i % 23 + 1)).ToList();
        foreach (var limit in new[] { 0, 5, 50, FieldLengths.ShoppingListItem.OriginalUnits, FieldLengths.ShoppingListItem.SourceRecipes })
        {
            BoundedJoin.Join(parts, " + ", limit).Length.Should().BeLessThanOrEqualTo(limit);
            BoundedJoin.Join(parts, ",", limit, marker: false).Length.Should().BeLessThanOrEqualTo(limit);
        }
    }

    [Fact]
    public void Repeats_collapse_into_a_count_keeping_first_appearance_order()
    {
        BoundedJoin.CollapseRepeats(["1 cup", "8 fl oz", "1 cup", "1 cup"])
            .Should().Equal("3 × 1 cup", "8 fl oz");
    }

    // ── Through the consolidator: OriginalUnits at and past its column ─────────────────────────────

    private static ShoppingListGenerator Generator() =>
        new(new Mock<IDbContextFactory<ApplicationDbContext>>().Object,
            new Mock<IShoppingListService>().Object,
            new UnitConverter(),
            NullLogger<ShoppingListGenerator>.Instance,
            TestClocks.System);

    private static RecipeIngredient Butter(int recipeId, decimal qty) => new()
    {
        HouseholdId = 1, RecipeId = recipeId, IngredientId = 1, Name = "butter", Quantity = qty, Unit = "tablespoons",
        Category = "Dairy", Recipe = new Recipe { HouseholdId = 1, RecipeId = recipeId, Name = $"R{recipeId}" }
    };

    [Fact]
    public async Task OriginalUnits_of_distinct_amounts_past_the_column_is_bounded_and_marked()
    {
        // 40 distinct amounts ("1.01 tablespoons" … ) cannot collapse, so the join itself must be cut.
        var ingredients = Enumerable.Range(1, 40).Select(i => Butter(i, 1m + i / 100m)).ToList();

        var result = (await Generator().ConsolidateIngredientsAsync(ingredients)).Single();

        result.OriginalUnits!.Length.Should().BeLessThanOrEqualTo(FieldLengths.ShoppingListItem.OriginalUnits);
        result.OriginalUnits.Should().StartWith("1.01 tablespoons + 1.02 tablespoons").And.MatchRegex(@"… \(\+\d+ more\)$");
        result.Quantity.Should().Be(Enumerable.Range(1, 40).Sum(i => 1m + i / 100m), "the total is not bounded, only its breakdown");
    }

    [Fact]
    public async Task OriginalUnits_of_one_amount_repeated_collapses_instead_of_overflowing()
    {
        var ingredients = Enumerable.Range(1, 30).Select(i => Butter(i, 1.25m)).ToList();

        var result = (await Generator().ConsolidateIngredientsAsync(ingredients)).Single();

        result.OriginalUnits.Should().Be("30 × 1.25 tablespoons");
    }

    [Fact]
    public async Task OriginalUnits_under_the_column_is_unchanged()
    {
        var ingredients = new List<RecipeIngredient> { Butter(1, 1m), Butter(2, 2m) };

        var result = (await Generator().ConsolidateIngredientsAsync(ingredients)).Single();

        result.OriginalUnits.Should().Be("1 tablespoons + 2 tablespoons");
    }
}
