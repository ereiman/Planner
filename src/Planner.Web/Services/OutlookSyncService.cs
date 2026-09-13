using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Planner.Web.Data;
using Planner.Web.Domain;

namespace Planner.Web.Services;

internal sealed class OutlookSyncService(
    IDbContextFactory<PlannerDbContext> factory,
    IMicrosoftTokenProvider tokenProvider,
    IOutlookGraphAdapter graph,
    IOptions<MicrosoftIntegrationOptions> options) : IOutlookSyncService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> AccountLocks = new();

    public async Task SyncAllAsync(CancellationToken cancellationToken = default)
    {
        if (!tokenProvider.IsConfigured) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var ids = await db.ConnectedMicrosoftAccounts.AsNoTracking().Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            try { await SyncAccountAsync(id, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* Each account records its own sanitized failure and must not stop another account. */ }
        }
    }

    public async Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (!tokenProvider.IsConfigured) return;
        var gate = AccountLocks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var token = await tokenProvider.AcquireTokenSilentAsync(accountId, cancellationToken);
            if (token is null) return;
            var calendar = await EnsureCalendarAsync(accountId, token, cancellationToken);
            await MarkAttemptAsync(calendar.Id, cancellationToken);
            var state = await ReadStateAsync(calendar.Id, cancellationToken);
            var fullSync = string.IsNullOrWhiteSpace(state.DeltaCursor);
            GraphDeltaPage page;
            try
            {
                page = await graph.GetCalendarDeltaAsync(token, state.HorizonStartsAtUtc, state.HorizonEndsAtUtc,
                    fullSync ? null : state.DeltaCursor, cancellationToken);
            }
            catch (InvalidDeltaCursorException)
            {
                fullSync = true;
                page = await graph.GetCalendarDeltaAsync(token, state.HorizonStartsAtUtc, state.HorizonEndsAtUtc, null, cancellationToken);
            }
            await ApplyAtomicallyAsync(calendar.Id, page, fullSync, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var message = SanitizeSyncError(exception);
            await RecordFailureAsync(accountId, message, cancellationToken);
            throw new InvalidOperationException(message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ExternalCalendar> EnsureCalendarAsync(Guid accountId, string token, CancellationToken cancellationToken)
    {
        var remote = await graph.GetDefaultCalendarAsync(token, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var calendar = await db.ExternalCalendars.Include(x => x.SyncState)
            .SingleOrDefaultAsync(x => x.ConnectedMicrosoftAccountId == accountId && x.ExternalId == remote.Id, cancellationToken);
        if (calendar is null)
        {
            var now = DateTimeOffset.UtcNow;
            var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-options.Value.HorizonPastDays);
            calendar = new ExternalCalendar
            {
                ConnectedMicrosoftAccountId = accountId,
                ExternalId = remote.Id,
                Name = remote.Name,
                IsDefault = true,
                SyncState = new CalendarSyncState
                {
                    HorizonStartsAtUtc = start,
                    HorizonEndsAtUtc = start.AddDays(options.Value.HorizonPastDays + options.Value.HorizonFutureDays),
                    Status = CalendarSyncStatus.NeverSynced
                }
            };
            db.ExternalCalendars.Add(calendar);
        }
        else
        {
            calendar.Name = remote.Name;
            calendar.IsDefault = true;
        }
        await db.SaveChangesAsync(cancellationToken);
        return calendar;
    }

    private async Task<CalendarSyncState> ReadStateAsync(Guid calendarId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.CalendarSyncStates.AsNoTracking().SingleAsync(x => x.ExternalCalendarId == calendarId, cancellationToken);
    }

    private async Task MarkAttemptAsync(Guid calendarId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var state = await db.CalendarSyncStates.SingleAsync(x => x.ExternalCalendarId == calendarId, cancellationToken);
        state.LastAttemptAtUtc = DateTimeOffset.UtcNow;
        state.Status = CalendarSyncStatus.Syncing;
        state.LastError = string.Empty;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyAtomicallyAsync(Guid calendarId, GraphDeltaPage page, bool fullSync, CancellationToken cancellationToken)
    {
        // The adapter has already collected every Graph page. No persistent changes occur until this transaction.
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (fullSync)
            await db.ExternalCalendarEvents.Where(x => x.ExternalCalendarId == calendarId).ExecuteDeleteAsync(cancellationToken);
        else if (page.RemovedIds.Count > 0)
            await db.ExternalCalendarEvents.Where(x => x.ExternalCalendarId == calendarId && page.RemovedIds.Contains(x.ExternalId)).ExecuteDeleteAsync(cancellationToken);

        var ids = page.Events.Select(x => x.Id).ToList();
        var existing = await db.ExternalCalendarEvents.Where(x => x.ExternalCalendarId == calendarId && ids.Contains(x.ExternalId))
            .ToDictionaryAsync(x => x.ExternalId, cancellationToken);
        foreach (var remote in page.Events)
        {
            if (!existing.TryGetValue(remote.Id, out var entity))
            {
                entity = new ExternalCalendarEvent { ExternalCalendarId = calendarId, ExternalId = remote.Id };
                db.ExternalCalendarEvents.Add(entity);
            }
            entity.ICalUId = remote.ICalUId;
            entity.Subject = remote.Sensitivity is ExternalEventSensitivity.Private or ExternalEventSensitivity.Confidential
                ? "Private event" : Truncate(remote.Subject, 500);
            entity.StartsAtUtc = remote.StartsAtUtc.ToUniversalTime();
            entity.EndsAtUtc = remote.EndsAtUtc.ToUniversalTime();
            entity.IsAllDay = remote.IsAllDay;
            entity.IsCancelled = remote.IsCancelled;
            entity.Sensitivity = remote.Sensitivity;
            entity.ShowAs = remote.ShowAs;
            entity.Response = remote.Response;
            entity.Kind = remote.Kind;
            entity.SeriesMasterId = remote.SeriesMasterId;
            entity.LastModifiedAtUtc = remote.LastModifiedAtUtc.ToUniversalTime();
        }
        var state = await db.CalendarSyncStates.SingleAsync(x => x.ExternalCalendarId == calendarId, cancellationToken);
        state.DeltaCursor = page.DeltaCursor;
        state.LastSuccessfulSyncAtUtc = DateTimeOffset.UtcNow;
        state.LastAttemptAtUtc = state.LastSuccessfulSyncAtUtc;
        state.Status = CalendarSyncStatus.Current;
        state.LastError = string.Empty;
        var account = await db.ConnectedMicrosoftAccounts.SingleAsync(
            x => x.Calendars.Any(calendar => calendar.Id == calendarId), cancellationToken);
        account.LastError = string.Empty;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RecordFailureAsync(Guid accountId, string message, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var account = await db.ConnectedMicrosoftAccounts.Include(x => x.Calendars).ThenInclude(x => x.SyncState)
                .SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken);
            if (account is null) return;
            account.LastError = message;
            foreach (var state in account.Calendars.Select(x => x.SyncState).OfType<CalendarSyncState>())
            {
                state.Status = CalendarSyncStatus.Failed;
                state.LastAttemptAtUtc = DateTimeOffset.UtcNow;
                state.LastError = message;
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        catch { /* Never replace the original, sanitized sync failure. */ }
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
    private static string SanitizeSyncError(Exception exception) => exception switch
    {
        InvalidDeltaCursorException => "The Microsoft calendar cursor expired and could not be refreshed.",
        HttpRequestException => "Microsoft Graph is currently unavailable.",
        _ => "Calendar sync failed. Try refreshing or reconnecting the account."
    };
}

internal sealed class OutlookCalendarService(
    IDbContextFactory<PlannerDbContext> factory,
    IMicrosoftTokenProvider tokenProvider,
    IOptions<MicrosoftIntegrationOptions> options) : IOutlookCalendarService
{
    public async Task<IReadOnlyList<ImportedCalendarEventDto>> ListEventsAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default)
    {
        startsAtUtc = startsAtUtc.ToUniversalTime();
        endsAtUtc = endsAtUtc.ToUniversalTime();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ExternalCalendarEvents.AsNoTracking()
            .Where(x => !x.IsCancelled && x.Response != ExternalEventResponse.Declined && x.StartsAtUtc < endsAtUtc && x.EndsAtUtc > startsAtUtc)
            .OrderBy(x => x.StartsAtUtc)
            .Select(x => new ImportedCalendarEventDto(
                x.Id, x.Calendar.ConnectedMicrosoftAccountId, x.Calendar.Account.DisplayName, x.Subject,
                x.StartsAtUtc, x.EndsAtUtc, x.IsAllDay, x.ShowAs, x.Response,
                db.TimeBlocks.Any(b => b.Status != TimeBlockStatus.Cancelled && b.StartsAtUtc < x.EndsAtUtc && b.EndsAtUtc > x.StartsAtUtc)))
            .ToListAsync(cancellationToken);
    }

    public async Task<OutlookAvailabilitySummary> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        if (!tokenProvider.IsConfigured)
            return new(false, 0, null, false, false, "Microsoft calendar is not configured.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var accounts = await db.ConnectedMicrosoftAccounts.AsNoTracking().Select(x => new
        {
            Last = x.Calendars.Select(y => y.SyncState == null ? (DateTimeOffset?)null : y.SyncState.LastSuccessfulSyncAtUtc).FirstOrDefault()
        }).ToListAsync(cancellationToken);
        if (accounts.Count == 0) return new(true, 0, null, false, false, "No Microsoft accounts are connected.");
        var last = accounts.Max(x => x.Last);
        var never = accounts.Any(x => x.Last is null);
        var stale = never || accounts.Any(x => x.Last < DateTimeOffset.UtcNow.AddMinutes(-options.Value.StaleAfterMinutes));
        var message = never ? "Availability warning: one or more calendars have never synced."
            : stale ? "Availability warning: cached Microsoft calendar data is stale."
            : "Microsoft calendar availability is current.";
        return new(true, accounts.Count, last, stale, never, message);
    }
}

internal sealed class OutlookPeriodicSyncService(IServiceScopeFactory scopeFactory, IOptions<MicrosoftIntegrationOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.SyncIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IOutlookSyncService>().SyncAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { /* Account-level details are persisted by OutlookSyncService. */ }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
