using Microsoft.EntityFrameworkCore;
using Planner.Web.Data;
using Planner.Web.Services;

namespace Planner.Tests;

internal sealed class TestDatabase : IDbContextFactory<PlannerDbContext>, IAsyncDisposable
{
    private readonly string _path;
    private readonly DbContextOptions<PlannerDbContext> _options;

    private TestDatabase(string path)
    {
        _path = path;
        _options = new DbContextOptionsBuilder<PlannerDbContext>().UseSqlite($"Data Source={path}").Options;
        Tasks = new TaskService(this);
        Projects = new ProjectService(this);
        Metadata = new MetadataService(this);
        Boards = new BoardService(this);
        Conflicts = new ConflictDetector(this);
        Schedule = new ScheduleService(this, Conflicts);
    }

    public ITaskService Tasks { get; }
    public IProjectService Projects { get; }
    public IMetadataService Metadata { get; }
    public IBoardService Boards { get; }
    public IConflictDetector Conflicts { get; }
    public IScheduleService Schedule { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var database = new TestDatabase(Path.Combine(Path.GetTempPath(), $"planner-{Guid.NewGuid():N}.db"));
        await using var context = database.CreateDbContext();
        await context.Database.MigrateAsync();
        return database;
    }

    public PlannerDbContext CreateDbContext() => new(_options);

    public Task<PlannerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public async ValueTask DisposeAsync()
    {
        await using var context = CreateDbContext();
        await context.Database.CloseConnectionAsync();
        SqliteConnectionClearPool(context);
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static void SqliteConnectionClearPool(PlannerDbContext context)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool((Microsoft.Data.Sqlite.SqliteConnection)context.Database.GetDbConnection());
    }
}
