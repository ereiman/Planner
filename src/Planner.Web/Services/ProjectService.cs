using Microsoft.EntityFrameworkCore;
using Planner.Web.Data;
using Planner.Web.Domain;

namespace Planner.Web.Services;

public sealed class ProjectService(IDbContextFactory<PlannerDbContext> factory) : IProjectService
{
    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Projects.AsNoTracking();
        if (!includeArchived) query = query.Where(x => x.ArchivedAtUtc == null);
        return await query.OrderBy(x => x.Order).Select(x => new ProjectSummary(
            x.Id, x.Name, x.Color, x.ArchivedAtUtc != null, x.Order,
            x.Tasks.Count(t => t.State == TaskState.Active && t.ArchivedAtUtc == null),
            x.Tasks.Count(t => t.State == TaskState.Completed && t.ArchivedAtUtc == null))).ToListAsync(cancellationToken);
    }

    public async Task<Guid> CreateAsync(string name, string color, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmedName = name.Trim();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.Projects.AnyAsync(x => x.Name.ToLower() == trimmedName.ToLower(), cancellationToken))
            throw new InvalidOperationException("A project with that name already exists.");
        var order = (await db.Projects.MaxAsync(x => (long?)x.Order, cancellationToken) ?? -1) + 1;
        var project = new Project { Name = trimmedName, Color = color, Order = order };
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);
        return project.Id;
    }

    public async Task RenameAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmedName = name.Trim();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.Projects.AnyAsync(x => x.Id != id && x.Name.ToLower() == trimmedName.ToLower(), cancellationToken))
            throw new InvalidOperationException("A project with that name already exists.");
        var project = await db.Projects.SingleAsync(x => x.Id == id, cancellationToken);
        project.Name = trimmedName;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.SingleAsync(x => x.Id == id, cancellationToken);
        project.ArchivedAtUtc = archived ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetDeletedAsync(Guid id, bool deleted, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var project = await db.Projects.IgnoreQueryFilters().SingleAsync(x => x.Id == id, cancellationToken);
        project.DeletedAtUtc = deleted ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class MetadataService(IDbContextFactory<PlannerDbContext> factory) : IMetadataService
{
    public async Task<IReadOnlyList<TypeSummary>> ListTypesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.TaskTypes.AsNoTracking().OrderBy(x => x.Name).Select(x => new TypeSummary(x.Id, x.Name, x.Color)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TagSummary>> ListTagsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Tags.AsNoTracking().OrderBy(x => x.Name).Select(x => new TagSummary(x.Id, x.Name, x.Color)).ToListAsync(cancellationToken);
    }

    public async Task<Guid> CreateTagAsync(string name, string color, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var tag = new Tag { Name = name.Trim(), Color = color };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(cancellationToken);
        return tag.Id;
    }
}
