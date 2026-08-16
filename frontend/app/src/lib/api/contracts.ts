// ─────────────────────────────────────────────────────────────────────────
// The TS half of the M9 wire contract.
//
// The C# side is pinned: each DTO that HAS a fixture under
// tests/FamilyCoordinationApp.Tests/Fixtures/ is serialized and byte-compared to it.
// Every island's types.ts claims in a comment to mirror one of those fixtures; this
// file makes the claim checkable. Each entry below states a fixture, the interface it
// mirrors, and a Shape; `Expect<Equals<…>>` holds the Shape to the interface at compile
// time and contracts.test.ts holds it to the fixture at run time.
//
// Adding a JSON fixture: add its pin here, or name it in SERVER_ONLY_FIXTURES. The
// test fails on any JSON fixture that is in neither, so the pin cannot silently fall
// behind the fixture tree.
//
// Scope — this is a fixture-driven guard, not complete SPA-response coverage:
//   - only .json is walked; the .txt/.srv1 fixtures are parser inputs, not payloads
//   - only response DTOs that HAVE a fixture. Request/write bodies and fixture-less
//     responses (ShoppingListSummaryDto, RecipeWriteRequest, DigestSettingsView, …)
//     are unpinned — they need a C# fixture before they can be pinned here.
// ─────────────────────────────────────────────────────────────────────────
import {
  arrayOf,
  bool,
  nullable,
  num,
  objectOf,
  oneOf,
  str,
  type Equals,
  type Expect,
  type Infer,
  type Shape,
} from './shape';
import type {
  ChoreBoardDto,
  ChoreEquityDto,
  ChoreLedgerDto,
  ChoreRecapDto,
  RecapWeekDto,
} from '../chores/lib/types';
import type { DashboardDto } from '../dashboard/lib/types';
import type { MealPlanBoardDto } from '../meal-plan/lib/types';
import type { RecipeFullDto, RecipeListDto } from '../recipes/lib/types';
import type { CategoryListDto, MemberListDto } from '../settings/lib/types';
import type { ConnectionsDto } from '../connections/lib/types';
import type { FeedbackListDto, HouseholdRequestsDto } from '../admin/lib/types';
import type { ShoppingListDto } from '../shopping-list/lib/types';

// ── Chores: board (Fixtures/ChoreBoard/board.json) ──────────────────────────

const capacityTier = oneOf('Full', 'Reduced', 'Minimal');

const choreBoard = objectOf({
  chores: arrayOf(
    objectOf({
      id: num,
      name: str,
      icon: str,
      description: nullable(str),
      roomIds: arrayOf(num),
      recurrenceMode: oneOf('OneOff', 'Fixed', 'Flexible'),
      intervalDays: nullable(num),
      daysOfWeek: nullable(str),
      anchorDate: nullable(str),
      dueState: oneOf('notDue', 'dueToday', 'overdue', 'scheduled'),
      colorTier: oneOf('fresh', 'mid', 'due', 'overdue'),
      nextDueAt: nullable(str),
      snoozedUntil: nullable(str),
      isSnoozed: bool,
      isClaimStale: bool,
      effortTier: oneOf('Quick', 'Standard', 'BigJob'),
      effortPoints: num,
      ownerUserId: nullable(num),
      assigneeUserId: nullable(num),
      assignmentKind: oneOf('none', 'assigned', 'claimed'),
      claimedAt: nullable(str),
      lastCompletedAt: nullable(str),
      photoPath: nullable(str),
      version: num,
      requiredCount: num,
      completedCount: num,
      roster: arrayOf(objectOf({ userId: num, state: oneOf('assigned', 'in', 'done') })),
      subtasks: arrayOf(
        objectOf({
          id: num,
          title: str,
          isDone: bool,
          sortOrder: num,
          completedByUserId: nullable(num),
          completedAt: nullable(str),
        })
      ),
    })
  ),
  rooms: arrayOf(
    objectOf({
      roomId: nullable(num),
      name: str,
      icon: str,
      photoPath: nullable(str),
      sortOrder: num,
      choreCount: num,
      dueCount: num,
      status: oneOf('clean', 'attention', 'needsWork'),
    })
  ),
  members: arrayOf(
    objectOf({ userId: num, displayName: str, initials: str, pictureUrl: nullable(str) })
  ),
  needsAttentionChoreIds: arrayOf(num),
  userDefaultView: nullable(str),
  callerCapacityTier: nullable(capacityTier),
});
export type _ChoreBoard = Expect<Equals<Infer<typeof choreBoard>, ChoreBoardDto>>;

