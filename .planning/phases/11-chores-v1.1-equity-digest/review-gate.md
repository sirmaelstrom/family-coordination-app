# Spec Review Gate — Chores v1.1 — Household Equity View + Weekly Discord Digest

Slug: `chores-v1.1-equity-digest` · 2026-05-31T08:35:52.7430585Z
Spec dir: `D:/Development/data/outputs/workshops/chores-v1.1-equity-digest`
Review level: **full** — 11 WPs, new domain (Discord integration, digest engine, secret encryption), real integration seams (cron→endpoint→builder→sender), and the one deliberate reversal of a v1.0 must-not (MN1).

## Summary

**Problem:** v1.0 chore board shipped dark with equity substrate (EffortPoints + ChoreCompletion log) and no consumer. v1.1 turns it into a **first-class equity/household-load view** plus a **weekly Discord digest** (equity's delivery surface), plus go-live table stakes (edit-chore dialog, backfill). Built dark, shipped as one drop, flag flipped at the end. Hard constraint: the digest stays a **collective channel broadcast — never an @-mention nag** (v1.0 escalate-to-visibility framing).

**Key decisions:**
- E1: Equity is a SEPARATE endpoint + DTO + fixture (board contract untouched, M9).
- E2: Pure TZ-aware ChoreEquityCalculator mirroring ChoreStatusCalculator; effort-weighted from EffortPointsSnapshot.
- E3: Equity windows = week + all-time; last-week trend deferred.
- E4: Equity is a 5th lens in the ViewSwitcher (first-class, not a panel in Mine).
- E5: Neutral proportional shares + an equal-share reference; NO thresholds/ranking/'behind' labels.
- E6: New HouseholdChoreDigestSettings entity (PK HouseholdId), NOT a column on Household.
- E7: Webhook URL encrypted at rest via existing DataProtection; never logged/returned.
- E8: Digest fires via EXTERNAL CRON → authenticated endpoint, NOT a BackgroundService (reverses v1.0 MN1).
- E9: Run endpoint auth = shared-secret token header (X-Digest-Trigger-Token), fixed-time compare, refuses if unconfigured.
- E10: 'Due' = TZ day-of-week + send-hour match AND not-sent-this-window (cron can fire hourly safely).
- E14/E15: Edit-chore dialog drives the existing PUT; backfill reuses the idempotent seed.
- E16: Dev-ONLY seed enrichment (multi-member + cross-member completions) so equity renders a real distribution locally.

**Verification:** Pure calculators/builder unit-tested with frozen now+tz fixtures; equity DTO pinned by its own `equity.json` contract fixture; the **digest seam** (idempotency, multi-tenant isolation, failure isolation, token auth, encryption-over-the-wire, backfill idempotency, migration-applies-clean) integration-tested on **real Postgres + the booted host** via the v1.0 Testcontainers/WebApplicationFactory harness with a **fake sender** (zero live Discord). Island = build + svelte-check.

**Constraints:**
- MN1 stays for code (no BackgroundService) — firing is cron→endpoint; E3 escalation: if cron proves infeasible, STOP for sign-off rather than adding a hosted service.
- MN7/E7: never log/return/display the webhook plaintext; no plaintext-storage fallback.
- M11/MN8: collective broadcast only — no @mentions/targeted nudges; allowed_mentions suppresses pings.
- MN5: digest config is its own entity — no new column on Household/User/Chore.
- E8(op)/HOLD: build + test + local commits only — no push, no PR, no prod flag flip until operator review.

## Flagged items

### E8 (DECISION) [decisions] — Digest fires via external cron → endpoint, NOT a BackgroundService (reverses v1.0 MN1)

This is the single place v1.1 deliberately reverses a v1.0 must-not (MN1: no BackgroundService/IHostedService). The chosen mechanism keeps MN1's spirit — the app stays request-driven, the schedule lives in a darktower cron that curls `POST /api/chores/digest/run` with a shared-secret token. It forecloses nothing (a future BackgroundService could call the same RunDueAsync). **This is the ratification gate you asked for.** If you'd rather have an in-process hosted service, only the trigger changes; the builder/sender/orchestration are identical.

Triage: ✅ approve · ✏️ revise · ❌ reject

### E3 (DECISION) [decisions] — Equity windows = week + all-time; last-week trend deferred

The endpoint ships `window ∈ {week, all}`. `week` is the digest window + lens default; `all` is near-free (no lower bound). A last-week comparison/trend (would add a second window + delta UI) is deferred — not load-bearing for 'is the load shared.' Flag if you want the trend in v1.1.

Triage: ✅ approve · ✏️ revise · ❌ reject

### E4 (DECISION) [decisions] — Equity is a 5th lens (not a panel buried in Mine)

Your north-star was 'first-class, not a token strip.' A 5th lens in the ViewSwitcher (needs-attention|rooms|up-for-grabs|mine|equity) is the honest expression; a user can even open onto it. The WP-12 held-count strip in Mine is superseded.

Triage: ✅ approve · ✏️ revise · ❌ reject

### E5 (DECISION) [decisions] — Equity = neutral shares + equal-share reference; no thresholds, no ranking, no 'behind' labels

Reconciles 'rich/visualized/actionable' with the non-punitive guardrail: per-member data is shown richly (proportional bars + a neutral equal-share line) but framed as a distribution, never a leaderboard. Built dark against synthetic data ⇒ no fairness threshold is validated (recorded gap GAP-3); tune post-launch. Confirm the equal-share reference line is acceptable (it's descriptive, not a judgment) — or drop even that.

Triage: ✅ approve · ✏️ revise · ❌ reject

### OQ1 (ASSUMPTION) [decisions] — Default cadence = weekly, Sunday 18:00 local (per-household tunable)

Weekly is locked; the default send window is a named constant (Sunday 18:00 in the app timezone), overridable per household via the settings surface. Pick a different default if Sunday evening isn't right for the family.

Triage: ✅ approve · ✏️ revise · ❌ reject

### A7 (ASSUMPTION) [decisions] — Run endpoint auth = shared-secret token in a header (not cookie, not query string)

Cron has no user session, so `POST /api/chores/digest/run` sits outside cookie auth and validates `CHORES_DIGEST_TRIGGER_TOKEN` from the `X-Digest-Trigger-Token` header with a fixed-time compare; it refuses to run (503) if the token is unconfigured. Query-string tokens are rejected (they leak to logs).

Triage: ✅ approve · ✏️ revise · ❌ reject

### A2 (ASSUMPTION) [decisions] — Webhook encrypted via the existing Data Protection (a new use of an already-registered capability)

AddDataProtection is configured and DATAPROTECTION_CERT is prod-required, but nothing currently encrypts a stored value (only cookies). v1.1 stores `protector.Protect(webhookUrl)` ciphertext; decrypts only at send time. No new package/infra. If Data Protection can't be used cleanly, the build STOPS rather than storing plaintext (E7).

Triage: ✅ approve · ✏️ revise · ❌ reject

### GAP (ASSUMPTION) [verification] — Manual-owed: live Discord send + island UI click-through; equity thresholds tuned post-launch

GAP-1: the literal discord.com POST is verified only by payload-shape assertions against a stub — a 2-minute manual check (paste a test webhook → hit the run endpoint) closes it. GAP-2: the Equity lens / edit dialog / settings surface are build+svelte-check verified, not browser-automated (OAuth, no harness). GAP-3: fairness thresholds unvalidated while dark (descriptive-only mitigates). GAP-5: single app timezone (per-household TZ deferred).

Triage: ✅ approve · ✏️ revise · ❌ reject

## Compile template

```
Spec review decisions for {spec_title} (slug: `{spec_slug}`, review_level: {review_level}):

{decisions_blob}

General feedback: {general_feedback}
Counts: {approved_count} approved, {revised_count} revised, {rejected_count} rejected.

Proceed to Phase 5 (Revision if any items were revised or rejected) then Phase 6 (Decomposition and Work Packages). Spec dir: {spec_dir}.
```