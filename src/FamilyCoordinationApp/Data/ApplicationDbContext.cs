using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Tenancy;

namespace FamilyCoordinationApp.Data;

/// <summary>
/// The app's EF context. <c>partial</c> so the tenancy work (fca-household-scope) can add its read filter
/// (WP-02) and write step (WP-03) in their own files.
/// </summary>
public partial class ApplicationDbContext : DbContext
{
    /// <summary>
    /// Options-only: a permanently <see cref="TenantState.Unfiltered"/> tenant with default options (D12). Kept for
    /// the unit tests' direct construction and EF design-time. Architecture-guard fact 3 bans every construction form
    /// it names (explicit and target-typed <c>new</c>, typed lambdas, activation, subclassing, and any mention of this
    /// context's options type) in <c>src</c> outside <see cref="TenantDbContextFactory"/>, so production code does not
    /// reach Unfiltered through it, within the guard's stated limits.
    /// </summary>
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : this(options, TenantContext.CreateUnfiltered(), new TenancyOptions()) { }

    /// <summary>The tenant-aware constructor, called only by <see cref="TenantDbContextFactory"/>.</summary>
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ITenantContext tenant, TenancyOptions tenancy)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(tenancy);
        Tenant = tenant;
        // A snapshot, taken here rather than in the factory so a hand-built context gets one too.
        Tenancy = new TenancySettings(tenancy);
    }

    /// <summary>The creating scope's tenant. Services reach <c>RunAs</c>/<c>AllowCrossTenantWrite</c> through here.</summary>
    internal ITenantContext Tenant { get; }

    /// <summary>
    /// An immutable snapshot of the <c>Tenancy</c> options, taken at construction. Nothing can change it afterwards:
    /// not this context, another context, or a later change to the <c>IOptions</c> source.
    /// </summary>
    internal TenancySettings Tenancy { get; }

    /// <summary>The clock WP-03's write step stamps audit fields from (D16). Set by the factory.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// The Tenant filter's bypass term (D12, D14, D15): the kill switch is off, the tenant is Unfiltered, or the
    /// tenant is out of request with <c>OutOfRequest=Unfiltered</c>. The out-of-request mode is read through the
    /// tenant, which is authoritative (WP-01). Static so <c>TenantFilterSpikeTests</c> pins this exact code.
    /// </summary>
    internal static bool BypassTenantFilterFor(ITenantContext tenant, TenancySettings tenancy) =>
        !tenancy.EnforceFilter
        || tenant.IsUnfiltered
        || (tenant.IsOutOfRequest && tenant.OutOfRequestMode == OutOfRequestMode.Unfiltered);

    /// <summary>
    /// The Tenant filter's household term. EF evaluates every filter parameter even when the bypass term
    /// short-circuits the <c>OR</c>, so a bypassing context answers 0 (unused) instead of throwing, which keeps the
    /// D14 rollback working. Otherwise the Caller or System household, or <see cref="TenantNotSetException"/>.
    /// </summary>
    internal static int CurrentHouseholdIdFor(ITenantContext tenant, TenancySettings tenancy)
    {
        if (BypassTenantFilterFor(tenant, tenancy)) return 0;
        if (tenant.State is TenantState.Caller or TenantState.System && tenant.HouseholdId is { } householdId)
        {
            return householdId;
        }
        throw TenantNotSetException.For(tenant);
    }

    public DbSet<Household> Households => Set<Household>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeIngredient> RecipeIngredients => Set<RecipeIngredient>();
    public DbSet<MealPlan> MealPlans => Set<MealPlan>();
    public DbSet<MealPlanEntry> MealPlanEntries => Set<MealPlanEntry>();
    public DbSet<ShoppingList> ShoppingLists => Set<ShoppingList>();
    public DbSet<ShoppingListItem> ShoppingListItems => Set<ShoppingListItem>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<RecipeDraft> RecipeDrafts => Set<RecipeDraft>();
    public DbSet<Feedback> Feedbacks => Set<Feedback>();
    public DbSet<HouseholdRequest> HouseholdRequests => Set<HouseholdRequest>();
    public DbSet<UserFavorite> UserFavorites => Set<UserFavorite>();
    public DbSet<HouseholdConnection> HouseholdConnections => Set<HouseholdConnection>();
    public DbSet<HouseholdInvite> HouseholdInvites => Set<HouseholdInvite>();
    public DbSet<HouseholdCalendarToken> HouseholdCalendarTokens => Set<HouseholdCalendarToken>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Chore> Chores => Set<Chore>();
    public DbSet<ChoreRoom> ChoreRooms => Set<ChoreRoom>();
    public DbSet<ChoreCompletion> ChoreCompletions => Set<ChoreCompletion>();
    public DbSet<ChoreEvent> ChoreEvents => Set<ChoreEvent>();
    public DbSet<ChoreSnoozeEvent> ChoreSnoozeEvents => Set<ChoreSnoozeEvent>();
    public DbSet<ChoreParticipationEvent> ChoreParticipationEvents => Set<ChoreParticipationEvent>();
    public DbSet<ChoreSubtask> ChoreSubtasks => Set<ChoreSubtask>();
    public DbSet<HouseholdChoreDigestSettings> ChoreDigestSettings => Set<HouseholdChoreDigestSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
