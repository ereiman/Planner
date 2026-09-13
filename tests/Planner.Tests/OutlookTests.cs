using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Planner.Web.Domain;
using Planner.Web.Services;

namespace Planner.Tests;

public sealed class OutlookTests
{
    private static readonly DateTimeOffset Start = new(2035, 1, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FullAndDeltaSyncMaskPrivacyAndUpsertUpdateDelete()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await AddAccount(database, "account-a");
        var graph = new FakeGraphAdapter();
        graph.Enqueue("account-a", Page(
            Event("one", "Visible", ExternalEventSensitivity.Normal),
            Event("secret", "Board acquisition", ExternalEventSensitivity.Private)));
        var sync = CreateSync(database, graph, new FakeTokenProvider((account, "account-a")));

        await sync.SyncAccountAsync(account);
        await using (var db = database.CreateDbContext())
        {
            var events = await db.ExternalCalendarEvents.OrderBy(x => x.ExternalId).ToListAsync();
            Assert.Equal(2, events.Count);
            Assert.Equal("Private event", events.Single(x => x.ExternalId == "secret").Subject);
            Assert.DoesNotContain("acquisition", events.Single(x => x.ExternalId == "secret").Subject, StringComparison.OrdinalIgnoreCase);
        }

        graph.Enqueue("account-a", new GraphDeltaPage(
            [Event("one", "Updated", ExternalEventSensitivity.Normal)], ["secret"], "cursor-2"));
        await sync.SyncAccountAsync(account);
        await using (var db = database.CreateDbContext())
        {
            var item = Assert.Single(await db.ExternalCalendarEvents.ToListAsync());
            Assert.Equal("Updated", item.Subject);
            Assert.Equal("cursor-2", (await db.CalendarSyncStates.SingleAsync()).DeltaCursor);
        }
    }

