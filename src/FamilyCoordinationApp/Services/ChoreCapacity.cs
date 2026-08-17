using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// The ONE owner of the physical-capacity doctrine (Phase 15 / R4′, quest e7f8be86): tier → equity
/// weight, tier → effort fit set, and the "Fits me" whitelist gate. Pure and static, mirroring
/// <see cref="ChoreStatusCalculator"/>'s shape. Tier strings are <see cref="CapacityTier"/> constants;
/// a null/absent/unrecognized tier always reads as <c>Full</c>.
/// <para>The SPA's <c>capacity-fit.ts</c> is a MIRROR of the fit-set half, held to this module by the
/// <c>Fixtures/ChoreCapacity/capacity-ladder.json</c> pin (byte-compared here, list-equality-checked
/// there) — change the doctrine in this file and both sides of the pin walk you through the rest.</para>
/// </summary>
public static class ChoreCapacity
{
    /// <summary>
    /// Tier → expected-share weight (Phase 15 D3). <c>Minimal</c> is 0.15, NOT zero — keeps a Minimal
    /// member's reference humane and Σweight &gt; 0.
    /// </summary>
    public static double WeightFor(string? tier) => tier switch
    {
        CapacityTier.Reduced => 0.5,
        CapacityTier.Minimal => 0.15,
        _ => 1.0, // Full, null, or any unrecognized value
    };

    /// <summary>
    /// The effort tiers that FIT a capacity tier: <c>Minimal</c> → Quick; <c>Reduced</c> → Quick+Standard;
    /// <c>Full</c>/unset → everything (the chip is hidden for these, so that branch is a safety net).
    /// </summary>
    public static IReadOnlyList<EffortTier> FitSetFor(string? tier) => tier switch
    {
        CapacityTier.Minimal => new[] { EffortTier.Quick },
        CapacityTier.Reduced => new[] { EffortTier.Quick, EffortTier.Standard },
        _ => new[] { EffortTier.Quick, EffortTier.Standard, EffortTier.BigJob },
    };

    /// <summary>Does a chore's declared effort tier fit the viewer's own capacity tier?</summary>
    public static bool Fits(EffortTier effortTier, string? tier) => FitSetFor(tier).Contains(effortTier);

    /// <summary>
    /// Does the "Fits me" affordance render at all? WHITELIST (R4′ V1.3): only <c>Reduced</c> and
    /// <c>Minimal</c>. Never express this as <c>tier != Full</c> — that would leak the chip to an
    /// unset-tier viewer, breaking the founding-case guarantee the feature exists to protect.
    /// </summary>
    public static bool ShowsFitsMe(string? tier) =>
        tier is CapacityTier.Reduced or CapacityTier.Minimal;
}
