using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Pure, stateless, parameterless-ctor calculator that aggregates the household's already-attributed
/// system-building / coordination labor into per-member, ALL-TIME, un-blended labeled tallies (Phase 15
/// planning lanes). Mirrors the <see cref="ChoreEquityCalculator"/> idiom: it receives PRE-FETCHED,
/// household-scoped sequences (the caller is responsible for HouseholdId scoping — M1) plus the member
/// list, and returns one <see cref="MemberPlanningDto"/> per member.
/// <para>
/// There is NO window math and NO <see cref="DateTime.UtcNow"/>/<see cref="DateTime.Now"/> reached inside
/// — planning is a STOCK (durable footprint), counted all-time regardless of the equity <c>window</c> param
/// (D5). Four lanes, each a plain source-noun count, NEVER summed and NEVER blended with physical points
/// (MN4):
/// </para>
/// <list type="bullet">
///   <item><c>choresSetUp</c> — <see cref="Chore.EnteredByUserId"/>.</item>
///   <item><c>recipesAdded</c> — non-deleted <see cref="Recipe"/> with a non-null author, credited to
///   <see cref="Recipe.CreatedByUserId"/>.</item>
///   <item><c>listItemsCurated</c> — manually-added <see cref="ShoppingListItem"/> with a non-null author,
///   credited to <see cref="ShoppingListItem.AddedByUserId"/>.</item>
///   <item><c>handOffs</c> — <see cref="ChoreEvent"/> hand-offs to ANOTHER member, credited to the actor
///   (see the invariant on the loop below, D6).</item>
/// </list>
/// <para>
/// Rows with a null author id are SKIPPED (no credit for unattributed rows); only members in the supplied
/// list are credited. Every supplied member appears with a (possibly all-zero) row — mirrors the equity
/// calculator's "all members appear" rule.
/// </para>
/// </summary>
public class ChorePlanningCalculator
{
    /// <summary>
    /// Compute the per-member, all-time planning tallies. All inputs are already household-scoped by the
    /// caller (M1); the calculator counts only what it is handed and credits only members in
    /// <paramref name="members"/>.
    /// </summary>
    public IReadOnlyList<MemberPlanningDto> Compute(
        IReadOnlyList<MemberDto> members,
        IEnumerable<Chore> chores,
        IEnumerable<Recipe> recipes,
        IEnumerable<ShoppingListItem> items,
        IEnumerable<ChoreEvent> events)
    {
        // Pre-seed a zero row for every supplied member so members with no activity still appear.
        var choresSetUp = new Dictionary<int, int>();
        var recipesAdded = new Dictionary<int, int>();
        var listItems = new Dictionary<int, int>();
        var handOffs = new Dictionary<int, int>();

        var memberIds = new HashSet<int>(members.Count);
        foreach (var m in members)
        {
            memberIds.Add(m.UserId);
        }

        // chores set up — EnteredByUserId (non-nullable on Chore; still gate on membership).
        foreach (var chore in chores)
        {
            Credit(choresSetUp, memberIds, chore.EnteredByUserId);
        }

        // recipes added — non-deleted recipes with a non-null author.
        foreach (var recipe in recipes)
        {
            if (recipe.IsDeleted || recipe.CreatedByUserId is not { } authorId)
            {
                continue;
            }

            Credit(recipesAdded, memberIds, authorId);
        }

        // list items curated — manually-added items with a non-null author.
        foreach (var item in items)
        {
            if (!item.IsManuallyAdded || item.AddedByUserId is not { } authorId)
            {
                continue;
            }

            Credit(listItems, memberIds, authorId);
        }

        // hand-offs — INVARIANT (D6): count only ChoreEvent rows where Type == HandedOff AND the target is
        // ANOTHER member (TargetUserId != null && TargetUserId != ActorUserId), credited to the ACTOR.
        // The HandedOff filter is PRIMARY (excludes Claimed/Dropped/AutoReleased self-noise); the
        // actor != target clause is the guard against a self-hand-off slipping credit to everyone equally.
        foreach (var ev in events)
        {
            if (ev.Type == ChoreEventType.HandedOff
                && ev.TargetUserId is { } target
                && target != ev.ActorUserId)
            {
                Credit(handOffs, memberIds, ev.ActorUserId);
            }
        }

        var result = new List<MemberPlanningDto>(members.Count);
        foreach (var m in members)
        {
            result.Add(new MemberPlanningDto(
                UserId: m.UserId,
                DisplayName: m.DisplayName,
                ChoresSetUp: choresSetUp.GetValueOrDefault(m.UserId),
                RecipesAdded: recipesAdded.GetValueOrDefault(m.UserId),
                ListItemsCurated: listItems.GetValueOrDefault(m.UserId),
                HandOffs: handOffs.GetValueOrDefault(m.UserId)));
        }

        return result;
    }

    /// <summary>Credit one to <paramref name="authorId"/> in <paramref name="counts"/> iff that author is a
    /// supplied member; unattributed/non-member rows are silently skipped.</summary>
    private static void Credit(Dictionary<int, int> counts, HashSet<int> memberIds, int authorId)
    {
        if (!memberIds.Contains(authorId))
        {
            return;
        }

        counts[authorId] = counts.GetValueOrDefault(authorId) + 1;
    }
}
