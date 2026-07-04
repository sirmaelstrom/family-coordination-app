// ─────────────────────────────────────────────────────────────────────────
// Connections view store (#connections-root). Source of truth for the active
// invite, the connected-households list, AND the invite-flow state machine
// (generate → enter code → validate → pre-connection confirm → accept; plus the
// disconnect-confirm flow). Faithful port of Connections.razor's @code block.
//
// ⚠ Svelte 5 rune rule (global CORRECTION): never `export` a reassigned `$state`.
// We wrap the mutable state in a class instance and export the instance.
//
// Parity-first ⇒ versionless / last-write-wins. The page does NOT poll (no
// liveness). Every mutation `await`s then refreshes; a `loadSeq` guard drops a
// stale reload that resolves after a newer one (memory fca-island-async-race-guards).
// There is no autosave/draft here (all actions are explicit buttons), so the
// flush-before-delete guard from that memory does not apply.
//
// Error surfacing parity: validate failures + accept failures render INLINE as
// codeError (a warning under the code input); generate/cancel/disconnect/copy
// failures surface as toasts. The validate/accept endpoints return a 200 outcome
// envelope, so a business "no" is `isValid:false` / `success:false`, not a throw.
// ─────────────────────────────────────────────────────────────────────────

import type { InviteDto, ConnectedDto } from './types';
import {
  ApiError,
  getConnections,
  generateInvite,
  cancelInvite,
  validateCode,
  acceptCode,
  disconnect,
} from './api';
import { showToast } from '$lib/shared/toast-store.svelte';

/**
 * Faithful port of Connections.razor's MapValidationError (:403-415). The service
 * returns prose error strings ("You cannot connect to your own household.") that
 * don't match these code labels, so today this falls through to the default and
 * passes the service message through — the port preserves that exact behavior.
 */
export function mapValidationError(error: string | null | undefined): string {
  switch ((error ?? '').toLowerCase()) {
    case 'self_connection':
    case 'self-connection':
    case 'self':
      return 'This is your own invite code. Share it with another family to connect.';
    case 'already_connected':
    case 'already-connected':
    case 'already connected':
      return "You're already connected with this family!";
    case 'expired':
      return 'This code has expired. Ask them to create a new one.';
    case 'invalid':
    case 'not_found':
    case 'not found':
      return "We couldn't find that code. Check the code and try again.";
    default:
      return error || "We couldn't find that code. Check the code and try again.";
  }
}

class ConnectionsStore {
  // ── Loaded data ──
  activeInvite = $state<InviteDto | null>(null);
  connected = $state<ConnectedDto[]>([]);
  loading = $state(true);
  error = $state<string | null>(null);

  // ── Share-section flow ──
  generating = $state(false);

  // ── Enter-a-code flow ──
  enteredCode = $state('');
  validating = $state(false);
  codeError = $state<string | null>(null);
  showConfirm = $state(false);
  pendingHouseholdName = $state<string | null>(null);
  accepting = $state(false);

  // ── Disconnect flow ──
  disconnectTarget = $state<ConnectedDto | null>(null);
  disconnecting = $state(false);

  /**
   * True once the FIRST load resolved. Gates the skeleton so reloads don't reflash it, AND lets the App keep
   * the sections mounted on a later reload error (a refresh failure must not swap the whole view back to the
   * error state — the dashboard pattern).
   */
  loaded = $state(false);
  /** Monotonic load id — a slower older response must not overwrite a newer one. */
  private seq = 0;

  /** Whether the Submit button is enabled (parity: exactly 6 chars + not mid-validate). */
  get canSubmit(): boolean {
    return this.enteredCode.length === 6 && !this.validating;
  }

  /** Load the active invite + connected list. Spinner only on the FIRST load; reloads refresh in place. */
  async load(): Promise<void> {
    const s = ++this.seq;
    try {
      if (!this.loaded) this.loading = true;
      this.error = null;
      const dto = await getConnections();
      if (s !== this.seq) return; // a newer load superseded this one
      this.activeInvite = dto.activeInvite;
      this.connected = dto.connected;
      this.loaded = true;
    } catch (e) {
      if (s !== this.seq) return;
      this.error = describe(e, 'load connections');
    } finally {
      if (s === this.seq) this.loading = false;
    }
  }

