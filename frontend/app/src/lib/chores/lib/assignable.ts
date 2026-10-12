import type { ChoreDto, MemberDto } from './types';

/**
 * The members an assignment picker may offer (quest 59b0e3b6): those the board lists as assignable (enabled), plus
 * anyone in `keepUserIds`, so an edit sheet still shows a disabled member already on the chore instead of silently
 * dropping them. The server refuses a NEW assignment to a disabled member (HouseholdMembership.EnsureAssignableAsync).
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

/** The people already on a chore: its owner, its assignee and its roster. */
export function peopleOnChore(chore: ChoreDto | null | undefined): number[] {
  if (!chore) return [];
  return [chore.ownerUserId, chore.assigneeUserId, ...chore.roster.map((r) => r.userId)].filter(
    (id): id is number => id != null,
  );
}
