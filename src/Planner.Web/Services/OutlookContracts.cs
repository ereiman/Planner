using Planner.Web.Domain;

namespace Planner.Web.Services;

public sealed class MicrosoftIntegrationOptions
{
    public const string SectionName = "Planner:Microsoft";
    public string ClientId { get; set; } = string.Empty;
    public string Authority { get; set; } = "https://login.microsoftonline.com/common";
    public int SyncIntervalMinutes { get; set; } = 15;
    public int HorizonPastDays { get; set; } = 30;
    public int HorizonFutureDays { get; set; } = 365;
    public int StaleAfterMinutes { get; set; } = 60;
}

public sealed record MicrosoftCalendarSummary(
    Guid Id,
    string Name,
    CalendarSyncStatus SyncStatus,
    DateTimeOffset? LastSuccessfulSyncAtUtc,
    string LastError);

public sealed record MicrosoftAccountSummary(
    Guid Id,
    string DisplayName,
    string EmailAddress,
    MicrosoftAccountConnectionStatus ConnectionStatus,
    DateTimeOffset? LastAuthenticatedAtUtc,
    DateTimeOffset? LastSuccessfulSyncAtUtc,
    CalendarSyncStatus SyncStatus,
    string LastError,
    IReadOnlyList<MicrosoftCalendarSummary> Calendars);

public sealed record ImportedCalendarEventDto(
    Guid Id,
    Guid AccountId,
    string AccountName,
    string Subject,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    bool IsAllDay,
    ExternalEventShowAs ShowAs,
    ExternalEventResponse Response,
    bool HasPlannerConflict);

public sealed record OutlookAvailabilitySummary(
    bool IsConfigured,
    int AccountCount,
    DateTimeOffset? LastSuccessfulSyncAtUtc,
    bool IsStale,
    bool HasNeverSyncedAccount,
    string Message);

public interface IMicrosoftAccountService
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<MicrosoftAccountSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<Guid> ConnectAsync(CancellationToken cancellationToken = default);
    Task ReconnectAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task DisconnectAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public interface IOutlookSyncService
{
    Task SyncAllAsync(CancellationToken cancellationToken = default);
    Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public interface IOutlookCalendarService
{
    Task<IReadOnlyList<ImportedCalendarEventDto>> ListEventsAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default);
    Task<OutlookAvailabilitySummary> GetAvailabilityAsync(CancellationToken cancellationToken = default);
}

internal interface IMicrosoftTokenProvider
{
    bool IsConfigured { get; }
    Task<string?> AcquireTokenSilentAsync(Guid accountId, CancellationToken cancellationToken);
}

internal sealed record GraphUserDto(string Id, string DisplayName, string EmailAddress, string TenantId);
internal sealed record GraphCalendarDto(string Id, string Name);
internal sealed record GraphEventDto(
    string Id,
    string ICalUId,
    string Subject,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    bool IsAllDay,
    bool IsCancelled,
    ExternalEventSensitivity Sensitivity,
    ExternalEventShowAs ShowAs,
    ExternalEventResponse Response,
    ExternalEventKind Kind,
    string SeriesMasterId,
    DateTimeOffset LastModifiedAtUtc);
internal sealed record GraphDeltaPage(IReadOnlyList<GraphEventDto> Events, IReadOnlyList<string> RemovedIds, string DeltaCursor);

internal interface IOutlookGraphAdapter
{
    Task<GraphUserDto> GetMeAsync(string accessToken, CancellationToken cancellationToken);
    Task<GraphCalendarDto> GetDefaultCalendarAsync(string accessToken, CancellationToken cancellationToken);
    Task<GraphDeltaPage> GetCalendarDeltaAsync(string accessToken, DateTimeOffset horizonStartUtc, DateTimeOffset horizonEndUtc, string? deltaCursor, CancellationToken cancellationToken);
}

internal sealed class InvalidDeltaCursorException : Exception
{
    public InvalidDeltaCursorException(string message, Exception? innerException = null) : base(message, innerException) { }
}
