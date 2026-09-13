using Microsoft.EntityFrameworkCore;
using Planner.Web.Data;
using Planner.Web.Domain;

namespace Planner.Web.Services;

public sealed class ConflictDetector(IDbContextFactory<PlannerDbContext> factory) : IConflictDetector
{
    public async Task<IReadOnlyList<ScheduleConflict>> DetectAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        Guid? excludingTimeBlockId = null,
        CancellationToken cancellationToken = default)
    {
        (startsAtUtc, endsAtUtc) = ScheduleService.ValidateAndNormalizeRange(startsAtUtc, endsAtUtc);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var conflicts = await db.TimeBlocks.AsNoTracking()
            .Where(x => x.Status != TimeBlockStatus.Cancelled
                && x.Id != excludingTimeBlockId
                && startsAtUtc < x.EndsAtUtc
                && endsAtUtc > x.StartsAtUtc)
            .OrderBy(x => x.StartsAtUtc)
            .Select(x => new ScheduleConflict(x.Id, x.TaskId ?? Guid.Empty, x.Task == null ? (x.Notes == "" ? "[Blank]" : x.Notes) : x.Task.Title, x.StartsAtUtc, x.EndsAtUtc))
            .ToListAsync(cancellationToken);
        var external = await db.ExternalCalendarEvents.AsNoTracking()
            .Where(x => !x.IsCancelled
                && x.Response != ExternalEventResponse.Declined
                && x.ShowAs != ExternalEventShowAs.Free
                && startsAtUtc < x.EndsAtUtc
                && endsAtUtc > x.StartsAtUtc)
            .OrderBy(x => x.StartsAtUtc)
            .Select(x => new
            {
                x.Id,
                AccountId = x.Calendar.ConnectedMicrosoftAccountId,
                x.Subject,
                x.StartsAtUtc,
                x.EndsAtUtc,
                x.ShowAs
            })
            .ToListAsync(cancellationToken);
        conflicts.AddRange(external.Select(x => new ScheduleConflict(x.Id, Guid.Empty, x.Subject, x.StartsAtUtc, x.EndsAtUtc)
        {
            Source = ScheduleConflictSource.MicrosoftCalendar,
            MicrosoftAccountId = x.AccountId,
            Severity = x.ShowAs is ExternalEventShowAs.Busy or ExternalEventShowAs.OutOfOffice
                ? ScheduleConflictSeverity.Blocking : ScheduleConflictSeverity.Warning
        }));
        var staleBefore = DateTimeOffset.UtcNow.AddHours(-1);
        var accountSyncStates = await db.ConnectedMicrosoftAccounts.AsNoTracking()
            .Select(x => new
            {
                x.Id,
                x.DisplayName,
                LastSyncs = x.Calendars.Select(calendar => calendar.SyncState == null
                    ? (DateTimeOffset?)null : calendar.SyncState.LastSuccessfulSyncAtUtc).ToList()
            })
            .ToListAsync(cancellationToken);
        var staleAccounts = accountSyncStates
            .Where(x => x.LastSyncs.Count == 0 || x.LastSyncs.Any(last => last is null || last < staleBefore))
            .ToList();
        conflicts.AddRange(staleAccounts.Select(x => new ScheduleConflict(Guid.Empty, Guid.Empty,
            $"Availability may be incomplete for {x.DisplayName}", startsAtUtc, endsAtUtc)
        {
            Source = ScheduleConflictSource.Availability,
            MicrosoftAccountId = x.Id,
            Severity = ScheduleConflictSeverity.Warning
        }));
        return conflicts.OrderBy(x => x.StartsAtUtc).ToList();
    }
}

