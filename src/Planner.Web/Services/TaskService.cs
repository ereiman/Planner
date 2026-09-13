using Microsoft.EntityFrameworkCore;
using Planner.Web.Data;
using Planner.Web.Domain;

namespace Planner.Web.Services;

public sealed class TaskService(IDbContextFactory<PlannerDbContext> factory) : ITaskService
{
    public async Task<IReadOnlyList<TaskSummary>> ListAsync(TaskState state = TaskState.Active, Guid? projectId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = SummaryQuery(db).Where(x => x.State == state && x.ArchivedAtUtc == null);
        if (projectId is not null)
            query = query.Where(x => x.ProjectId == projectId);
        return (await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(cancellationToken)).Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<TaskSummary>> SearchAsync(TaskFilter filter, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = SummaryQuery(db).Where(x => x.State == TaskState.Active && x.ArchivedAtUtc == null);
        if (filter.ProjectId is not null)
            query = query.Where(x => x.ProjectId == filter.ProjectId);
        if (filter.DueDate is not null)
            query = query.Where(x => x.DueDate == filter.DueDate);
        if (filter.HighPriority == true)
            query = query.Where(x => x.Priority >= TaskPriority.High);
        if (filter.DueSoon == true)
        {
            var soon = DateOnly.FromDateTime(DateTime.Today.AddDays(7));
            query = query.Where(x => x.DueDate != null && x.DueDate.Value <= soon);
        }
        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            var name = filter.Name.Trim();
            query = query.Where(x => x.Title.Contains(name));
        }
        var results = (await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(cancellationToken)).Select(ToSummary);
        if (!string.IsNullOrWhiteSpace(filter.Id))
        {
            var id = filter.Id.Trim();
            results = results.Where(task => task.Id.ToString().Contains(id, StringComparison.OrdinalIgnoreCase));
        }
        return results.ToList();
    }

