---
phase: 04-shopping-list-core
plan: 01
type: tdd
subsystem: shopping-list
tags: [unit-conversion, tdd, cooking-measurements, consolidation]
requires: []
provides:
  - UnitConverter service for cooking measurement conversions
  - Lookup-table approach for volume/weight/count families
  - Support for ingredient consolidation in shopping lists
affects:
  - 04-02 (ShoppingListService may use UnitConverter)
  - 04-03 (ShoppingListGenerator will use UnitConverter for consolidation)
tech-stack:
  added: []
  patterns:
    - Lookup table for unit conversions
    - Decimal rounding to avoid floating-point precision issues
    - TDD red-green-refactor cycle
key-files:
  created:
    - src/FamilyCoordinationApp/Services/UnitConverter.cs
    - tests/FamilyCoordinationApp.Tests/Services/UnitConverterTests.cs
  modified: []
decisions:
  - id: unit-conversion-lookup-table
    what: Use lookup table instead of UnitsNet library
    why: UnitsNet supports 100+ unit types (overkill). Cooking domain has ~15 units. Lookup table is 50 lines and sufficient.
    alternatives: UnitsNet (too heavy), runtime calculation (harder to maintain)
    impact: Lightweight, maintainable, fast lookups
  - id: decimal-rounding-precision
    what: Round conversion results to 10 decimal places
    why: Division introduces floating-point precision errors (e.g., 0.0625 / 0.020833... ≠ exactly 3)
    alternatives: Using Fractions library (adds complexity), accepting precision errors (test failures)
    impact: All conversions produce clean results without precision artifacts
  - id: base-unit-approach
    what: Convert to base unit (cup for volume, gram for weight) then to target
    why: Simplifies conversion table - only need one factor per unit (to base), not N×N matrix
    alternatives: Direct conversion factors between all pairs (exponentially more entries)
    impact: Conversion table is linear in size, easy to extend with new units
  - id: count-family-no-conversion
    what: Count units (piece, can, bunch) don't convert to other families
    why: Cannot convert "1 can" to cups or grams - incompatible measurement types
    alternatives: Allow cross-conversion (nonsensical), omit count units (lose important shopping units)
    impact: Consolidation only works within same unit family
metrics:
  duration: 3 minutes 35 seconds
  completed: 2026-01-24
---

# Phase 04 Plan 01: UnitConverter Service Summary

**One-liner:** Lookup-table unit converter for cooking measurements with volume/weight/count family validation and precision rounding

## What Was Built

Implemented TDD-driven UnitConverter service enabling ingredient consolidation across different units during shopping list generation.

**Core capabilities:**
- Volume conversions: tsp ↔ tbsp ↔ cup ↔ fl oz ↔ ml ↔ l
- Weight conversions: oz ↔ lb ↔ g ↔ kg
- Count units: piece, can, bunch, clove (no cross-conversion)
- FindCommonUnit for automatic unit selection during consolidation
- CanConvert for compatibility checking before conversion attempts
- Case-insensitive, whitespace-tolerant normalization

**TDD process:**
1. **RED:** Wrote 28 failing tests covering all conversion paths and edge cases
2. **GREEN:** Implemented conversion table with base-unit approach and decimal rounding
3. **REFACTOR:** No changes needed - implementation clean from start

## Technical Decisions

### Lookup Table vs Library

**Chose:** Lookup table with ~50 lines
**Over:** UnitsNet library (2.5k+ stars, 100+ unit types)

UnitsNet provides engineering units (Pascals, Newtons, Coulombs) irrelevant to cooking. Cooking domain has 15 units across 3 families. Lookup table approach:
- 50 lines of code vs 1 MB+ NuGet package
- Instant table lookup vs library initialization overhead
- Domain-specific units only (no cognitive load filtering)

### Decimal Rounding for Precision

**Problem:** Division introduced floating-point errors
```csharp
// Without rounding:
1 tbsp → tsp = 3.0000000000000000000000000048 ❌
```

**Solution:** `Math.Round(result, 10)` after conversion

This preserves cooking precision (no one measures to 11 decimal places) while eliminating test failures from arithmetic artifacts.

### Base Unit Conversion Strategy

**Architecture:**
```
cup ← base (volume)
tbsp: 0.0625 (1/16 cup)
tsp: 0.0208... (1/48 cup)

Convert tbsp → tsp:
  (1 tbsp × 0.0625) / 0.0208... = 3 tsp
```

