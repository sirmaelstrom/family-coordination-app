// ─────────────────────────────────────────────────────────────────────────
// The TS half of the M9 wire contract.
//
// The C# side is pinned: each DTO that HAS a fixture under
// tests/FamilyCoordinationApp.Tests/Fixtures/ is serialized and byte-compared to it.
// Every island's types.ts claims in a comment to mirror one of those fixtures; this
// file makes the claim checkable. `PinnedTypes` lists the island type each pin holds
// its Shape to; `SHAPES` is a mapped record over it, and Shape's invariance makes each
// entry a compile-time exactness assertion — a pin cannot exist without its assertion,
// because the assertion IS the manifest entry. contracts.test.ts holds each Shape to
// its fixture at run time.
//
// Adding a JSON fixture: add its type to PinnedTypes — the compiler then requires a
// SHAPES entry and a PIN_FIXTURES entry — or name it in SERVER_ONLY_FIXTURES. The
// test fails on any JSON fixture that is in neither, so the pin cannot silently fall
// behind the fixture tree.
//
// Scope — this is a fixture-driven guard, not complete SPA-response coverage:
//   - only .json is walked; the .txt/.srv1 fixtures are parser inputs, not payloads
//   - request/write bodies ARE pinned where a fixture exists (RecipeWriteRequest,
//     SaveDraftRequest, CategoryWriteRequest); a payload without a C# fixture is
//     still unpinned until one is checked in.
// ─────────────────────────────────────────────────────────────────────────
import { arrayOf, bool, nullable, num, objectOf, oneOf, str, type Shape } from './shape';
import type {
  ChoreBoardDto,
  ChoreEquityDto,
  ChoreLedgerDto,
  ChoreRecapDto,
  DigestSettingsView,
  RecapWeekDto,
} from '../chores/lib/types';
import type { DashboardDto } from '../dashboard/lib/types';
import type { MealPlanBoardDto } from '../meal-plan/lib/types';
import type {
  RecipeFullDto,
  RecipeListDto,
  RecipeWriteRequest,
  SaveDraftRequest,
} from '../recipes/lib/types';
import type {
  CategoryListDto,
  CategoryWriteRequest,
  MemberListDto,
} from '../settings/lib/types';
import type { ConnectionsDto } from '../connections/lib/types';
import type { FeedbackListDto, HouseholdRequestsDto } from '../admin/lib/types';
import type { ShoppingListDto, ShoppingListSummaryDto } from '../shopping-list/lib/types';

// ── Wire-enum vocabularies (Fixtures/Enums/wire-enums.json) ────────────────
//
// Every C# enum that reaches the wire, with its serialized member list. The C# side
// pins this to Enum.GetValues (WireEnumContractTests → the wire-enums fixture) and
// contracts.test.ts asserts list-equality here — so a NEW C# enum member fails the C#
// suite until the fixture grows, and the grown fixture fails npm test until the list
// here (and the island union it feeds) grows too. Membership-only oneOf checks cannot
// see a new member; this list-equals pin is what closes that hole.
//
// Two serialization groups, matching how each field actually reaches the wire:
//   - enum-typed DTO fields → JsonStringEnumConverter(CamelCase) → camelCase strings
//   - string-typed DTO fields carrying Enum.ToString() (ChoreBoardService's
//     recurrenceMode/effortTier) → PascalCase strings
// Member order mirrors the C# declaration order.
export const WIRE_ENUMS = {
  RecipeType: [
    'main',
    'side',
    'appetizer',
    'dessert',
    'beverage',
    'sauce',
    'breakfast',
    'snack',
    'other',
  ],
  MealType: ['breakfast', 'lunch', 'dinner', 'snack'],
  DueState: ['notDue', 'dueToday', 'overdue', 'scheduled'],
  ColorTier: ['fresh', 'mid', 'due', 'overdue'],
  AssignmentKind: ['none', 'assigned', 'claimed'],
  RosterState: ['assigned', 'in', 'done'],
  RoomRollupStatus: ['clean', 'attention', 'needsWork'],
  EquityWindow: ['week', 'all'],
  FeedbackType: ['bug', 'featureRequest', 'general'],
  HouseholdRequestStatus: ['pending', 'approved', 'rejected'],
  DigestCadence: ['weekly'],
  DayOfWeek: ['sunday', 'monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday'],
  RecurrenceMode: ['OneOff', 'Fixed', 'Flexible'],
  EffortTier: ['Quick', 'Standard', 'BigJob'],
} as const;

