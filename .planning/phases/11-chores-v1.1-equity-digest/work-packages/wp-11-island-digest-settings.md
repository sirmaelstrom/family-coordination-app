# WP-11: Island digest-settings surface

**Wave:** 9 · **Execution:** review-needed *(handles a secret input)* · **Depends on:** WP-10 (same island files), WP-06 (settings endpoints)

## Precondition
`GET/PUT /api/chores/digest-settings` exist (WP-06); `GET` returns a safe view (`hasWebhook` + masked hint, no
URL). The edit dialog + island conventions (WP-10) have landed.

## Goal
A small settings surface to configure the household's weekly Discord digest: paste/replace the webhook URL,
toggle enabled, pick send day + hour. Never display the stored URL.

## Files
- **Create** `frontend/chores/src/lib/components/DigestSettings.svelte`
- **Modify** `frontend/chores/src/lib/api.ts` (`getDigestSettings()`, `updateDigestSettings(body)`)
- **Modify** `frontend/chores/src/lib/types.ts` (`DigestSettingsView`, `DigestSettingsUpdate`)
- **Modify** `frontend/chores/src/App.svelte` (a settings entry point — e.g. a gear/overflow that opens the sheet)

## Implementation notes
- `getDigestSettings()` → the safe view; render `enabled` toggle, `sendDayOfWeek` (Sun–Sat), `sendHourLocal`
  (0–23 or a friendly time picker), and a **webhook field**: show `hasWebhook ? "Webhook set ••••<hint>" :
  "Not configured"`, with a "Replace webhook" input that submits a new URL (the input is write-only; the
  current URL is **never** fetched/displayed — MN7). A "Send a test digest" hint can point the user to the
  manual cron/endpoint check (GAP-1) but the surface itself does not expose the token.
- `updateDigestSettings(body)` → PUT. **Wire casing is camelCase enum strings** (council — the global
  `JsonStringEnumConverter(CamelCase)` serializes `DigestCadence.Weekly`→`"weekly"` and `DayOfWeek.Sunday`→
  `"sunday"`): `cadence: 'weekly'` (typed `'weekly'` now, union-extensible later), `sendDayOfWeek:
  'sunday'|'monday'|…|'saturday'`, `sendHourLocal: number`. The `types.ts` `DigestSettingsView`/
  `DigestSettingsUpdate` MUST use these lowercase unions (mirror WP-06's frozen contract). **`webhookUrl`
  (frozen tri-state, identical to WP-03/06):** omit ⇒ unchanged; non-blank string ⇒ replace; explicit `null`
  or `''` ⇒ clear. Success/validation toasts.
- The webhook input is `type="url"`, `autocomplete="off"`; do not log it client-side; do not place it in any
  query string.

## Verification (V4 island portion, V14, GAP-2)
- `npm run build` + `npx svelte-check` = 0/0. **Manual (owed):** set a webhook → reload → shows "Webhook set
  ••••" (never the URL); toggle enabled + pick day/hour persist; clearing works.

## Failure criteria
- The stored webhook URL is ever fetched into or rendered by the client (MN7). · The URL placed in a query
  string or logged. · `cadence` hardcoded such that the enum can't extend later. · Shopping-list island
  touched (MN6).

## Boundary
Digest-settings island files ONLY. No backend change. Last island WP — after this the island is feature-
complete for v1.1.

## Notes for downstream
- After WP-11 the bundle is code-complete. Remaining: WP-08 integration green, full local verification, and
  the deploy-time flag flip + cron wiring (documented in the morning handoff, E19) — NOT in code, NOT pushed.
