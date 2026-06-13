using System.Net.Http.Headers;
using System.Text.Json;

namespace TradeFlow.AlertPoC.Services;

/// <summary>
/// Thin HTTP client for the Xtrades v2 alerts endpoint used by the AlertPoC program.
/// The production implementation lives in TradeFlow.Worker.Services.AlertApiClient,
/// which is configured via IHttpClientFactory. This client is intentionally simple —
/// it exists only to keep the POC self-contained.
/// </summary>
public class AlertApiPocClient : IAlertApiClient
{
    private readonly HttpClient _httpClient;

    private const string AlertsUrl =
        "https://app.xtrades.net/api/v2/alerts" +
        "?DateSpec=Today" +
        "&Page=1" +
        "&PageSize=10" +
        "&OrderBy=TimeOfEntryAlertEpoch%20desc" +
        "&Side=bto" +
        "&AlertType=all";

    public AlertApiPocClient(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token, nameof(token));

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Fetches the most recent alerts from the Xtrades API.
    /// Throws <see cref="AlertApiPocException"/> on any network, HTTP, or deserialization failure.
    /// </summary>
    public async Task<List<Alert>> GetAlertsAsync(
        CancellationToken cancellationToken = default,
        int pageSize = 10)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(AlertsUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new AlertApiPocException($"Network error: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AlertApiPocException("Request timed out.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AlertApiPocException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        AlertsResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<AlertsResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new AlertApiPocException($"Failed to deserialize response: {ex.Message}", ex);
        }

        return result?.Alerts ?? result?.Data ?? result?.Items ?? [];
    }

    /// <summary>
    /// Returns true if the Xtrades API responds successfully with the current token.
    /// Returns false for any non-success response or network failure.
    /// </summary>
    public async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(AlertsUrl, cancellationToken);
            return response.IsSuccessStatusCode
                || response.StatusCode == System.Net.HttpStatusCode.NoContent;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Thrown by <see cref="AlertApiPocClient"/> for any communication failure with the Xtrades API.
/// </summary>
public class AlertApiPocException : Exception
{
    public AlertApiPocException(string message) : base(message) { }
    public AlertApiPocException(string message, Exception inner) : base(message, inner) { }
}