    public async Task<IReadOnlyList<TaskSummary>> ListArchivedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return (await SummaryQuery(db).Where(x => x.ArchivedAtUtc != null).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.ArchivedAtUtc).Select(ToSummary).ToList();
    }

    public async Task<TaskSummary?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var task = await SummaryQuery(db).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return task is null ? null : ToSummary(task);
    }

    public async Task<TaskEditDetails?> GetEditDetailsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var task = await SummaryQuery(db).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return task is null ? null : new TaskEditDetails(ToSummary(task), task.TaskTags.Select(x => x.TagId).ToList(),
            task.Checklists.OrderBy(x => x.Order).Select(x => new ChecklistDto(x.Id, x.Title,
                x.Items.OrderBy(i => i.Order).Select(i => new ChecklistItemDto(i.Id, i.Title, i.IsCompleted)).ToList())).ToList());
    }

    public async Task<IReadOnlyList<TaskSummary>> ListDueAsync(DateOnly startsOn, DateOnly endsBefore, CancellationToken cancellationToken = default)
    {
        if (endsBefore <= startsOn)
            throw new ArgumentException("The due-date range must end after it starts.", nameof(endsBefore));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return (await SummaryQuery(db)
                .Where(x => x.DueDate >= startsOn && x.DueDate < endsBefore && x.ArchivedAtUtc == null)
                .OrderBy(x => x.DueDate).ThenBy(x => x.DueTime).ThenBy(x => x.Title)
                .ToListAsync(cancellationToken))
            .Select(ToSummary).ToList();
    }

    public async Task<Guid> CreateAsync(string title, Guid? projectId = null, Guid? typeId = null, TaskPriority priority = TaskPriority.None, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var order = (await db.Tasks.MaxAsync(x => (long?)x.SortOrder, cancellationToken) ?? -1) + 1;
        var task = new TaskItem
        {
            Title = title.Trim(),
            ProjectId = projectId,
            TypeId = typeId ?? BuiltInTaskTypes.TaskId,
            Priority = priority,
            SortOrder = order,
            State = TaskState.Active
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);
        return task.Id;
    }

    public async Task UpdateAsync(Guid id, string title, string description, Guid? projectId, Guid typeId, TaskPriority priority, DateOnly? dueDate, TimeOnly? dueTime = null, IReadOnlyCollection<Guid>? tagIds = null, int? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(description);
        if (dueTime is not null && dueDate is null)
            throw new ArgumentException("A due time requires a due date.", nameof(dueTime));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.Include(x => x.TaskTags).SingleAsync(x => x.Id == id, cancellationToken);
        if (expectedVersion is not null)
            db.Entry(task).Property(x => x.Version).OriginalValue = expectedVersion.Value;
        task.Title = title.Trim();
        task.Description = description.Trim();
        task.ProjectId = projectId;
        task.TypeId = typeId;
        task.Priority = priority;
        task.DueDate = dueDate;
        task.DueTime = dueTime;
        if (tagIds is not null)
        {
            db.TaskTags.RemoveRange(task.TaskTags.Where(x => !tagIds.Contains(x.TagId)));
            var existing = task.TaskTags.Select(x => x.TagId).ToHashSet();
            foreach (var tagId in tagIds.Where(x => !existing.Contains(x))) task.TaskTags.Add(new TaskTag { TagId = tagId });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStateAsync(Guid id, TaskState state, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.SingleAsync(x => x.Id == id, cancellationToken);
        task.State = state;
        task.CompletedAtUtc = state == TaskState.Completed ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.SingleAsync(x => x.Id == id, cancellationToken);
        task.ArchivedAtUtc = archived ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetDeletedAsync(Guid id, bool deleted, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.IgnoreQueryFilters().SingleAsync(x => x.Id == id, cancellationToken);
        task.DeletedAtUtc = deleted ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReorderAsync(Guid id, int direction, CancellationToken cancellationToken = default)
    {
        if (direction == 0) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var current = await db.Tasks.SingleOrDefaultAsync(x => x.Id == id && x.ArchivedAtUtc == null, cancellationToken);
        if (current is null) return;
        var tasks = await db.Tasks.Where(x => x.State == current.State && x.ArchivedAtUtc == null).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        var index = tasks.FindIndex(x => x.Id == id);
        var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= tasks.Count) return;
        var moved = tasks[index];
        tasks.RemoveAt(index);
        tasks.Insert(target, moved);
        for (var i = 0; i < tasks.Count; i++) tasks[i].SortOrder = i;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetTagsAsync(Guid id, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.Include(x => x.TaskTags).SingleAsync(x => x.Id == id, cancellationToken);
        db.TaskTags.RemoveRange(task.TaskTags.Where(x => !tagIds.Contains(x.TagId)));
        var existing = task.TaskTags.Select(x => x.TagId).ToHashSet();
        foreach (var tagId in tagIds.Where(x => !existing.Contains(x))) task.TaskTags.Add(new TaskTag { TagId = tagId });
        task.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Guid> AddChecklistAsync(Guid taskId, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.SingleAsync(x => x.Id == taskId, cancellationToken);
        var order = (await db.Checklists.Where(x => x.TaskId == taskId).MaxAsync(x => (long?)x.Order, cancellationToken) ?? -1) + 1;
        var checklist = new Checklist { TaskId = taskId, Title = title.Trim(), Order = order };
        db.Checklists.Add(checklist);
        task.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return checklist.Id;
    }

    public async Task<Guid> AddChecklistItemAsync(Guid checklistId, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var checklist = await db.Checklists.Include(x => x.Task).SingleAsync(x => x.Id == checklistId, cancellationToken);
        var order = (await db.ChecklistItems.Where(x => x.ChecklistId == checklistId).MaxAsync(x => (long?)x.Order, cancellationToken) ?? -1) + 1;
        var item = new ChecklistItem { ChecklistId = checklistId, Title = title.Trim(), Order = order };
        db.ChecklistItems.Add(item);
        checklist.Task.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return item.Id;
    }

    public async Task SetChecklistItemCompletedAsync(Guid itemId, bool completed, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var item = await db.ChecklistItems.Include(x => x.Checklist).ThenInclude(x => x.Task).SingleAsync(x => x.Id == itemId, cancellationToken);
        item.IsCompleted = completed;
        item.Checklist.Task.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static IQueryable<TaskItem> SummaryQuery(PlannerDbContext db) => db.Tasks.AsNoTracking().AsSplitQuery()
        .Include(x => x.Type).Include(x => x.Project).Include(x => x.TaskTags).ThenInclude(x => x.Tag)
        .Include(x => x.Checklists).ThenInclude(x => x.Items);

    private static TaskSummary ToSummary(TaskItem task)
    {
        var items = task.Checklists.SelectMany(x => x.Items).ToList();
        return new TaskSummary(task.Id, task.Title, task.Description, task.State, task.Priority, task.SortOrder,
            task.Type.Name, task.TypeId, task.Project?.Name, task.ProjectId, task.DueDate, task.DueTime,
            task.TaskTags.Select(x => x.Tag.Name).Order().ToList(), items.Count(x => x.IsCompleted), items.Count, task.Version);
    }
}
