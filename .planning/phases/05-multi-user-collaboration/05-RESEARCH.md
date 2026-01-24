# Phase 5: Multi-User Collaboration - Research

**Researched:** 2026-01-24
**Domain:** Multi-user state synchronization, optimistic concurrency, user presence tracking
**Confidence:** HIGH

## Summary

Multi-user collaboration in Blazor Server with polling-based synchronization requires three core components: **optimistic concurrency** (EF Core + PostgreSQL xmin), **timed polling** (PeriodicTimer in background service), and **thread-safe UI updates** (InvokeAsync + StateHasChanged). The standard stack leverages PostgreSQL's built-in `xmin` system column for automatic concurrency tokens, avoiding manual versioning. User presence tracking uses a polling heartbeat pattern with state stored server-side and broadcast via a pub-sub notifier service. Conflict resolution follows industry patterns: last-write-wins with client-side merging for simple conflicts, user intervention for true conflicts.

The Blazor Server architecture already provides SignalR for real-time UI updates, but the requirement is **polling-based sync** (not WebSocket push), meaning components periodically fetch fresh data and re-render. This prevents the complexity of maintaining WebSocket state while providing "good enough" real-time feel (5-10 second sync intervals).

**Primary recommendation:** Use PostgreSQL's `xmin` concurrency token with EF Core optimistic concurrency, implement PeriodicTimer-based background polling service with DataNotifier pub-sub pattern, store user profile pictures from Google OAuth claims, and handle conflicts with DbUpdateConcurrencyException retry loops with smart merge strategies.

## Standard Stack

The established libraries/tools for multi-user collaboration in Blazor Server + PostgreSQL:

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| EF Core | 10.0.x | Optimistic concurrency with `xmin` | Built-in concurrency token support, DbUpdateConcurrencyException handling |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.x | PostgreSQL xmin system column mapping | Automatic concurrency token via hidden system column (no manual updates) |
| PeriodicTimer | .NET 10 | Background polling loop | Modern async-first timer, natural cancellation, no callback races |
| MudBlazor MudAvatar | 8.15.0 | User avatar display | Material Design avatars with initials fallback |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Google OAuth picture claim | ASP.NET Core 10 | Profile picture URL from Google | Map with `ClaimActions.MapJsonKey("urn:google:picture", "picture")` |
| IHostedService | ASP.NET Core 10 | Background polling service | Run continuous polling independent of component lifecycle |
| InvokeAsync | Blazor | Thread-safe UI updates | Marshal background thread updates to Blazor render context |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| PostgreSQL xmin | SQL Server rowversion | xmin is automatic (no migration column), SQL Server requires explicit byte[] column |
| PeriodicTimer | System.Timers.Timer | PeriodicTimer is async-first with natural cancellation, old Timer uses callbacks with race conditions |
| Polling | SignalR push notifications | Polling is simpler (no WebSocket state), push is more efficient (but phase explicitly defers) |
| Optimistic concurrency | Pessimistic locking | Optimistic assumes conflicts are rare (family app), pessimistic blocks other users |

**Installation:**
No additional packages required - all components already in project (EF Core, Npgsql, MudBlazor, ASP.NET Core hosting).

## Architecture Patterns

### Recommended Project Structure
```
src/FamilyCoordinationApp/
├── Data/
│   └── Entities/          # Add Version (uint) concurrency token to entities
├── Services/
│   ├── DataNotifier.cs    # Pub-sub event notifier (singleton)
│   ├── PresenceService.cs # Track online users (singleton)
│   └── PollingService.cs  # Background PeriodicTimer (IHostedService)
└── Components/
    ├── Shared/
    │   ├── UserAvatar.razor       # Reusable avatar with presence indicator
    │   └── PresenceIndicator.razor # Online/away/offline dot
    └── Pages/
        └── [Pages use DataNotifier subscription pattern]
```

### Pattern 1: PostgreSQL xmin Concurrency Token

**What:** Map a `uint` property to PostgreSQL's hidden `xmin` system column for automatic concurrency detection.

