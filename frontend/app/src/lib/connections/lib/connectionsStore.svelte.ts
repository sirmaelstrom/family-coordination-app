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
// liveness). Load and write ordering live in a Resource (quest 41337b6f): every
// write runs inside its window, and the list refresh a write asks for runs once
// when it settles.
// There is no autosave/draft here (all actions are explicit buttons), so the
// flush-before-delete guard from that memory does not apply.
//
// Error surfacing parity: validate failures + accept failures render INLINE as
// codeError (a warning under the code input); generate/cancel/disconnect/copy
// failures surface as toasts. The validate/accept endpoints return a 200 outcome
// envelope, so a business "no" is `isValid:false` / `success:false`, not a throw.
// ─────────────────────────────────────────────────────────────────────────

import type { InviteDto, ConnectedDto, ConnectionsDto } from './types';
import { Resource } from '$lib/data/resource.svelte';
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
  /**
   * Load + write ordering (quest 41337b6f): the Resource drops a superseded load, dispatches none while
   * a write is pending (it is owed and runs once at settle), and retires one already in flight when a
   * write starts — so a GET that overlaps a generate/cancel/accept/disconnect never commits.
   */
  #resource = new Resource<ConnectionsDto>({
    fetch: () => getConnections(),
    commit: (dto) => {
      this.activeInvite = dto.activeInvite;
      this.connected = dto.connected;
      this.loaded = true;
    },
    onLoadStart: () => {
      if (!this.loaded) this.loading = true; // spinner only on the FIRST load
      this.error = null;
    },
    onLoadError: (e) => {
      this.error = describe(e, 'load connections');
    },
    onLoadIdle: () => {
      this.loading = false;
    },
  });

  /** Whether the Submit button is enabled (parity: exactly 6 chars + not mid-validate). */
  get canSubmit(): boolean {
    return this.enteredCode.length === 6 && !this.validating;
  }

  /** Load the active invite + connected list. Spinner only on the FIRST load; reloads refresh in place. */
  async load(): Promise<void> {
    await this.#resource.load();
  }

  // ── Share section ───────────────────────────────────────────────────────────

  async generate(): Promise<void> {
    this.generating = true;
    try {
      // reload 'never' (parity: the response IS the new invite); a load it retired is still owed.
      await this.#resource.run({
        call: () => generateInvite(),
        commit: (invite) => {
          this.activeInvite = invite;
        },
        reload: 'never',
      });
    } catch (e) {
      showToast({ message: describe(e, 'create invite'), kind: 'error' });
    } finally {
      this.generating = false;
    }
  }

  async cancelActiveInvite(): Promise<void> {
    try {
      await this.#resource.run({
        call: () => cancelInvite(),
        commit: () => {
          this.activeInvite = null;
        },
        reload: 'never',
      });
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
      // reload 'always': the connected list refreshes once the write settles (the outcome is a 200
      // envelope, so a business "no" refreshes too — harmless, and it converges on server truth).
      const code = this.enteredCode;
      const res = await this.#resource.run({ call: () => acceptCode(code) });
      // As before the migration, the confirm stays up (accepting) until the refreshed list has landed,
      // so the new connection is listed when it closes (council r3).
      await this.#resource.settled();
      if (res.success) {
        showToast({
          message: `You're now connected with ${res.connectedHouseholdName}! Browse their recipes from your Recipes page.`,
          kind: 'success',
        });
        this.showConfirm = false;
        this.pendingHouseholdName = null;
        this.enteredCode = '';
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
      // The confirmed disconnect drops its row at once (council r3: no "Stop Sharing" left on a row the
      // toast says is gone); reload 'always' then refreshes the list, success or failure.
      await this.#resource.run({
        call: () => disconnect(target.householdId),
        commit: () => {
          this.connected = this.connected.filter((c) => c.householdId !== target.householdId);
        },
      });
      showToast({ message: `Stopped sharing with ${target.householdName}`, kind: 'success' });
    } catch (e) {
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