// ── Chores: equity (Fixtures/ChoreEquity/equity.json) ───────────────────────

const choreEquity = objectOf({
  window: oneOf('week', 'all'),
  totalPoints: num,
  totalCompletions: num,
  equalSharePct: num,
  fallingBehindCount: num,
  upForGrabsCount: num,
  members: arrayOf(
    objectOf({
      userId: num,
      displayName: str,
      initials: str,
      pictureUrl: nullable(str),
      points: num,
      completions: num,
      sharePct: num,
      expectedSharePct: num,
    })
  ),
  planning: arrayOf(
    objectOf({
      userId: num,
      displayName: str,
      choresSetUp: num,
      recipesAdded: num,
      listItemsCurated: num,
      handOffs: num,
    })
  ),
  callerCapacityTier: nullable(capacityTier),
});
export type _ChoreEquity = Expect<Equals<Infer<typeof choreEquity>, ChoreEquityDto>>;

// ── Chores: recap (Fixtures/ChoreRecap/{recap,recap-current}.json) ──────────

const recapMemberLine = objectOf({ displayName: str, points: num, sharePct: num });

const recapWeek = objectOf({
  weekStartLocal: str,
  headline: str,
  totalCompletions: num,
  totalPoints: num,
  distribution: arrayOf(recapMemberLine),
  fallingBehind: arrayOf(str),
  upForGrabsCount: num,
});
export type _RecapWeek = Expect<Equals<Infer<typeof recapWeek>, RecapWeekDto>>;

/** Shared by the recap and ledger payloads — one C# record, one Shape. */
const goneQuiet = objectOf({
  choreName: str,
  cadenceLabel: str,
  lastCompletedLocalDate: nullable(str),
  reason: oneOf('snoozed', 'slipped'),
});

const choreRecap = objectOf({
  current: recapWeek,
  trend: arrayOf(
    objectOf({
      weekStartLocal: str,
      totalCompletions: num,
      totalPoints: num,
      isCurrent: bool,
      distribution: arrayOf(recapMemberLine),
    })
  ),
  milestones: objectOf({
    bestWeek: nullable(
      objectOf({ weekStartLocal: str, totalCompletions: num, totalPoints: num })
    ),
    longestActiveStreakWeeks: num,
    firstEvers: arrayOf(objectOf({ choreName: str, localDate: str })),
    seasonTotalCompletions: num,
    seasonTotalPoints: num,
  }),
  keptMoments: arrayOf(
    objectOf({ localDate: str, choreName: str, note: nullable(str), hasPhoto: bool })
  ),
  whatGotTended: arrayOf(objectOf({ roomName: str, completions: num })),
  goneQuiet: arrayOf(goneQuiet),
});
export type _ChoreRecap = Expect<Equals<Infer<typeof choreRecap>, ChoreRecapDto>>;

// ── Chores: ledger (Fixtures/ChoreHistory/ledger.json) ──────────────────────

const choreLedger = objectOf({
  windowStartLocal: str,
  windowEndLocal: str,
  events: arrayOf(
    objectOf({
      choreName: str,
      doerDisplayName: str,
      localDate: str,
      points: num,
      note: nullable(str),
      hasPhoto: bool,
    })
  ),
  weeks: arrayOf(objectOf({ weekStartLocal: str, completions: num })),
  ghosts: arrayOf(
    objectOf({ choreName: str, expectedLocalDate: str, reason: oneOf('snoozed', 'slipped') })
  ),
  goneQuiet: arrayOf(goneQuiet),
});
export type _ChoreLedger = Expect<Equals<Infer<typeof choreLedger>, ChoreLedgerDto>>;

// ── Dashboard (Fixtures/Dashboard/dashboard.json) ───────────────────────────

const mealType = oneOf('breakfast', 'lunch', 'dinner', 'snack');