public sealed class ScheduleService(
    IDbContextFactory<PlannerDbContext> factory,
    IConflictDetector conflictDetector) : IScheduleService
{
    public async Task<IReadOnlyList<TimeBlockDto>> ListAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        (startsAtUtc, endsAtUtc) = ValidateAndNormalizeRange(startsAtUtc, endsAtUtc);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var blocks = await db.TimeBlocks.AsNoTracking()
            .Where(x => x.StartsAtUtc < endsAtUtc && x.EndsAtUtc > startsAtUtc)
            .OrderBy(x => x.StartsAtUtc)
            .ThenBy(x => x.EndsAtUtc)
            .Select(x => new TimeBlockDto(x.Id, x.TaskId, x.Task == null ? string.Empty : x.Task.Title, x.Task == null ? (x.Notes == "" ? "[Blank]" : x.Notes) : x.Task.Title, x.StartsAtUtc, x.EndsAtUtc,
                x.Status, x.Notes, x.ConflictOverrideAtUtc, x.Version))
            .ToListAsync(cancellationToken);
        return blocks;
    }

    public async Task<ScheduleSaveResult> CreateAsync(
        Guid? taskId,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        string notes = "",
        bool overrideConflicts = false,
        CancellationToken cancellationToken = default)
    {
        (startsAtUtc, endsAtUtc) = ValidateAndNormalizeRange(startsAtUtc, endsAtUtc);
        var conflicts = await conflictDetector.DetectAsync(startsAtUtc, endsAtUtc, cancellationToken: cancellationToken);
        if (conflicts.Count > 0 && !overrideConflicts)
            return new(false, null, conflicts);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var task = taskId is null ? null : await db.Tasks.SingleAsync(x => x.Id == taskId.Value, cancellationToken);
        var block = new TimeBlock
        {
            TaskId = taskId,
            Task = task,
            StartsAtUtc = startsAtUtc,
            EndsAtUtc = endsAtUtc,
            Notes = notes.Trim(),
            ConflictOverrideAtUtc = conflicts.Count > 0 && overrideConflicts ? DateTimeOffset.UtcNow : null
        };
        db.TimeBlocks.Add(block);
        await db.SaveChangesAsync(cancellationToken);
        return new(true, ToDto(block), conflicts);
    }

    public async Task<ScheduleSaveResult> UpdateAsync(
        Guid id,
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        string notes,
        bool overrideConflicts = false,
        Guid? taskId = null,
        CancellationToken cancellationToken = default)
    {
        (startsAtUtc, endsAtUtc) = ValidateAndNormalizeRange(startsAtUtc, endsAtUtc);
        var conflicts = await conflictDetector.DetectAsync(startsAtUtc, endsAtUtc, id, cancellationToken);
        if (conflicts.Count > 0 && !overrideConflicts)
            return new(false, null, conflicts);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var block = await db.TimeBlocks.Include(x => x.Task).SingleAsync(x => x.Id == id, cancellationToken);
        block.StartsAtUtc = startsAtUtc;
        block.EndsAtUtc = endsAtUtc;
        block.Notes = notes.Trim();
        block.ConflictOverrideAtUtc = conflicts.Count > 0 && overrideConflicts ? DateTimeOffset.UtcNow : null;
        if (taskId != block.TaskId)
        {
            if (taskId is null)
            {
                block.Task = null;
                block.TaskId = null;
            }
            else
            {
                var newTask = await db.Tasks.SingleAsync(x => x.Id == taskId.Value, cancellationToken);
                block.Task = newTask;
                block.TaskId = newTask.Id;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(true, ToDto(block), conflicts);
    }

    public async Task SetStatusAsync(Guid id, TimeBlockStatus status, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var block = await db.TimeBlocks.SingleAsync(x => x.Id == id, cancellationToken);
        block.Status = status;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetDeletedAsync(Guid id, bool deleted, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var block = await db.TimeBlocks.IgnoreQueryFilters().SingleAsync(x => x.Id == id, cancellationToken);
        block.DeletedAtUtc = deleted ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static (DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc) ValidateAndNormalizeRange(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc)
    {
        startsAtUtc = startsAtUtc.ToUniversalTime();
        endsAtUtc = endsAtUtc.ToUniversalTime();
        if (endsAtUtc <= startsAtUtc)
            throw new ArgumentException("A time block must end after it starts.", nameof(endsAtUtc));
        return (startsAtUtc, endsAtUtc);
    }

    private static TimeBlockDto ToDto(TimeBlock block) =>
        new(block.Id, block.TaskId, block.Task?.Title ?? string.Empty, BlockTitle(block), block.StartsAtUtc, block.EndsAtUtc,
            block.Status, block.Notes, block.ConflictOverrideAtUtc, block.Version);

    private static string BlockTitle(TimeBlock block) =>
        block.Task?.Title ?? (string.IsNullOrWhiteSpace(block.Notes) ? "[Blank]" : block.Notes.Trim());
}
