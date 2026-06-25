// ─────────────────────────────────────────────────────────────────────────
// TS mirror of the /api/settings admin contract (M9 lockstep). Source of truth:
//   - Services/Dtos/SettingsAdminDtos.cs (the C# records — admin-scoped names)
//   - tests/.../Fixtures/Settings/{household-requests,feedback}.json (byte-locked tripwires)
//   - Endpoints/SettingsAdminEndpoints.cs (request DTOs + the 403/IDOR contract)
//
// ⚠ CASING: all keys camelCase.
// ⚠ ENUMS (R-C10): status / type are string unions matching the camelCase wire
//   values the global JsonStringEnumConverter emits.
// ⚠ DATES (X5): requestedAt / reviewedAt / createdAt are FULL ISO-8601 instants
//   (UTC, "…Z") — render local via new Date(iso).toLocaleString() (NOT noon-UTC).
// ─────────────────────────────────────────────────────────────────────────

/** The per-page context the Razor host hands the island via data- attributes. */
export interface ShellContext {
  householdId: number;
  userId: number;
  userName: string;
  view: 'households' | 'feedback';
}

// ── Household requests (Fixtures/Settings/household-requests.json) ───────────

export type HouseholdRequestStatus = 'pending' | 'approved' | 'rejected';

export interface HouseholdRequestDto {
  id: number;
  householdName: string;
  displayName: string;
  email: string;
  status: HouseholdRequestStatus;
  /** full ISO-8601 instant (UTC). */
  requestedAt: string;
  /** full ISO-8601 instant (UTC), or null until reviewed. */
  reviewedAt: string | null;
  reviewedBy: string | null;
  rejectionReason: string | null;
}

export interface HouseholdSummaryDto {
  householdId: number;
  name: string;
  memberCount: number;
  /** full ISO-8601 instant (UTC). */
  createdAt: string;
}

export interface HouseholdRequestsDto {
  requests: HouseholdRequestDto[];
  households: HouseholdSummaryDto[];
}

// ── Feedback (Fixtures/Settings/feedback.json) ──────────────────────────────

export type FeedbackType = 'bug' | 'featureRequest' | 'general';

export interface FeedbackDto {
  id: number;
  type: FeedbackType;
  message: string;
  currentPage: string | null;
  isRead: boolean;
  isResolved: boolean;
  /** full ISO-8601 instant (UTC). */
  createdAt: string;
  /** Author 3-way (R-C6): live user → name; deleted user → null + authorDeleted; anonymous → null + !authorDeleted. */
  authorName: string | null;
  authorDeleted: boolean;
}

export interface FeedbackListDto {
  isSiteAdmin: boolean;
  items: FeedbackDto[];
}
