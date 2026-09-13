using Microsoft.EntityFrameworkCore;
using Planner.Web.Data;
using Planner.Web.Domain;

namespace Planner.Web.Services;

public sealed class BoardService(IDbContextFactory<PlannerDbContext> factory) : IBoardService
{
    public async Task<IReadOnlyList<BoardSummary>> ListAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Boards.AsNoTracking();
        if (!includeArchived) query = query.Where(x => x.ArchivedAtUtc == null);
        return await query.OrderBy(x => x.Order).Select(x => new BoardSummary(x.Id, x.Name, x.Mode,
            x.Placements.Count(p => p.RemovedAtUtc == null && !p.IsSuppressed && (p.IsManual || p.IsRuleMatch) &&
                p.Task.State == TaskState.Active && p.Task.ArchivedAtUtc == null))).ToListAsync(cancellationToken);
    }

    public async Task<BoardDetails?> GetAsync(Guid id, bool includeCompleted = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var board = await db.Boards.AsNoTracking().Include(x => x.Rules)
            .Include(x => x.Columns).ThenInclude(x => x.Placements).ThenInclude(x => x.Task).ThenInclude(x => x.Project)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (board is null) return null;
        var columns = board.Columns.OrderBy(x => x.Order).Select(column => new BoardColumnDto(
            column.Id, column.Name, column.Color, column.Order, column.IsDefault,
            column.Placements.Where(x => x.IsEffective && x.Task.ArchivedAtUtc == null &&
                    (x.Task.State == TaskState.Active || includeCompleted && x.Task.State == TaskState.Completed))
                .OrderBy(x => x.Order).Select(x => new BoardCard(x.Id, x.TaskId, x.Task.Title, x.Task.Priority,
                    x.Task.State, x.Order, x.Task.Project?.Name, x.IsManual, x.IsRuleMatch, x.IsSuppressed)).ToList())).ToList();
        var suppressed = board.Columns.SelectMany(x => x.Placements).Where(x => x.IsSuppressed && x.IsRuleMatch && x.Task.ArchivedAtUtc == null)
            .Select(x => new BoardCard(x.Id, x.TaskId, x.Task.Title, x.Task.Priority, x.Task.State, x.Order, x.Task.Project?.Name, x.IsManual, x.IsRuleMatch, x.IsSuppressed)).ToList();
        return new BoardDetails(board.Id, board.Name, board.Mode, columns,
            board.Rules.OrderBy(x => x.Id).Select(x => new BoardRuleDto(x.Id, x.Field, x.Operator, x.Value)).ToList(), includeCompleted, suppressed);
    }

    public async Task<Guid> CreateAsync(string name, BoardMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var order = (await db.Boards.MaxAsync(x => (long?)x.Order, cancellationToken) ?? -1) + 1;
        var board = new Board { Name = name.Trim(), Mode = mode, Order = order };
        board.Columns.Add(new BoardColumn { Name = "To do", Order = 0, Color = "#03A7E1", IsDefault = true });
        board.Columns.Add(new BoardColumn { Name = "Doing", Order = 1, Color = "#1976D2" });
        board.Columns.Add(new BoardColumn { Name = "Done", Order = 2, Color = "#2E7D32" });
        db.Boards.Add(board);
        await db.SaveChangesAsync(cancellationToken);
        return board.Id;
    }

    public async Task SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var board = await db.Boards.SingleAsync(x => x.Id == id, cancellationToken);
        board.ArchivedAtUtc = archived ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetDeletedAsync(Guid id, bool deleted, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var board = await db.Boards.IgnoreQueryFilters().SingleAsync(x => x.Id == id, cancellationToken);
        board.DeletedAtUtc = deleted ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddPlacementAsync(Guid boardId, Guid taskId, Guid? columnId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var board = await db.Boards.Include(x => x.Columns).SingleAsync(x => x.Id == boardId && x.ArchivedAtUtc == null, cancellationToken);
        _ = await db.Tasks.SingleAsync(x => x.Id == taskId && x.ArchivedAtUtc == null, cancellationToken);
        var placement = await db.BoardPlacements.SingleOrDefaultAsync(x => x.BoardId == board.Id && x.TaskId == taskId, cancellationToken);
        if (placement is not null)
        {
            placement.IsManual = true;
            placement.IsSuppressed = false;
            placement.RemovedAtUtc = null;
            if (columnId is not null && placement.ColumnId != columnId)
                await MoveExistingPlacement(db, placement, boardId, columnId.Value, null, cancellationToken);
            board.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var target = columnId is null ? board.Columns.Single(x => x.IsDefault) : board.Columns.Single(x => x.Id == columnId);
        var order = (await db.BoardPlacements.Where(x => x.ColumnId == target.Id && x.RemovedAtUtc == null).MaxAsync(x => (long?)x.Order, cancellationToken) ?? -1) + 1;
        db.BoardPlacements.Add(new BoardPlacement { BoardId = boardId, TaskId = taskId, ColumnId = target.Id, Order = order, IsManual = true });
        board.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemovePlacementAsync(Guid boardId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var placement = await db.BoardPlacements.SingleOrDefaultAsync(x => x.BoardId == boardId && x.TaskId == taskId, cancellationToken);
        if (placement is null) return;
        var board = await db.Boards.SingleAsync(x => x.Id == boardId, cancellationToken);
        placement.IsManual = false;
        if (placement.IsRuleMatch)
        {
            placement.IsSuppressed = true;
            placement.RemovedAtUtc = null;
        }
        else
        {
            placement.IsSuppressed = false;
            placement.RemovedAtUtc ??= DateTimeOffset.UtcNow;
        }
        board.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MovePlacementAsync(Guid boardId, Guid taskId, Guid columnId, int? targetIndex = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var board = await db.Boards.SingleAsync(x => x.Id == boardId, cancellationToken);
        var placement = await db.BoardPlacements.SingleAsync(x => x.BoardId == board.Id && x.TaskId == taskId && x.RemovedAtUtc == null && !x.IsSuppressed, cancellationToken);
        await MoveExistingPlacement(db, placement, boardId, columnId, targetIndex, cancellationToken);
        board.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task AddRuleAsync(Guid boardId, BoardRuleField field, string value, CancellationToken cancellationToken = default) =>
        AddRuleAsync(boardId, field, BoardRuleOperator.Equals, value, cancellationToken);

    public async Task AddRuleAsync(Guid boardId, BoardRuleField field, BoardRuleOperator ruleOperator, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmedValue = value.Trim();
        ValidateRule(field, ruleOperator, trimmedValue);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var board = await db.Boards.Include(x => x.Rules).Include(x => x.Columns).SingleAsync(x => x.Id == boardId, cancellationToken);
        if (board.Rules.Any(x => x.Field == field && x.Operator == ruleOperator && x.Value == trimmedValue)) return;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rule = new BoardRule { BoardId = boardId, Board = board, Field = field, Operator = ruleOperator, Value = trimmedValue };
        db.BoardRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
        await ApplyRulesAsync(db, board, cancellationToken);
        if (board.Mode == BoardMode.Manual) board.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ApplyRulesAsync(Guid boardId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var board = await db.Boards.Include(x => x.Rules).Include(x => x.Columns).SingleAsync(x => x.Id == boardId, cancellationToken);
        await ApplyRulesAsync(db, board, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task ApplyRulesAsync(PlannerDbContext db, Board board, CancellationToken cancellationToken)
    {
        if (board.Mode == BoardMode.Manual) return;
        IQueryable<TaskItem> query = db.Tasks.Where(x => x.ArchivedAtUtc == null);
        foreach (var rule in board.Rules) query = ApplyRule(query, rule);
        var matchingIds = board.Rules.Count == 0 ? [] : await query.Select(x => x.Id).ToHashSetAsync(cancellationToken);
        var placements = await db.BoardPlacements.Where(x => x.BoardId == board.Id).ToListAsync(cancellationToken);
        var byTask = placements.ToDictionary(x => x.TaskId);
        var defaultColumn = board.Columns.Single(x => x.IsDefault);
        var nextOrder = (await db.BoardPlacements.Where(x => x.ColumnId == defaultColumn.Id && x.RemovedAtUtc == null).MaxAsync(x => (long?)x.Order, cancellationToken) ?? -1) + 1;

        foreach (var taskId in matchingIds)
        {
            if (byTask.TryGetValue(taskId, out var placement))
            {
                placement.IsRuleMatch = true;
                if (!placement.IsSuppressed) placement.RemovedAtUtc = null;
            }
            else
            {
                db.BoardPlacements.Add(new BoardPlacement { BoardId = board.Id, TaskId = taskId, ColumnId = defaultColumn.Id, Order = nextOrder++, IsRuleMatch = true });
            }
        }
        foreach (var placement in placements.Where(x => !matchingIds.Contains(x.TaskId)))
        {
            placement.IsRuleMatch = false;
            if (board.Mode == BoardMode.Smart) placement.IsManual = false;
            if (!placement.IsManual) placement.RemovedAtUtc ??= DateTimeOffset.UtcNow;
        }
        if (db.ChangeTracker.HasChanges()) board.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public async Task<Guid> AddColumnAsync(Guid boardId, string name, string color = "#03A7E1", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var board = await db.Boards.Include(x => x.Columns).SingleAsync(x => x.Id == boardId, cancellationToken);
        var column = new BoardColumn { BoardId = boardId, Name = name.Trim(), Color = color, Order = board.Columns.Count == 0 ? 0 : board.Columns.Max(x => x.Order) + 1, IsDefault = board.Columns.Count == 0 };
        db.BoardColumns.Add(column);
        board.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return column.Id;
    }

    public async Task RenameColumnAsync(Guid boardId, Guid columnId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var column = await db.BoardColumns.Include(x => x.Board).SingleAsync(x => x.Id == columnId && x.BoardId == boardId, cancellationToken);
        column.Name = name.Trim(); column.Board.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReorderColumnAsync(Guid boardId, Guid columnId, int direction, CancellationToken cancellationToken = default)
    {
        if (direction == 0) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var columns = await db.BoardColumns.Where(x => x.BoardId == boardId).OrderBy(x => x.Order).ToListAsync(cancellationToken);
        var index = columns.FindIndex(x => x.Id == columnId); var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= columns.Count) return;
        (columns[index], columns[target]) = (columns[target], columns[index]);
        for (var i = 0; i < columns.Count; i++) columns[i].Order = i;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveColumnAsync(Guid boardId, Guid columnId, Guid? destinationColumnId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var board = await db.Boards.Include(x => x.Columns).ThenInclude(x => x.Placements).SingleAsync(x => x.Id == boardId, cancellationToken);
        if (board.Columns.Count <= 1) throw new InvalidOperationException("A board must have at least one column.");
        var column = board.Columns.Single(x => x.Id == columnId);
        var hasPlacements = column.Placements.Count != 0;
        if (hasPlacements && destinationColumnId is null) throw new InvalidOperationException("A destination column is required for a column with placement history.");
        if (destinationColumnId == columnId) throw new ArgumentException("Destination must be another column.", nameof(destinationColumnId));
        if (destinationColumnId is { } destination)
        {
            var target = board.Columns.Single(x => x.Id == destination);
            var next = target.Placements.Where(x => x.RemovedAtUtc == null).Select(x => x.Order).DefaultIfEmpty(-1).Max() + 1;
            foreach (var placement in column.Placements.ToList()) { placement.ColumnId = target.Id; placement.Column = target; target.Placements.Add(placement); placement.Order = next++; }
        }
        BoardColumn? replacementDefault = null;
        if (column.IsDefault)
        {
            replacementDefault = board.Columns.First(x => x.Id != column.Id);
            column.IsDefault = false;
            await db.SaveChangesAsync(cancellationToken); // Satisfy the filtered unique default index before assigning its replacement.
            replacementDefault.IsDefault = true;
        }
        db.BoardColumns.Remove(column);
        var ordered = board.Columns.Where(x => x.Id != columnId).OrderBy(x => x.Order).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        board.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetDefaultColumnAsync(Guid boardId, Guid columnId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var board = await db.Boards.Include(x => x.Columns).SingleAsync(x => x.Id == boardId, cancellationToken);
        var target = board.Columns.SingleOrDefault(x => x.Id == columnId);
        if (target is null) throw new ArgumentException("Column does not belong to board.", nameof(columnId));
        if (target.IsDefault) return;
        var current = board.Columns.FirstOrDefault(x => x.IsDefault);
        if (current is not null)
        {
            current.IsDefault = false;
            await db.SaveChangesAsync(cancellationToken);
        }
        target.IsDefault = true;
        board.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveRuleAsync(Guid boardId, Guid ruleId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var board = await db.Boards.Include(x => x.Rules).Include(x => x.Columns).SingleAsync(x => x.Id == boardId, cancellationToken);
        var rule = board.Rules.Single(x => x.Id == ruleId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        board.Rules.Remove(rule);
        db.BoardRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        await ApplyRulesAsync(db, board, cancellationToken);
        if (board.Mode == BoardMode.Manual) board.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RestoreRulePlacementAsync(Guid boardId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var placement = await db.BoardPlacements.SingleAsync(x => x.BoardId == boardId && x.TaskId == taskId && x.IsRuleMatch, cancellationToken);
        placement.IsSuppressed = false; placement.RemovedAtUtc = null; await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Guid> QuickCreateAsync(Guid boardId, Guid columnId, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        _ = await db.BoardColumns.SingleAsync(x => x.BoardId == boardId && x.Id == columnId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var task = new TaskItem
        {
            Title = title.Trim(),
            TypeId = BuiltInTaskTypes.TaskId,
            State = TaskState.Active,
            SortOrder = (await db.Tasks.MaxAsync(x => (long?)x.SortOrder, cancellationToken) ?? -1) + 1
        };
        db.Tasks.Add(task);
        db.BoardPlacements.Add(new BoardPlacement
        {
            BoardId = boardId,
            ColumnId = columnId,
            Task = task,
            IsManual = true,
            Order = (await db.BoardPlacements.Where(x => x.ColumnId == columnId && x.RemovedAtUtc == null).MaxAsync(x => (long?)x.Order, cancellationToken) ?? -1) + 1
        });
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return task.Id;
    }

    private static async Task MoveExistingPlacement(PlannerDbContext db, BoardPlacement placement, Guid boardId, Guid columnId, int? targetIndex, CancellationToken cancellationToken)
    {
        _ = await db.BoardColumns.SingleAsync(x => x.Id == columnId && x.BoardId == boardId, cancellationToken);
        var cards = await db.BoardPlacements.Where(x => x.ColumnId == columnId && x.Id != placement.Id && x.RemovedAtUtc == null && !x.IsSuppressed && (x.IsManual || x.IsRuleMatch)).OrderBy(x => x.Order).ToListAsync(cancellationToken);
        var index = Math.Clamp(targetIndex ?? cards.Count, 0, cards.Count);
        cards.Insert(index, placement);
        placement.ColumnId = columnId;
        for (var i = 0; i < cards.Count; i++) cards[i].Order = i;
    }

    private static void ValidateRule(BoardRuleField field, BoardRuleOperator ruleOperator, string value)
    {
        var validCombination = ruleOperator switch
        {
            BoardRuleOperator.ContainsTag or BoardRuleOperator.DoesNotContainTag => field == BoardRuleField.Tag,
            BoardRuleOperator.AtLeastPriority => field == BoardRuleField.Priority,
            BoardRuleOperator.Equals or BoardRuleOperator.NotEquals => true,
            _ => false
        };
        var validValue = field switch
        {
            BoardRuleField.Project or BoardRuleField.Type or BoardRuleField.Tag => Guid.TryParse(value, out _),
            BoardRuleField.Priority => Enum.TryParse<TaskPriority>(value, true, out _),
            BoardRuleField.State => Enum.TryParse<TaskState>(value, true, out _),
            _ => false
        };
        if (!validCombination || !validValue) throw new ArgumentException("The rule field, operator, and value are incompatible.", nameof(value));
    }

    private static IQueryable<TaskItem> ApplyRule(IQueryable<TaskItem> query, BoardRule rule)
    {
        if (rule.Operator is BoardRuleOperator.ContainsTag or BoardRuleOperator.DoesNotContainTag && Guid.TryParse(rule.Value, out var tagId))
            return rule.Operator == BoardRuleOperator.ContainsTag ? query.Where(x => x.TaskTags.Any(t => t.TagId == tagId)) : query.Where(x => !x.TaskTags.Any(t => t.TagId == tagId));
        if (rule.Operator == BoardRuleOperator.AtLeastPriority && Enum.TryParse<TaskPriority>(rule.Value, true, out var minimum))
            return query.Where(x => x.Priority >= minimum);
        var equals = rule.Operator == BoardRuleOperator.Equals;
        return rule.Field switch
        {
            BoardRuleField.Project when Guid.TryParse(rule.Value, out var id) => equals ? query.Where(x => x.ProjectId == id) : query.Where(x => x.ProjectId != id),
            BoardRuleField.Type when Guid.TryParse(rule.Value, out var id) => equals ? query.Where(x => x.TypeId == id) : query.Where(x => x.TypeId != id),
            BoardRuleField.Tag when Guid.TryParse(rule.Value, out var id) => equals ? query.Where(x => x.TaskTags.Any(t => t.TagId == id)) : query.Where(x => !x.TaskTags.Any(t => t.TagId == id)),
            BoardRuleField.Priority when Enum.TryParse<TaskPriority>(rule.Value, true, out var priority) => equals ? query.Where(x => x.Priority == priority) : query.Where(x => x.Priority != priority),
            BoardRuleField.State when Enum.TryParse<TaskState>(rule.Value, true, out var state) => equals ? query.Where(x => x.State == state) : query.Where(x => x.State != state),
            _ => query.Where(x => false)
        };
    }
}