    [Fact]
    public async Task FailedCollectedPageDoesNotPartiallyChangeEventsOrCursor()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await AddAccount(database, "atomic");
        var graph = new FakeGraphAdapter();
        graph.Enqueue("atomic", Page(Event("one", "Original", ExternalEventSensitivity.Normal)));
        var sync = CreateSync(database, graph, new FakeTokenProvider((account, "atomic")));
        await sync.SyncAccountAsync(account);
        graph.EnqueueFailure("atomic", new HttpRequestException("page two failed with sensitive URL"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncAccountAsync(account));
        await using var db = database.CreateDbContext();
        Assert.Equal("Original", (await db.ExternalCalendarEvents.SingleAsync()).Subject);
        Assert.Equal("cursor-1", (await db.CalendarSyncStates.SingleAsync()).DeltaCursor);
        Assert.DoesNotContain("sensitive", (await db.ConnectedMicrosoftAccounts.SingleAsync()).LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidCursorFallsBackToAtomicFullSync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await AddAccount(database, "expired");
        var graph = new FakeGraphAdapter();
        graph.Enqueue("expired", Page(Event("old", "Old", ExternalEventSensitivity.Normal)));
        var sync = CreateSync(database, graph, new FakeTokenProvider((account, "expired")));
        await sync.SyncAccountAsync(account);
        graph.EnqueueFailure("expired", new InvalidDeltaCursorException("expired"));
        graph.Enqueue("expired", new GraphDeltaPage([Event("new", "New", ExternalEventSensitivity.Normal)], [], "fresh"));

        await sync.SyncAccountAsync(account);
        await using var db = database.CreateDbContext();
        var item = Assert.Single(await db.ExternalCalendarEvents.ToListAsync());
        Assert.Equal("new", item.ExternalId);
        Assert.Equal("fresh", (await db.CalendarSyncStates.SingleAsync()).DeltaCursor);
    }

    [Fact]
    public async Task SyncAllIsolatesAccountFailures()
    {
        await using var database = await TestDatabase.CreateAsync();
        var failing = await AddAccount(database, "bad");
        var working = await AddAccount(database, "good");
        var graph = new FakeGraphAdapter();
        graph.EnqueueFailure("bad", new HttpRequestException("offline"));
        graph.Enqueue("good", Page(Event("ok", "Good", ExternalEventSensitivity.Normal)));
        var sync = CreateSync(database, graph, new FakeTokenProvider((failing, "bad"), (working, "good")));

        await sync.SyncAllAsync();
        await using var db = database.CreateDbContext();
        Assert.Single(await db.ExternalCalendarEvents.ToListAsync());
        Assert.NotEmpty((await db.ConnectedMicrosoftAccounts.SingleAsync(x => x.Id == failing)).LastError);
        Assert.Empty((await db.ConnectedMicrosoftAccounts.SingleAsync(x => x.Id == working)).LastError);
    }

    [Fact]
    public async Task ConflictPoliciesIncludeBothAccountsAndIgnoreUnavailableEvents()
    {
        await using var database = await TestDatabase.CreateAsync();
        var first = await AddCalendarWithState(database, "first", DateTimeOffset.UtcNow);
        var second = await AddCalendarWithState(database, "second", DateTimeOffset.UtcNow);
        await AddExternal(database, first, "busy", ExternalEventShowAs.Busy);
        await AddExternal(database, second, "oof", ExternalEventShowAs.OutOfOffice);
        await AddExternal(database, first, "tentative", ExternalEventShowAs.Tentative);
        await AddExternal(database, first, "elsewhere", ExternalEventShowAs.WorkingElsewhere);
        await AddExternal(database, first, "unknown", ExternalEventShowAs.Unknown);
        await AddExternal(database, first, "free", ExternalEventShowAs.Free);
        await AddExternal(database, first, "declined", ExternalEventShowAs.Busy, ExternalEventResponse.Declined);
        await AddExternal(database, first, "cancelled", ExternalEventShowAs.Busy, cancelled: true);

        var conflicts = await database.Conflicts.DetectAsync(Start, Start.AddHours(1));
        Assert.Equal(5, conflicts.Count(x => x.Source == ScheduleConflictSource.MicrosoftCalendar));
        Assert.Equal(2, conflicts.Count(x => x.Severity == ScheduleConflictSeverity.Blocking));
        Assert.Equal(3, conflicts.Count(x => x.Severity == ScheduleConflictSeverity.Warning));
        Assert.Equal(2, conflicts.Where(x => x.MicrosoftAccountId is not null).Select(x => x.MicrosoftAccountId).Distinct().Count());
    }

    [Fact]
    public async Task BusyImportedEventBlocksNewPlannerMeeting()
    {
        await using var database = await TestDatabase.CreateAsync();
        var calendar = await AddCalendarWithState(database, "work", DateTimeOffset.UtcNow);
        await AddExternal(database, calendar, "meeting", ExternalEventShowAs.Busy);
        var task = await database.Tasks.CreateAsync("Focus");

        var result = await database.Schedule.CreateAsync(task, Start, Start.AddMinutes(30));
        Assert.False(result.Saved);
        Assert.True(result.RequiresOverride);
        Assert.Contains(result.Conflicts, x => x.Source == ScheduleConflictSource.MicrosoftCalendar);
    }

    [Fact]
    public async Task NeverSyncedAndStaleAccountsProduceAvailabilityWarnings()
    {
        await using var database = await TestDatabase.CreateAsync();
        await AddCalendarWithState(database, "never", null);
        await AddCalendarWithState(database, "stale", DateTimeOffset.UtcNow.AddHours(-2));

        var conflicts = await database.Conflicts.DetectAsync(Start, Start.AddHours(1));
        Assert.Equal(2, conflicts.Count(x => x.Source == ScheduleConflictSource.Availability));
        Assert.All(conflicts.Where(x => x.Source == ScheduleConflictSource.Availability), x => Assert.Equal(ScheduleConflictSeverity.Warning, x.Severity));
    }

    [Fact]
    public async Task NoConfigurationNoOpsWithoutGraphOrDatabaseChanges()
    {
        await using var database = await TestDatabase.CreateAsync();
        var graph = new FakeGraphAdapter();
        var sync = CreateSync(database, graph, new FakeTokenProvider());
        await sync.SyncAllAsync();
        await sync.SyncAccountAsync(Guid.NewGuid());
        Assert.Equal(0, graph.CallCount);
        await using var db = database.CreateDbContext();
        Assert.Empty(await db.ConnectedMicrosoftAccounts.ToListAsync());
    }

    private static OutlookSyncService CreateSync(TestDatabase database, FakeGraphAdapter graph, FakeTokenProvider tokens) =>
        new(database, tokens, graph, Options.Create(new MicrosoftIntegrationOptions()));

    private static GraphDeltaPage Page(params GraphEventDto[] events) => new(events, [], "cursor-1");
    private static GraphEventDto Event(string id, string subject, ExternalEventSensitivity sensitivity) =>
        new(id, id, subject, Start, Start.AddHours(1), false, false, sensitivity, ExternalEventShowAs.Busy,
            ExternalEventResponse.Accepted, ExternalEventKind.SingleInstance, string.Empty, Start);

    private static async Task<Guid> AddAccount(TestDatabase database, string token)
    {
        await using var db = database.CreateDbContext();
        var account = new ConnectedMicrosoftAccount
        {
            MsalHomeAccountId = token,
            DisplayName = token,
            EmailAddress = $"{token}@example.test",
            ConnectedAtUtc = DateTimeOffset.UtcNow
        };
        db.ConnectedMicrosoftAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private static async Task<Guid> AddCalendarWithState(TestDatabase database, string name, DateTimeOffset? lastSync)
    {
        var accountId = await AddAccount(database, name);
        await using var db = database.CreateDbContext();
        var calendar = new ExternalCalendar
        {
            ConnectedMicrosoftAccountId = accountId,
            ExternalId = $"calendar-{name}",
            Name = name,
            IsDefault = true,
            SyncState = new CalendarSyncState
            {
                HorizonStartsAtUtc = Start.AddDays(-1),
                HorizonEndsAtUtc = Start.AddDays(2),
                LastSuccessfulSyncAtUtc = lastSync,
                Status = lastSync is null ? CalendarSyncStatus.NeverSynced : CalendarSyncStatus.Current
            }
        };
        db.ExternalCalendars.Add(calendar);
        await db.SaveChangesAsync();
        return calendar.Id;
    }

    private static async Task AddExternal(TestDatabase database, Guid calendarId, string id, ExternalEventShowAs showAs,
        ExternalEventResponse response = ExternalEventResponse.Accepted, bool cancelled = false)
    {
        await using var db = database.CreateDbContext();
        db.ExternalCalendarEvents.Add(new ExternalCalendarEvent
        {
            ExternalCalendarId = calendarId,
            ExternalId = id,
            Subject = id,
            StartsAtUtc = Start,
            EndsAtUtc = Start.AddHours(1),
            ShowAs = showAs,
            Response = response,
            IsCancelled = cancelled,
            LastModifiedAtUtc = Start
        });
        await db.SaveChangesAsync();
    }

    private sealed class FakeTokenProvider(params (Guid Id, string Token)[] accounts) : IMicrosoftTokenProvider
    {
        private readonly Dictionary<Guid, string> _tokens = accounts.ToDictionary(x => x.Id, x => x.Token);
        public bool IsConfigured => _tokens.Count > 0;
        public Task<string?> AcquireTokenSilentAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult(_tokens.GetValueOrDefault(accountId));
    }

    private sealed class FakeGraphAdapter : IOutlookGraphAdapter
    {
        private readonly Dictionary<string, Queue<object>> _responses = [];
        public int CallCount { get; private set; }
        public void Enqueue(string token, GraphDeltaPage page) => Queue(token).Enqueue(page);
        public void EnqueueFailure(string token, Exception exception) => Queue(token).Enqueue(exception);
        public Task<GraphUserDto> GetMeAsync(string accessToken, CancellationToken cancellationToken) =>
            Task.FromResult(new GraphUserDto(accessToken, accessToken, $"{accessToken}@example.test", "tenant"));
        public Task<GraphCalendarDto> GetDefaultCalendarAsync(string accessToken, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new GraphCalendarDto($"calendar-{accessToken}", "Calendar"));
        }
        public Task<GraphDeltaPage> GetCalendarDeltaAsync(string accessToken, DateTimeOffset horizonStartUtc, DateTimeOffset horizonEndUtc, string? deltaCursor, CancellationToken cancellationToken)
        {
            CallCount++;
            var response = Queue(accessToken).Dequeue();
            return response is Exception exception ? Task.FromException<GraphDeltaPage>(exception) : Task.FromResult((GraphDeltaPage)response);
        }
        private Queue<object> Queue(string token)
        {
            if (!_responses.TryGetValue(token, out var queue)) _responses[token] = queue = new Queue<object>();
            return queue;
        }
    }
}
