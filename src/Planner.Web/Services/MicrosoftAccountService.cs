using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Planner.Web.Data;
using Planner.Web.Domain;

namespace Planner.Web.Services;

internal sealed class MicrosoftAccountService : IMicrosoftAccountService, IMicrosoftTokenProvider
{
    private static readonly string[] Scopes = ["User.Read", "Calendars.Read"];
    private readonly IDbContextFactory<PlannerDbContext> _factory;
    private readonly IOutlookGraphAdapter _graph;
    private readonly IPublicClientApplication? _application;

    public MicrosoftAccountService(
        IDbContextFactory<PlannerDbContext> factory,
        IOutlookGraphAdapter graph,
        IOptions<MicrosoftIntegrationOptions> options)
    {
        _factory = factory;
        _graph = graph;
        var configuration = options.Value;
        if (string.IsNullOrWhiteSpace(configuration.ClientId)) return;

        _application = PublicClientApplicationBuilder.Create(configuration.ClientId.Trim())
            .WithAuthority(configuration.Authority, validateAuthority: true)
            .WithDefaultRedirectUri()
            .Build();

        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Planner", "Auth");
        Directory.CreateDirectory(cacheDirectory);
        var storage = new StorageCreationPropertiesBuilder("msal.cache", cacheDirectory).Build();
        var cacheHelper = MsalCacheHelper.CreateAsync(storage).GetAwaiter().GetResult();
        cacheHelper.RegisterCache(_application.UserTokenCache);
    }

    public bool IsConfigured => _application is not null;

