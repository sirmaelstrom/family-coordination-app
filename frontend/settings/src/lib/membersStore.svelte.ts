// ─────────────────────────────────────────────────────────────────────────
// Manage-Users view store (#settings-users-root). Source of truth for the
// household member list + the caller's id (for "You" + self-gating).
//
// Same discipline as the categories store: untrack one-time load (in the App),
// `loadSeq` guard, await-then-reload mutations, no liveness. The self / last-active
// / last-user guards are enforced SERVER-side (the source of truth); the client
// mirror here is only for button visibility (parity WhitelistAdmin).
// ─────────────────────────────────────────────────────────────────────────

import type { MemberDto } from './types';
import { ApiError, getMembers, addMember, setWhitelist, deleteMember } from './api';
import { showToast } from './toasts.svelte';

class MembersStore {
  members = $state<MemberDto[]>([]);
  currentUserId = $state(0);
  loading = $state(true);
  error = $state<string | null>(null);

  private hasLoaded = false;
  private seq = 0;

  /** Active (whitelisted) member count — mirrors the server's last-active guard for button visibility. */
  activeCount = $derived(this.members.filter((m) => m.isWhitelisted).length);

  async load(): Promise<void> {
    const s = ++this.seq;
    try {
      if (!this.hasLoaded) this.loading = true;
      this.error = null;
      const dto = await getMembers();
      if (s !== this.seq) return;
      this.members = dto.members;
      this.currentUserId = dto.currentUserId;
      this.hasLoaded = true;
    } catch (e) {
      if (s !== this.seq) return;
      this.error = describe(e, 'load members');
    } finally {
      if (s === this.seq) this.loading = false;
    }
  }

  async add(email: string): Promise<boolean> {
    const trimmed = email.trim();
    if (!trimmed) return false;
    try {
      const res = await addMember(trimmed);
      await this.load();
      if (res.outcome === 'created') {
        showToast({ message: `Added ${res.member.email}.`, kind: 'success' });
      } else if (res.outcome === 'reenabled') {
        showToast({ message: `Re-enabled ${res.member.email}.`, kind: 'success' });
      } else {
        // alreadyActive — a benign notice, NOT an error (parity: a warning today, review R-A1).
        showToast({ message: `${res.member.email} already has access.`, kind: 'info' });
      }
      return true;
    } catch (e) {
      showToast({ message: describe(e, 'add member'), kind: 'error' });
      return false;
    }
  }

  async toggle(userId: number, isWhitelisted: boolean): Promise<void> {
    try {
      await setWhitelist(userId, isWhitelisted);
      await this.load();
    } catch (e) {
      if (e instanceof ApiError) await this.load();
      showToast({ message: describe(e, isWhitelisted ? 'enable member' : 'disable member'), kind: 'error' });
    }
  }

  async remove(userId: number): Promise<void> {
    try {
      await deleteMember(userId);
      await this.load();
      showToast({ message: 'Member removed.', kind: 'success' });
    } catch (e) {
      if (e instanceof ApiError) await this.load();
      showToast({ message: describe(e, 'remove member'), kind: 'error' });
    }
  }
}

function describe(e: unknown, action: string): string {
  if (e instanceof ApiError) return e.message || `Couldn't ${action} (HTTP ${e.status}).`;
  if (e instanceof Error) return e.message;
  return `Couldn't ${action}.`;
}

export const membersStore = new MembersStore();
