using Microsoft.EntityFrameworkCore;

namespace Planner.Web.Data;

public static class DatabaseInitialization
{
    public static string GetDatabasePath()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Planner");
        var directory = Path.Combine(root, "Data");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "planner.db");
        var legacyPath = Path.Combine(root, "planner.db");
        if (!File.Exists(path) && File.Exists(legacyPath))
            File.Copy(legacyPath, path); // Preserve the legacy file as a non-destructive rollback copy.
        return path;
    }

    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlannerDbContext>>();
        var backups = scope.ServiceProvider.GetRequiredService<Planner.Web.Services.IBackupService>();
        await using var db = await factory.CreateDbContextAsync();
        if (await db.Database.CanConnectAsync() && (await db.Database.GetPendingMigrationsAsync()).Any())
            await backups.CreateAsync("preMigration");
        await db.Database.MigrateAsync();
        await backups.RotateDailyAsync();
    }
}