const dashboard = objectOf({
  greetingName: str,
  householdName: str,
  today: str,
  chores: objectOf({ activeTotal: num, overdue: num, dueToday: num, upForGrabs: num }),
  shopping: objectOf({ remaining: num, checked: num, total: num }),
  todaysMeals: arrayOf(objectOf({ mealType, displayName: str })),
});
export type _Dashboard = Expect<Equals<Infer<typeof dashboard>, DashboardDto>>;

// ── Meal plan (Fixtures/MealPlanBoard/board.json) ───────────────────────────

const recipeType = oneOf(
  'main',
  'side',
  'appetizer',
  'dessert',
  'beverage',
  'sauce',
  'breakfast',
  'snack',
  'other'
);

const mealPlanBoard = objectOf({
  weekStartDate: str,
  mealPlanId: nullable(num),
  entries: arrayOf(
    objectOf({
      mealPlanId: num,
      entryId: num,
      date: str,
      mealType,
      recipe: nullable(
        objectOf({
          recipeId: num,
          name: str,
          imagePath: nullable(str),
          recipeType,
          servings: nullable(num),
        })
      ),
      customMealName: nullable(str),
      notes: nullable(str),
      servings: nullable(num),
    })
  ),
});
export type _MealPlanBoard = Expect<Equals<Infer<typeof mealPlanBoard>, MealPlanBoardDto>>;

// ── Recipes (Fixtures/RecipeList/list.json, Fixtures/RecipeFull/recipe.json) ─

const recipeList = objectOf({
  recipes: arrayOf(
    objectOf({
      recipeId: num,
      name: str,
      recipeType,
      imagePath: nullable(str),
      hasSourceUrl: bool,
      createdByName: nullable(str),
      createdByPictureUrl: nullable(str),
      ingredientPreview: arrayOf(str),
      ingredientCount: num,
    })
  ),
  favoriteRecipeIds: arrayOf(num),
});
export type _RecipeList = Expect<Equals<Infer<typeof recipeList>, RecipeListDto>>;

const recipeFull = objectOf({
  recipeId: num,
  version: num,
  name: str,
  recipeType,
  description: nullable(str),
  instructions: nullable(str),
  instructionsHtml: str,
  imagePath: nullable(str),
  sourceUrl: nullable(str),
  prepTimeMinutes: nullable(num),
  cookTimeMinutes: nullable(num),
  servings: nullable(num),
  createdByName: nullable(str),
  createdByPictureUrl: nullable(str),
  sharedFromHouseholdName: nullable(str),
  ingredients: arrayOf(
    objectOf({
      ingredientId: num,
      quantity: nullable(num),
      unit: nullable(str),
      name: str,
      category: str,
      notes: nullable(str),
      groupName: nullable(str),
      sortOrder: num,
    })
  ),
});
export type _RecipeFull = Expect<Equals<Infer<typeof recipeFull>, RecipeFullDto>>;

// ── Settings (Fixtures/Settings/{categories,members}.json) ──────────────────

const category = objectOf({
  categoryId: num,
  name: str,
  iconEmoji: nullable(str),
  color: str,
  isDefault: bool,
  sortOrder: num,
  deletedAt: nullable(str),
});

const categoryList = objectOf({ active: arrayOf(category), deleted: arrayOf(category) });
export type _CategoryList = Expect<Equals<Infer<typeof categoryList>, CategoryListDto>>;

const memberList = objectOf({
  currentUserId: num,
  members: arrayOf(
    objectOf({ userId: num, email: str, displayName: nullable(str), isWhitelisted: bool })
  ),
});
export type _MemberList = Expect<Equals<Infer<typeof memberList>, MemberListDto>>;

// ── Connections (Fixtures/Settings/connections.json) ────────────────────────

const connections = objectOf({
  activeInvite: nullable(objectOf({ code: str, expiresAt: str })),
  connected: arrayOf(objectOf({ householdId: num, householdName: str, connectedAt: str })),
});
export type _Connections = Expect<Equals<Infer<typeof connections>, ConnectionsDto>>;

// ── Admin (Fixtures/Settings/{household-requests,feedback}.json) ────────────