  // ── Share section ───────────────────────────────────────────────────────────

  async generate(): Promise<void> {
    this.generating = true;
    try {
      this.activeInvite = await generateInvite();
    } catch (e) {
      showToast({ message: describe(e, 'create invite'), kind: 'error' });
    } finally {
      this.generating = false;
    }
  }

  async cancelActiveInvite(): Promise<void> {
    try {
      await cancelInvite();
      this.activeInvite = null;
    } catch (e) {
      showToast({ message: describe(e, 'cancel invite'), kind: 'error' });
    }
  }

  /** Copy the active code to the clipboard, with the parity "select manually" fallback. */
  async copyCode(): Promise<void> {
    const code = this.activeInvite?.code;
    if (!code) return;
    try {
      await navigator.clipboard.writeText(code);
      showToast({ message: 'Code copied to clipboard', kind: 'success' });
    } catch {
      showToast({ message: 'Unable to copy — please select the code manually', kind: 'error' });
    }
  }

  // ── Enter-a-code section ──────────────────────────────────────────────────────

  /** Port of OnCodeTextChanged (:361-370): uppercase, strip non-alphanumerics, cap at 6, clear the error. */
  setCode(raw: string): void {
    this.enteredCode = [...raw.toUpperCase()]
      .filter((c) => /[A-Z0-9]/.test(c))
      .slice(0, 6)
      .join('');
    this.codeError = null;
  }

  async validate(): Promise<void> {
    if (this.enteredCode.length !== 6) return;
    this.validating = true;
    this.codeError = null;
    try {
      const res = await validateCode(this.enteredCode);
      if (res.isValid) {
        this.pendingHouseholdName = res.householdName;
        this.showConfirm = true;
      } else {
        this.codeError = mapValidationError(res.error);
      }
    } catch (e) {
      this.codeError = `Something went wrong. Please try again. (${describe(e, 'validate code')})`;
    } finally {
      this.validating = false;
    }
  }

  cancelConfirm(): void {
    this.showConfirm = false;
    this.pendingHouseholdName = null;
  }

  async accept(): Promise<void> {
    this.accepting = true;
    try {
      const res = await acceptCode(this.enteredCode);
      if (res.success) {
        showToast({
          message: `You're now connected with ${res.connectedHouseholdName}! Browse their recipes from your Recipes page.`,
          kind: 'success',
        });
        this.showConfirm = false;
        this.pendingHouseholdName = null;
        this.enteredCode = '';
        await this.load(); // refresh the connected list
      } else {
        // Accept failure returns to the ENTRY view with the mapped error (review R-B3).
        this.codeError = mapValidationError(res.error);
        this.showConfirm = false;
      }
    } catch (e) {
      showToast({ message: describe(e, 'connect'), kind: 'error' });
      this.showConfirm = false;
    } finally {
      this.accepting = false;
    }
  }

  // ── Disconnect section ────────────────────────────────────────────────────────

  askDisconnect(household: ConnectedDto): void {
    this.disconnectTarget = household;
  }

  cancelDisconnect(): void {
    this.disconnectTarget = null;
  }

  async confirmDisconnect(): Promise<void> {
    const target = this.disconnectTarget;
    if (!target) return;
    this.disconnecting = true;
    try {
      await disconnect(target.householdId);
      showToast({ message: `Stopped sharing with ${target.householdName}`, kind: 'success' });
      await this.load();
    } catch (e) {
      if (e instanceof ApiError) await this.load(); // reconcile to truth on a 4xx
      showToast({ message: describe(e, 'disconnect'), kind: 'error' });
    } finally {
      this.disconnectTarget = null;
      this.disconnecting = false;
    }
  }
}

function describe(e: unknown, action: string): string {
  if (e instanceof ApiError) return e.message || `Couldn't ${action} (HTTP ${e.status}).`;
  if (e instanceof Error) return e.message;
  return `Couldn't ${action}.`;
}

/** The single shared connections-view store instance (export the instance, not the runes). */
export const connectionsStore = new ConnectionsStore();