**When to use:** All entities that multiple users can modify concurrently (Recipe, ShoppingListItem, MealPlanEntry).

**Example:**
```csharp
// Source: https://www.npgsql.org/efcore/modeling/concurrency.html
public class Recipe
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public string Name { get; set; } = string.Empty;

    [Timestamp]  // Maps to xmin in PostgreSQL
    public uint Version { get; set; }
}

// Fluent API alternative (in OnModelCreating):
modelBuilder.Entity<Recipe>()
    .Property(r => r.Version)
    .IsRowVersion();
```

**Why xmin:** PostgreSQL automatically updates xmin on every row modification (no manual assignment), making it ideal for optimistic concurrency.

### Pattern 2: DataNotifier Pub-Sub Bridge

**What:** Singleton service that decouples background polling from UI updates using event notifications.

**When to use:** When background services need to trigger UI re-renders without direct component references.

**Example:**
```csharp
// Source: https://blazorise.com/blog/task-scheduling-and-background-services-in-blazor-server
public class DataNotifier
{
    public event Action? OnRecipesChanged;
    public event Action? OnShoppingListChanged;
    public event Action? OnPresenceChanged;

    public void NotifyRecipesChanged() => OnRecipesChanged?.Invoke();
    public void NotifyShoppingListChanged() => OnShoppingListChanged?.Invoke();
    public void NotifyPresenceChanged() => OnPresenceChanged?.Invoke();
}

// Component subscription:
@implements IDisposable
@inject DataNotifier Notifier

protected override void OnInitialized()
{
    Notifier.OnRecipesChanged += OnDataChanged;
}

private void OnDataChanged()
{
    InvokeAsync(async () =>
    {
        // Reload data from database
        await LoadRecipes();
        StateHasChanged();
    });
}

public void Dispose()
{
    Notifier.OnRecipesChanged -= OnDataChanged;
}
```

**Why pub-sub:** Separates data change detection (background service) from UI updates (components), prevents memory leaks via proper Dispose.

### Pattern 3: PeriodicTimer Background Service

**What:** IHostedService that runs continuous polling loop using async PeriodicTimer.

**When to use:** Server-side polling for data changes and user presence heartbeat.

**Example:**
```csharp
// Source: https://kabsang.com/2026/01/01/understanding-periodictimer-in-net-the-modern-way-to-schedule-background-work/
public class PollingService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    DataNotifier notifier,
    ILogger<PollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckForDataChanges(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during polling check");
            }
        }
    }

    private async Task CheckForDataChanges(CancellationToken ct)
    {
        using var db = await dbFactory.CreateDbContextAsync(ct);
        // Check for changes, notify components
        notifier.NotifyShoppingListChanged();
    }
}

// Registration in Program.cs:
builder.Services.AddSingleton<DataNotifier>();
builder.Services.AddHostedService<PollingService>();
```

**Why PeriodicTimer:** Natural async loop with cancellation, no callback races, automatic "no overlap" (waits if work takes longer than interval).

### Pattern 4: DbUpdateConcurrencyException Handling

**What:** Retry loop that catches concurrency conflicts and applies merge strategy.

**When to use:** SaveChanges on any entity with concurrency token where conflicts are possible.

**Example:**
```csharp
// Source: https://learn.microsoft.com/en-us/ef/core/saving/concurrency
public async Task UpdateRecipeAsync(Recipe recipe)
{
    var saved = false;
    while (!saved)
    {
        try
        {
            await _db.SaveChangesAsync();
            saved = true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                if (entry.Entity is Recipe conflictedRecipe)
                {
                    var proposedValues = entry.CurrentValues;
                    var databaseValues = await entry.GetDatabaseValuesAsync();

                    if (databaseValues == null)
                    {
                        // Entity was deleted - handle delete conflict
                        throw new InvalidOperationException("Recipe was deleted by another user");
                    }

                    // Apply merge strategy (example: last-write-wins for most fields)
                    foreach (var property in proposedValues.Properties)
                    {
                        var proposedValue = proposedValues[property];
                        var databaseValue = databaseValues[property];

                        // Keep proposed value (last-write-wins)
                        // OR implement smart merge (e.g., "checked wins" for shopping items)
                    }

                    // Refresh original values to bypass next concurrency check
                    entry.OriginalValues.SetValues(databaseValues);
                }
            }
        }
    }
}
```

