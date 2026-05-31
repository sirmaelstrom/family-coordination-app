# Review Lens: gemini
**Workshop:** Chores v1.1 — Household Equity View + Weekly Discord Digest
**Surface:** spec
**Round:** 1
**Duration:** 97967ms
**Status:** success

---

The following review is based on a deep-dive exploration of the `family-coordination-app` codebase and a surgical audit of the Chores v1.1 specification artifacts.

### Per-Package Findings

**WP-05: DigestService.RunDueAsync (orchestration)**
- **Finding** (Severity: Minor): Missing explicit dependency on WP-02 in the metadata.
  - **Location:** `work-packages/wp-05-digest-run-service.md`, "Depends on" section.
  - **Recommendation:** Add `WP-02` to the "Depends on" list, as the implementation requires `ChoreEquityCalculator`.

**WP-06: Endpoints + Program.cs wiring**
- **Finding** (Severity: Minor): Potential ambiguity in `Program.cs` regarding `ChoreEquityCalculator` registration.
  - **Location:** `work-packages/wp-06-endpoints-wiring.md`, "Implementation notes" for Program.cs.
  - **Recommendation:** Ensure the spec explicitly mentions registering `ChoreEquityCalculator` as a **singleton** (matching the `ChoreStatusCalculator` pattern already in `Program.cs:106`).

**WP-07: Dev seed enrichment**
- **Finding** (Severity: Minor): The existing `SeedDevelopmentDataAsync` early-return guard will block new seeds on existing dev environments.
  - **Location:** `src/FamilyCoordinationApp/Data/SeedData.cs`, line 13.
  - **Recommendation:** The spec already identifies this "recipe-guard footgun" in WP-07 implementation notes. Ensure the agent prioritizes the restructuring of this method over simple appending.

### Cross-Cutting Findings

- **Data Protection Consistency:** The spec (A2/E7) correctly identifies that `IDataProtectionProvider` is already registered and used for auth cookies. However, it should be noted that `PersistKeysToFileSystem` uses a user-profile directory (`~/.aspnet/DataProtection-Keys`). Agents must ensure the named protector `CreateProtector("ChoreDigestWebhook")` is used consistently across WP-03 and WP-05.
- **DST Safety in Scheduling:** The logic for `sendWindowStartUtc` in WP-05 (using `tz.IsInvalidTime`) is an excellent catch for DST spring-forward gaps. This must be strictly followed to prevent `ArgumentException` during the selection loop.
- **Endpoint Status Codes:** The WP-06 requirement to include a JSON body in 401/503 responses is critical. The global `app.UseStatusCodePagesWithReExecute("/not-found")` in `Program.cs:222` will rewrite empty-body error responses to the Blazor 404 page, which would break cron-job diagnostics.

### Targeted Analysis Results

**A. Self-Containment Audit:**
- **Trace:** WP-05 (Orchestration) depends on WP-01 (Entity), WP-03 (SettingsService), and WP-04 (Builder/Sender).
- **Result:** With the addition of WP-02 (Calculator) to its dependencies, it is fully self-contained. The types and interfaces (e.g., `ChoreEquityResult`, `IDigestSender`, `IDigestSettingsService`) are clearly defined in the upstream WPs.

**B. Interface Contract Check:**
- **Trace:** Data flow from `ChoreCompletion` (Entity) → `ChoreEquityCalculator` (WP-02) → `ChoreEquityDto` (WP-06) → `EquityBoard.svelte` (WP-09).
- **Result:** Field names (`EffortPointsSnapshot`, `sharePct`, `fallingBehindCount`) are consistent across the boundary. The deliberate inclusion of `fallingBehindCount` and `upForGrabsCount` in the `ChoreEquityDto` (but not the calculator) is a clean way to keep the Equity lens independent of the frozen `board.json`.

**C. Blast Radius Scan:**
- **Trace:** Every package was checked for file ownership and secondary effects.
- **Result:** The consolidation of `Program.cs` changes into WP-06 successfully prevents Wave 5 collisions. The use of `ApplyConfigurationsFromAssembly` in `ApplicationDbContext.cs` ensures that WP-01's new entity configuration is picked up without manual registration in the context file.

