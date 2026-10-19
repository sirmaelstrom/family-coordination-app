// ─────────────────────────────────────────────────────────────────────────
// Pure state transitions for the calendar-feed card (quest 17e38a88). The
// store (feedStore.svelte.ts) is a thin reactive shell over these; the part a
// unit test can pin down is the COPY-ONCE rule: the feed URL exists in the UI
// state only from the POST response until the next page (re)load, a revoke, a
// failed create/rotate, or a status saying no token is active — it is never
// re-derived from GET, because the server cannot return it. (The store keeps the
// reveal apart from the status, so the confirming status load a write owes does
// not erase it — quest 41337b6f.)
// ─────────────────────────────────────────────────────────────────────────

import type { CalendarTokenStatusDto, CalendarTokenCreatedDto } from './types';

/** What the card knows about the caller's own feed link (links are per member, quest 8dba87f4). */
export type FeedPhase =
  /** GET says no active token. */
  | { kind: 'none' }
  /** GET says a token is active, but the URL is not readable (created earlier, or copied already). */
  | { kind: 'active'; createdAt: string | null }
  /** The POST response is in hand: the URL is readable until a page (re)load, a revoke, or a failed write. */
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
export function statusLine(phase: FeedPhase, feed: FeedKind = 'meal'): string {
  switch (phase.kind) {
    case 'none':
      return 'You have no subscription link yet. Create one, then add it to your phone or Google Calendar.';
    case 'active':
      return 'Your subscription link is active. For security it is shown only once, when it is created — if you need it again, rotate it (your old link stops working).';
    case 'revealed':
      return `Copy this link now — it will not be shown again. Anyone with the link can read ${READS[feed]}.`;
  }
}

/** Which feed a card is for (quest 3bdbcab7 adds the caller's chores). */
export type FeedKind = 'meal' | 'chores';

/** What anyone holding the link can read, per feed. */
const READS: Record<FeedKind, string> = {
  meal: 'the household meal plan',
  chores: 'the chores assigned to you',
};

/** Confirm-dialog copy for the destructive actions. Each touches only the caller's own link. */
export const ROTATE_CONFIRM = {
  title: 'Rotate your subscription link',
  message:
    'This creates a new link and immediately stops your current one. Calendars you subscribed with the old link stop updating until you re-subscribe them with the new one. Other members’ links are not affected. Continue?',
  confirmLabel: 'Rotate',
} as const;

export const REVOKE_CONFIRM = {
  title: 'Revoke your subscription link',
  message:
    'This stops your link. Calendars you subscribed with it stop updating. Other members’ links are not affected. You can create a new link at any time.',
  confirmLabel: 'Revoke',
} as const;

/** Confirm-dialog copy for the chores card (quest 3bdbcab7). */
export const CHORES_ROTATE_CONFIRM = {
  title: 'Rotate your chores link',
  message:
    'This creates a new chores link and immediately stops your current one. Calendars you subscribed with the old link stop updating until you re-subscribe them. Your meal-plan link is not affected. Continue?',
  confirmLabel: 'Rotate',
} as const;

export const CHORES_REVOKE_CONFIRM = {
  title: 'Revoke your chores link',
  message:
    'This stops your chores link. Calendars you subscribed with it stop updating. Your meal-plan link is not affected. You can create a new link at any time.',
  confirmLabel: 'Revoke',
} as const;

/** The legacy household-shared link (from before per-member links): what the card says about it, or null to hide the block. */
export function sharedLinkLine(status: { sharedLinkActive?: boolean } | null): string | null {
  if (!status?.sharedLinkActive) return null;
  return 'An older shared household link is still active. Members who subscribed with it keep receiving updates until it is revoked. Once each of them has their own link, revoke it here.';
}

export const REVOKE_SHARED_CONFIRM = {
  title: 'Revoke the shared household link',
  message:
    'This stops the older shared link for everyone who subscribed with it. Members’ own links keep working. It cannot be re-created. Continue?',
  confirmLabel: 'Revoke shared link',
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
