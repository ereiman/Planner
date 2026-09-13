using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Planner.Web.Services;

namespace Planner.Tests;

public sealed class BackupTests
{
    [Fact]
    public async Task BackupDatabaseProducesOpenableConsistentSqliteFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"planner-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "planner.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await new SqliteCommand("CREATE TABLE Sample(Value TEXT); INSERT INTO Sample VALUES ('ok');", connection).ExecuteNonQueryAsync();
        }
        var service = new BackupService(databasePath, Options.Create(new BackupOptions
        {
            RetentionCount = 2,
            BackupFolder = Path.Combine(root, "backups")
        }));
        var backup = await service.CreateAsync("test");
        await using var restored = new SqliteConnection($"Data Source={backup};Mode=ReadOnly");
        await restored.OpenAsync();
        Assert.Equal("ok", await new SqliteCommand("SELECT Value FROM Sample", restored).ExecuteScalarAsync());
        await restored.CloseAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }
}
