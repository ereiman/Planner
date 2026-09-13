using Microsoft.EntityFrameworkCore;
using Planner.Web.Domain;

namespace Planner.Tests;

public sealed class BoardTests
{
    [Fact]
    public async Task PopulatedColumnRequiresDestinationAndMovesCardsWhenProvided()
    {
        await using var database = await TestDatabase.CreateAsync();
        var boardId = await database.Boards.CreateAsync("Flow", BoardMode.Manual);
        var board = (await database.Boards.GetAsync(boardId))!;
        var source = board.Columns[0]; var destination = board.Columns[1];
        var taskId = await database.Tasks.CreateAsync("Card");
        await database.Tasks.SetStateAsync(taskId, TaskState.Active);
        await database.Boards.AddPlacementAsync(boardId, taskId, source.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Boards.RemoveColumnAsync(boardId, source.Id, null));
        await database.Boards.RemoveColumnAsync(boardId, source.Id, destination.Id);
        var updated = (await database.Boards.GetAsync(boardId))!;
        Assert.DoesNotContain(updated.Columns, x => x.Id == source.Id);
        Assert.Contains(updated.Columns.Single(x => x.Id == destination.Id).Cards, x => x.TaskId == taskId);
    }

    [Fact]
    public async Task ColumnWithRemovedPlacementHistoryRequiresDestination()
    {
        await using var database = await TestDatabase.CreateAsync();
        var boardId = await database.Boards.CreateAsync("History", BoardMode.Manual);
        var board = (await database.Boards.GetAsync(boardId))!;
        var source = board.Columns[0];
        var destination = board.Columns[1];
        var taskId = await database.Tasks.CreateAsync("Removed card");
        await database.Boards.AddPlacementAsync(boardId, taskId, source.Id);
        await database.Boards.RemovePlacementAsync(boardId, taskId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Boards.RemoveColumnAsync(boardId, source.Id, null));
        await database.Boards.RemoveColumnAsync(boardId, source.Id, destination.Id);

        await using var db = database.CreateDbContext();
        Assert.Equal(destination.Id, await db.BoardPlacements.Where(x => x.TaskId == taskId).Select(x => x.ColumnId).SingleAsync());
    }

    [Fact]
    public async Task DeletedBoardNameCanBeReused()
    {
        await using var database = await TestDatabase.CreateAsync();
        var boardId = await database.Boards.CreateAsync("Reusable board", BoardMode.Manual);
        await database.Boards.SetDeletedAsync(boardId, true);

        var replacementId = await database.Boards.CreateAsync("Reusable board", BoardMode.Manual);

        Assert.NotEqual(boardId, replacementId);
    }

    [Fact]
    public async Task QuickCreateCreatesCanonicalTaskAndPlacementTransactionally()
    {
        await using var database = await TestDatabase.CreateAsync();
        var boardId = await database.Boards.CreateAsync("Flow", BoardMode.Manual);
        var column = (await database.Boards.GetAsync(boardId))!.Columns[0];
        var taskId = await database.Boards.QuickCreateAsync(boardId, column.Id, "Direct task");
        Assert.Equal(TaskState.Active, (await database.Tasks.GetAsync(taskId))!.State);
        Assert.Contains((await database.Boards.GetAsync(boardId))!.Columns[0].Cards, x => x.TaskId == taskId);
        await using var db = database.CreateDbContext();
        Assert.Equal(1, await db.BoardPlacements.CountAsync(x => x.TaskId == taskId));
    }
}