    public async Task<IReadOnlyList<MicrosoftAccountSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var accounts = await db.ConnectedMicrosoftAccounts.AsNoTracking()
            .Include(x => x.Calendars).ThenInclude(c => c.SyncState)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);
        return accounts.Select(x => new MicrosoftAccountSummary(
            x.Id, x.DisplayName, x.EmailAddress, x.ConnectionStatus, x.LastAuthenticatedAtUtc,
            x.Calendars.Select(y => y.SyncState?.LastSuccessfulSyncAtUtc).FirstOrDefault(),
            x.Calendars.Select(y => y.SyncState?.Status ?? CalendarSyncStatus.NeverSynced).FirstOrDefault(),
            x.LastError != string.Empty ? x.LastError : x.Calendars.Select(y => y.SyncState?.LastError).FirstOrDefault() ?? string.Empty,
            x.Calendars.Select(y => new MicrosoftCalendarSummary(
                y.Id, y.Name, y.SyncState?.Status ?? CalendarSyncStatus.NeverSynced, y.SyncState?.LastSuccessfulSyncAtUtc, y.SyncState?.LastError ?? string.Empty)).ToList()))
            .ToList();
    }

    public async Task<Guid> ConnectAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        AuthenticationResult result;
        try
        {
            result = await _application!.AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(cancellationToken);
        }
        catch (MsalException exception)
        {
            throw new InvalidOperationException(SanitizeAuthenticationError(exception), exception);
        }

        return await SaveConnectedAccountAsync(result, cancellationToken);
    }

    public async Task ReconnectAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var connected = await db.ConnectedMicrosoftAccounts.SingleAsync(x => x.Id == accountId, cancellationToken);
        var account = (await _application!.GetAccountsAsync()).SingleOrDefault(x => x.HomeAccountId.Identifier == connected.MsalHomeAccountId);
        try
        {
            var builder = _application.AcquireTokenInteractive(Scopes).WithUseEmbeddedWebView(false);
            var result = await (account is null ? builder.WithPrompt(Prompt.SelectAccount) : builder.WithAccount(account))
                .ExecuteAsync(cancellationToken);
            if (!string.Equals(result.Account.HomeAccountId.Identifier, connected.MsalHomeAccountId, StringComparison.Ordinal))
                throw new InvalidOperationException("The selected Microsoft account does not match the account being reconnected.");
            var profile = await _graph.GetMeAsync(result.AccessToken, cancellationToken);
            connected.DisplayName = profile.DisplayName;
            connected.EmailAddress = profile.EmailAddress;
            connected.MicrosoftUserId = profile.Id;
            connected.TenantId = result.TenantId;
            connected.ConnectionStatus = MicrosoftAccountConnectionStatus.Connected;
            connected.LastAuthenticatedAtUtc = DateTimeOffset.UtcNow;
            connected.LastError = string.Empty;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (MsalException exception)
        {
            connected.ConnectionStatus = MicrosoftAccountConnectionStatus.ReconnectRequired;
            connected.LastError = SanitizeAuthenticationError(exception);
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(connected.LastError, exception);
        }
    }

    public async Task DisconnectAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var connected = await db.ConnectedMicrosoftAccounts.SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (connected is null) return;
        if (_application is not null)
        {
            var account = (await _application.GetAccountsAsync()).SingleOrDefault(x => x.HomeAccountId.Identifier == connected.MsalHomeAccountId);
            if (account is not null) await _application.RemoveAsync(account);
        }
        db.ConnectedMicrosoftAccounts.Remove(connected);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> AcquireTokenSilentAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (_application is null) return null;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var connected = await db.ConnectedMicrosoftAccounts.SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (connected is null) return null;
        var account = (await _application.GetAccountsAsync()).SingleOrDefault(x => x.HomeAccountId.Identifier == connected.MsalHomeAccountId);
        if (account is null)
        {
            await MarkReconnectRequiredAsync(connected, db, "Microsoft sign-in is required.", cancellationToken);
            return null;
        }
        try
        {
            var result = await _application.AcquireTokenSilent(Scopes, account).ExecuteAsync(cancellationToken);
            connected.ConnectionStatus = MicrosoftAccountConnectionStatus.Connected;
            connected.LastAuthenticatedAtUtc = DateTimeOffset.UtcNow;
            connected.LastError = string.Empty;
            await db.SaveChangesAsync(cancellationToken);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            await MarkReconnectRequiredAsync(connected, db, "Microsoft sign-in is required.", cancellationToken);
            return null;
        }
        catch (MsalException)
        {
            await MarkReconnectRequiredAsync(connected, db, "Microsoft authentication failed. Reconnect the account.", cancellationToken);
            return null;
        }
    }

    private async Task<Guid> SaveConnectedAccountAsync(AuthenticationResult result, CancellationToken cancellationToken)
    {
        var profile = await _graph.GetMeAsync(result.AccessToken, cancellationToken);
        var homeAccountId = result.Account.HomeAccountId.Identifier;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var connected = await db.ConnectedMicrosoftAccounts.SingleOrDefaultAsync(x => x.MsalHomeAccountId == homeAccountId, cancellationToken);
        if (connected is null)
        {
            connected = new ConnectedMicrosoftAccount { MsalHomeAccountId = homeAccountId, ConnectedAtUtc = DateTimeOffset.UtcNow };
            db.ConnectedMicrosoftAccounts.Add(connected);
        }
        connected.TenantId = result.TenantId;
        connected.MicrosoftUserId = profile.Id;
        connected.DisplayName = profile.DisplayName;
        connected.EmailAddress = profile.EmailAddress;
        connected.ConnectionStatus = MicrosoftAccountConnectionStatus.Connected;
        connected.LastAuthenticatedAtUtc = DateTimeOffset.UtcNow;
        connected.LastError = string.Empty;
        await db.SaveChangesAsync(cancellationToken);
        return connected.Id;
    }

    private void EnsureConfigured()
    {
        if (_application is null)
            throw new InvalidOperationException("Microsoft integration is not configured. Set Planner:Microsoft:ClientId first.");
    }

    private static async Task MarkReconnectRequiredAsync(ConnectedMicrosoftAccount account, PlannerDbContext db, string message, CancellationToken cancellationToken)
    {
        account.ConnectionStatus = MicrosoftAccountConnectionStatus.ReconnectRequired;
        account.LastError = message;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string SanitizeAuthenticationError(MsalException exception) => exception.ErrorCode switch
    {
        "authentication_canceled" => "Microsoft sign-in was cancelled.",
        "access_denied" => "Microsoft sign-in was denied.",
        _ => "Microsoft authentication failed. Try reconnecting the account."
    };
}
