// ─────────────────────────────────────────────────────────────────────────
// Household-requests view store (#admin-households-root). Source of truth for
// the request list + the existing-households table. SITE-ADMIN ONLY: a 403 on
// the list GET sets `accessDenied` (the 403 IS the signal — R-C4, no /context
// endpoint), and the App renders "Access denied".
//
// Discipline (memories svelte5-setup-effect-async-loader-loop + fca-island-async-
// race-guards): onMount one-time load, `loadSeq` monotonic guard on every load
// (this view POLLS at 30s, so a stale poll response must never clobber a newer
// one), await-then-reload mutations, reconcile (refetch) on any 4xx. The approve
// transaction / 409 already-reviewed guard / IDOR live SERVER-side (the source of
// truth); the client just reflects them.
// ─────────────────────────────────────────────────────────────────────────

import type { HouseholdRequestDto, HouseholdSummaryDto } from './types';
import { ApiError, getHouseholdRequests, approveRequest, rejectRequest } from './api';
import { showToast } from './toasts.svelte';

class HouseholdRequestsStore {
  requests = $state<HouseholdRequestDto[]>([]);
  households = $state<HouseholdSummaryDto[]>([]);
  loading = $state(true);
  error = $state<string | null>(null);
  /** True after a 403 on the list GET — the caller is not a site admin (R-C4). */
  accessDenied = $state(false);

  private hasLoaded = false;
  private seq = 0;

  async load(): Promise<void> {
    const s = ++this.seq;
    try {
      if (!this.hasLoaded) this.loading = true;
      this.error = null;
      const dto = await getHouseholdRequests();
      if (s !== this.seq) return; // a newer load already applied — drop this stale one
      this.requests = dto.requests;
      this.households = dto.households;
      this.accessDenied = false;
      this.hasLoaded = true;
    } catch (e) {
      if (s !== this.seq) return;
      if (e instanceof ApiError && e.status === 403) {
        this.accessDenied = true; // the 403 IS the access-denied signal (R-C4)
      } else {
        this.error = describe(e, 'load household requests');
      }
    } finally {
      if (s === this.seq) this.loading = false;
    }
  }

  async approve(id: number, householdName: string): Promise<void> {
    try {
      const summary = await approveRequest(id);
      await this.load();
      showToast({ message: `Approved — created “${summary.name}”.`, kind: 'success' });
    } catch (e) {
      // A 409 (already reviewed by another admin / a stale poll) reconciles to the truth, R-C3.
      if (e instanceof ApiError) await this.load();
      showToast({ message: describe(e, `approve “${householdName}”`), kind: 'error' });
    }
  }

  async reject(id: number, reason: string, requestorName: string): Promise<void> {
    try {
      await rejectRequest(id, reason);
      await this.load();
      showToast({ message: `Rejected request from ${requestorName}.`, kind: 'info' });
    } catch (e) {
      if (e instanceof ApiError) await this.load();
      showToast({ message: describe(e, 'reject request'), kind: 'error' });
    }
  }
}

function describe(e: unknown, action: string): string {
  if (e instanceof ApiError) return e.message || `Couldn't ${action} (HTTP ${e.status}).`;
  if (e instanceof Error) return e.message;
  return `Couldn't ${action}.`;
}

export const householdRequestsStore = new HouseholdRequestsStore();
