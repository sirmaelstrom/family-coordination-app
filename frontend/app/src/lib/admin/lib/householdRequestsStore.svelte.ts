// ─────────────────────────────────────────────────────────────────────────
// Household-requests view store (#admin-households-root). Source of truth for
// the request list + the existing-households table. SITE-ADMIN ONLY: a 403 on
// the list GET sets `accessDenied` (the 403 IS the signal — R-C4, no /context
// endpoint), and the App renders "Access denied".
//
// Discipline (memory svelte5-setup-effect-async-loader-loop): onMount one-time
// load; this view POLLS at 30s. Load and write ordering live in a Resource
// (quest 41337b6f): a stale poll never clobbers a newer load, no poll that
// overlaps an approve/reject/create commits, and the list reloads once when the
// last write settles (success or failure — a 409 already-reviewed reconciles to
// the truth, R-C3). The approve transaction / 409 guard / IDOR live SERVER-side
// (the source of truth); the client just reflects them.
// ─────────────────────────────────────────────────────────────────────────

import type { HouseholdRequestDto, HouseholdRequestsDto, HouseholdSummaryDto } from './types';
import { ApiError, getHouseholdRequests, approveRequest, rejectRequest, createHousehold } from './api';
import { Resource } from '$lib/data/resource.svelte';
import { showToast } from '$lib/shared/toast-store.svelte';

class HouseholdRequestsStore {
  requests = $state<HouseholdRequestDto[]>([]);
  households = $state<HouseholdSummaryDto[]>([]);
  loading = $state(true);
  error = $state<string | null>(null);
  /** True after a 403 on the list GET — the caller is not a site admin (R-C4). */
  accessDenied = $state(false);
  /** True while an admin create-household POST is in flight (disables the invite form). */
  creating = $state(false);

  private hasLoaded = false;

  #resource = new Resource<HouseholdRequestsDto>({
    fetch: () => getHouseholdRequests(),
    commit: (dto) => {
      this.requests = dto.requests;
      this.households = dto.households;
      this.accessDenied = false;
      this.hasLoaded = true;
    },
    onLoadStart: () => {
      if (!this.hasLoaded) this.loading = true; // spinner only on the FIRST load
      this.error = null;
    },
    onLoadError: (e) => {
      if (e instanceof ApiError && e.status === 403) {
        this.accessDenied = true; // the 403 IS the access-denied signal (R-C4)
      } else {
        this.error = describe(e, 'load household requests');
      }
    },
    onLoadIdle: () => {
      this.loading = false;
    },
  });

  async load(): Promise<void> {
    await this.#resource.load();
  }

  async approve(id: number, householdName: string): Promise<void> {
    let summary: HouseholdSummaryDto;
    try {
      summary = await this.#resource.run({ call: () => approveRequest(id) });
    } catch (e) {
      showToast({ message: describe(e, `approve “${householdName}”`), kind: 'error' });
      return;
    }
    showToast({ message: `Approved — created “${summary.name}”.`, kind: 'success' });
  }

  async reject(id: number, reason: string, requestorName: string): Promise<void> {
    try {
      await this.#resource.run({ call: () => rejectRequest(id, reason) });
    } catch (e) {
      showToast({ message: describe(e, 'reject request'), kind: 'error' });
      return;
    }
    showToast({ message: `Rejected request from ${requestorName}.`, kind: 'info' });
  }

  /**
   * Admin-initiated household create (the "push" invite). Returns true on success so the form can clear itself.
   * Validation/collision (400/409) surface as a calm toast; the server is the source of truth, the client reflects it.
   */
  async createHousehold(householdName: string, ownerEmail: string, ownerDisplayName: string): Promise<boolean> {
    if (this.creating) return false;
    this.creating = true;
    try {
      const summary = await this.#resource.run({
        call: () => createHousehold(householdName, ownerEmail, ownerDisplayName.trim() || undefined),
      });
      showToast({ message: `Created “${summary.name}” — ${ownerEmail} can now sign in.`, kind: 'success' });
      return true;
    } catch (e) {
      showToast({ message: describe(e, 'create household'), kind: 'error' });
      return false;
    } finally {
      this.creating = false;
    }
  }
}

function describe(e: unknown, action: string): string {
  if (e instanceof ApiError) return e.message || `Couldn't ${action} (HTTP ${e.status}).`;
  if (e instanceof Error) return e.message;
  return `Couldn't ${action}.`;
}

export const householdRequestsStore = new HouseholdRequestsStore();
