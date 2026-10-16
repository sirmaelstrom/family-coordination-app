# Tenancy bypass inventory (fca-household-scope WP-02, quest ec788d69)

The discovery census that comes **before** the read switch (constraints M4, decisions D6). It classifies every place
in `src/FamilyCoordinationApp` where a household id meets a query, and every tenant read or write that can run with
no tenant set. WP-02 turns on the named `"Tenant"` global query filter; this file says, site by site, why each one
still works afterwards. WP-05 reruns it before removing the hand-written predicates.

**Classification** is exactly one of:
- `caller-scoped (no change)`: the Tenant filter agrees with the predicate already there, or no tenant set is read.
- `bypass: <gate>`: a legitimate cross-household read. It is `IgnoreQueryFilters(["Tenant"])` (plus `"SoftDelete"`
  where the site already ignored soft delete) with a `// TENANT-SCOPE-OK:` pragma naming the gate.
- `RunAs: <household source>`: system work with no caller. It runs inside `context.Tenant.RunAs(householdId)`.
- `AllowCrossTenantWrite: <reason>`: a write to another household's row. Inert until WP-03's write step.

**How the rows were found** (run from `src/FamilyCoordinationApp`, `Migrations/` excluded):
1. Every tenant comparison, both operand orders, **including caller-scoped ones** (163 hits before the switch):
   - `grep -rnE 'HouseholdId\w*\s*(==|!=)' . --include=*.cs --include=*.cshtml`
   - `grep -rnE '(==|!=)\s*[\w.?]*[Hh]ouseholdId' . --include=*.cs --include=*.cshtml`
2. A call-site pass over every call that passes a household argument **not** taken from the resolved caller
   (`user.`/`ctx.`/`caller.`): `grep -nE '[Hh]ouseholdId' Endpoints/*.cs` minus the caller-derived forms, then each
   service that calls another with a non-caller id (setup, approve/create, digest, the dev seed).
3. Every `TENANT-SCOPE-OK` pragma site (the fca#111 guard's 21).
4. Every tenant-set read or write reachable with no tenant: identity (the caller middleware's resolver, the whitelist
   handler, the dev bypass), the onboarding Razor Pages, the OAuth `OnCreatingTicket` event, the digest cron, the
   anonymous calendar feed, first-run setup, admin approve/create, and the dev seed. Found by reading every file
   that opens a context (`grep -rln 'IDbContextFactory<ApplicationDbContext>' .`) against the D3 marker map.

**Result.** No site outside the problem statement's known list (D6, D11) turned up, so E2 did not fire. Beyond the
list: `HouseholdMemberService` gains the pragma it never had, and `FeedbackService`'s author include is a bypass
(WP-02 step 5). `SetupService.GetUserByEmailAsync` has no caller, so it stays filtered and would fail loudly.

**Counts at the switch:** 200 rows: 23 `bypass:`, 23 `RunAs:`, 1 `AllowCrossTenantWrite:`, 153
`caller-scoped (no change)`. The census grep now has 165 hits: the 163 above, plus `GetConnectedRecipeAsync`'s
predicate (new, the 23rd bypass) and the filter's own doc comment in `ApplicationDbContext.cs`. The pre-switch
census (198 rows, 22 bypasses) is this file's first commit.

**WP-05 census rerun** (base `d33b3ca`, before any removal): the two step-1 greps give **168** hits, against 165 at
the switch. #122 (WP-03) added three: the write step's ownership query keeps a household term in `Owned` and
`OwnedAsync` (`Data/ApplicationDbContext.Writes.cs:262,267`; WP-05 keeps it, per the WP-03 ruling), and
`CrossTenantWriteException.Reassigned` compares two households in memory (`Tenancy/CrossTenantWriteException.cs:64`).
#122 also added the two `ExistsInAnyHousehold` bypasses (rows below). Everything else that moved since the switch
(#123 `user.`/`ctx.` → `caller.`, #124, #125 `WhereEmailMatches`, #126 `clock.UtcNow`, #129) is a rename or a line
shift of an existing row, with no new comparison and no new cross-household path, so E2 did not fire. 206 rows
(203 before the rerun): 26 `bypass:`, 23 `RunAs:`, 1 `AllowCrossTenantWrite:`, 156 `caller-scoped (no change)`. The
stamps WP-05 classifies are in the second table, at the end.

**WP-05 removal.** WP-05 removed every caller-scoped predicate on a filtered entity. It kept, on purpose: each `bypass:`
read's predicate on the other household it reads, the predicates in `SeedData.cs`, and the write step's ownership term
(`ApplicationDbContext.Writes.cs:266,271`, a deliberate duplicate of the filter). A Tenant bypass is checked for its
pragma and its row here (`TenantFilterArchitectureTests` fact 2), not for a predicate. Removed: 135 rows, the 130
`caller-scoped` predicates on `ITenantEntity` sets and the 5 predicates inside the digest's `RunAs` (each one the
`RunAs` household, so the filter states it). `grep -rn '\.HouseholdId ==' src --include=*.cs | grep -v /Migrations/`
went from 158 to 23: the 5 `bypass:` predicates (non-caller households), the 2 `Feedback` predicates (not a tenant
entity, D5), the 4 in-memory comparisons, the 11 in `SeedData.cs`, and the filter's own doc comment. It also removed
the 21 `removable` stamps (second table). 71 rows remain: 26 `bypass:`, 18 `RunAs:`, 1 `AllowCrossTenantWrite:`, 26
`caller-scoped (no change)`, the last now meaning: a predicate on a set the filter doesn't cover (`Feedback`,
`HouseholdConnection`, `Household`), an in-memory comparison, a call site, or text that isn't a query.