**Why retry loop:** Allows automatic conflict resolution with merge strategies, provides values for user intervention on true conflicts.

### Pattern 5: Thread-Safe Component Updates

**What:** Use InvokeAsync to marshal background thread callbacks to Blazor synchronization context.

**When to use:** Always when background thread (polling service callback) needs to trigger StateHasChanged.

**Example:**
```csharp
// Source: https://blazor-university.com/components/multi-threaded-rendering/invokeasync/
private void OnDataChanged()
{
    InvokeAsync(async () =>
    {
        lastUpdated = DateTime.Now;
        await LoadData();
        StateHasChanged();
    });
}
```

**Why InvokeAsync:** Background threads run outside Blazor synchronization context, direct StateHasChanged throws exception, InvokeAsync marshals to correct context.

### Pattern 6: Google OAuth Picture Claim Mapping

**What:** Configure claim mapping to extract profile picture URL from Google OAuth response.

**When to use:** Store user avatar URLs from Google for attribution display.

**Example:**
```csharp
// Source: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/additional-claims
builder.Services.AddAuthentication().AddGoogle(options =>
{
    options.ClientId = configuration["Authentication:Google:ClientId"];
    options.ClientSecret = configuration["Authentication:Google:ClientSecret"];

    // Map picture claim
    options.ClaimActions.MapJsonKey("urn:google:picture", "picture", "url");

    options.Scope.Add("profile");
});

// Store in User entity during login:
public class User
{
    public string? PictureUrl { get; set; }  // Store Google avatar URL
    public string Initials { get; set; } = string.Empty;  // Fallback (computed from DisplayName)
}

// On external login callback:
var pictureClaim = info.Principal.FindFirst("urn:google:picture")?.Value;
user.PictureUrl = pictureClaim;
user.Initials = GetInitials(user.DisplayName);  // e.g., "John Smith" -> "JS"
```

**Why store locally:** Google picture URLs can change, local storage allows future auth provider expansion, initials fallback for when URL unavailable.

### Pattern 7: MudAvatar with Presence Badge

**What:** Display user avatar with online status indicator using CSS positioning.

**When to use:** Show user attribution with presence (recipe creator, shopping item adder).

**Example:**
```razor
<!-- Source: https://mudblazor.com/components/avatar -->
<div style="position: relative; display: inline-block;">
    @if (!string.IsNullOrEmpty(user.PictureUrl))
    {
        <MudAvatar Image="@user.PictureUrl" Alt="@user.DisplayName" referrerpolicy="no-referrer" />
    }
    else
    {
        <MudAvatar Color="Color.Primary">@user.Initials</MudAvatar>
    }

    @if (isOnline)
    {
        <div style="position: absolute; bottom: 2px; right: 2px; width: 12px; height: 12px; background-color: #4caf50; border: 2px solid white; border-radius: 50%;"></div>
    }
</div>
```

**Why MudAvatar:** Built-in Material Design styling, automatic initials display, works with existing MudBlazor theme.

