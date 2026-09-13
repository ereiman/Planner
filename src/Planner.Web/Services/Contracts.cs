using Planner.Web.Domain;

namespace Planner.Web.Services;

public sealed record TaskFilter(string? Name = null, string? Id = null, Guid? ProjectId = null, DateOnly? DueDate = null, bool? HighPriority = null, bool? DueSoon = null);

public sealed record TaskSummary(
    Guid Id,
    string Title,
    string Description,
    TaskState State,
    TaskPriority Priority,
    long Order,
    string Type,
    Guid TypeId,
    string? Project,
    Guid? ProjectId,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    IReadOnlyList<string> Tags,
    int ChecklistDone,
    int ChecklistTotal,
    int Version);

public sealed record ChecklistItemDto(Guid Id, string Title, bool IsCompleted);
public sealed record ChecklistDto(Guid Id, string Title, IReadOnlyList<ChecklistItemDto> Items);
public sealed record TaskEditDetails(TaskSummary Task, IReadOnlyList<Guid> TagIds, IReadOnlyList<ChecklistDto> Checklists);

public sealed record ProjectSummary(Guid Id, string Name, string Color, bool IsArchived, long Order, int ActiveTasks, int CompletedTasks);
public sealed record TypeSummary(Guid Id, string Name, string Color);
public sealed record TagSummary(Guid Id, string Name, string Color);
public sealed record BoardSummary(Guid Id, string Name, BoardMode Mode, int TaskCount);
public sealed record BoardRuleDto(Guid Id, BoardRuleField Field, BoardRuleOperator Operator, string Value);
public sealed record BoardCard(Guid PlacementId, Guid TaskId, string Title, TaskPriority Priority, TaskState State, long Order, string? Project, bool IsManual, bool IsRuleMatch, bool IsSuppressed);
public sealed record BoardColumnDto(Guid Id, string Name, string Color, long Order, bool IsDefault, IReadOnlyList<BoardCard> Cards);
public sealed record BoardDetails(Guid Id, string Name, BoardMode Mode, IReadOnlyList<BoardColumnDto> Columns, IReadOnlyList<BoardRuleDto> Rules, bool IncludesCompleted, IReadOnlyList<BoardCard> SuppressedCards);
public sealed record TodayMeeting(Guid Id, string Subject, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, ExternalEventShowAs ShowAs);
public sealed record AvailabilityFreshness(string Account, DateTimeOffset? LastSuccessfulSyncAtUtc, CalendarSyncStatus Status, bool IsStale);
public sealed record TodayDashboard(IReadOnlyList<TaskSummary> DueTasks, IReadOnlyList<TimeBlockDto> TimeBlocks,
    IReadOnlyList<TodayMeeting> Meetings, IReadOnlyList<ScheduleConflict> FutureConflicts,
    IReadOnlyList<TaskSummary> UnscheduledHighPriorityTasks, IReadOnlyList<AvailabilityFreshness> Availability);

public interface ITodayQueryService
{
    Task<TodayDashboard> GetAsync(DateOnly today, TimeZoneInfo timeZone, CancellationToken cancellationToken = default);
}

