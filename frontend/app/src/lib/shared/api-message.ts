/**
 * Pull the `message` out of an /api error body.
 *
 * Every /api 4xx carries `{ "message": ... }` — handlers write their own, and the pipeline backfills a generic
 * one for any that don't (Program.cs `/api` UseStatusCodePages branch). Without this, a client that fell back to
 * `res.statusText` now shows the user raw JSON instead of "Not Found".
 *
 * Returns null when the body is empty or isn't a JSON object with a string message, so callers keep their own
 * fallback: `messageFrom(text) ?? res.statusText`.
 *
 * The settings / admin / connections clients carry an equivalent inline version; fold them in if they're touched.
 */
export function messageFrom(text: string): string | null {
  if (!text) return null;
  try {
    const parsed = JSON.parse(text);
    if (parsed && typeof parsed.message === 'string' && parsed.message) return parsed.message;
  } catch {
    /* not JSON — the caller falls back */
  }
  return text;
}