export const WIRE_ENUM_FIXTURE = 'Enums/wire-enums.json';

// ── Chores: board (Fixtures/ChoreBoard/board.json) ──────────────────────────

// NOT an enum: User.PhysicalCapacityTier is a string column with this vocabulary, so
// there is no Enum.GetValues to pin it to until the A9 ChoreCapacity module exists.
const capacityTier = oneOf('Full', 'Reduced', 'Minimal');

const choreBoard = objectOf({
  chores: arrayOf(
    objectOf({
      id: num,
      name: str,
      icon: str,
      description: nullable(str),
      roomIds: arrayOf(num),
      recurrenceMode: oneOf(...WIRE_ENUMS.RecurrenceMode),
      intervalDays: nullable(num),
      daysOfWeek: nullable(str),
      anchorDate: nullable(str),
      dueState: oneOf(...WIRE_ENUMS.DueState),
      colorTier: oneOf(...WIRE_ENUMS.ColorTier),
      nextDueAt: nullable(str),
      snoozedUntil: nullable(str),
      isSnoozed: bool,
      isClaimStale: bool,
      effortTier: oneOf(...WIRE_ENUMS.EffortTier),
      effortPoints: num,
      ownerUserId: nullable(num),
      assigneeUserId: nullable(num),
      assignmentKind: oneOf(...WIRE_ENUMS.AssignmentKind),
      claimedAt: nullable(str),
      lastCompletedAt: nullable(str),
      photoPath: nullable(str),
      version: num,
      requiredCount: num,
      completedCount: num,
      roster: arrayOf(objectOf({ userId: num, state: oneOf(...WIRE_ENUMS.RosterState) })),
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
      status: oneOf(...WIRE_ENUMS.RoomRollupStatus),
    })
  ),
  members: arrayOf(
    objectOf({ userId: num, displayName: str, initials: str, pictureUrl: nullable(str) })
  ),
  needsAttentionChoreIds: arrayOf(num),
  userDefaultView: nullable(str),
  callerCapacityTier: nullable(capacityTier),
});

// ── Chores: equity (Fixtures/ChoreEquity/equity.json) ───────────────────────

const choreEquity = objectOf({
  window: oneOf(...WIRE_ENUMS.EquityWindow),
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

/** Shared by the recap and ledger payloads — one C# record, one Shape. */
const goneQuiet = objectOf({
  choreName: str,
  cadenceLabel: str,
  lastCompletedLocalDate: nullable(str),
  // NOT an enum: GoneQuietDto.Reason is a string field documented as this vocabulary.
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
    // Reason: string field, not an enum — same vocabulary as goneQuiet above.
    objectOf({ choreName: str, expectedLocalDate: str, reason: oneOf('snoozed', 'slipped') })
  ),
  goneQuiet: arrayOf(goneQuiet),
});

// ── Chores: digest settings (Fixtures/Settings/digest-settings.json) ────────

const digestSettingsView = objectOf({
  enabled: bool,
  cadence: oneOf(...WIRE_ENUMS.DigestCadence),
  sendDayOfWeek: oneOf(...WIRE_ENUMS.DayOfWeek),
  sendHourLocal: num,
  hasWebhook: bool,
  webhookHint: nullable(str),
  lastSentAt: nullable(str),
});

// ── Dashboard (Fixtures/Dashboard/dashboard.json) ───────────────────────────

const mealType = oneOf(...WIRE_ENUMS.MealType);

const dashboard = objectOf({
  greetingName: str,
  householdName: str,
  today: str,
  chores: objectOf({ activeTotal: num, overdue: num, dueToday: num, upForGrabs: num }),
  shopping: objectOf({ remaining: num, checked: num, total: num }),
  todaysMeals: arrayOf(objectOf({ mealType, displayName: str })),
});

// ── Meal plan (Fixtures/MealPlanBoard/board.json) ───────────────────────────

const recipeType = oneOf(...WIRE_ENUMS.RecipeType);

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
      version: num,
    })
  ),
});

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