const householdRequests = objectOf({
  requests: arrayOf(
    objectOf({
      id: num,
      householdName: str,
      displayName: str,
      email: str,
      status: oneOf('pending', 'approved', 'rejected'),
      requestedAt: str,
      reviewedAt: nullable(str),
      reviewedBy: nullable(str),
      rejectionReason: nullable(str),
    })
  ),
  households: arrayOf(
    objectOf({ householdId: num, name: str, memberCount: num, createdAt: str })
  ),
});
export type _HouseholdRequests = Expect<
  Equals<Infer<typeof householdRequests>, HouseholdRequestsDto>
>;

const feedbackList = objectOf({
  isSiteAdmin: bool,
  items: arrayOf(
    objectOf({
      id: num,
      type: oneOf('bug', 'featureRequest', 'general'),
      message: str,
      currentPage: nullable(str),
      isRead: bool,
      isResolved: bool,
      createdAt: str,
      authorName: nullable(str),
      authorDeleted: bool,
    })
  ),
});
export type _FeedbackList = Expect<Equals<Infer<typeof feedbackList>, FeedbackListDto>>;

// ── Shopping list (Fixtures/ShoppingList/list.json) ─────────────────────────

const shoppingList = objectOf({
  id: num,
  name: str,
  isFavorite: bool,
  isArchived: bool,
  items: arrayOf(
    objectOf({
      id: num,
      name: str,
      quantity: nullable(num),
      unit: nullable(str),
      category: str,
      isChecked: bool,
      checkedAt: nullable(str),
      sortOrder: num,
      addedByName: nullable(str),
      addedByInitials: nullable(str),
      addedByPictureUrl: nullable(str),
      version: num,
    })
  ),
});
export type _ShoppingList = Expect<Equals<Infer<typeof shoppingList>, ShoppingListDto>>;

// ── The manifest ────────────────────────────────────────────────────────────

export interface ContractPin {
  /** Path under tests/FamilyCoordinationApp.Tests/Fixtures/. */
  readonly fixture: string;
  /** The interface the fixture is pinned to, for the failure message. */
  readonly type: string;
  readonly shape: Shape<unknown>;
}

export const CONTRACT_PINS: readonly ContractPin[] = [
  { fixture: 'ChoreBoard/board.json', type: 'ChoreBoardDto', shape: choreBoard },
  { fixture: 'ChoreEquity/equity.json', type: 'ChoreEquityDto', shape: choreEquity },
  { fixture: 'ChoreHistory/ledger.json', type: 'ChoreLedgerDto', shape: choreLedger },
  { fixture: 'ChoreRecap/recap.json', type: 'ChoreRecapDto', shape: choreRecap },
  { fixture: 'ChoreRecap/recap-current.json', type: 'RecapWeekDto', shape: recapWeek },
  { fixture: 'Dashboard/dashboard.json', type: 'DashboardDto', shape: dashboard },
  { fixture: 'MealPlanBoard/board.json', type: 'MealPlanBoardDto', shape: mealPlanBoard },
  { fixture: 'RecipeFull/recipe.json', type: 'RecipeFullDto', shape: recipeFull },
  { fixture: 'RecipeList/list.json', type: 'RecipeListDto', shape: recipeList },
  { fixture: 'Settings/categories.json', type: 'CategoryListDto', shape: categoryList },
  { fixture: 'Settings/connections.json', type: 'ConnectionsDto', shape: connections },
  { fixture: 'Settings/feedback.json', type: 'FeedbackListDto', shape: feedbackList },
  {
    fixture: 'Settings/household-requests.json',
    type: 'HouseholdRequestsDto',
    shape: householdRequests,
  },
  { fixture: 'Settings/members.json', type: 'MemberListDto', shape: memberList },
  { fixture: 'ShoppingList/list.json', type: 'ShoppingListDto', shape: shoppingList },
];

/** Fixtures with no SPA consumer: they pin a payload the server sends or receives elsewhere. */
export const SERVER_ONLY_FIXTURES: readonly string[] = [
  'Digest/discord-payload.json', // outbound Discord webhook body
  'Gemini/malformed-response.json', // inbound Gemini API responses
  'Gemini/no-recipe-found.json',
  'Gemini/successful-extraction.json',
  'YtDlp/no-captions.meta.json', // yt-dlp metadata output
  'YtDlp/sample-video.meta.json',
];
