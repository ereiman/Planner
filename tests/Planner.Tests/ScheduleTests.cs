using Microsoft.EntityFrameworkCore;
using Planner.Web.Domain;
using Planner.Web.Services;

namespace Planner.Tests;

public sealed class ScheduleTests
{
    [Fact]
    public async Task HalfOpenBoundariesDoNotConflictButRealOverlapDoes()
    {
        await using var database = await TestDatabase.CreateAsync();
        var taskId = await ActiveTask(database, "Boundary");
        var start = new DateTimeOffset(2035, 4, 8, 9, 0, 0, TimeSpan.Zero);
        var created = await database.Schedule.CreateAsync(taskId, start, start.AddHours(1));

        Assert.True(created.Saved);
        Assert.Empty(await database.Conflicts.DetectAsync(start.AddHours(-1), start));
        Assert.Empty(await database.Conflicts.DetectAsync(start.AddHours(1), start.AddHours(2)));
        Assert.Single(await database.Conflicts.DetectAsync(start.AddMinutes(59), start.AddHours(2)));
        await Assert.ThrowsAsync<ArgumentException>(() => database.Schedule.CreateAsync(taskId, start, start));
    }

    [Fact]
    public async Task MultipleBlocksCanBelongToOneTaskAndPersistByUtcRange()
    {
        await using var database = await TestDatabase.CreateAsync();
        var taskId = await ActiveTask(database, "Split work");
        var start = new DateTimeOffset(2035, 5, 1, 8, 0, 0, TimeSpan.Zero);

        var morning = await database.Schedule.CreateAsync(taskId, start, start.AddHours(1), "First");
        var afternoon = await database.Schedule.CreateAsync(taskId, start.AddHours(5), start.AddHours(6), "Second");

        Assert.True(morning.Saved);
        Assert.True(afternoon.Saved);
        var listed = await database.Schedule.ListAsync(start, start.AddDays(1));
        Assert.Equal(2, listed.Count);
        Assert.All(listed, block => Assert.Equal(taskId, block.TaskId));
        await using var context = database.CreateDbContext();
        Assert.Equal(2, await context.TimeBlocks.CountAsync(x => x.TaskId == taskId));
        Assert.All(await context.TimeBlocks.ToListAsync(), block =>
        {
            Assert.True(block.CreatedAtUtc <= block.UpdatedAtUtc);
            Assert.Equal(1, block.Version);
        });
    }

    [Fact]
    public async Task DetectorExcludesCancelledDeletedAndSelf()
    {
        await using var database = await TestDatabase.CreateAsync();
        var firstTask = await ActiveTask(database, "First");
        var secondTask = await ActiveTask(database, "Second");
        var start = new DateTimeOffset(2035, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var first = (await database.Schedule.CreateAsync(firstTask, start, start.AddHours(1))).TimeBlock!;
        var second = (await database.Schedule.CreateAsync(secondTask, start.AddHours(2), start.AddHours(3))).TimeBlock!;

        Assert.Empty(await database.Conflicts.DetectAsync(start, start.AddHours(1), first.Id));
        await database.Schedule.SetStatusAsync(first.Id, TimeBlockStatus.Cancelled);
        Assert.Empty(await database.Conflicts.DetectAsync(start, start.AddHours(1)));
        await database.Schedule.SetDeletedAsync(second.Id, true);
        Assert.Empty(await database.Conflicts.DetectAsync(start.AddHours(2), start.AddHours(3)));
        Assert.DoesNotContain(await database.Schedule.ListAsync(start, start.AddDays(1)), block => block.Id == second.Id);
    }

    [Fact]
    public async Task ConflictRequiresExplicitOverrideAndRecordsIt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var firstTask = await ActiveTask(database, "Existing");
        var secondTask = await ActiveTask(database, "Candidate");
        var start = new DateTimeOffset(2035, 7, 1, 10, 0, 0, TimeSpan.Zero);
        await database.Schedule.CreateAsync(firstTask, start, start.AddHours(2));

        var warning = await database.Schedule.CreateAsync(secondTask, start.AddMinutes(30), start.AddHours(1));
        Assert.False(warning.Saved);
        Assert.True(warning.RequiresOverride);
        Assert.Single(warning.Conflicts);
        Assert.Single(await database.Schedule.ListAsync(start, start.AddHours(3)));

        var overridden = await database.Schedule.CreateAsync(secondTask, start.AddMinutes(30), start.AddHours(1), "Accepted", true);
        Assert.True(overridden.Saved);
        Assert.NotNull(overridden.TimeBlock!.ConflictOverrideAtUtc);
        await using var context = database.CreateDbContext();
        var persisted = await context.TimeBlocks.SingleAsync(x => x.Id == overridden.TimeBlock.Id);
        Assert.NotNull(persisted.ConflictOverrideAtUtc);
        Assert.Equal("Accepted", persisted.Notes);
    }

    [Fact]
    public async Task WarningConflictAlsoRequiresOverrideAndRecordsIt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var taskId = await ActiveTask(database, "Warning candidate");
        var start = new DateTimeOffset(2035, 7, 2, 10, 0, 0, TimeSpan.Zero);
        var warning = new ScheduleConflict(Guid.Empty, Guid.Empty, "Availability may be stale", start, start.AddHours(1))
        { Severity = ScheduleConflictSeverity.Warning, Source = ScheduleConflictSource.Availability };
        var schedule = new ScheduleService(database, new StubConflictDetector(warning));

        var rejected = await schedule.CreateAsync(taskId, start, start.AddHours(1));
        Assert.False(rejected.Saved);
        Assert.True(rejected.RequiresOverride);
        var overridden = await schedule.CreateAsync(taskId, start, start.AddHours(1), overrideConflicts: true);
        Assert.True(overridden.Saved);
        Assert.NotNull(overridden.TimeBlock!.ConflictOverrideAtUtc);
    }

    [Fact]
    public async Task CompletingTaskAndCompletingBlockAreIndependent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var taskId = await ActiveTask(database, "Independent");
        var start = new DateTimeOffset(2035, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var block = (await database.Schedule.CreateAsync(taskId, start, start.AddHours(1))).TimeBlock!;

        await database.Tasks.SetStateAsync(taskId, TaskState.Completed);
        Assert.Equal(TimeBlockStatus.Planned, (await database.Schedule.ListAsync(start, start.AddHours(2))).Single().Status);
        await database.Schedule.SetStatusAsync(block.Id, TimeBlockStatus.Completed);
        Assert.Equal(TaskState.Completed, (await database.Tasks.GetAsync(taskId))!.State);
    }

    private sealed class StubConflictDetector(params ScheduleConflict[] conflicts) : IConflictDetector
    {
        public Task<IReadOnlyList<ScheduleConflict>> DetectAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, Guid? excludingTimeBlockId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduleConflict>>(conflicts);
    }

    private static async Task<Guid> ActiveTask(TestDatabase database, string title)
    {
        var id = await database.Tasks.CreateAsync(title);
        await database.Tasks.SetStateAsync(id, TaskState.Active);
        return id;
    }
}