// ── Recipes: write bodies (Fixtures/RecipeWrite/request.json, RecipeDraft/save-request.json)

/** One shape for the C# twins RecipeIngredientWrite and DraftIngredientBody (same 7 fields). */
const writeIngredient = objectOf({
  name: str,
  quantity: nullable(num),
  unit: nullable(str),
  category: str,
  notes: nullable(str),
  groupName: nullable(str),
  sortOrder: num,
});

const recipeWriteRequest = objectOf({
  name: str,
  description: nullable(str),
  instructions: nullable(str),
  sourceUrl: nullable(str),
  servings: nullable(num),
  prepTimeMinutes: nullable(num),
  cookTimeMinutes: nullable(num),
  recipeType,
  imagePath: nullable(str),
  ingredients: arrayOf(writeIngredient),
  version: nullable(num),
});

const saveDraftRequest = objectOf({
  recipeId: nullable(num),
  name: str,
  description: nullable(str),
  instructions: nullable(str),
  imagePath: nullable(str),
  sourceUrl: nullable(str),
  servings: nullable(num),
  prepTimeMinutes: nullable(num),
  cookTimeMinutes: nullable(num),
  ingredients: arrayOf(writeIngredient),
});

// ── Settings (Fixtures/Settings/{categories,members,category-write}.json) ───

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

const categoryWriteRequest = objectOf({ name: str, iconEmoji: nullable(str), color: str });

const memberList = objectOf({
  currentUserId: num,
  members: arrayOf(
    objectOf({ userId: num, email: str, displayName: nullable(str), isWhitelisted: bool })
  ),
});

// ── Connections (Fixtures/Settings/connections.json) ────────────────────────

const connections = objectOf({
  activeInvite: nullable(objectOf({ code: str, expiresAt: str })),
  connected: arrayOf(objectOf({ householdId: num, householdName: str, connectedAt: str })),
});

// ── Admin (Fixtures/Settings/{household-requests,feedback}.json) ────────────

