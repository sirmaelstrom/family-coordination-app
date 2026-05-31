# WP-07: Dev seed enrichment (multi-member + cross-member completions)

**Wave:** 2 · **Execution:** autonomous · **Depends on:** — (existing entities; independent of WP-01)

## Precondition
`SeedData.SeedChoresAndRoomsAsync` (`Data/SeedData.cs:135`) seeds 5 rooms + 11 chores + backdated completions
**all authored by the single first household user**. `SeedDevelopmentDataAsync` is the dev startup path.

## Goal
Make the **dev** seed render a real multi-person equity distribution: ensure ≥3 household members and spread
backdated completions across them with varied effort. Production behavior unchanged.

## Files
- **Modify** `src/FamilyCoordinationApp/Data/SeedData.cs` (a dev-only enrichment, called from `SeedDevelopmentDataAsync` only)
- **Create/Modify** `tests/FamilyCoordinationApp.Tests/Services/ChoreSeedEquityTests.cs` (or extend `ChoreSeedTests`)

## Implementation notes
- **Recipe-guard footgun (P1 from review):** the existing `SeedDevelopmentDataAsync` (`SeedData.cs:~10`) returns
  early when `Recipes.Any()`, and the v1.0 chore seed sits *inside* that guard — so on any dev DB that already
  has recipes, neither the chore seed nor a naively-appended enrichment runs. **Restructure** so the recipe/
  category seed stays behind the recipe guard, but `SeedChoresAndRoomsAsync` **and** the new
  `SeedDevEquityDataAsync` are invoked on a path that executes **even when recipes already exist** (each is
  independently idempotent via its own guard). Otherwise the enrichment is dead code on an existing dev DB.
- `SeedDevEquityDataAsync(dbFactory, householdId)`: ensure the dev household has additional `User` members
  (realistic names — e.g. Natalie, Tristan, Samantha alongside the existing seed user; reuse the existing
  `User` creation idiom, HouseholdId-scoped, composite keys). Then insert **backdated `ChoreCompletion` rows
  authored by different `CompletedByUserId`s** within the last week, varied `EffortPointsSnapshot`
  (Quick/Standard/BigJob mix) so the distribution is non-trivial. Direct context insert (M13). **Own
  idempotency guard:** no-op if the household already has >1 member AND cross-member completions exist (so
  repeated dev startups don't duplicate). Follow the existing `SeedDevelopmentDataAsync` **synchronous**
  `using var context = dbFactory.CreateDbContext()` pattern for consistency (the method may be async for the
  inserts; match the surrounding style).
- Wire ONLY into the dev path (`SeedDevelopmentDataAsync` in `SeedData.cs`) — **not** `SetupService.
  CreateHouseholdAsync` (a fresh prod household legitimately has one member; MN11). This is a `SeedData.cs`
  change only — do **not** touch `Program.cs` (its dev-seed call site is unchanged; Program.cs is WP-06's fence).

## Verification (V10)
- Unit (InMemory): call `SeedDevEquityDataAsync` **directly** (not via the recipe-guarded wrapper, so the test
  is independent of the recipe guard): after it runs, the household has ≥3 members and `ChoreCompletion`s
  grouped by `CompletedByUserId` show >1 author with non-zero `EffortPointsSnapshot`; a second direct call
  does **not** duplicate (its own guard). Unit suite green; format clean.

## Failure criteria
- Production (`CreateHouseholdAsync`) household gains synthetic extra members/completions (MN11). · Completions
  all still one author. · Non-idempotent (duplicates on re-run). · Touches non-seed files.

## Boundary
`SeedData.cs` (dev path) + its test ONLY. No entity/migration change, no endpoint, no `Program.cs`, no
production-seed change.

## Notes for downstream
- This is what makes the WP-09 Equity lens demoable locally with `CHORES_USE_ISLAND=true` and what GAP-2's
  manual UI check renders against. WP-08 may reuse the multi-member shape for an equity-isolation assertion.
