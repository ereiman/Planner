using Microsoft.EntityFrameworkCore;
using Planner.Web.Data;
using Planner.Web.Domain;

namespace Planner.Web.Services;

public sealed class TodayQueryService(IDbContextFactory<PlannerDbContext> factory, IConflictDetector conflicts) : ITodayQueryService
{
    public async Task<TodayDashboard> GetAsync(DateOnly today, TimeZoneInfo timeZone, CancellationToken cancellationToken = default)
    {
        var localStart = today.ToDateTime(TimeOnly.MinValue);
        var start = TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone);
        var end = TimeZoneInfo.ConvertTimeToUtc(localStart.AddDays(1), timeZone);
        var startsAtUtc = new DateTimeOffset(start, TimeSpan.Zero);
        var endsAtUtc = new DateTimeOffset(end, TimeSpan.Zero);
        var futureEnd = startsAtUtc.AddDays(14);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var dueEntities = await TaskQuery(db).Where(x => x.State != TaskState.Completed && x.State != TaskState.Cancelled &&
            x.ArchivedAtUtc == null && x.DueDate != null && x.DueDate <= today).OrderBy(x => x.DueDate).ToListAsync(cancellationToken);
        var blocks = await db.TimeBlocks.AsNoTracking().Where(x => x.Status != TimeBlockStatus.Cancelled &&
            x.StartsAtUtc < endsAtUtc && x.EndsAtUtc > startsAtUtc).OrderBy(x => x.StartsAtUtc)
            .Select(x => new TimeBlockDto(x.Id, x.TaskId, x.Task == null ? string.Empty : x.Task.Title, x.Task == null ? (x.Notes == "" ? "[Blank]" : x.Notes) : x.Task.Title, x.StartsAtUtc, x.EndsAtUtc, x.Status, x.Notes, x.ConflictOverrideAtUtc, x.Version))
            .ToListAsync(cancellationToken);
        var meetings = await db.ExternalCalendarEvents.AsNoTracking().Where(x => !x.IsCancelled && x.Response != ExternalEventResponse.Declined &&
            x.StartsAtUtc < endsAtUtc && x.EndsAtUtc > startsAtUtc).OrderBy(x => x.StartsAtUtc)
            .Select(x => new TodayMeeting(x.Id, x.Subject, x.StartsAtUtc, x.EndsAtUtc, x.ShowAs)).ToListAsync(cancellationToken);
        var highEntities = await TaskQuery(db).Where(x => x.State == TaskState.Active && x.ArchivedAtUtc == null &&
            x.Priority >= TaskPriority.High && !x.TimeBlocks.Any(b => b.Status != TimeBlockStatus.Cancelled && b.EndsAtUtc > startsAtUtc))
            .OrderByDescending(x => x.Priority).ThenBy(x => x.DueDate).ToListAsync(cancellationToken);
        var availability = await db.ConnectedMicrosoftAccounts.AsNoTracking().Select(x => new
        {
            x.DisplayName,
            Syncs = x.Calendars.Select(c => c.SyncState).ToList()
        }).ToListAsync(cancellationToken);
        var freshness = availability.Select(x =>
        {
            var successful = x.Syncs.Where(s => s != null).Select(s => s!.LastSuccessfulSyncAtUtc).Max();
            var status = x.Syncs.Where(s => s != null).Select(s => s!.Status).DefaultIfEmpty(CalendarSyncStatus.NeverSynced).OrderBy(s => s).First();
            return new AvailabilityFreshness(x.DisplayName, successful, status, successful is null || successful < DateTimeOffset.UtcNow.AddHours(-1));
        }).ToList();

        var futureBlocks = await db.TimeBlocks.AsNoTracking().Where(x => x.Status != TimeBlockStatus.Cancelled && x.StartsAtUtc < futureEnd && x.EndsAtUtc > endsAtUtc)
            .OrderBy(x => x.StartsAtUtc).Select(x => new { x.Id, x.StartsAtUtc, x.EndsAtUtc }).ToListAsync(cancellationToken);
        var derived = new List<ScheduleConflict>();
        foreach (var block in futureBlocks)
            derived.AddRange((await conflicts.DetectAsync(block.StartsAtUtc, block.EndsAtUtc, block.Id, cancellationToken)).Select(c => c with { TimeBlockId = block.Id }));

        return new TodayDashboard(dueEntities.Select(ToSummary).ToList(), blocks, meetings, derived, highEntities.Select(ToSummary).ToList(), freshness);
    }

    private static IQueryable<TaskItem> TaskQuery(PlannerDbContext db) => db.Tasks.AsNoTracking().AsSplitQuery().Include(x => x.Type).Include(x => x.Project)
        .Include(x => x.TaskTags).ThenInclude(x => x.Tag).Include(x => x.Checklists).ThenInclude(x => x.Items);

    private static TaskSummary ToSummary(TaskItem task)
    {
        var items = task.Checklists.SelectMany(x => x.Items).ToList();
        return new(task.Id, task.Title, task.Description, task.State, task.Priority, task.SortOrder, task.Type.Name, task.TypeId,
            task.Project?.Name, task.ProjectId, task.DueDate, task.DueTime, task.TaskTags.Select(x => x.Tag.Name).Order().ToList(),
            items.Count(x => x.IsCompleted), items.Count, task.Version);
    }
}
