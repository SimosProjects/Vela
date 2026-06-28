using System.Text.Json;

namespace Vela.Worker.Services;

/// <summary>
/// Thin HTTP client for the Xtrades alerts endpoint. Fetches and deserializes alerts only.
/// The HttpClient is injected by IHttpClientFactory with base address, auth headers,
/// and resilience policies configured in Program.cs.
/// </summary>
public class AlertApiClient : IAlertApiClient
{
    private readonly HttpClient _httpClient;

    public AlertApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Fetches recent entry alerts (BTO/AVG) from the Xtrades REST API ordered by
    /// entry time descending. STC/BTC alerts are not included — they sort by exit time
    /// and would fall off this page behind same-day entries.
    /// Throws <see cref="AlertApiException"/> for any network, HTTP, or deserialization failure.
    /// On 401 or 403 the exception carries the status code so callers can distinguish
    /// an expired token from a transient error.
    /// </summary>
    public async Task<List<Alert>> GetAlertsAsync(
        CancellationToken cancellationToken = default,
        int pageSize = 10)
    {
        var path = "/api/v2/alerts" +
            "?DateSpec=Today" +
            "&Page=1" +
            $"&PageSize={pageSize}" +
            "&OrderBy=TimeOfEntryAlertEpoch%20desc" +
            "&Side=bto" +
            "&AlertType=all";

        return await FetchAlertsAsync(path, cancellationToken);
    }

    /// <summary>
    /// Fetches recent exit alerts (STC/BTC) from the Xtrades REST API ordered by exit
    /// time descending. Uses a wider date window than the entry fetch so that exits for
    /// positions opened earlier in the week are still returned. Called on every poll cycle
    /// to catch exits that fired during a SignalR gap.
    /// Throws <see cref="AlertApiException"/> for any network, HTTP, or deserialization failure.
    /// </summary>
    public async Task<List<Alert>> GetExitAlertsAsync(
        CancellationToken cancellationToken = default,
        int pageSize = 20)
    {
        var path = "/api/v2/alerts" +
            "?DateSpec=Week" +
            "&Page=1" +
            $"&PageSize={pageSize}" +
            "&OrderBy=id%20desc" +
            "&Side=stc" +
            "&AlertType=all";

        return await FetchAlertsAsync(path, cancellationToken);
    }

    /// <summary>
    /// Verifies that the Xtrades API is reachable and the current token is valid by
    /// making a lightweight authenticated request. Uses the same configured HTTP client
    /// as <see cref="GetAlertsAsync"/> so the auth header is always present.
    /// Throws <see cref="AlertApiException"/> with the HTTP status code on 401 or 403
    /// so callers can distinguish an expired token from an unreachable service.
    /// Returns false for other non-success responses and network failures.
    /// </summary>
    public async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                "/api/v2/alerts?DateSpec=Today&Page=1&PageSize=1&OrderBy=TimeOfEntryAlertEpoch%20desc&Side=bto&AlertType=all",
                cancellationToken);

            var statusCode = (int)response.StatusCode;

            if (statusCode is 401 or 403)
                throw new AlertApiException(
                    $"Xtrades authentication failed ({statusCode}) — token may be expired.",
                    statusCode);

            return response.IsSuccessStatusCode
                || response.StatusCode == System.Net.HttpStatusCode.NoContent;
        }
        catch (AlertApiException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    // -- Helpers --

    private async Task<List<Alert>> FetchAlertsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(path, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new AlertApiException($"Network error: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new AlertApiException("Request timed out.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AlertApiException(
                $"HTTP {statusCode} {response.ReasonPhrase}: {body}",
                statusCode);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return [];

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        AlertsResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<AlertsResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new AlertApiException(
                $"Failed to deserialize response: {ex.Message}", ex);
        }

        return result?.Alerts ?? result?.Data ?? result?.Items ?? [];
    }
}

/// <summary>
/// Thrown by <see cref="AlertApiClient"/> for any network, HTTP, or deserialization
/// failure when communicating with the Xtrades API. Carries <see cref="StatusCode"/>
/// for HTTP-level failures so callers can classify 401/403 (expired token) without
/// string-parsing the message.
/// </summary>
public class AlertApiException : Exception
{
    /// <summary>
    /// HTTP status code returned by the server. Zero for non-HTTP failures
    /// such as network errors, timeouts, or deserialization exceptions.
    /// </summary>
    public int StatusCode { get; }

    public AlertApiException(string message, Exception? inner = null)
        : base(message, inner) { }

    public AlertApiException(string message, int statusCode, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}