**Line numbers** point at the WP-05 tree (the rerun re-pointed every row, and the gate citations lane yd4 listed as
stale after WP-04); a `bypass:` row's line is the **first line of the statement** holding the
bypass. Each `bypass:` row also carries a **site id**, `` `Member#fingerprint` ``: the enclosing method and the first
8 hex digits of SHA-256 over the statement's comment- and string-stripped text with all whitespace removed. Guard
fact 2 (`TenantFilterArchitectureTests`) matches each Tenant bypass in `src` to exactly one row of its file with the
same site id, and each row to one bypass: a new or substituted bypass without a row, or a stale row, fails CI, even
when the file's count is unchanged. Lines, whitespace and comments are not in the id, so an unrelated edit that
shifts or re-flows a bypass doesn't fail. Editing the bypass statement, renaming its method or moving it to another
method or file does: review the gate, then re-point the row (the failure prints the new id beside the statement).
**Limit:** the fingerprint covers only the bypass statement. A move within its method that leaves that statement's
text unchanged (out of a gating `if`/`else`, say) keeps the id, and so do edits to a separate statement the bypass
widens. Behavior tests pin those, not fact 2, so a bypass carries its gate and base query in its own statement where
it can. `FeedbackService.GetFeedbackAsync` deliberately doesn't since quest 92034618: its gate is an `if`, so the
non-admin predicate is inline where the scope scan sees it, and `Integration/FeedbackTenancyTests` pins the gate.