const householdRequests = objectOf({
  requests: arrayOf(
    objectOf({
      id: num,
      householdName: str,
      displayName: str,
      email: str,
      status: oneOf(...WIRE_ENUMS.HouseholdRequestStatus),
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

const feedbackList = objectOf({
  isSiteAdmin: bool,
  items: arrayOf(
    objectOf({
      id: num,
      type: oneOf(...WIRE_ENUMS.FeedbackType),
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

// ── Shopping list (Fixtures/ShoppingList/{list,summaries}.json) ─────────────

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

const shoppingListSummaries = arrayOf(
  objectOf({ id: num, name: str, isFavorite: bool, itemCount: num, uncheckedCount: num })
);

// ── The manifest ────────────────────────────────────────────────────────────

/**
 * The island type each pin holds its Shape to. This is the structural link: SHAPES is a
 * mapped record over these keys, and Shape's invariance rejects any entry whose inferred
 * type is not exactly the declared one — so a pin cannot be added without its compile-time
 * assertion, and a Shape cannot drift from the interface it claims to mirror.
 */
interface PinnedTypes {
  ChoreBoardDto: ChoreBoardDto;
  ChoreEquityDto: ChoreEquityDto;
  ChoreLedgerDto: ChoreLedgerDto;
  ChoreRecapDto: ChoreRecapDto;
  RecapWeekDto: RecapWeekDto;
  DigestSettingsView: DigestSettingsView;
  DashboardDto: DashboardDto;
  MealPlanBoardDto: MealPlanBoardDto;
  RecipeFullDto: RecipeFullDto;
  RecipeListDto: RecipeListDto;
  RecipeWriteRequest: RecipeWriteRequest;
  SaveDraftRequest: SaveDraftRequest;
  CategoryListDto: CategoryListDto;
  CategoryWriteRequest: CategoryWriteRequest;
  ConnectionsDto: ConnectionsDto;
  FeedbackListDto: FeedbackListDto;
  HouseholdRequestsDto: HouseholdRequestsDto;
  MemberListDto: MemberListDto;
  ShoppingListDto: ShoppingListDto;
  'ShoppingListSummaryDto[]': ShoppingListSummaryDto[];
}

const SHAPES: { readonly [K in keyof PinnedTypes]: Shape<PinnedTypes[K]> } = {
  ChoreBoardDto: choreBoard,
  ChoreEquityDto: choreEquity,
  ChoreLedgerDto: choreLedger,
  ChoreRecapDto: choreRecap,
  RecapWeekDto: recapWeek,
  DigestSettingsView: digestSettingsView,
  DashboardDto: dashboard,
  MealPlanBoardDto: mealPlanBoard,
  RecipeFullDto: recipeFull,
  RecipeListDto: recipeList,
  RecipeWriteRequest: recipeWriteRequest,
  SaveDraftRequest: saveDraftRequest,
  CategoryListDto: categoryList,
  CategoryWriteRequest: categoryWriteRequest,
  ConnectionsDto: connections,
  FeedbackListDto: feedbackList,
  HouseholdRequestsDto: householdRequests,
  MemberListDto: memberList,
  ShoppingListDto: shoppingList,
  'ShoppingListSummaryDto[]': shoppingListSummaries,
};

const PIN_FIXTURES: { readonly [K in keyof PinnedTypes]: string } = {
  ChoreBoardDto: 'ChoreBoard/board.json',
  ChoreEquityDto: 'ChoreEquity/equity.json',
  ChoreLedgerDto: 'ChoreHistory/ledger.json',
  ChoreRecapDto: 'ChoreRecap/recap.json',
  RecapWeekDto: 'ChoreRecap/recap-current.json',
  DigestSettingsView: 'Settings/digest-settings.json',
  DashboardDto: 'Dashboard/dashboard.json',
  MealPlanBoardDto: 'MealPlanBoard/board.json',
  RecipeFullDto: 'RecipeFull/recipe.json',
  RecipeListDto: 'RecipeList/list.json',
  RecipeWriteRequest: 'RecipeWrite/request.json',
  SaveDraftRequest: 'RecipeDraft/save-request.json',
  CategoryListDto: 'Settings/categories.json',
  CategoryWriteRequest: 'Settings/category-write.json',
  ConnectionsDto: 'Settings/connections.json',
  FeedbackListDto: 'Settings/feedback.json',
  HouseholdRequestsDto: 'Settings/household-requests.json',
  MemberListDto: 'Settings/members.json',
  ShoppingListDto: 'ShoppingList/list.json',
  'ShoppingListSummaryDto[]': 'ShoppingList/summaries.json',
};

export interface ContractPin {
  /** Path under tests/FamilyCoordinationApp.Tests/Fixtures/. */
  readonly fixture: string;
  /** The interface the fixture is pinned to, for the failure message. */
  readonly type: string;
  // Shape<any>: the compile-time typing lives in SHAPES; this heterogeneous list erases it.
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  readonly shape: Shape<any>;
}

export const CONTRACT_PINS: readonly ContractPin[] = (
  Object.keys(SHAPES) as (keyof PinnedTypes)[]
).map((type) => ({ fixture: PIN_FIXTURES[type], type, shape: SHAPES[type] }));

/** Fixtures with no SPA consumer: they pin a payload the server sends or receives elsewhere. */
export const SERVER_ONLY_FIXTURES: readonly string[] = [
  'Digest/discord-payload.json', // outbound Discord webhook body
  'Gemini/malformed-response.json', // inbound Gemini API responses
  'Gemini/no-recipe-found.json',
  'Gemini/successful-extraction.json',
  'YtDlp/no-captions.meta.json', // yt-dlp metadata output
  'YtDlp/sample-video.meta.json',
];