**Alternative considered:** Direct conversion matrix
```csharp
{ ("tbsp", "tsp"), 3 },
{ ("tsp", "tbsp"), 1/3 },
{ ("cup", "tsp"), 48 },
{ ("tsp", "cup"), 1/48 },
// ... 15 units × 14 conversions each = 210 entries
```

Lookup table approach scales linearly (N units = N entries), not quadratically (N² entries).

## Testing

**Coverage:** 28 tests, all passing

**Test categories:**
- Volume conversions (7 tests): tbsp→tsp, cup→tbsp, fl oz→cup, cup→ml, l→cup
- Weight conversions (3 tests): oz→lb, g→kg, lb→g
- Edge cases (10 tests): null, empty, whitespace, case, plurals, same unit, unknown unit
- Mixed families (2 tests): volume+weight throws exception
- FindCommonUnit (5 tests): same family, mixed families, empty, null, most common
- CanConvert (3 tests): same family true, different families false, unknown false

**Key test patterns:**
```csharp
// Precision test
Assert.Equal(236.588m, result, precision: 3); // ±0.001 tolerance

// Exception test
Assert.Throws<InvalidOperationException>(() =>
    converter.Convert(1, "cup", "oz")); // Volume → weight

// Normalization test
converter.Convert(1, " Cup ", " TBSP "); // Whitespace + case
```

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Decimal precision error in conversions**
- **Found during:** GREEN phase, test execution
- **Issue:** `1 tbsp → tsp` returned `3.0000000000000000000000000048` instead of `3`
- **Root cause:** Decimal division `0.0625 / 0.020833...` introduced floating-point artifact
- **Fix:** Added `Math.Round(result, 10)` to conversion method
- **Files modified:** `src/FamilyCoordinationApp/Services/UnitConverter.cs`
- **Commit:** a6ff1ca (included in feat commit)

## What Works

✅ Volume family conversions (tsp, tbsp, cup, fl oz, ml, l)
✅ Weight family conversions (oz, lb, g, kg)
✅ Count units (piece, can, bunch, clove) recognized but don't cross-convert
✅ FindCommonUnit returns most frequent unit from same family
✅ FindCommonUnit returns null for mixed families (volume + weight)
✅ CanConvert validates unit compatibility before conversion
✅ Null/empty units return quantity unchanged (graceful degradation)
✅ Case-insensitive matching ("Cup" = "cup" = "CUP")
✅ Plural handling ("cups" = "cup", "tablespoons" = "tbsp")
✅ Whitespace trimming (" cup " = "cup")
✅ Unknown units throw InvalidOperationException with clear message

## What Doesn't Work

None - all planned functionality implemented and tested.

**Future enhancements (out of scope):**
- Temperature conversions (°F ↔ °C) - not needed for shopping lists
- Metric cup (250ml) vs US cup (236.588ml) - locale support deferred
- Fuzzy unit matching ("tablsp" → "tbsp") - exact match required for now

## Next Phase Readiness

**Ready for 04-02 (ShoppingListService):**
- UnitConverter can be injected as service if needed
- Currently stateless (static table), can be singleton

**Ready for 04-03 (ShoppingListGenerator consolidation):**
- FindCommonUnit identifies compatible units
- Convert normalizes quantities to common unit
- CanConvert validates before attempting conversion

**Blockers:** None

**Concerns:** None

## How to Use

```csharp
var converter = new UnitConverter();

// Convert quantities
var tsp = converter.Convert(1, "tbsp", "tsp"); // 3
var cups = converter.Convert(8, "fl oz", "cup"); // 1
var grams = converter.Convert(1, "lb", "g"); // 453.592

// Find common unit for consolidation
var units = new List<string?> { "cup", "tbsp", "tsp" };
var common = converter.FindCommonUnit(units); // "cup" (most frequent)

// Check compatibility before converting
if (converter.CanConvert("cup", "oz"))
{
    // false - different families
}
```

## Files Changed

**Created:**
- `src/FamilyCoordinationApp/Services/UnitConverter.cs` (157 lines)
- `tests/FamilyCoordinationApp.Tests/Services/UnitConverterTests.cs` (406 lines)

**Modified:** None

## Commits

- `9c18fd2` - test(04-01): add failing test for UnitConverter
- `a6ff1ca` - feat(04-01): implement UnitConverter service

---
*Generated: 2026-01-24*
