using Planner.Web.Domain;

namespace Planner.Tests;

public sealed class ProjectTests
{
    [Fact]
    public async Task ProjectCanBeRenamedAndTaskUsesNewName()
    {
        await using var database = await TestDatabase.CreateAsync();
        var projectId = await database.Projects.CreateAsync("Original", "#6750A4");
        var taskId = await database.Tasks.CreateAsync("Task", projectId);

        await database.Projects.RenameAsync(projectId, "Renamed");

        Assert.Equal("Renamed", (await database.Tasks.GetAsync(taskId))!.Project);
        Assert.Equal("Renamed", (await database.Projects.ListAsync()).Single().Name);
    }

    [Fact]
    public async Task DeletedProjectNameCanBeReused()
    {
        await using var database = await TestDatabase.CreateAsync();
        var projectId = await database.Projects.CreateAsync("Reusable", "#6750A4");
        await database.Projects.SetDeletedAsync(projectId, true);

        var replacementId = await database.Projects.CreateAsync("Reusable", "#6750A4");

        Assert.NotEqual(projectId, replacementId);
    }

    [Fact]
    public async Task ProjectNamesAreUniqueIgnoringCase()
    {
        await using var database = await TestDatabase.CreateAsync();
        var projectId = await database.Projects.CreateAsync("Alpha", "#6750A4");
        _ = await database.Projects.CreateAsync("Beta", "#6750A4");

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Projects.CreateAsync("alpha", "#6750A4"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Projects.RenameAsync(projectId, "BETA"));
    }
}