**Note on Google pictures:** Add `referrerpolicy="no-referrer"` attribute to avoid 403 errors from Google CDN (source: https://mazeez.dev/posts/why-google-oauth-profile-picture-returns-403/).

### Anti-Patterns to Avoid

- **Storing concurrency token in application state** - PostgreSQL xmin is automatic, manual version tracking is redundant and error-prone
- **Timer callbacks without InvokeAsync** - Blazor rejects StateHasChanged from background threads, causes crashes
- **Not disposing event subscriptions** - Memory leaks from stale component references in DataNotifier events
- **Using foreach with concurrency retry** - DbContext tracks entities, retrying in same context reuses stale data (create new context or refresh)
- **Polling faster than operations complete** - PeriodicTimer naturally prevents overlap, but database query must finish before next tick
- **Not handling delete conflicts** - User A deletes, User B edits → GetDatabaseValuesAsync returns null, must detect and handle

## Don't Hand-Roll

Problems that look simple but have existing solutions:

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Concurrency tokens | Manual version column with auto-increment | PostgreSQL `xmin` system column | xmin updates automatically on every write, no triggers or application code |
| Timer-based polling | System.Timers.Timer with callbacks | PeriodicTimer with async/await | PeriodicTimer has natural cancellation, no race conditions, async-first design |
| Conflict resolution | Custom versioning logic | EF Core DbUpdateConcurrencyException | Built-in exception with entry values (current, original, database) for merge logic |
| User presence tracking | Custom heartbeat table with manual cleanup | Singleton service with ConcurrentDictionary + expiration | .NET concurrent collections handle thread safety, simple timeout pattern |
| Avatar display | Custom image component | MudAvatar with initials | Material Design styling, automatic fallback, theme integration |
| Profile picture storage | Custom image upload | Google OAuth picture claim | Profile pic already available from OAuth, no upload/storage needed |

**Key insight:** Blazor Server already runs on server with persistent circuit state and SignalR connection. Polling is for *data synchronization* (checking database for changes by other users), not for keeping circuit alive. Don't conflate SignalR connection management with application data polling.

## Common Pitfalls

### Pitfall 1: Forgetting InvokeAsync in Event Handlers

**What goes wrong:** Background service calls `DataNotifier.NotifyDataChanged()`, component's subscribed method calls `StateHasChanged()` directly, Blazor throws "The current thread is not associated with the Dispatcher."

**Why it happens:** Event is raised on background thread (PeriodicTimer in PollingService), but StateHasChanged must run on Blazor synchronization context.

**How to avoid:** Always wrap StateHasChanged in InvokeAsync when called from event handlers.

**Warning signs:** Exceptions mentioning Dispatcher, synchronization context, or "wrong thread" during polling updates.

```csharp
// WRONG:
private void OnDataChanged()
{
    StateHasChanged();  // CRASH if called from background thread
}

// CORRECT:
private void OnDataChanged()
{
    InvokeAsync(StateHasChanged);  // Marshals to Blazor context
}
```

### Pitfall 2: Not Disposing Event Subscriptions

**What goes wrong:** Component subscribes to `DataNotifier.OnRecipesChanged` in OnInitialized, user navigates away, component re-renders on next notification even after disposal, causes memory leaks and stale UI updates.

**Why it happens:** Events hold strong references to subscribers, components must explicitly unsubscribe.

**How to avoid:** Implement IDisposable and unsubscribe in Dispose method.

**Warning signs:** Components updating after navigation, increasing memory usage, multiple instances of same component reacting to events.

```csharp
@implements IDisposable

protected override void OnInitialized()
{
    Notifier.OnRecipesChanged += OnDataChanged;
}

public void Dispose()
{
    Notifier.OnRecipesChanged -= OnDataChanged;  // MUST unsubscribe
}
```

### Pitfall 3: Infinite Retry Loop on Conflict

**What goes wrong:** DbUpdateConcurrencyException retry loop doesn't update entity values, same conflict happens every iteration, infinite loop.

**Why it happens:** After catching exception, must refresh `entry.OriginalValues` with database values to bypass next concurrency check.

**How to avoid:** Call `entry.OriginalValues.SetValues(databaseValues)` after resolving conflict.

**Warning signs:** SaveChanges in tight loop, CPU spike, same DbUpdateConcurrencyException repeatedly.

```csharp
catch (DbUpdateConcurrencyException ex)
{
    var databaseValues = await entry.GetDatabaseValuesAsync();

    // Apply merge strategy...

    // CRITICAL: Refresh original values
    entry.OriginalValues.SetValues(databaseValues);  // Without this = infinite loop
}
```

### Pitfall 4: Polling Too Aggressively

**What goes wrong:** Polling interval set to 1 second, database CPU spikes, UI feels sluggish from constant re-renders.

**Why it happens:** Every user's polling service queries database simultaneously, N users = N queries per second.

**How to avoid:** Use 5-10 second intervals for "good enough" real-time feel, consider exponential backoff when idle.

**Warning signs:** High database CPU, Blazor circuit reconnections, UI jank during re-renders.

**Recommended intervals:**
- Active collaboration: 5 seconds (shopping list during grocery trip)
- Background sync: 10-30 seconds (recipe edits, meal planning)
- Idle detection: Reduce to 60 seconds after 5 minutes of inactivity

### Pitfall 5: Forgetting referrerpolicy for Google Avatars

**What goes wrong:** Google profile pictures return 403 Forbidden when displayed in `<img>` tags.

**Why it happens:** Google CDN checks Referer header, blocks requests from external domains.

**How to avoid:** Add `referrerpolicy="no-referrer"` to image elements displaying Google avatar URLs.

**Warning signs:** Broken image icons, 403 errors in browser console for googleusercontent.com URLs.

```html
<!-- WRONG: -->
<img src="@user.PictureUrl" />

<!-- CORRECT: -->
<img src="@user.PictureUrl" referrerpolicy="no-referrer" />
```

### Pitfall 6: Using Same DbContext Across Retry Loop

**What goes wrong:** DbContext caches entities during first SaveChanges attempt, retry uses stale tracked entities.

**Why it happens:** EF Core change tracker maintains entity state, doesn't auto-refresh on concurrency exception.

**How to avoid:** Either refresh entry values (as in Pattern 4) OR create new DbContext for retry.

**Warning signs:** Merge logic appears correct but changes don't persist, OriginalValues don't match database.

## Code Examples

Verified patterns from official sources:

### Example 1: Complete Polling Service with DataNotifier

```csharp
// Source: Synthesis of https://blazorise.com/blog/task-scheduling-and-background-services-in-blazor-server
//         and https://kabsang.com/2026/01/01/understanding-periodictimer-in-net-the-modern-way-to-schedule-background-work/

public class DataNotifier
{
    public event Action? OnShoppingListChanged;
    public void NotifyShoppingListChanged() => OnShoppingListChanged?.Invoke();
}

public class ShoppingListPollingService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    DataNotifier notifier,
    ILogger<ShoppingListPollingService> logger) : BackgroundService
{
    private DateTime _lastCheck = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckForChanges(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error checking for shopping list changes");
            }
        }
    }

    private async Task CheckForChanges(CancellationToken ct)
    {
        using var db = await dbFactory.CreateDbContextAsync(ct);

        // Simple change detection: check if any items modified since last check
        var hasChanges = await db.ShoppingListItems
            .AnyAsync(item => item.UpdatedAt > _lastCheck, ct);

        _lastCheck = DateTime.UtcNow;

        if (hasChanges)
        {
            notifier.NotifyShoppingListChanged();
        }
    }
}

// Program.cs registration:
builder.Services.AddSingleton<DataNotifier>();
builder.Services.AddHostedService<ShoppingListPollingService>();
```

### Example 2: Component with Polling Subscription

```razor
@page "/shopping-list"
@implements IDisposable
@inject DataNotifier Notifier
@inject IDbContextFactory<ApplicationDbContext> DbFactory

<MudContainer>
    <MudText>Last updated: @lastUpdated.ToString("h:mm:ss tt")</MudText>

    @foreach (var item in items)
    {
        <ShoppingListItemCard Item="@item" OnChanged="@LoadItems" />
    }
</MudContainer>

@code {
    private List<ShoppingListItem> items = new();
    private DateTime lastUpdated = DateTime.Now;

    protected override async Task OnInitializedAsync()
    {
        Notifier.OnShoppingListChanged += OnDataChanged;
        await LoadItems();
    }

    private void OnDataChanged()
    {
        InvokeAsync(async () =>
        {
            await LoadItems();
            lastUpdated = DateTime.Now;
            StateHasChanged();
        });
    }

    private async Task LoadItems()
    {
        using var db = await DbFactory.CreateDbContextAsync();
        items = await db.ShoppingListItems
            .Include(i => i.AddedByUser)  // For attribution
            .OrderBy(i => i.CategoryOrder)
            .ToListAsync();
    }

    public void Dispose()
    {
        Notifier.OnShoppingListChanged -= OnDataChanged;
    }
}
```

### Example 3: Optimistic Concurrency with Smart Merge

```csharp
// Source: https://learn.microsoft.com/en-us/ef/core/saving/concurrency
public async Task ToggleShoppingItemAsync(int itemId, bool isChecked)
{
    var saved = false;
    var retries = 0;
    const int maxRetries = 3;

    while (!saved && retries < maxRetries)
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var item = await db.ShoppingListItems.FindAsync(itemId);

            if (item == null)
                throw new InvalidOperationException("Item not found");

            item.IsChecked = isChecked;
            item.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            saved = true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            retries++;

            foreach (var entry in ex.Entries)
            {
                if (entry.Entity is ShoppingListItem item)
                {
                    var databaseValues = await entry.GetDatabaseValuesAsync();

                    if (databaseValues == null)
                    {
                        // Item was deleted
                        throw new InvalidOperationException("Item was deleted by another user");
                    }

                    // "Checked wins" strategy: if either user checked it, keep checked
                    var proposedChecked = (bool)entry.CurrentValues[nameof(ShoppingListItem.IsChecked)];
                    var databaseChecked = (bool)databaseValues[nameof(ShoppingListItem.IsChecked)];

                    if (proposedChecked || databaseChecked)
                    {
                        entry.CurrentValues[nameof(ShoppingListItem.IsChecked)] = true;
                    }

                    // Keep database values for other fields (e.g., Quantity if edited)
                    foreach (var property in entry.CurrentValues.Properties)
                    {
                        if (property.Name != nameof(ShoppingListItem.IsChecked))
                        {
                            entry.CurrentValues[property] = databaseValues[property];
                        }
                    }

                    entry.OriginalValues.SetValues(databaseValues);
                }
            }

            if (retries >= maxRetries)
            {
                throw new InvalidOperationException("Could not save changes after multiple conflicts");
            }
        }
    }
}
```

### Example 4: User Presence Service

```csharp
// Based on polling patterns from search results
public class PresenceService
{
    private readonly ConcurrentDictionary<int, UserPresence> _presence = new();
    private readonly TimeSpan _awayTimeout = TimeSpan.FromMinutes(5);

    public event Action? OnPresenceChanged;

    public void Heartbeat(int userId)
    {
        _presence.AddOrUpdate(
            userId,
            new UserPresence { UserId = userId, LastSeen = DateTime.UtcNow, Status = PresenceStatus.Online },
            (_, existing) =>
            {
                existing.LastSeen = DateTime.UtcNow;
                existing.Status = PresenceStatus.Online;
                return existing;
            });

        OnPresenceChanged?.Invoke();
    }

    public void UpdatePresence()
    {
        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var kvp in _presence)
        {
            var timeSinceLastSeen = now - kvp.Value.LastSeen;
            var newStatus = timeSinceLastSeen > _awayTimeout
                ? PresenceStatus.Away
                : PresenceStatus.Online;

            if (kvp.Value.Status != newStatus)
            {
                kvp.Value.Status = newStatus;
                changed = true;
            }
        }

        if (changed)
        {
            OnPresenceChanged?.Invoke();
        }
    }

    public IEnumerable<UserPresence> GetOnlineUsers() =>
        _presence.Values.Where(p => p.Status == PresenceStatus.Online);
}

public class UserPresence
{
    public int UserId { get; set; }
    public DateTime LastSeen { get; set; }
    public PresenceStatus Status { get; set; }
}

public enum PresenceStatus { Online, Away, Offline }

// Add to polling service:
public class PresencePollingService(
    PresenceService presenceService,
    ILogger<PresencePollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            presenceService.UpdatePresence();
        }
    }
}
```

### Example 5: User Avatar Component

```razor
<!-- Source: https://mudblazor.com/components/avatar combined with CSS badge pattern -->
<div class="user-avatar-container">
    @if (!string.IsNullOrEmpty(User.PictureUrl))
    {
        <MudAvatar Image="@User.PictureUrl"
                   Alt="@User.DisplayName"
                   Size="@Size"
                   referrerpolicy="no-referrer" />
    }
    else
    {
        <MudAvatar Color="Color.Primary" Size="@Size">
            @User.Initials
        </MudAvatar>
    }

    @if (ShowPresence && IsOnline)
    {
        <div class="presence-badge presence-online"></div>
    }
    else if (ShowPresence && IsAway)
    {
        <div class="presence-badge presence-away"></div>
    }
</div>

@code {
    [Parameter] public User User { get; set; } = default!;
    [Parameter] public bool ShowPresence { get; set; }
    [Parameter] public bool IsOnline { get; set; }
    [Parameter] public bool IsAway { get; set; }
    [Parameter] public Size Size { get; set; } = Size.Medium;
}

<style>
    .user-avatar-container {
        position: relative;
        display: inline-block;
    }

    .presence-badge {
        position: absolute;
        bottom: 2px;
        right: 2px;
        width: 12px;
        height: 12px;
        border: 2px solid white;
        border-radius: 50%;
    }

    .presence-online {
        background-color: #4caf50;  /* Green */
    }

    .presence-away {
        background-color: #ff9800;  /* Orange */
    }
</style>
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| System.Timers.Timer callbacks | PeriodicTimer async/await | .NET 6 (2021) | Eliminates callback race conditions, natural cancellation |
| Manual rowversion column | PostgreSQL xmin system column | Npgsql.EF 3.0+ | Automatic concurrency token, no migrations needed |
| AspNetUsers claims table | Store picture URL in User entity | Modern pattern | Simpler queries, better for local/future auth providers |
| SignalR push for all updates | Polling for data sync | Context-dependent | Simpler state management for low-frequency updates |
| setTimeout polling | PeriodicTimer in BackgroundService | .NET 6+ | Server-side polling independent of client lifecycle |

**Deprecated/outdated:**
- **System.Timers.Timer in async code**: Use PeriodicTimer (cleaner async loop, automatic cancellation)
- **Manual version tracking in PostgreSQL**: Use xmin (automatic, no application code)
- **Long-polling**: For this use case, simple polling is sufficient (long-polling adds complexity without benefit)
- **WebSocket for every update**: Polling is "good enough" for family collaboration (updates not millisecond-critical)

## Open Questions

Things that couldn't be fully resolved:

1. **Exact polling interval**
   - What we know: Industry uses 5-30 seconds depending on real-time needs, adaptive polling based on activity is best practice
   - What's unclear: Optimal interval for this specific family app (shopping vs recipes vs meal planning have different urgency)
   - Recommendation: Start with 5 seconds for shopping list (active during grocery trip), 10 seconds for recipes/meal plan, implement adaptive strategy if performance issues arise

2. **Conflict UI display pattern**
   - What we know: Industry shows conflicting values with "yours vs theirs" comparison, provides pick/edit/merge options
   - What's unclear: Best MudBlazor component pattern (inline dialog, snackbar with action, dedicated conflict resolution page)
   - Recommendation: Start with MudAlert inline on conflicted item, escalate to MudDialog for complex conflicts, iterate based on user feedback

3. **Change history tracking depth**
   - What we know: Full audit trail requires separate history table with triggers, lightweight tracking just needs UpdatedAt + UpdatedByUserId
   - What's unclear: Whether family users need to see "Sarah changed this 2 hours ago" vs just "Sarah added this"
   - Recommendation: Start lightweight (CreatedByUserId, CreatedAt, UpdatedByUserId, UpdatedAt), defer full history to future phase if requested

4. **Presence heartbeat from client vs server detection**
   - What we know: Blazor Server maintains SignalR circuit, could detect disconnection without client heartbeat
   - What's unclear: Whether SignalR circuit state is reliable indicator of "user is actively viewing page" vs "browser tab is open"
   - Recommendation: Use SignalR circuit OnConnected/OnDisconnected for basic online/offline, add client-side heartbeat (page visibility API) for fine-grained presence in future phase if needed

5. **Away timeout duration**
   - What we know: PCI DSS uses 15 minutes, Citrix minimum is 5 minutes, Microsoft Outlook uses 6 hours default
   - What's unclear: Family app context (not security-critical, but also not enterprise productivity)
   - Recommendation: 5 minutes for away (reasonable for "stepped away from kitchen"), simple online/offline binary for MVP

## Sources

### Primary (HIGH confidence)
- [Npgsql EF Core Concurrency Documentation](https://www.npgsql.org/efcore/modeling/concurrency.html) - xmin system column configuration
- [Microsoft Learn: EF Core Concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency) - DbUpdateConcurrencyException handling patterns
- [Microsoft Learn: Additional Claims from External Providers](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/additional-claims?view=aspnetcore-9.0) - Google OAuth picture claim mapping
- [Blazorise: Task Scheduling and Background Services in Blazor Server](https://blazorise.com/blog/task-scheduling-and-background-services-in-blazor-server) - DataNotifier pub-sub pattern
- [Understanding PeriodicTimer in .NET](https://kabsang.com/2026/01/01/understanding-periodictimer-in-net-the-modern-way-to-schedule-background-work/) - Modern timer patterns
- [MudBlazor Avatar Component](https://mudblazor.com/components/avatar) - Avatar with initials

### Secondary (MEDIUM confidence)
- [Blazor University: InvokeAsync](https://blazor-university.com/components/multi-threaded-rendering/invokeasync/) - Thread safety patterns
- [Milan Jovanovic: Optimistic Locking in EF Core](https://www.milanjovanovic.tech/blog/solving-race-conditions-with-ef-core-optimistic-locking) - Concurrency patterns
- [DEV Community: Avatar with Status Indicator CSS](https://dev.to/designyff/simple-avatar-with-status-indicator-css-only-a-step-by-step-tutorial-4ge7) - Presence badge styling
- [Medium: Polling Techniques Guide](https://medium.com/@pranayjain1382/understanding-polling-techniques-a-complete-guide-to-real-time-data-updates-1eb28f003eb1) - Polling intervals
- [Medium: Conflict Resolution Strategies](https://mobterest.medium.com/conflict-resolution-strategies-in-data-synchronization-2a10be5b82bc) - Last-write-wins, CRDTs

### Tertiary (LOW confidence)
- [Ably: User Presence at Scale](https://ably.com/blog/user-presence-at-scale) - Presence patterns (scale not applicable but concepts valid)
- [NIST Session Management](https://pages.nist.gov/800-63-3-Implementation-Resources/63B/Session/) - Timeout standards (security context, not UX)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - Official Npgsql and Microsoft documentation, PeriodicTimer is .NET standard
- Architecture patterns: HIGH - All patterns verified against official sources (Microsoft Learn, Npgsql docs, MudBlazor docs)
- Pitfalls: MEDIUM - Based on documented issues (GitHub, Stack Overflow) and official warnings, not all tested in exact project context
- Polling intervals: MEDIUM - Industry practices vary, no universal standard, recommendations are context-appropriate but need validation
- Presence timeout: MEDIUM - Security standards (PCI DSS, NIST) provide bounds, but family app UX differs from enterprise

**Research date:** 2026-01-24
**Valid until:** 60 days (stable technologies, .NET 10 released, patterns unlikely to change rapidly)
