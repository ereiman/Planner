using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Planner.Web.Services;

public sealed class BackupOptions
{
    public const string SectionName = "Planner:Backup";
    public int RetentionCount { get; set; } = 14;
    public string? BackupFolder { get; set; }
}

public interface IBackupService
{
    string DataFolder { get; }
    string BackupFolder { get; }
    Task<string> CreateAsync(string reason = "manual", CancellationToken cancellationToken = default);
    Task RotateDailyAsync(CancellationToken cancellationToken = default);
}

public sealed class BackupService(string databasePath, IOptions<BackupOptions> options) : IBackupService
{
    public string DataFolder => Path.GetDirectoryName(databasePath)!;
    public string BackupFolder { get; } = string.IsNullOrWhiteSpace(options.Value.BackupFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Planner", "Backups")
        : Path.GetFullPath(options.Value.BackupFolder);

    public async Task<string> CreateAsync(string reason = "manual", CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) throw new FileNotFoundException("The Planner database does not exist yet.", databasePath);
        Directory.CreateDirectory(BackupFolder);
        var safeReason = new string(reason.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var target = Path.Combine(BackupFolder, $"planner-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{safeReason}.db");
        await using var source = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await using var destination = new SqliteConnection($"Data Source={target}");
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
        await destination.CloseAsync();
        Rotate();
        return target;
    }

    public async Task RotateDailyAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) return;
        Directory.CreateDirectory(BackupFolder);
        var prefix = $"planner-{DateTime.UtcNow:yyyyMMdd}-";
        if (!Directory.EnumerateFiles(BackupFolder, $"{prefix}*-daily.db").Any())
            await CreateAsync("daily", cancellationToken);
        else
            Rotate();
    }

    private void Rotate()
    {
        var keep = Math.Max(1, options.Value.RetentionCount);
        foreach (var file in Directory.EnumerateFiles(BackupFolder, "planner-*.db").OrderByDescending(File.GetCreationTimeUtc).Skip(keep))
            File.Delete(file);
    }
}