| file:line | expression (trimmed) | classification | gate / household source |
|---|---|---|---|
| `Authorization/DevAuthBypassMiddleware.cs:68` `InvokeAsync#82e5e72c` | `Users.OrderBy(u => u.Id).FirstOrDefaultAsync()` | bypass: dev-only seed identity | gated by `IsDevelopment()` at Authorization/DevAuthBypassMiddleware.cs:42 (registration is env-gated too; `DevAuthStartupGuard` fail-closes outside Development) |
| `Authorization/WhitelistedEmailHandler.cs:55` `HandleRequirementAsync#1c3c03bc` | `Users.WhereEmailMatches(email).Select(u => new { u.IsWhitelisted }).SingleIdentityOrDefaultAsync()` | bypass: global whitelist check | the auth boundary: runs in `UseAuthorization`, before `CallerTenantMiddleware` sets a tenant; reads only `IsWhitelisted` |
| `Data/ApplicationDbContext.cs:112` | `/// <c>"Tenant"</c>: <c>e => BypassTenantFilter \|\| e.HouseholdId == CurrentHouseholdId</c>. It scopes reads,` | caller-scoped (no change) | not a query: the Tenant filter's own doc comment (`ApplyTenantFilter`) |
| `Data/ApplicationDbContext.Writes.cs:283` `ExistsInAnyHousehold#d13ce568` | `Set<TEntity>().IgnoreQueryFilters(["Tenant", "SoftDelete"]).Any(e => EF.Property<int>(e, "Id") == id)` | bypass: the write step's absent-vs-foreign check (WP-03 amendment 1) | returns a boolean only, consulted only after the tenant-scoped ownership query failed at Data/ApplicationDbContext.Writes.cs:53 (sync save): true refuses the write (`NotOwned`), false lets base `SaveChanges` raise `DbUpdateConcurrencyException` |
| `Data/ApplicationDbContext.Writes.cs:291` `ExistsInAnyHouseholdAsync#cae5a4e9` | `Set<TEntity>().IgnoreQueryFilters(["Tenant", "SoftDelete"]).AnyAsync(e => EF.Property<int>(e, "Id") == id, cancellationToken)` | bypass: the write step's absent-vs-foreign check (WP-03 amendment 1) | returns a boolean only, consulted only after the tenant-scoped ownership query failed at Data/ApplicationDbContext.Writes.cs:65 (async save): true refuses the write (`NotOwned`), false lets base `SaveChangesAsync` raise `DbUpdateConcurrencyException` |
| `Data/ApplicationDbContext.Writes.cs:266` | `.Any(e => EF.Property<int>(e, "Id") == id && EF.Property<int>(e, nameof(ITenantEntity.HouseholdId)) == householdId);` (`Owned`) | caller-scoped (no change) | the write step's surrogate-key ownership query (WP-03): the tenant's household, passed by `SaveChanges`. Not one of the hand-written predicates: WP-03 built it for the `EnforceFilter=false` case, which WP-05 removed. The WP-03 ruling says WP-05 keeps it, as a deliberate duplicate: it runs only when write bypass is off, which is exactly when the filter is on (`Writes.cs:123-124` vs `ApplicationDbContext.cs:53-55`), so it is never the sole check |
| `Data/ApplicationDbContext.Writes.cs:271` | `.AnyAsync(e => EF.Property<int>(e, "Id") == id && EF.Property<int>(e, nameof(ITenantEntity.HouseholdId)) == householdId, cancellationToken);` (`OwnedAsync`) | caller-scoped (no change) | as the row above, for `SaveChangesAsync` |
| `Data/SeedData.cs:19` | `context.Tenant.RunAs(household.Id)` from the `Users` lookup to the end of `SeedDevelopmentDataAsync` | RunAs: the dev household | the first `Households` row (not a tenant entity); covers the helpers' own contexts (shared scoped tenant) |
| `Data/SeedData.cs:21` | `var user = await context.Users.FirstOrDefaultAsync(u => u.HouseholdId == household.Id);` | RunAs: the dev household (`Households.FirstOrDefaultAsync()`) | inside `RunAs(household.Id)` at Data/SeedData.cs:19 |
| `Data/SeedData.cs:37` `SeedDevelopmentDataAsync#b59f1f18` | `Recipes.AnyAsync()` (dev seed guard over the whole database) | bypass: dev seed whole-database guard | Development-only startup seed (Program.cs `IsDevelopment()` block); returns a boolean only |
| `Data/SeedData.cs:163` | `var alreadySeeded = await context.Rooms.AnyAsync(r => r.HouseholdId == householdId)` | RunAs: the household being seeded | the tenant in force: the enclosing `RunAs(newHouseholdId)` (SetupService, HouseholdRequestService approve/create, SeedDevelopmentDataAsync) or the Caller at Endpoints/ChoresEndpoints.cs:893 |
| `Data/SeedData.cs:164` | `\|\| await context.Chores.AnyAsync(c => c.HouseholdId == householdId);` | RunAs: the household being seeded | the tenant in force: the enclosing `RunAs(newHouseholdId)` (SetupService, HouseholdRequestService approve/create, SeedDevelopmentDataAsync) or the Caller at Endpoints/ChoresEndpoints.cs:893 |
| `Data/SeedData.cs:171` | `.Where(u => u.HouseholdId == householdId)` | RunAs: the household being seeded | the tenant in force: the enclosing `RunAs(newHouseholdId)` (SetupService, HouseholdRequestService approve/create, SeedDevelopmentDataAsync) or the Caller at Endpoints/ChoresEndpoints.cs:893 |
| `Data/SeedData.cs:371` | `var memberCount = await context.Users.CountAsync(u => u.HouseholdId == householdId);` | RunAs: the household being seeded | inside `RunAs(household.Id)` at `Data/SeedData.cs:19` (the dev seed only) |
| `Data/SeedData.cs:375` | `.Where(cc => cc.HouseholdId == householdId)` | RunAs: the household being seeded | inside `RunAs(household.Id)` at `Data/SeedData.cs:19` (the dev seed only) |
| `Data/SeedData.cs:386` | `.Where(u => u.HouseholdId == householdId)` | RunAs: the household being seeded | inside `RunAs(household.Id)` at `Data/SeedData.cs:19` (the dev seed only) |
| `Data/SeedData.cs:402` | `.WhereEmailMatches(email).Where(u => u.HouseholdId == householdId)` | RunAs: the household being seeded | inside `RunAs(household.Id)` at `Data/SeedData.cs:19` (the dev seed only) |
| `Data/SeedData.cs:431` | `.Where(c => c.HouseholdId == householdId)` | RunAs: the household being seeded | inside `RunAs(household.Id)` at `Data/SeedData.cs:19` (the dev seed only) |
| `Data/SeedData.cs:440` | `.Where(cc => cc.HouseholdId == householdId)` | RunAs: the household being seeded | inside `RunAs(household.Id)` at `Data/SeedData.cs:19` (the dev seed only) |
| `Data/SeedData.cs:490` | `.CountAsync(c => c.HouseholdId == householdId);` | RunAs: the household being seeded | the tenant in force: the enclosing `RunAs` (SetupService, SeedDevelopmentDataAsync); soft-delete-only ignore (step 3) |
| `Endpoints/CalendarTokenEndpoints.cs:104` | `tenant.RunAs(calendarToken.HouseholdId)` around the four board loads | RunAs: the token's household | the bypass-resolved token's `HouseholdId`; the scope wraps the whole `Task.WhenAll` (concurrent loads share the scoped tenant) |
| `Endpoints/CalendarTokenEndpoints.cs:107` | `GetBoardAsync(calendarToken.HouseholdId, …)` ×4 | caller-scoped (no change) | runs inside the feed's `RunAs(calendarToken.HouseholdId)` (row above), so the Tenant filter scopes the board's queries to the token's household (WP-05 removed their own predicates) |
| `Endpoints/MeEndpoints.cs:44` | `Users.WhereEmailMatches(email)` (/api/me) | caller-scoped (no change) | marked group: the middleware set Caller from this same email, so the row is in the Caller household; fca#111 pragma unchanged |
| `Endpoints/PresenceEndpoints.cs:57` | `Users.WhereEmailMatches(email)` (presence) | caller-scoped (no change) | marked group: the middleware set Caller from this same email, so the row is in the Caller household; fca#111 pragma unchanged |
| `Endpoints/RecipesEndpoints.cs:408` | `connected detail: `GetConnectedRecipeAsync(chId, recipeId)` (was `GetRecipeAsync(chId, …)`)` | caller-scoped (no change) | call site of the bypassing variant (the row below); gated by `AreHouseholdsConnectedAsync` at Endpoints/RecipesEndpoints.cs:402 |
| `Endpoints/RecipesEndpoints.cs:429` | `copy validation: `GetConnectedRecipeAsync(chId, recipeId)` (was `GetRecipeAsync(chId, …)`)` | caller-scoped (no change) | call site of the bypassing variant; gated by `AreHouseholdsConnectedAsync` at Endpoints/RecipesEndpoints.cs:423 |
| `Endpoints/RecipesEndpoints.cs:432` | `CopyRecipeFromConnectedHouseholdAsync(chId, recipeId, caller.HouseholdId, …)` | caller-scoped (no change) | foreign `chId` flows to the copy-source bypasses in RecipeService (rows there); the write targets `caller.HouseholdId` |
| `Endpoints/UploadsEndpoints.cs:100` | `route value `householdId` → `CanReadHouseholdUploadsAsync`` | caller-scoped (no change) | the route value reaches a query only through the rule-2 bypass (row at the `ImagePath == storedPath` line) |
| `Endpoints/UploadsEndpoints.cs:148` | `if (caller.HouseholdId == householdId) return true;` | caller-scoped (no change) | in-memory rule 1: the caller's own household (no query) |
| `Endpoints/UploadsEndpoints.cs:167` `CanReadHouseholdUploadsAsync#2ec31887` | `.AnyAsync(r => r.HouseholdId == householdId && r.ImagePath == storedPath, ct);` | bypass: connected-household recipe image | gated by `AreHouseholdsConnectedAsync` at Endpoints/UploadsEndpoints.cs:161 plus the owning household's recipe referencing this exact file |
| `Endpoints/UserContextResolver.cs:37` `ResolveUserAsync#2cb03759` | `Users.WhereEmailMatches(email).SingleIdentityOrDefaultAsync()` | bypass: identity resolution | the query IS the scope source: the caller's own authenticated email claim; runs in `CallerTenantMiddleware` BEFORE the tenant exists (its one caller since WP-04) |
| `Pages/Account/AccessDenied.cshtml.cs:43` `OnGetAsync#91eeb5aa` | `Users.WhereEmailMatches(email).FirstOrDefaultAsync()` | bypass: onboarding identity lookup | the query IS the scope source: the caller's own authenticated email claim; unmarked Razor Page (D3) |
| `Pages/Household/Pending.cshtml.cs:46` `OnGetAsync#91eeb5aa` | `Users.WhereEmailMatches(email).FirstOrDefaultAsync()` | bypass: onboarding identity lookup | the query IS the scope source: the caller's own authenticated email claim; unmarked Razor Page (D3) |
| `Pages/Household/Request.cshtml.cs:55` `OnGetAsync#93573098` | `Users.WhereEmailMatches(UserEmail).FirstOrDefaultAsync()` (GET) | bypass: onboarding identity lookup | the query IS the scope source: the caller's own authenticated email claim; unmarked Razor Page (D3) |
| `Pages/Household/Request.cshtml.cs:99` `OnPostAsync#93573098` | `Users.WhereEmailMatches(UserEmail).FirstOrDefaultAsync()` (POST) | bypass: onboarding identity lookup | the query IS the scope source: the caller's own authenticated email claim; unmarked Razor Page (D3) |
| `Services/Calendar/HouseholdCalendarTokenService.cs:34` | `SELECT pg_advisory_xact_lock({householdId})` (raw SQL) | caller-scoped (no change) | not a query over a tenant set; `householdId` from the marked management group; fca#111 pragma unchanged |
| `Services/Calendar/HouseholdCalendarTokenService.cs:109` `ResolveActiveAsync#38e289b5` | `HouseholdCalendarTokens.Where(t => t.TokenHash == hash && t.Feed == feed && t.RevokedAt == null && (t.UserId == null \|\| (t.User.HouseholdId == t.HouseholdId && t.User.IsWhitelisted)))` | bypass: calendar capability lookup | the token hash is the capability (a 256-bit secret, globally unique); called from the anonymous feed, unmarked. The bypass is query-wide, so the `User` navigation is read unfiltered as well; its predicate pins a member token's user to the token's own household and requires them enabled (quest 8dba87f4) |
| `Services/Digest/ChoreRecapService.cs:70` | `.Where(h => h.Id == householdId)` | caller-scoped (no change) | `Households` is not a tenant entity; marked chores group (`caller.HouseholdId`) |
| `Services/Digest/DigestService.cs:55` `RunDueAsync#7c097521` | `ChoreDigestSettings.Where(s => s.Enabled && s.WebhookUrlProtected != null)` (all-households sweep) | bypass: digest all-households sweep | gated by `ValidateTriggerToken` (the cron's shared-secret header) at Endpoints/ChoresEndpoints.cs:917; `HouseholdId` comes only from these rows |
| `Services/Digest/DigestService.cs:70` | `tenant.RunAs(candidate.HouseholdId)` around claim, build, decrypt, send and compensate | RunAs: the swept household | each candidate row's `HouseholdId` (D11) |
| `Services/Digest/DigestService.cs:160` | `.Where(hh => hh.Id == householdId)` | caller-scoped (no change) | `Households` is not a tenant entity; `h` comes from the sweep rows |
| `Services/FeedbackService.cs:87` `GetFeedbackAsync#8e37d1a3` | `return await context.Feedbacks.IgnoreQueryFilters(["Tenant"]).AsNoTracking().Include(f => f.User)...` (the site-admin branch) | bypass: feedback authors across households | gated by `if (isSiteAdmin)` around this statement (`siteAdmin.IsSiteAdmin` at Endpoints/SettingsAdminEndpoints.cs:325, a data scope, not an access gate): only the admin branch ignores the filter, so only the admin loads other households' authors. Split from the old dual-mode statement (quest 92034618) so the non-admin predicate is inline; keeps its fca#111 pragma. `Feedback` itself is not a tenant entity; the `User` include is |
| `Services/FeedbackService.cs:93` | `context.Feedbacks.AsNoTracking().Include(f => f.User).Where(f => f.HouseholdId == householdId)` (the non-admin branch) | caller-scoped (no change) | `Feedback` is not a tenant entity (dual-mode, R-C1; excluded from the filter by D5), so the non-admin predicate stays after WP-05, now inline where `TenantScopeArchitectureTests` sees it; a non-admin's `User` include stays under the Tenant filter |
| `Services/FeedbackService.cs:120` | `Feedbacks.Where(f => f.Id == id)` (MutateAsync, the site-admin branch) | caller-scoped (no change) | `Feedback` is not a tenant entity and nothing is included, so no Tenant filter applies; fca#111 pragma on the admin branch |
| `Services/FeedbackService.cs:125` | `Feedbacks.Where(f => f.Id == id && f.HouseholdId == householdId)` (MutateAsync, the non-admin branch) | caller-scoped (no change) | `Feedback` is not a tenant entity (dual-mode, R-C1; excluded from the filter by D5), so the non-admin predicate stays after WP-05, now inline |
| `Services/HouseholdConnectionService.cs:125` `ValidateInviteCodeAsync#1c1f6b7d` | `HouseholdInvites.Include(i => i.Household).FirstOrDefaultAsync(i => i.InviteCode == normalizedCode)` (validate) | bypass: invite validate | the invite code is the capability (exact match, then `IsUsed`/`ExpiresAt`/self checks below it), rate-limited by `IsRateLimited` at Services/HouseholdConnectionService.cs:116 |
| `Services/HouseholdConnectionService.cs:149` | `if (invite.HouseholdId == acceptingHouseholdId)` | caller-scoped (no change) | in-memory self-connection guard on a bypass-loaded invite (no query) |
| `Services/HouseholdConnectionService.cs:181` `AcceptInviteAsync#1c1f6b7d` | `HouseholdInvites.Include(i => i.Household).FirstOrDefaultAsync(i => i.InviteCode == normalizedCode)` (accept) | bypass: invite accept | the invite code is the capability (exact match, then `IsUsed`/`ExpiresAt`/self/already-connected checks below it) |
| `Services/HouseholdConnectionService.cs:195` | `if (invite.HouseholdId == acceptingHouseholdId)` | caller-scoped (no change) | in-memory self-connection guard on a bypass-loaded invite (no query) |
| `Services/HouseholdConnectionService.cs:223` | `context.Tenant.AllowCrossTenantWrite(...)` around the accept's `SaveChangesAsync` | AllowCrossTenantWrite: invite accept writes `UsedByHouseholdId` on the inviting household's row | the only `AllowCrossTenantWrite` site (D9); inert until WP-03 |
| `Services/HouseholdConnectionService.cs:252` | `.Where(c => c.HouseholdId1 == householdId \|\| c.HouseholdId2 == householdId)` | caller-scoped (no change) | `HouseholdConnection` is not a tenant entity (a pair, D5); ids are caller-derived |
| `Services/HouseholdConnectionService.cs:259` | `var otherHousehold = c.HouseholdId1 == householdId ? c.Household2 : c.Household1;` | caller-scoped (no change) | `HouseholdConnection` is not a tenant entity (a pair, D5); ids are caller-derived |
| `Services/HouseholdConnectionService.cs:278` | `.FirstOrDefaultAsync(c => c.HouseholdId1 == minId && c.HouseholdId2 == maxId, cancellationToken);` | caller-scoped (no change) | `HouseholdConnection` is not a tenant entity (a pair, D5); ids are caller-derived |
| `Services/HouseholdConnectionService.cs:307` | `.AnyAsync(c => c.HouseholdId1 == minId && c.HouseholdId2 == maxId, cancellationToken);` | caller-scoped (no change) | `HouseholdConnection` is not a tenant entity (a pair, D5); ids are caller-derived |
| `Services/HouseholdMemberService.cs:36` `AddMemberAsync#72cf3b30` | `.WhereEmailMatches(normalized).AnyAsync(u => u.HouseholdId != householdId, cancellationToken);` | bypass: cross-household email uniqueness (returns only a boolean) | gated by the marked members group (Endpoints/SettingsEndpoints.cs:46); the read returns only the `OtherHousehold` outcome, never data |
| `Services/HouseholdRequestService.cs:34` `GetDataAsync#f8038eba` | `Households.Include(h => h.Users)` (site-admin household list) | bypass: site-admin household list | gated by the `SiteAdmin` policy at Endpoints/SettingsAdminEndpoints.cs:45 (GetHouseholdRequests); needs every household's member count |
| `Services/HouseholdRequestService.cs:72` `ApproveAsync#fd801409` | `Users.WhereEmailMatches(request.Email).AnyAsync()` | bypass: cross-tenant email uniqueness (returns only a boolean) | gated by the `SiteAdmin` policy at Endpoints/SettingsAdminEndpoints.cs:45 (ApproveRequest); refuses a case-duplicate of a legacy mixed-case user (quest 3642dbb8); returns only a boolean |
| `Services/HouseholdRequestService.cs:91` | `context.Tenant.RunAs(household.Id)` (approve) from the new id through the post-commit seed | RunAs: the household being approved | `household.Id` of the row this transaction just inserted (gate: the `SiteAdmin` policy at Endpoints/SettingsAdminEndpoints.cs:45) |
| `Services/HouseholdRequestService.cs:198` `CreateHouseholdAsync#95c70f93` | `Users.WhereEmailMatches(email).AnyAsync()` | bypass: cross-tenant email uniqueness (returns only a boolean) | gated by the `SiteAdmin` policy at Endpoints/SettingsAdminEndpoints.cs:53 (CreateHousehold); returns only a boolean |
| `Services/HouseholdRequestService.cs:216` | `context.Tenant.RunAs(household.Id)` (admin create) from the new id through the post-commit seed | RunAs: the household being created | `household.Id` of the row this transaction just inserted (gate: the `SiteAdmin` policy at Endpoints/SettingsAdminEndpoints.cs:53) |
| `Services/LoginProfileService.cs:44` `RefreshAsync#e5a709d4` | `Users.WhereEmailMatches(email).SingleIdentityOrDefaultAsync()` | bypass: login-time identity lookup | the OAuth `OnCreatingTicket` event (Program.cs), unmarked; the principal's email was just authenticated by Google |
| `Services/LoginProfileService.cs:53` | `context.Tenant.RunAs(user.HouseholdId)` from the first mutation through `SaveChangesAsync` | RunAs: the signed-in user's household | the bypass-loaded user's own `HouseholdId` (D11); inert until WP-03's write step |
| `Services/PresenceService.cs:106` | `_presence.Values.Where(p => p.HouseholdId == householdId && p.Status != PresenceStatus.Offline);` | caller-scoped (no change) | in-memory roster, not EF; `householdId` from the resolved caller |
| `Services/RecipeService.cs:255` `GetRecipesFromConnectedHouseholdAsync#d7486f63` | `.Where(r => r.HouseholdId == connectedHouseholdId)` | bypass: connected-household recipe list | gated by `AreHouseholdsConnectedAsync` at Endpoints/RecipesEndpoints.cs:382 (ListConnectedRecipes, the only caller) |
| `Services/RecipeService.cs:282` `GetConnectedRecipeAsync#c858e81a` | `.Where(r => r.HouseholdId == connectedHouseholdId && r.RecipeId == recipeId)` | bypass: connected-household recipe detail (`GetConnectedRecipeAsync`, new on `IRecipeService`) | gated by `AreHouseholdsConnectedAsync` at Endpoints/RecipesEndpoints.cs:402 and Endpoints/RecipesEndpoints.cs:423 (its only callers); the ordinary `GetRecipeAsync` stays filtered. It loads no author (`CreatedBy`): both callers discard it |
| `Services/RecipeService.cs:338` `GetSourceImagePathAsync#e106ceaf` | `.Where(r => r.HouseholdId == sourceHouseholdId && r.RecipeId == sourceRecipeId)` | bypass: connected-household copy source | gated by `AreHouseholdsConnectedAsync` at Endpoints/RecipesEndpoints.cs:423 (CopyConnectedRecipe, the only caller) |
| `Services/RecipeService.cs:357` `CopyRecipeRowAsync#7940aa02` | `.Where(r => r.HouseholdId == sourceHouseholdId && r.RecipeId == sourceRecipeId)` | bypass: connected-household copy source | gated by `AreHouseholdsConnectedAsync` at Endpoints/RecipesEndpoints.cs:423 (CopyConnectedRecipe, the only caller) |
| `Services/SetupService.cs:63` `CreateHouseholdAsync#7bdf7dcd` | `Users.WhereEmailMatches(userEmail).FirstOrDefaultAsync()` | bypass: first-run setup identity check | gated by `IsSetupCompleteAsync` at Pages/Setup/FirstRunSetup.cshtml.cs:52 (setup refuses once any household exists); returns only whether the email exists |
| `Services/SetupService.cs:81` | `context.Tenant.RunAs(household.Id)` from the new household's id through both seeds and the commit | RunAs: the household being created | `household.Id` of the row this method just inserted (inside the command's transaction, quest b942b943); covers the Users save, `AddDefaultCategories` and `SeedChoresAndRoomsAsync`, all on the setup context |
| `Services/SetupService.cs:118` | `Users.Include(u => u.Household).WhereEmailMatches(email).SingleIdentityOrDefaultAsync()` (`GetUserByEmailAsync`) | caller-scoped (no change) | no caller in `src` or `tests`: left filtered so a future unmarked caller fails loudly (D12); WP-05 rewrote its stale "pre-household" pragma to say so |
| `Tenancy/CrossTenantWriteException.cs:64` | `fromHouseholdId == toHouseholdId` (`Reassigned`) | caller-scoped (no change) | in-memory comparison choosing the refusal's message (no query) |

