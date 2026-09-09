// ─────────────────────────────────────────────────────────────────────────
// Pure state transitions for the calendar-feed card (quest 17e38a88). The
// store (feedStore.svelte.ts) is a thin reactive shell over these; the part a
// unit test can pin down is the COPY-ONCE rule: the feed URL exists in the UI
// state only between the POST response and the next status load or revoke —
// it is never re-derived from GET, because the server cannot return it.
// ─────────────────────────────────────────────────────────────────────────

import type { CalendarTokenStatusDto, CalendarTokenCreatedDto } from './types';

/** What the card knows about the household's feed. */
export type FeedPhase =
  /** GET says no active token. */
  | { kind: 'none' }
  /** GET says a token is active, but the URL is not readable (created earlier, or copied already). */
  | { kind: 'active'; createdAt: string | null }
  /** The POST response is in hand: the URL is readable exactly until the next load/revoke. */
  | { kind: 'revealed'; createdAt: string | null; url: string };

/** GET → phase. A status load can never produce a readable URL (copy-once). */
export function phaseFromStatus(status: CalendarTokenStatusDto): FeedPhase {
  if (!status.active) return { kind: 'none' };
  return { kind: 'active', createdAt: status.createdAt ?? null };
}

/** POST → phase. The only transition that carries a URL. `now` is the client's clock for the createdAt display. */
export function phaseFromCreated(created: CalendarTokenCreatedDto, now: Date): FeedPhase {
  return { kind: 'revealed', createdAt: now.toISOString(), url: created.url };
}

/** DELETE → phase. */
export function phaseAfterRevoke(): FeedPhase {
  return { kind: 'none' };
}

/** The URL the user can copy right now, or null. Only the `revealed` phase has one. */
export function copyableUrl(phase: FeedPhase): string | null {
  return phase.kind === 'revealed' ? phase.url : null;
}

/** True when a POST would kill a live URL (rotate), false when it merely creates the first one. */
export function postIsRotate(phase: FeedPhase): boolean {
  return phase.kind !== 'none';
}

/** The primary button's label for the current phase. */
export function primaryActionLabel(phase: FeedPhase): string {
  return postIsRotate(phase) ? 'Rotate link' : 'Create subscription link';
}

/**
 * The one-line status the card shows under its title. The active branch is
 * deliberately explicit that the link is NOT re-readable, because the natural
 * expectation ("show me my link") is exactly what the server refuses.
 */
export function statusLine(phase: FeedPhase): string {
  switch (phase.kind) {
    case 'none':
      return 'No subscription link yet. Create one, then add it to your phone or Google Calendar.';
    case 'active':
      return 'A subscription link is active. For security it is shown only once, when it is created — if you need it again, rotate it (the old link stops working).';
    case 'revealed':
      return 'Copy this link now — it will not be shown again. Anyone with the link can read the household meal plan.';
  }
}

/** Confirm-dialog copy for the destructive actions. */
export const ROTATE_CONFIRM = {
  title: 'Rotate subscription link',
  message:
    'This creates a new link and immediately stops the current one. Every phone or calendar subscribed with the old link will stop updating until it is re-subscribed with the new one. Continue?',
  confirmLabel: 'Rotate',
} as const;

export const REVOKE_CONFIRM = {
  title: 'Revoke subscription link',
  message:
    'This stops the household feed for everyone who subscribed. Calendars that used it will stop updating. You can create a new link at any time.',
  confirmLabel: 'Revoke',
} as const;

/** Short, client-specific how-to lines. Kept as data so the card and a future help page share them. */
export const CLIENT_HOWTO: ReadonlyArray<{ client: string; steps: string }> = [
  { client: 'iPhone / iPad', steps: 'Settings → Calendar → Accounts → Add Account → Other → Add Subscribed Calendar, then paste the link.' },
  { client: 'Google Calendar', steps: 'On the web: Other calendars → + → From URL, then paste the link. It appears on your phone after the next sync.' },
  { client: 'Outlook', steps: 'Add calendar → Subscribe from web, then paste the link.' },
];

/** Render a created-at instant for the card (local time). */
export function describeCreatedAt(iso: string | null, now: Date = new Date()): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const sameDay = d.toDateString() === now.toDateString();
  return sameDay
    ? `Created today at ${d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}`
    : `Created ${d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })}`;
}