public interface ITaskService
{
    Task<IReadOnlyList<TaskSummary>> ListAsync(TaskState state = TaskState.Active, Guid? projectId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskSummary>> SearchAsync(TaskFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskSummary>> ListArchivedAsync(CancellationToken cancellationToken = default);
    Task<TaskSummary?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TaskEditDetails?> GetEditDetailsAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskSummary>> ListDueAsync(DateOnly startsOn, DateOnly endsBefore, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(string title, Guid? projectId = null, Guid? typeId = null, TaskPriority priority = TaskPriority.None, CancellationToken cancellationToken = default);
    Task UpdateAsync(Guid id, string title, string description, Guid? projectId, Guid typeId, TaskPriority priority, DateOnly? dueDate, TimeOnly? dueTime = null, IReadOnlyCollection<Guid>? tagIds = null, int? expectedVersion = null, CancellationToken cancellationToken = default);
    Task SetStateAsync(Guid id, TaskState state, CancellationToken cancellationToken = default);
    Task SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default);
    Task SetDeletedAsync(Guid id, bool deleted, CancellationToken cancellationToken = default);
    Task ReorderAsync(Guid id, int direction, CancellationToken cancellationToken = default);
    Task SetTagsAsync(Guid id, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken = default);
    Task<Guid> AddChecklistAsync(Guid taskId, string title, CancellationToken cancellationToken = default);
    Task<Guid> AddChecklistItemAsync(Guid checklistId, string title, CancellationToken cancellationToken = default);
    Task SetChecklistItemCompletedAsync(Guid itemId, bool completed, CancellationToken cancellationToken = default);
}

public sealed record TimeBlockDto(
    Guid Id,
    Guid? TaskId,
    string TaskTitle,
    string Title,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    TimeBlockStatus Status,
    string Notes,
    DateTimeOffset? ConflictOverrideAtUtc,
    int Version);

public enum ScheduleConflictSeverity { Warning, Blocking }
public enum ScheduleConflictSource { PlannerBlock, MicrosoftCalendar, Availability }

public sealed record ScheduleConflict(
    Guid TimeBlockId,
    Guid TaskId,
    string TaskTitle,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc)
{
    public ScheduleConflictSeverity Severity { get; init; } = ScheduleConflictSeverity.Blocking;
    public ScheduleConflictSource Source { get; init; } = ScheduleConflictSource.PlannerBlock;
    public Guid? MicrosoftAccountId { get; init; }
}

public sealed record ScheduleSaveResult(bool Saved, TimeBlockDto? TimeBlock, IReadOnlyList<ScheduleConflict> Conflicts)
{
    public bool RequiresOverride => !Saved && Conflicts.Count > 0;
}

public interface IConflictDetector
{
    Task<IReadOnlyList<ScheduleConflict>> DetectAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, Guid? excludingTimeBlockId = null, CancellationToken cancellationToken = default);
}

public interface IScheduleService
{
    Task<IReadOnlyList<TimeBlockDto>> ListAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default);
    Task<ScheduleSaveResult> CreateAsync(Guid? taskId, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, string notes = "", bool overrideConflicts = false, CancellationToken cancellationToken = default);
    Task<ScheduleSaveResult> UpdateAsync(Guid id, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, string notes, bool overrideConflicts = false, Guid? taskId = null, CancellationToken cancellationToken = default);
    Task SetStatusAsync(Guid id, TimeBlockStatus status, CancellationToken cancellationToken = default);
    Task SetDeletedAsync(Guid id, bool deleted, CancellationToken cancellationToken = default);
}

public interface IProjectService
{
    Task<IReadOnlyList<ProjectSummary>> ListAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(string name, string color, CancellationToken cancellationToken = default);
    Task RenameAsync(Guid id, string name, CancellationToken cancellationToken = default);
    Task SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default);
    Task SetDeletedAsync(Guid id, bool deleted, CancellationToken cancellationToken = default);
}

public interface IMetadataService
{
    Task<IReadOnlyList<TypeSummary>> ListTypesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TagSummary>> ListTagsAsync(CancellationToken cancellationToken = default);
    Task<Guid> CreateTagAsync(string name, string color, CancellationToken cancellationToken = default);
}

public interface IBoardService
{
    Task<IReadOnlyList<BoardSummary>> ListAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<BoardDetails?> GetAsync(Guid id, bool includeCompleted = false, CancellationToken cancellationToken = default);
    Task<Guid> CreateAsync(string name, BoardMode mode, CancellationToken cancellationToken = default);
    Task SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default);
    Task SetDeletedAsync(Guid id, bool deleted, CancellationToken cancellationToken = default);
    Task AddPlacementAsync(Guid boardId, Guid taskId, Guid? columnId = null, CancellationToken cancellationToken = default);
    Task RemovePlacementAsync(Guid boardId, Guid taskId, CancellationToken cancellationToken = default);
    Task MovePlacementAsync(Guid boardId, Guid taskId, Guid columnId, int? targetIndex = null, CancellationToken cancellationToken = default);
    Task AddRuleAsync(Guid boardId, BoardRuleField field, string value, CancellationToken cancellationToken = default);
    Task AddRuleAsync(Guid boardId, BoardRuleField field, BoardRuleOperator ruleOperator, string value, CancellationToken cancellationToken = default);
    Task ApplyRulesAsync(Guid boardId, CancellationToken cancellationToken = default);
    Task<Guid> AddColumnAsync(Guid boardId, string name, string color = "#03A7E1", CancellationToken cancellationToken = default);
    Task RenameColumnAsync(Guid boardId, Guid columnId, string name, CancellationToken cancellationToken = default);
    Task ReorderColumnAsync(Guid boardId, Guid columnId, int direction, CancellationToken cancellationToken = default);
    Task RemoveColumnAsync(Guid boardId, Guid columnId, Guid? destinationColumnId, CancellationToken cancellationToken = default);
    Task SetDefaultColumnAsync(Guid boardId, Guid columnId, CancellationToken cancellationToken = default);
    Task RemoveRuleAsync(Guid boardId, Guid ruleId, CancellationToken cancellationToken = default);
    Task RestoreRulePlacementAsync(Guid boardId, Guid taskId, CancellationToken cancellationToken = default);
    Task<Guid> QuickCreateAsync(Guid boardId, Guid columnId, string title, CancellationToken cancellationToken = default);
}
