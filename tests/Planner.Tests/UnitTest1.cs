using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Planner.Web.Data;
using Planner.Web.Domain;

namespace Planner.Tests;

public sealed class PlannerInvariantTests
{
    [Fact]
    public async Task TaskLifecycleArchiveAndSoftDeleteAreIndependent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.Tasks.CreateAsync("Captured");
        Assert.Single(await database.Tasks.ListAsync(TaskState.Active));
        Assert.Empty(await database.Tasks.ListAsync(TaskState.Completed));

        await database.Tasks.SetStateAsync(id, TaskState.Completed);
        await database.Tasks.SetArchivedAsync(id, true);
        Assert.Empty(await database.Tasks.ListAsync(TaskState.Completed));
        Assert.Single(await database.Tasks.ListArchivedAsync());

        await database.Tasks.SetArchivedAsync(id, false);
        Assert.Single(await database.Tasks.ListAsync(TaskState.Completed));
        await database.Tasks.SetStateAsync(id, TaskState.Cancelled);
        await database.Tasks.SetDeletedAsync(id, true);
        Assert.Empty(await database.Tasks.ListAsync(TaskState.Cancelled));

        await using (var context = database.CreateDbContext())
        {
            var task = await context.Tasks.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
            Assert.Equal(TaskState.Cancelled, task.State);
            Assert.NotNull(task.DeletedAtUtc);
            Assert.Null(task.ArchivedAtUtc);
            Assert.Null(task.CompletedAtUtc);
            Assert.True(task.CreatedAtUtc <= task.UpdatedAtUtc);
            Assert.True(task.Version >= 6);
        }
        await database.Tasks.SetDeletedAsync(id, false);
        Assert.Single(await database.Tasks.ListAsync(TaskState.Cancelled));
    }

    [Fact]
    public async Task DateOnlyAndTimedDueValuesRoundTripAndTimeRequiresDate()
    {
        await using var database = await TestDatabase.CreateAsync();
        var dateOnlyId = await database.Tasks.CreateAsync("Date only");
        var timedId = await database.Tasks.CreateAsync("Timed");
        var dueDate = new DateOnly(2032, 2, 29);
        var dueTime = new TimeOnly(14, 35);

        await database.Tasks.UpdateAsync(dateOnlyId, "Date only", "", null, BuiltInTaskTypes.TaskId, TaskPriority.Low, dueDate);
        await database.Tasks.UpdateAsync(timedId, "Timed", "", null, BuiltInTaskTypes.TaskId, TaskPriority.High, dueDate, dueTime);

        var dateOnly = await database.Tasks.GetAsync(dateOnlyId);
        var timed = await database.Tasks.GetAsync(timedId);
        Assert.Equal(dueDate, dateOnly!.DueDate);
        Assert.Null(dateOnly.DueTime);
        Assert.Equal(dueDate, timed!.DueDate);
        Assert.Equal(dueTime, timed.DueTime);
        Assert.Equal(TaskState.Active, timed.State);
        await Assert.ThrowsAsync<ArgumentException>(() => database.Tasks.UpdateAsync(timedId, "Timed", "", null, BuiltInTaskTypes.TaskId, TaskPriority.High, null, dueTime));
    }

    [Fact]
    public async Task ProjectAndBoardArchiveDeleteAndAuditSemanticsAreIndependent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var projectId = await database.Projects.CreateAsync("Project", "#123456");
        var boardId = await database.Boards.CreateAsync("Board", BoardMode.Manual);

        await database.Projects.SetArchivedAsync(projectId, true);
        await database.Boards.SetArchivedAsync(boardId, true);
        Assert.Empty(await database.Projects.ListAsync());
        Assert.Empty(await database.Boards.ListAsync());
        Assert.Single(await database.Projects.ListAsync(true));
        Assert.Single(await database.Boards.ListAsync(true));

        await database.Projects.SetArchivedAsync(projectId, false);
        await database.Boards.SetArchivedAsync(boardId, false);
        await database.Projects.SetDeletedAsync(projectId, true);
        await database.Boards.SetDeletedAsync(boardId, true);
        Assert.Empty(await database.Projects.ListAsync(true));
        Assert.Empty(await database.Boards.ListAsync(true));
        await using var context = database.CreateDbContext();
        var project = await context.Projects.IgnoreQueryFilters().SingleAsync(x => x.Id == projectId);
        var board = await context.Boards.IgnoreQueryFilters().SingleAsync(x => x.Id == boardId);
        Assert.NotNull(project.DeletedAtUtc);
        Assert.NotNull(board.DeletedAtUtc);
        Assert.True(project.Version > 1);
        Assert.True(board.Version > 1);
    }

    [Fact]
    public async Task ManualRemovalRetainsHistoryAndAddingRestoresSamePlacement()
    {
        await using var database = await TestDatabase.CreateAsync();
        var taskId = await ActiveTask(database, "Manual");
        var boardId = await database.Boards.CreateAsync("Manual board", BoardMode.Manual);
        await database.Boards.AddPlacementAsync(boardId, taskId);
        var before = await Placement(database, boardId, taskId);

        await database.Boards.RemovePlacementAsync(boardId, taskId);
        Assert.Empty((await database.Boards.GetAsync(boardId))!.Columns.SelectMany(x => x.Cards));
        var removed = await Placement(database, boardId, taskId);
        Assert.Equal(before.Id, removed.Id);
        Assert.NotNull(removed.RemovedAtUtc);
        Assert.False(removed.IsManual);

        await database.Boards.AddPlacementAsync(boardId, taskId);
        var restored = await Placement(database, boardId, taskId);
        Assert.Equal(before.Id, restored.Id);
        Assert.Null(restored.RemovedAtUtc);
        Assert.True(restored.IsManual);
    }

    [Fact]
    public async Task RuleRemovalSuppressesAndManualRestoreSurvivesReconciliation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var taskId = await ActiveTask(database, "Rule", priority: TaskPriority.High);
        var boardId = await database.Boards.CreateAsync("Hybrid rules", BoardMode.Hybrid);
        await database.Boards.AddRuleAsync(boardId, BoardRuleField.Priority, BoardRuleOperator.AtLeastPriority, "High");
        var original = await Placement(database, boardId, taskId);

        await database.Boards.RemovePlacementAsync(boardId, taskId);
        await database.Boards.ApplyRulesAsync(boardId);
        var suppressed = await Placement(database, boardId, taskId);
        Assert.Equal(original.Id, suppressed.Id);
        Assert.True(suppressed.IsRuleMatch);
        Assert.True(suppressed.IsSuppressed);
        Assert.Empty((await database.Boards.GetAsync(boardId))!.Columns.SelectMany(x => x.Cards));

        await database.Boards.AddPlacementAsync(boardId, taskId);
        await database.Boards.ApplyRulesAsync(boardId);
        var restored = await Placement(database, boardId, taskId);
        Assert.Equal(original.Id, restored.Id);
        Assert.True(restored.IsManual);
        Assert.True(restored.IsRuleMatch);
        Assert.False(restored.IsSuppressed);
        Assert.Single((await database.Boards.GetAsync(boardId))!.Columns.SelectMany(x => x.Cards));
    }

    [Fact]
    public async Task ReconciliationIsIdempotentAndPreservesPlacementStageAndOrder()
    {
        await using var database = await TestDatabase.CreateAsync();
        var taskId = await ActiveTask(database, "Stable", priority: TaskPriority.High);
        var boardId = await database.Boards.CreateAsync("Stable board", BoardMode.Hybrid);
        await database.Boards.AddRuleAsync(boardId, BoardRuleField.Priority, "High");
        var board = await database.Boards.GetAsync(boardId);
        await database.Boards.MovePlacementAsync(boardId, taskId, board!.Columns[1].Id, 0);
        var before = await Placement(database, boardId, taskId);

        await database.Boards.ApplyRulesAsync(boardId);
        await database.Boards.ApplyRulesAsync(boardId);
        var after = await Placement(database, boardId, taskId);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.ColumnId, after.ColumnId);
        Assert.Equal(before.Order, after.Order);
        Assert.Equal(1, await PlacementCount(database, boardId, taskId));
    }

    [Fact]
    public async Task TypedAndRulesSupportNegationTagsAndPriorityThreshold()
    {
        await using var database = await TestDatabase.CreateAsync();
        var tagId = await database.Metadata.CreateTagAsync("Ready", "#123456");
        var matching = await ActiveTask(database, "Matching", priority: TaskPriority.Urgent);
        var tooLow = await ActiveTask(database, "Too low", priority: TaskPriority.Medium);
        var noTag = await ActiveTask(database, "No tag", priority: TaskPriority.Urgent);
        await database.Tasks.SetTagsAsync(matching, [tagId]);
        await database.Tasks.SetTagsAsync(tooLow, [tagId]);
        var boardId = await database.Boards.CreateAsync("Typed rules", BoardMode.Smart);

        await database.Boards.AddRuleAsync(boardId, BoardRuleField.Tag, BoardRuleOperator.ContainsTag, tagId.ToString());
        await database.Boards.AddRuleAsync(boardId, BoardRuleField.Priority, BoardRuleOperator.AtLeastPriority, "High");
        var cards = (await database.Boards.GetAsync(boardId))!.Columns.SelectMany(x => x.Cards).ToList();
        Assert.Single(cards);
        Assert.Equal(matching, cards[0].TaskId);

        var second = await database.Boards.CreateAsync("Negative rule", BoardMode.Smart);
        await database.Boards.AddRuleAsync(second, BoardRuleField.Tag, BoardRuleOperator.DoesNotContainTag, tagId.ToString());
        cards = (await database.Boards.GetAsync(second))!.Columns.SelectMany(x => x.Cards).ToList();
        Assert.Single(cards);
        Assert.Equal(noTag, cards[0].TaskId);
    }

    [Fact]
    public async Task CompletedTasksAreHiddenButPlacementIsRetainedForHistoryToggle()
    {
        await using var database = await TestDatabase.CreateAsync();
        var taskId = await ActiveTask(database, "Complete me");
        var boardId = await database.Boards.CreateAsync("History", BoardMode.Manual);
        await database.Boards.AddPlacementAsync(boardId, taskId);
        var placement = await Placement(database, boardId, taskId);

        await database.Tasks.SetStateAsync(taskId, TaskState.Completed);
        Assert.Empty((await database.Boards.GetAsync(boardId))!.Columns.SelectMany(x => x.Cards));
        var historical = (await database.Boards.GetAsync(boardId, true))!.Columns.SelectMany(x => x.Cards).Single();
        Assert.Equal(placement.Id, historical.PlacementId);
        Assert.Equal(TaskState.Completed, historical.State);
        Assert.Equal(1, await PlacementCount(database, boardId, taskId));
    }

    [Fact]
    public async Task CanonicalTaskHasIndependentPlacementStagesOnMultipleBoards()
    {
        await using var database = await TestDatabase.CreateAsync();
        var taskId = await ActiveTask(database, "Canonical");
        var first = await database.Boards.CreateAsync("Delivery", BoardMode.Manual);
        var second = await database.Boards.CreateAsync("Team", BoardMode.Manual);
        await database.Boards.AddPlacementAsync(first, taskId);
        await database.Boards.AddPlacementAsync(second, taskId);
        var firstBoard = await database.Boards.GetAsync(first);
        await database.Boards.MovePlacementAsync(first, taskId, firstBoard!.Columns[2].Id);

        var secondBoard = await database.Boards.GetAsync(second);
        Assert.Single(firstBoard.Columns[0].Cards);
        firstBoard = await database.Boards.GetAsync(first);
        Assert.Single(firstBoard!.Columns[2].Cards);
        Assert.Single(secondBoard!.Columns[0].Cards);
        Assert.Equal(2, await PlacementCount(database, null, taskId));
    }

    [Fact]
    public async Task VersionIsAnEnforcedConcurrencyToken()
    {
        await using var database = await TestDatabase.CreateAsync();
        var taskId = await database.Tasks.CreateAsync("Concurrent");
        await using var first = database.CreateDbContext();
        await using var second = database.CreateDbContext();
        var firstTask = await first.Tasks.SingleAsync(x => x.Id == taskId);
        var secondTask = await second.Tasks.SingleAsync(x => x.Id == taskId);
        firstTask.Title = "First";
        await first.SaveChangesAsync();
        secondTask.Title = "Second";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task TaskUpdateRejectsAnEditorVersionThatBecameStale()
    {
        await using var database = await TestDatabase.CreateAsync();
        var taskId = await database.Tasks.CreateAsync("Original");
        var opened = (await database.Tasks.GetEditDetailsAsync(taskId))!;
        await database.Tasks.SetStateAsync(taskId, TaskState.Completed);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => database.Tasks.UpdateAsync(
            taskId, "Stale edit", "", null, BuiltInTaskTypes.TaskId, TaskPriority.None, null,
            tagIds: [], expectedVersion: opened.Task.Version));

        Assert.Equal("Original", (await database.Tasks.GetAsync(taskId))!.Title);
    }

    [Fact]
    public async Task ReplacementMigrationPreservesLegacyLifecycleDeadlineAndPlacementData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"planner-migration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite($"Data Source={path}").Options;
        var taskId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        var columnId = Guid.NewGuid();
        try
        {
            await using (var legacy = new PlannerDbContext(options))
            {
                await legacy.GetService<IMigrator>().MigrateAsync("20260903154719_InitialCreate");
                await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Projects (Id, Name, Color, IsArchived, [Order]) VALUES ({projectId}, {"Legacy project"}, {"#123456"}, {true}, {0})");
                await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Boards (Id, Name, Mode, [Order]) VALUES ({boardId}, {"Legacy board"}, {0}, {0})");
                await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO BoardColumns (Id, BoardId, Name, Color, [Order]) VALUES ({columnId}, {boardId}, {"First"}, {"#123456"}, {0})");
                await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Tasks (Id, Title, Description, State, Priority, InboxOrder, CreatedAt, UpdatedAt, CompletedAt, TypeId, ProjectId) VALUES ({taskId}, {"Legacy task"}, {string.Empty}, {1}, {3}, {0}, {DateTimeOffset.UtcNow.AddDays(-2)}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow}, {BuiltInTaskTypes.TaskId}, {projectId})");
                await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Deadlines (Id, TaskId, DueAt, HasTime) VALUES ({Guid.NewGuid()}, {taskId}, {new DateTimeOffset(2035, 6, 7, 13, 45, 0, TimeSpan.Zero)}, {true})");
                await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO BoardPlacements (Id, BoardId, TaskId, ColumnId, [Order], IsRuleManaged) VALUES ({Guid.NewGuid()}, {boardId}, {taskId}, {columnId}, {4}, {false})");
                await legacy.GetService<IMigrator>().MigrateAsync();
            }

            await using var upgraded = new PlannerDbContext(options);
            var task = await upgraded.Tasks.SingleAsync(x => x.Id == taskId);
            var project = await upgraded.Projects.SingleAsync(x => x.Id == projectId);
            var placement = await upgraded.BoardPlacements.SingleAsync(x => x.TaskId == taskId);
            var column = await upgraded.BoardColumns.SingleAsync(x => x.Id == columnId);
            Assert.Equal(TaskState.Completed, task.State);
            Assert.Equal(new DateOnly(2035, 6, 7), task.DueDate);
            Assert.Equal(new TimeOnly(13, 45), task.DueTime);
            Assert.NotNull(project.ArchivedAtUtc);
            Assert.True(placement.IsManual);
            Assert.False(placement.IsRuleMatch);
            Assert.True(column.IsDefault);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task<Guid> ActiveTask(TestDatabase database, string title, TaskPriority priority = TaskPriority.None)
    {
        var id = await database.Tasks.CreateAsync(title, priority: priority);
        await database.Tasks.SetStateAsync(id, TaskState.Active);
        return id;
    }

    private static async Task<BoardPlacement> Placement(TestDatabase database, Guid boardId, Guid taskId)
    {
        await using var context = database.CreateDbContext();
        return await context.BoardPlacements.AsNoTracking().SingleAsync(x => x.BoardId == boardId && x.TaskId == taskId);
    }

    private static async Task<int> PlacementCount(TestDatabase database, Guid? boardId, Guid taskId)
    {
        await using var context = database.CreateDbContext();
        return await context.BoardPlacements.CountAsync(x => x.TaskId == taskId && (boardId == null || x.BoardId == boardId));
    }
}
