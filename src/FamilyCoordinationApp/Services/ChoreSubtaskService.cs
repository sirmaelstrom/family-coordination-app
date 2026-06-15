using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// CRUD for a chore's lightweight checklist (Phase 14). One short-lived <see cref="ApplicationDbContext"/> per
/// op (M2); every query is filtered by the caller-supplied <c>householdId</c> (M1, never client-supplied), so
/// cross-household access naturally 404s. Timestamps are UTC from the injected <see cref="TimeProvider"/>.
/// <para>Versionless / last-write-wins by design: there is NO xmin token on <see cref="ChoreSubtask"/>, and
/// these writes deliberately touch ONLY the subtask row — never <c>Chore.Version</c>. Subtasks NEVER gate
/// chore completion; the reset-on-completion of a recurring chore lives in <c>ChoreService.CompleteAsync</c>.</para>
/// </summary>
public class ChoreSubtaskService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    TimeProvider timeProvider) : IChoreSubtaskService
{
    /// <summary>Max checklist items per chore (D-Phase14).</summary>
    private const int MaxSubtasksPerChore = 50;
    private const int MaxTitleLength = 200;

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<ChoreSubtaskDto> CreateAsync(int householdId, int choreId, string title, CancellationToken ct = default)
    {
        var cleanTitle = ValidateTitle(title);

        await using var context = await dbFactory.CreateDbContextAsync(ct);

        // The chore must exist in the household (else 404). We never read Chore.Version here.
        var choreExists = await context.Chores
            .AnyAsync(c => c.HouseholdId == householdId && c.ChoreId == choreId, ct);
        if (!choreExists)
        {
            throw new ChoreNotFoundException($"Chore {choreId} not found for household {householdId}.");
        }

        var existing = await context.ChoreSubtasks
            .Where(s => s.HouseholdId == householdId && s.ChoreId == choreId)
            .Select(s => (int?)s.SortOrder)
            .ToListAsync(ct);

        if (existing.Count >= MaxSubtasksPerChore)
        {
            throw new ChoreValidationException($"A chore can have at most {MaxSubtasksPerChore} checklist items.");
        }

        var nextSortOrder = existing.Count == 0 ? 0 : existing.Max()!.Value + 1;

        var subtask = new ChoreSubtask
        {
            HouseholdId = householdId,
            ChoreId = choreId,
            // SubtaskId is DB-generated — do NOT set it.
            Title = cleanTitle,
            IsDone = false,
            SortOrder = nextSortOrder,
            CreatedAt = UtcNow()
        };

        context.ChoreSubtasks.Add(subtask);
        await context.SaveChangesAsync(ct);

        return ToDto(subtask);
    }

    public async Task<ChoreSubtaskDto> UpdateAsync(int householdId, int choreId, int subtaskId, string? title, bool? isDone, int? sortOrder, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var subtask = await LoadSubtaskAsync(context, householdId, choreId, subtaskId, ct);

        // Apply only the supplied fields. No version check; we touch only the subtask row (never the chore).
        if (title is not null)
        {
            subtask.Title = ValidateTitle(title);
        }
        if (isDone is { } done)
        {
            subtask.IsDone = done;
        }
        if (sortOrder is { } order)
        {
            subtask.SortOrder = order;
        }

        await context.SaveChangesAsync(ct);

        return ToDto(subtask);
    }

    public async Task DeleteAsync(int householdId, int choreId, int subtaskId, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var subtask = await LoadSubtaskAsync(context, householdId, choreId, subtaskId, ct);

        context.ChoreSubtasks.Remove(subtask);
        await context.SaveChangesAsync(ct);
    }

    private static async Task<ChoreSubtask> LoadSubtaskAsync(
        ApplicationDbContext context, int householdId, int choreId, int subtaskId, CancellationToken ct)
    {
        return await context.ChoreSubtasks
            .FirstOrDefaultAsync(s => s.HouseholdId == householdId && s.ChoreId == choreId && s.SubtaskId == subtaskId, ct)
            ?? throw new ChoreNotFoundException(
                $"Subtask {subtaskId} not found for chore {choreId} in household {householdId}.");
    }

    private static string ValidateTitle(string? title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ChoreValidationException("Subtask title is required.");
        }
        if (trimmed.Length > MaxTitleLength)
        {
            throw new ChoreValidationException($"Subtask title must be at most {MaxTitleLength} characters.");
        }
        return trimmed;
    }

    private static ChoreSubtaskDto ToDto(ChoreSubtask s) =>
        new(s.SubtaskId, s.Title, s.IsDone, s.SortOrder);
}
