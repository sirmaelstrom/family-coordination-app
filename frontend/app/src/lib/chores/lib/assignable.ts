import type { MemberDto } from './types';

/**
 * The members one assignment picker may offer (quest 59b0e3b6): those the board lists as assignable (enabled), plus
 * whoever ALREADY holds that picker's role (`keepUserIds`), so an edit sheet still shows a disabled member where they
 * are instead of silently dropping them. Pass each role its own holders: a disabled minder kept in the minder picker
 * must not appear in the assignee picker, because the server refuses a NEW assignment to a disabled member
 * (HouseholdMembership.EnsureAssignableAsync).
 */
export function assignableMembers(
  members: readonly MemberDto[],
  assignableUserIds: readonly number[],
  keepUserIds: readonly (number | null | undefined)[] = [],
): MemberDto[] {
  const allowed = new Set<number>(assignableUserIds);
  for (const id of keepUserIds) if (id != null) allowed.add(id);
  return members.filter((m) => allowed.has(m.userId));
}