**D. Wave Ordering Challenge:**
- **Result:** The wave plan is optimal. It builds the database and internal services (Waves 1-4) before exposing the HTTP surface (Wave 5) and finally the UI (Waves 7-9). Wave 6 (Integration Tests) is perfectly placed to verify the full backend stack before the frontend is wired.

### Overall Assessment

**Verdict:** Ready for dispatch (with minor amendments to WP-05 metadata)

The specification is exceptionally well-researched and grounded. It respects the "Phase 10 v1.0" substrate perfectly, particularly the frozen `board.json` contract and the Svelte 5 rune patterns. The decision to use external cron hitting an authenticated endpoint (E8) honors the "no BackgroundService" spirit while delivering the required functionality. The spec is diagnostic, prescriptive where necessary, and maintains high security standards (webhook encryption, mention suppression).

---
*Topic updated: Final Review Summary published.*

The specification for **Chores v1.1 — Household Equity View + Weekly Discord Digest** has been reviewed against the `family-coordination-app` codebase.

### Per-Package Findings

**WP-05: DigestService.RunDueAsync (orchestration)**
- **Finding** (Severity: Minor): Missing explicit dependency on WP-02 in the metadata.
  - **Location:** `work-packages/wp-05-digest-run-service.md`, "Depends on" section.
  - **Recommendation:** Add `WP-02` to the "Depends on" list, as the implementation requires `ChoreEquityCalculator`.

**WP-06: Endpoints + Program.cs wiring**
- **Finding** (Severity: Minor): Registration of `ChoreEquityCalculator` as a singleton should be explicitly mentioned to match existing patterns.
  - **Location:** `work-packages/wp-06-endpoints-wiring.md`, "Implementation notes" for Program.cs.
  - **Recommendation:** Ensure the registration mirrors the `ChoreStatusCalculator` pattern already in `Program.cs:106`.

**WP-07: Dev seed enrichment**
- **Finding** (Severity: Minor): The existing `SeedDevelopmentDataAsync` early-return guard (`if (await context.Recipes.AnyAsync()) return;`) will block new seeds on existing dev environments.
  - **Location:** `src/FamilyCoordinationApp/Data/SeedData.cs`, line 13.
  - **Recommendation:** The spec identifies this "recipe-guard footgun" in WP-07 implementation notes. The agent must prioritize restructuring this method to ensure the chore seed is independent of the recipe check.

### Cross-Cutting Findings

- **Endpoint Status Codes:** The WP-06 requirement to include a JSON body in 401/503 responses is critical. The global `app.UseStatusCodePagesWithReExecute("/not-found")` in `Program.cs:222` will rewrite empty-body error responses to the Blazor 404 page, which would break cron-job diagnostics.
- **DST Safety:** The logic for `sendWindowStartUtc` in WP-05 (using `tz.IsInvalidTime`) is an excellent catch for DST spring-forward gaps, preventing potential `ArgumentException` failures during the selection loop.

### Targeted Analysis Results

**A. Self-Containment Audit:**
WP-05 (Orchestration) is logically dependent on the internal services defined in WP-01, WP-02, WP-03, and WP-04. With the addition of WP-02 to its dependency list, it is fully implementable.

**B. Interface Contract Check:**
The data flow for `ChoreEquityDto` from WP-02 to WP-09 is consistent. Field names like `EffortPointsSnapshot` and `sharePct` are correctly traced. The inclusion of `fallingBehindCount` in the DTO (but not computed by the calculator) successfully preserves the frozen `board.json` contract.

**C. Blast Radius Scan:**
The specification successfully identifies the necessary files. The consolidation of `Program.cs` changes into WP-06 is a key mitigation against wave-level merge conflicts. `ApplyConfigurationsFromAssembly` in `ApplicationDbContext.cs` ensures WP-01's configuration is auto-discovered.

**D. Wave Ordering Challenge:**
The wave plan is optimal, sequentially building the database, internal services, and endpoint surface before proceeding to integration testing (Wave 6) and final UI implementation (Waves 7-9).

### Overall Assessment

**Verdict:** Ready for dispatch (after adding the WP-02 dependency to WP-05)

The specification is exceptionally well-grounded. It respects the Phase 10 v1.0 architecture (Svelte 5 runes, frozen board DTO, and timezone-aware server math). The secret handling (encrypted webhooks) and non-punitive framing (no targeted nudges) are clearly enforced through constraints.

---

