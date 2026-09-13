using Microsoft.EntityFrameworkCore;
using Planner.Web.Domain;

namespace Planner.Web.Data;

public sealed class PlannerDbContext(DbContextOptions<PlannerDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TaskType> TaskTypes => Set<TaskType>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TaskTag> TaskTags => Set<TaskTag>();
    public DbSet<Checklist> Checklists => Set<Checklist>();
    public DbSet<ChecklistItem> ChecklistItems => Set<ChecklistItem>();
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<BoardColumn> BoardColumns => Set<BoardColumn>();
    public DbSet<BoardPlacement> BoardPlacements => Set<BoardPlacement>();
    public DbSet<BoardRule> BoardRules => Set<BoardRule>();
    public DbSet<TimeBlock> TimeBlocks => Set<TimeBlock>();
    public DbSet<ConnectedMicrosoftAccount> ConnectedMicrosoftAccounts => Set<ConnectedMicrosoftAccount>();
    public DbSet<ExternalCalendar> ExternalCalendars => Set<ExternalCalendar>();
    public DbSet<ExternalCalendarEvent> ExternalCalendarEvents => Set<ExternalCalendarEvent>();
    public DbSet<CalendarSyncState> CalendarSyncStates => Set<CalendarSyncState>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditValues();
        ValidateDueValues();
        ValidateTimeBlocks();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAuditValues();
        ValidateDueValues();
        ValidateTimeBlocks();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(10_000);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.ToTable(table => table.HasCheckConstraint("CK_Tasks_DueTimeRequiresDueDate", "DueTime IS NULL OR DueDate IS NOT NULL"));
            entity.HasQueryFilter(x => x.DeletedAtUtc == null);
            entity.HasOne(x => x.Project).WithMany(x => x.Tasks).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.Type).WithMany(x => x.Tasks).HasForeignKey(x => x.TypeId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Project>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.Name).HasFilter("DeletedAtUtc IS NULL").IsUnique();
            entity.HasQueryFilter(x => x.DeletedAtUtc == null);
        });
        modelBuilder.Entity<TaskType>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasData(
                new TaskType { Id = BuiltInTaskTypes.TaskId, Name = "Task", Color = "#03A7E1" },
                new TaskType { Id = BuiltInTaskTypes.BugId, Name = "Bug", Color = "#D32F2F" });
        });
        modelBuilder.Entity<Tag>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
        });
        modelBuilder.Entity<TaskTag>().HasKey(x => new { x.TaskId, x.TagId });
        modelBuilder.Entity<TaskTag>().HasQueryFilter(x => x.Task.DeletedAtUtc == null);
        modelBuilder.Entity<TaskTag>().HasOne(x => x.Task).WithMany(x => x.TaskTags).HasForeignKey(x => x.TaskId);
        modelBuilder.Entity<TaskTag>().HasOne(x => x.Tag).WithMany(x => x.TaskTags).HasForeignKey(x => x.TagId);
        modelBuilder.Entity<Checklist>(entity =>
        {
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.HasQueryFilter(x => x.Task.DeletedAtUtc == null);
        });
        modelBuilder.Entity<ChecklistItem>(entity =>
        {
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.HasQueryFilter(x => x.Checklist.Task.DeletedAtUtc == null);
        });
        modelBuilder.Entity<Board>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.Name).HasFilter("DeletedAtUtc IS NULL").IsUnique();
            entity.HasQueryFilter(x => x.DeletedAtUtc == null);
        });
        modelBuilder.Entity<BoardColumn>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => new { x.BoardId, x.Name }).IsUnique();
            entity.HasIndex(x => x.BoardId).HasFilter("IsDefault = 1").IsUnique();
            entity.HasQueryFilter(x => x.Board.DeletedAtUtc == null);
        });
        modelBuilder.Entity<BoardPlacement>(entity =>
        {
            entity.HasIndex(x => new { x.BoardId, x.TaskId }).IsUnique();
            entity.Ignore(x => x.IsEffective);
            entity.HasQueryFilter(x => x.Board.DeletedAtUtc == null && x.Task.DeletedAtUtc == null);
            entity.HasOne(x => x.Board).WithMany(x => x.Placements).HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Task).WithMany(x => x.Placements).HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Column).WithMany(x => x.Placements).HasForeignKey(x => x.ColumnId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<BoardRule>(entity =>
        {
            entity.Property(x => x.Value).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => new { x.BoardId, x.Field, x.Operator, x.Value }).IsUnique();
            entity.HasQueryFilter(x => x.Board.DeletedAtUtc == null);
        });
        modelBuilder.Entity<TimeBlock>(entity =>
        {
            entity.Property(x => x.Notes).HasMaxLength(2_000);
            ConfigureUtc(entity.Property(x => x.StartsAtUtc));
            ConfigureUtc(entity.Property(x => x.EndsAtUtc));
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.ToTable(table => table.HasCheckConstraint("CK_TimeBlocks_EndAfterStart", "EndsAtUtc > StartsAtUtc"));
            entity.HasQueryFilter(x => x.DeletedAtUtc == null && (x.Task == null || x.Task.DeletedAtUtc == null));
            entity.HasIndex(x => new { x.StartsAtUtc, x.EndsAtUtc });
            entity.HasIndex(x => new { x.TaskId, x.StartsAtUtc });
            entity.HasOne(x => x.Task).WithMany(x => x.TimeBlocks).HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ConnectedMicrosoftAccount>(entity =>
        {
            entity.Property(x => x.MsalHomeAccountId).HasMaxLength(300).IsRequired();
            entity.Property(x => x.TenantId).HasMaxLength(100);
            entity.Property(x => x.MicrosoftUserId).HasMaxLength(150);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.EmailAddress).HasMaxLength(320);
            entity.Property(x => x.LastError).HasMaxLength(500);
            entity.HasIndex(x => x.MsalHomeAccountId).IsUnique();
        });
        modelBuilder.Entity<ExternalCalendar>(entity =>
        {
            entity.Property(x => x.ExternalId).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.HasIndex(x => new { x.ConnectedMicrosoftAccountId, x.ExternalId }).IsUnique();
            entity.HasOne(x => x.Account).WithMany(x => x.Calendars).HasForeignKey(x => x.ConnectedMicrosoftAccountId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ExternalCalendarEvent>(entity =>
        {
            entity.Property(x => x.ExternalId).HasMaxLength(500).IsRequired();
            entity.Property(x => x.ICalUId).HasMaxLength(500);
            entity.Property(x => x.Subject).HasMaxLength(500);
            entity.Property(x => x.SeriesMasterId).HasMaxLength(500);
            ConfigureUtc(entity.Property(x => x.StartsAtUtc));
            ConfigureUtc(entity.Property(x => x.EndsAtUtc));
            ConfigureUtc(entity.Property(x => x.LastModifiedAtUtc));
            entity.ToTable(table => table.HasCheckConstraint("CK_ExternalCalendarEvents_EndAfterStart", "EndsAtUtc > StartsAtUtc"));
            entity.HasIndex(x => new { x.ExternalCalendarId, x.ExternalId }).IsUnique();
            entity.HasIndex(x => new { x.StartsAtUtc, x.EndsAtUtc });
            entity.HasOne(x => x.Calendar).WithMany(x => x.Events).HasForeignKey(x => x.ExternalCalendarId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<CalendarSyncState>(entity =>
        {
            entity.Property(x => x.DeltaCursor).HasMaxLength(4_000);
            entity.Property(x => x.LastError).HasMaxLength(500);
            ConfigureUtc(entity.Property(x => x.HorizonStartsAtUtc));
            ConfigureUtc(entity.Property(x => x.HorizonEndsAtUtc));
            entity.HasIndex(x => x.ExternalCalendarId).IsUnique();
            entity.HasOne(x => x.Calendar).WithOne(x => x.SyncState).HasForeignKey<CalendarSyncState>(x => x.ExternalCalendarId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureUtc(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<DateTimeOffset> property) =>
        property.HasConversion(x => x.UtcDateTime, x => new DateTimeOffset(DateTime.SpecifyKind(x, DateTimeKind.Utc)));

    private void ApplyAuditValues()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<TaskItem>())
            ApplyAudit(entry, now, x => x.CreatedAtUtc, x => x.UpdatedAtUtc, x => x.Version);
        foreach (var entry in ChangeTracker.Entries<Project>())
            ApplyAudit(entry, now, x => x.CreatedAtUtc, x => x.UpdatedAtUtc, x => x.Version);
        foreach (var entry in ChangeTracker.Entries<Board>())
            ApplyAudit(entry, now, x => x.CreatedAtUtc, x => x.UpdatedAtUtc, x => x.Version);
        foreach (var entry in ChangeTracker.Entries<TimeBlock>())
            ApplyAudit(entry, now, x => x.CreatedAtUtc, x => x.UpdatedAtUtc, x => x.Version);
    }

    private static void ApplyAudit<TEntity>(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity> entry, DateTimeOffset now,
        System.Linq.Expressions.Expression<Func<TEntity, DateTimeOffset>> created,
        System.Linq.Expressions.Expression<Func<TEntity, DateTimeOffset>> updated,
        System.Linq.Expressions.Expression<Func<TEntity, int>> version) where TEntity : class
    {
        if (entry.State == EntityState.Added)
        {
            entry.Property(created).CurrentValue = now;
            entry.Property(updated).CurrentValue = now;
            entry.Property(version).CurrentValue = 1;
        }
        else if (entry.State == EntityState.Modified)
        {
            entry.Property(updated).CurrentValue = now;
            entry.Property(version).CurrentValue++;
        }
    }

    private void ValidateDueValues()
    {
        if (ChangeTracker.Entries<TaskItem>().Any(x => x.State is EntityState.Added or EntityState.Modified && x.Entity.DueTime is not null && x.Entity.DueDate is null))
            throw new InvalidOperationException("A due time requires a due date.");
    }

    private void ValidateTimeBlocks()
    {
        if (ChangeTracker.Entries<TimeBlock>().Any(x => x.State is EntityState.Added or EntityState.Modified && x.Entity.EndsAtUtc <= x.Entity.StartsAtUtc))
            throw new InvalidOperationException("A time block must end after it starts.");
    }
}
