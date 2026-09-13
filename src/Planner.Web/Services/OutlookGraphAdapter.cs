using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Planner.Web.Domain;

namespace Planner.Web.Services;

internal sealed class OutlookGraphAdapter(IHttpClientFactory httpClientFactory) : IOutlookGraphAdapter
{
    public async Task<GraphUserDto> GetMeAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var document = await GetAsync("https://graph.microsoft.com/v1.0/me?$select=id,displayName,mail,userPrincipalName", accessToken, false, cancellationToken);
        var root = document.RootElement;
        return new GraphUserDto(
            Text(root, "id"), Text(root, "displayName"),
            Text(root, "mail", Text(root, "userPrincipalName")), string.Empty);
    }

    public async Task<GraphCalendarDto> GetDefaultCalendarAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var document = await GetAsync("https://graph.microsoft.com/v1.0/me/calendar?$select=id,name", accessToken, false, cancellationToken);
        return new GraphCalendarDto(Text(document.RootElement, "id"), Text(document.RootElement, "name", "Calendar"));
    }

    public async Task<GraphDeltaPage> GetCalendarDeltaAsync(
        string accessToken,
        DateTimeOffset horizonStartUtc,
        DateTimeOffset horizonEndUtc,
        string? deltaCursor,
        CancellationToken cancellationToken)
    {
        var url = string.IsNullOrWhiteSpace(deltaCursor)
            ? $"https://graph.microsoft.com/v1.0/me/calendarView/delta?startDateTime={Uri.EscapeDataString(horizonStartUtc.UtcDateTime.ToString("O"))}&endDateTime={Uri.EscapeDataString(horizonEndUtc.UtcDateTime.ToString("O"))}&$select=id,iCalUId,subject,start,end,isAllDay,isCancelled,sensitivity,showAs,responseStatus,type,seriesMasterId,lastModifiedDateTime"
            : deltaCursor;
        var events = new List<GraphEventDto>();
        var removed = new List<string>();
        string? finalCursor = null;
        while (!string.IsNullOrWhiteSpace(url))
        {
            using var document = await GetAsync(url, accessToken, !string.IsNullOrWhiteSpace(deltaCursor), cancellationToken);
            var root = document.RootElement;
            if (root.TryGetProperty("value", out var values))
            {
                foreach (var item in values.EnumerateArray())
                {
                    var id = Text(item, "id");
                    if (item.TryGetProperty("@removed", out _))
                    {
                        if (id.Length > 0) removed.Add(id);
                        continue;
                    }
                    var starts = ParseGraphDateTime(item.GetProperty("start"));
                    var ends = ParseGraphDateTime(item.GetProperty("end"));
                    if (id.Length == 0 || ends <= starts) continue;
                    events.Add(new GraphEventDto(
                        id, Text(item, "iCalUId"), Text(item, "subject", "(No title)"), starts, ends,
                        Boolean(item, "isAllDay"), Boolean(item, "isCancelled"),
                        ParseSensitivity(Text(item, "sensitivity")), ParseShowAs(Text(item, "showAs")),
                        ParseResponse(item), ParseKind(Text(item, "type")), Text(item, "seriesMasterId"),
                        ParseDate(Text(item, "lastModifiedDateTime")) ?? DateTimeOffset.UtcNow));
                }
            }
            url = Text(root, "@odata.nextLink");
            var cursor = Text(root, "@odata.deltaLink");
            if (cursor.Length > 0) finalCursor = cursor;
        }
        if (string.IsNullOrWhiteSpace(finalCursor))
            throw new InvalidOperationException("Microsoft Graph did not return a delta cursor.");
        return new GraphDeltaPage(events, removed, finalCursor);
    }

    private async Task<JsonDocument> GetAsync(string url, string accessToken, bool deltaRequest, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Microsoft Graph returned an invalid continuation link.");
        var client = httpClientFactory.CreateClient(nameof(OutlookGraphAdapter));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\"");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (deltaRequest && response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.BadRequest)
            throw new InvalidDeltaCursorException("The Microsoft calendar cursor expired.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Microsoft Graph request failed ({(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static DateTimeOffset ParseGraphDateTime(JsonElement value)
    {
        var text = Text(value, "dateTime");
        if (DateTimeOffset.TryParse(text, out var offset)) return offset.ToUniversalTime();
        if (DateTime.TryParse(text, out var date))
            return new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc));
        throw new InvalidOperationException("Microsoft Graph returned an invalid event time.");
    }

    private static DateTimeOffset? ParseDate(string value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;
    private static string Text(JsonElement element, string property, string fallback = "") =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static bool Boolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static ExternalEventSensitivity ParseSensitivity(string value) => value.ToLowerInvariant() switch
    {
        "personal" => ExternalEventSensitivity.Personal,
        "private" => ExternalEventSensitivity.Private,
        "confidential" => ExternalEventSensitivity.Confidential,
        _ => ExternalEventSensitivity.Normal
    };
    private static ExternalEventShowAs ParseShowAs(string value) => value.ToLowerInvariant() switch
    {
        "free" => ExternalEventShowAs.Free,
        "tentative" => ExternalEventShowAs.Tentative,
        "busy" => ExternalEventShowAs.Busy,
        "oof" => ExternalEventShowAs.OutOfOffice,
        "workingelsewhere" => ExternalEventShowAs.WorkingElsewhere,
        _ => ExternalEventShowAs.Unknown
    };
    private static ExternalEventResponse ParseResponse(JsonElement item)
    {
        if (!item.TryGetProperty("responseStatus", out var response)) return ExternalEventResponse.None;
        return Text(response, "response").ToLowerInvariant() switch
        {
            "organizer" => ExternalEventResponse.Organizer,
            "tentativelyaccepted" => ExternalEventResponse.TentativelyAccepted,
            "accepted" => ExternalEventResponse.Accepted,
            "declined" => ExternalEventResponse.Declined,
            "notresponded" => ExternalEventResponse.NotResponded,
            _ => ExternalEventResponse.None
        };
    }
    private static ExternalEventKind ParseKind(string value) => value.ToLowerInvariant() switch
    {
        "occurrence" => ExternalEventKind.Occurrence,
        "exception" => ExternalEventKind.Exception,
        "seriesmaster" => ExternalEventKind.SeriesMaster,
        _ => ExternalEventKind.SingleInstance
    };
}
