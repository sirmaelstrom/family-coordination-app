// ─────────────────────────────────────────────────────────────────────────
// Manage-Users view store (#settings-users-root). Source of truth for the
// household member list + the caller's id (for "You" + self-gating).
//
// Same discipline as the categories store: untrack one-time load (in the App),
// load and write ordering in a Resource (quest 41337b6f: every write runs in its
// window and the list reloads once when the last write settles), no liveness.
// The self / last-active / last-user guards are enforced SERVER-side (the source
// of truth); the client mirror here is only for button visibility (parity
// WhitelistAdmin).
// ─────────────────────────────────────────────────────────────────────────

import type { MemberDto, MemberListDto } from './types';
import { ApiError, getMembers, addMember, setWhitelist, deleteMember } from './api';
import { Resource } from '$lib/data/resource.svelte';
import { showToast } from '$lib/shared/toast-store.svelte';

class MembersStore {
  members = $state<MemberDto[]>([]);
  currentUserId = $state(0);
  loading = $state(true);
  error = $state<string | null>(null);

  private hasLoaded = false;

  #resource = new Resource<MemberListDto>({
    fetch: () => getMembers(),
    commit: (dto) => {
      this.members = dto.members;
      this.currentUserId = dto.currentUserId;
      this.hasLoaded = true;
    },
    onLoadStart: () => {
      if (!this.hasLoaded) this.loading = true; // spinner only on the FIRST load
      this.error = null;
    },
    onLoadError: (e) => {
      this.error = describe(e, 'load members');
    },
    onLoadIdle: () => {
      this.loading = false;
    },
  });

  /** Active (whitelisted) member count — mirrors the server's last-active guard for button visibility. */
  activeCount = $derived(this.members.filter((m) => m.isWhitelisted).length);

  async load(): Promise<void> {
    await this.#resource.load();
  }

  async add(email: string): Promise<boolean> {
    const trimmed = email.trim();
    if (!trimmed) return false;
    let res;
    try {
      // reload 'always' (the run default): the list refreshes once the write settles.
      res = await this.#resource.run({ call: () => addMember(trimmed) });
    } catch (e) {
      showToast({ message: describe(e, 'add member'), kind: 'error' });
      return false;
    }
    if (res.outcome === 'created') {
      showToast({ message: `Added ${res.member.email}.`, kind: 'success' });
    } else if (res.outcome === 'reenabled') {
      showToast({ message: `Re-enabled ${res.member.email}.`, kind: 'success' });
    } else {
      // alreadyActive — a benign notice, NOT an error (parity: a warning today, review R-A1).
      showToast({ message: `${res.member.email} already has access.`, kind: 'info' });
    }
    return true;
  }

  async toggle(userId: number, isWhitelisted: boolean): Promise<void> {
    try {
      await this.#resource.run({ call: () => setWhitelist(userId, isWhitelisted) });
    } catch (e) {
      showToast({ message: describe(e, isWhitelisted ? 'enable member' : 'disable member'), kind: 'error' });
    }
  }

  async remove(userId: number): Promise<void> {
    try {
      await this.#resource.run({ call: () => deleteMember(userId) });
    } catch (e) {
      showToast({ message: describe(e, 'remove member'), kind: 'error' });
      return;
    }
    showToast({ message: 'Member removed.', kind: 'success' });
  }
}

function describe(e: unknown, action: string): string {
  if (e instanceof ApiError) return e.message || `Couldn't ${action} (HTTP ${e.status}).`;
  if (e instanceof Error) return e.message;
  return `Couldn't ${action}.`;
}

export const membersStore = new MembersStore();
