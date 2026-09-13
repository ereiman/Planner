namespace Planner.Web.Domain;

public enum TaskState { Active = 1, Completed = 2, Cancelled = 3 }
public enum TaskPriority { None, Low, Medium, High, Urgent }
public enum BoardMode { Manual, Smart, Hybrid }
public enum BoardRuleField { Project, Type, Tag, Priority, State }
public enum BoardRuleOperator { Equals, NotEquals, ContainsTag, DoesNotContainTag, AtLeastPriority }
public enum TimeBlockStatus { Planned, Completed, Cancelled }
public enum MicrosoftAccountConnectionStatus { Connected, ReconnectRequired }
public enum CalendarSyncStatus { NeverSynced, Syncing, Current, Stale, Failed }
public enum ExternalEventSensitivity { Normal, Personal, Private, Confidential }
public enum ExternalEventShowAs { Unknown, Free, Tentative, Busy, OutOfOffice, WorkingElsewhere }
public enum ExternalEventResponse { None, Organizer, TentativelyAccepted, Accepted, Declined, NotResponded }
public enum ExternalEventKind { SingleInstance, Occurrence, Exception, SeriesMaster }

public sealed class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TaskState State { get; set; } = TaskState.Active;
    public TaskPriority Priority { get; set; }
    public long SortOrder { get; set; }
    public DateOnly? DueDate { get; set; }
    public TimeOnly? DueTime { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? ArchivedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public int Version { get; set; }
    public Guid TypeId { get; set; }
    public TaskType Type { get; set; } = null!;
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public ICollection<TaskTag> TaskTags { get; set; } = [];
    public ICollection<Checklist> Checklists { get; set; } = [];
    public ICollection<BoardPlacement> Placements { get; set; } = [];
    public ICollection<TimeBlock> TimeBlocks { get; set; } = [];
}

public sealed class TimeBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TaskId { get; set; }
    public TaskItem? Task { get; set; }
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset EndsAtUtc { get; set; }
    public TimeBlockStatus Status { get; set; } = TimeBlockStatus.Planned;
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset? ConflictOverrideAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public int Version { get; set; }
}

public sealed class ConnectedMicrosoftAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MsalHomeAccountId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string MicrosoftUserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public MicrosoftAccountConnectionStatus ConnectionStatus { get; set; } = MicrosoftAccountConnectionStatus.Connected;
    public DateTimeOffset ConnectedAtUtc { get; set; }
    public DateTimeOffset? LastAuthenticatedAtUtc { get; set; }
    public string LastError { get; set; } = string.Empty;
    public ICollection<ExternalCalendar> Calendars { get; set; } = [];
}

public sealed class ExternalCalendar
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConnectedMicrosoftAccountId { get; set; }
    public ConnectedMicrosoftAccount Account { get; set; } = null!;
    public string ExternalId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public ICollection<ExternalCalendarEvent> Events { get; set; } = [];
    public CalendarSyncState? SyncState { get; set; }
}

public sealed class ExternalCalendarEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExternalCalendarId { get; set; }
    public ExternalCalendar Calendar { get; set; } = null!;
    public string ExternalId { get; set; } = string.Empty;
    public string ICalUId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset EndsAtUtc { get; set; }
    public bool IsAllDay { get; set; }
    public bool IsCancelled { get; set; }
    public ExternalEventSensitivity Sensitivity { get; set; }
    public ExternalEventShowAs ShowAs { get; set; }
    public ExternalEventResponse Response { get; set; }
    public ExternalEventKind Kind { get; set; }
    public string SeriesMasterId { get; set; } = string.Empty;
    public DateTimeOffset LastModifiedAtUtc { get; set; }
}

public sealed class CalendarSyncState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExternalCalendarId { get; set; }
    public ExternalCalendar Calendar { get; set; } = null!;
    public string DeltaCursor { get; set; } = string.Empty;
    public DateTimeOffset HorizonStartsAtUtc { get; set; }
    public DateTimeOffset HorizonEndsAtUtc { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? LastSuccessfulSyncAtUtc { get; set; }
    public CalendarSyncStatus Status { get; set; } = CalendarSyncStatus.NeverSynced;
    public string LastError { get; set; } = string.Empty;
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#03A7E1";
    public long Order { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ArchivedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public int Version { get; set; }
    public ICollection<TaskItem> Tasks { get; set; } = [];
}

public sealed class TaskType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public ICollection<TaskItem> Tasks { get; set; } = [];
}

public static class BuiltInTaskTypes
{
    public static readonly Guid TaskId = Guid.Parse("b953ebc8-41c1-4480-9ba5-c017f3f329c1");
    public static readonly Guid BugId = Guid.Parse("0d18cc2f-6b6f-4d21-8471-1f34a59cd18b");
}

public sealed class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#607D8B";
    public ICollection<TaskTag> TaskTags { get; set; } = [];
}

public sealed class TaskTag
{
    public Guid TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public Guid TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}

public sealed class Checklist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public long Order { get; set; }
    public ICollection<ChecklistItem> Items { get; set; } = [];
}

public sealed class ChecklistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChecklistId { get; set; }
    public Checklist Checklist { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public long Order { get; set; }
}

public sealed class Board
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public BoardMode Mode { get; set; }
    public long Order { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ArchivedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public int Version { get; set; }
    public ICollection<BoardColumn> Columns { get; set; } = [];
    public ICollection<BoardPlacement> Placements { get; set; } = [];
    public ICollection<BoardRule> Rules { get; set; } = [];
}

public sealed class BoardColumn
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BoardId { get; set; }
    public Board Board { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#03A7E1";
    public long Order { get; set; }
    public bool IsDefault { get; set; }
    public ICollection<BoardPlacement> Placements { get; set; } = [];
}

public sealed class BoardPlacement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BoardId { get; set; }
    public Board Board { get; set; } = null!;
    public Guid TaskId { get; set; }
    public TaskItem Task { get; set; } = null!;
    public Guid ColumnId { get; set; }
    public BoardColumn Column { get; set; } = null!;
    public long Order { get; set; }
    public bool IsManual { get; set; }
    public bool IsRuleMatch { get; set; }
    public bool IsSuppressed { get; set; }
    public DateTimeOffset? RemovedAtUtc { get; set; }
    public bool IsEffective => RemovedAtUtc is null && !IsSuppressed && (IsManual || IsRuleMatch);
}

public sealed class BoardRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BoardId { get; set; }
    public Board Board { get; set; } = null!;
    public BoardRuleField Field { get; set; }
    public BoardRuleOperator Operator { get; set; }
    public string Value { get; set; } = string.Empty;
}
