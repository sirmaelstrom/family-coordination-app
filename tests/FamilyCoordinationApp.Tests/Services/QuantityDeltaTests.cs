using FluentAssertions;
using FamilyCoordinationApp.Endpoints;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// The delta math that makes QuantityDelta live (quest f63bb90a, spec-lite §Regenerate pt 3). The
/// baseline — the generator's own number — must be invariant under repeated edits, or deltas would
/// compound: baseline = currentQuantity − (currentDelta ?? 0).
/// </summary>
public class QuantityDeltaTests
{
    [Fact]
    public void FirstEdit_RecordsTheDifferenceFromTheGeneratedQuantity()
    {
        ShoppingListEndpoints.ComputeQuantityDelta(5m, currentQuantity: 3m, currentDelta: null)
            .Should().Be(2m);
    }

    [Fact]
    public void SecondEdit_ComputesAgainstTheInvariantBaseline_NotTheLastEdit()
    {
        // Generated 3, edited to 5 (delta 2), now edited to 4: the delta is 1 over the ORIGINAL 3,
        // not −1 over the last edit — compounding against the last edit would corrupt regenerate.
        ShoppingListEndpoints.ComputeQuantityDelta(4m, currentQuantity: 5m, currentDelta: 2m)
            .Should().Be(1m);
    }

    [Fact]
    public void RevertingToTheBaseline_ClearsTheDelta()
    {
        ShoppingListEndpoints.ComputeQuantityDelta(3m, currentQuantity: 5m, currentDelta: 2m)
            .Should().BeNull();
    }

    [Fact]
    public void NullCurrentQuantity_TreatsTheBaselineAsZero()
    {
        ShoppingListEndpoints.ComputeQuantityDelta(2m, currentQuantity: null, currentDelta: null)
            .Should().Be(2m);
    }
}