## Stamps (WP-05)

Every explicit `HouseholdId = …` on a new `ITenantEntity` row, found with
`grep -rnE '\bHouseholdId\s*=\s*[^=]' src --include=*.cs` (minus `Migrations/`, `SeedData.cs`, which WP-05 keeps,
and three hits that are not stamps: the in-memory presence roster at `Services/PresenceService.cs:36,49` and the
`TenantContext.HouseholdId` getter). WP-03's write step stamps an Added row whose `HouseholdId` is unset, at save
time, so an explicit stamp is redundant **unless something reads the value before `SaveChanges`**:
- `removable`: nothing reads the row's `HouseholdId` before its save, and no tracked row can share its key.
- `needed-pre-save`: code reads it first (id generation, another row's key, a log line), the row is never saved, or EF
  must match its full key at `Add` against a tracked row of the same key.
- `excluded`: not an `ITenantEntity` (D5); its hand-written stamp stays.

WP-05 removed the 21 `removable` stamps; the 12 `needed-pre-save` and the `excluded` one stay. Lines are the `d33b3ca`
tree, where the classification was made.

| file:line | row | classification | why |
|---|---|---|---|
| `Endpoints/MealPlanEndpoints.cs:236` | `Recipe` (quick create) | needed-pre-save | classified when `RecipeService.CreateRecipeAsync` read `recipe.HouseholdId` for the next id. **Re-read after WP-05's removals: no pre-save reader remains.** The id helper ignores its household argument, a quick-create recipe has no ingredients to copy it into, and the log line runs after the save. Kept pending lane ag3's Amendment 1 ask |
| `Endpoints/RecipesEndpoints.cs:493` | `Recipe` (`MapToRecipe`, create and update) | needed-pre-save | update: `RecipeService.UpdateRecipeAsync` copies `recipe.HouseholdId` into the replacement ingredients' keys, which must equal the Deleted rows' keys (row `RecipeService.cs:127`); create: `CreateRecipeAsync` copies it into each ingredient's key before `Add` (row `RecipeService.cs:66`). The id-helper reader is gone (it ignores the argument) |
| `Endpoints/SettingsEndpoints.cs:75` | `Category` | needed-pre-save | classified when `CategoryService.CreateCategoryAsync`'s id helper and sort-order max read `category.HouseholdId`. **Re-read after WP-05's removals: no pre-save reader remains.** The helper ignores the argument, the sort-order max lost its predicate, and the log line runs after the save. Kept pending lane ag3's Amendment 1 ask |
| `Endpoints/ShoppingListEndpoints.cs:411` | `ShoppingListItem` (manual add) | needed-pre-save | classified when `ShoppingListService.AddManualItemAsync`'s max-item-id query compared `item.HouseholdId`. **Re-read after WP-05's removals: no pre-save reader remains.** That predicate is gone, and the log line runs after the save. Kept pending lane ag3's Amendment 1 ask |
| `Services/Calendar/HouseholdCalendarTokenService.cs:46` | `HouseholdCalendarToken` | removable | added and saved; only the token and time are returned |
| `Services/ChoreRoomMembership.cs:85` | `ChoreRoom` | removable | added; the reconcile removes other keys only, so no tracked row shares the key |
| `Services/ChoreService.cs:70` | `Chore` (create) | needed-pre-save | classified when `AppendEvent` and `AppendParticipation` copied `chore.HouseholdId` into the event rows. **Re-read after WP-05's removals: those copies are gone (`ChoreService.cs:604,622,635` rows), and no pre-save reader remains** (the membership reconcile and member checks take the `householdId` parameter). Kept pending lane ag3's Amendment 1 ask |
| `Services/ChoreService.cs:376` | `ChoreCompletion` | removable | added in a loop, nothing reads it |
| `Services/ChoreService.cs:604` | `ChoreEvent` (`AppendEvent`) | removable | nothing reads the event's own value; the chore's is still set (the row above, or a loaded chore) |
| `Services/ChoreService.cs:622` | `ChoreParticipationEvent` (`AppendParticipation`) | removable | as the row above |
| `Services/ChoreService.cs:635` | `ChoreSnoozeEvent` (`AppendSnoozeEvent`) | removable | as the row above |
| `Services/ChoreSubtaskService.cs:55` | `ChoreSubtask` | removable | added and saved; `ToDto` reads it after the save |
| `Services/DigestSettingsService.cs:78` | `HouseholdChoreDigestSettings` | removable | added; the view built after the save is the only reader |
| `Services/DraftService.cs:69` | `RecipeDraft` | removable | added and saved, nothing reads it |
| `Services/FeedbackService.cs:40` | `Feedback` | excluded | `Feedback` is not a tenant entity (D5) |
| `Services/HouseholdConnectionService.cs:43` | `HouseholdInvite` | needed-pre-save | nothing in the service reads it, but the protected V6 test `RecipesEndpointTests.Connected_ListDetailCopy_HappyPath` (M1: never edited) generates its invite by calling this service out of request, where the test host is Unfiltered and the write step stamps nothing; reclassified at WP-05 per the WP's failure criteria. The protected helper is `RecipesEndpointTests.cs:478-488` (`ConnectHouseholdsAsync`). Side effect in that harness (`OutOfRequest=Unfiltered`): the `InvalidateInviteAsync` call just before the add lost its household predicate, so there it marks every household's active invites used. Production runs `OutOfRequest=Throw`, and the only caller (`SettingsConnectionsEndpoints.cs:68`) is in a `RequireTenant()` group, so the filter scopes it to the caller's household |
| `Services/HouseholdMemberService.cs:76` | `User` (add member) | removable | added and saved; the log line uses the parameter |
| `Services/HouseholdRequestService.cs:95` | `User` (approve) | removable | inside `RunAs(household.Id)`, a household saved earlier in the transaction; nothing reads it |
| `Services/HouseholdRequestService.cs:224` | `User` (admin create) | removable | as the row above |
| `Services/MealPlanService.cs:54` | `MealPlan` | removable | added and saved; the log line uses the parameter |
| `Services/MealPlanService.cs:133` | `MealPlanEntry` | removable | added; the timestamp update reads the parameter |
| `Services/RecipeImportService.cs:187` | `Recipe` (import) | needed-pre-save | never saved here: `ImportFromUrlAsync` returns the unsaved entity to the preview and create flows |
| `Services/RecipeImportService.cs:215` | `RecipeIngredient` (import) | needed-pre-save | as the row above |
| `Services/RecipeImportService.cs:230` | `RecipeIngredient` (import) | needed-pre-save | as the row above |
| `Services/RecipeService.cs:66` | `RecipeIngredient` (create) | needed-pre-save | the WP's named example. Its id-generation reason is gone (`GetNextRecipeIdInternalAsync` ignores the household argument since WP-05). What remains: it copies the parent's stamped `recipe.HouseholdId` into each ingredient's key before `Add`, which keeps the child keys consistent only while the parent stamp (`RecipesEndpoints.cs:493`) stays. Part of lane ag3's Amendment 1 ask |
| `Services/RecipeService.cs:127` | `RecipeIngredient` (update) | needed-pre-save | the replacements reuse the removed ingredients' keys (`IngredientId` restarts at 1): EF matches the full key at `Add` against the Deleted rows |
| `Services/RecipeService.cs:238` | `UserFavorite` | removable | added only when no row with the key exists; nothing reads it |
| `Services/RecipeService.cs:378` | `Recipe` (connected copy) | removable | added with fresh keys (`GetNextRecipeIdInternalAsync`); `targetHouseholdId` is the caller's household, the tenant |
| `Services/RecipeService.cs:402` | `RecipeIngredient` (connected copy) | removable | added through the copy's navigation, keys fresh |
| `Services/RoomService.cs:50` | `Room` | removable | added and saved; the log line uses the parameter |
| `Services/SetupService.cs:83` | `User` (first run) | removable | inside `RunAs(household.Id)`, a household saved before it; the log line reads `user.Id` after the save |
| `Services/ShoppingListGenerator.cs:110` | `ShoppingListItem` (generate) | needed-pre-save | classified when `AddManualItemAsync`'s max-item-id query compared `item.HouseholdId`. **Re-read after WP-05's removals: no pre-save reader remains** (that predicate is gone; the log line runs after the save). Kept pending lane ag3's Amendment 1 ask |
| `Services/ShoppingListGenerator.cs:209` | `ShoppingListItem` (regenerate) | removable | added with ids above the list's maximum, so no removed row shares the key |
| `Services/ShoppingListService.cs:28` | `ShoppingList` | removable | added and saved; the log line uses the parameter |
