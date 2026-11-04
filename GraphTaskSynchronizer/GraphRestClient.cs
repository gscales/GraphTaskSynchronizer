using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace GraphTaskSynchronizer;

/// <summary>Supplies bearer tokens for Microsoft Graph.</summary>
public interface IGraphTokenProvider
{
    Task<string> GetTokenAsync(CancellationToken ct);
}

/// <summary>A non-success Graph response, with the error code from the body when there is one.</summary>
public sealed class GraphRequestException : Exception
{
    public GraphRequestException(HttpStatusCode statusCode, string? errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }
}

/// <summary>
/// A small Microsoft Graph REST client: bearer auth, JSON in/out, @odata.nextLink paging,
/// and retries for throttling and transient failures (honouring Retry-After).
///
/// Why REST rather than the Graph .NET SDK: the SDK's TodoTask model has no
/// SingleValueExtendedProperties navigation property (checked v5.56.0 through 5.105.0),
/// so the PidLidPercentComplete value that Graph returns for
/// $expand=singleValueExtendedProperties(...) on a todoTask has nowhere typed to land.
/// Plain DTOs (GraphModels.cs) carry it directly.
/// </summary>
public sealed class GraphRestClient
{
    public static readonly Uri DefaultBaseAddress = new("https://graph.microsoft.com/v1.0/");

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly IGraphTokenProvider _tokens;
    private readonly ILogger<GraphRestClient> _logger;
    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;

    public GraphRestClient(
        HttpClient http,
        IGraphTokenProvider tokens,
        ILoggerFactory loggerFactory,
        int maxAttempts = 5,
        TimeSpan? baseDelay = null)
    {
        _http = http;
        _http.BaseAddress ??= DefaultBaseAddress;
        _tokens = tokens;
        _logger = loggerFactory.CreateLogger<GraphRestClient>();
        _maxAttempts = Math.Max(1, maxAttempts);
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>GET, returning the raw JSON body.</summary>
    public async Task<string> GetStringAsync(string url, CancellationToken ct) =>
        await SendAsync(HttpMethod.Get, url, null, ct) ?? string.Empty;

    public async Task<T?> GetAsync<T>(string url, CancellationToken ct) =>
        Deserialize<T>(await SendAsync(HttpMethod.Get, url, null, ct));

    public async Task<T?> PostAsync<T>(string url, object body, CancellationToken ct) =>
        Deserialize<T>(await SendAsync(HttpMethod.Post, url, body, ct));

    /// <summary>POST with no request body, for actions such as subscriptions/{id}/reauthorize.</summary>
    public Task PostAsync(string url, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, url, null, ct);

    public async Task<T?> PatchAsync<T>(string url, object body, CancellationToken ct) =>
        Deserialize<T>(await SendAsync(HttpMethod.Patch, url, body, ct));

    public Task DeleteAsync(string url, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, url, null, ct);

    /// <summary>
    /// One round of a delta query: follows @odata.nextLink until the page that carries the
    /// @odata.deltaLink for the next round. <paramref name="prefer"/> (e.g.
    /// odata.maxpagesize=100) is sent with every page.
    /// </summary>
    public async Task<(List<T> Items, string DeltaLink)> GetDeltaAsync<T>(string url, string? prefer, CancellationToken ct)
    {
        var items = new List<T>();
        var next = url;

        while (true)
        {
            var page = Deserialize<GraphCollection<T>>(await SendAsync(HttpMethod.Get, next, null, ct, prefer));
            if (page?.Value is not null)
                items.AddRange(page.Value);

            if (!string.IsNullOrEmpty(page?.NextLink))
            {
                next = page.NextLink;
                continue;
            }

            if (!string.IsNullOrEmpty(page?.DeltaLink))
                return (items, page.DeltaLink);

            throw new GraphRequestException(HttpStatusCode.OK, null,
                $"GET {next} → a delta page with neither @odata.nextLink nor @odata.deltaLink");
        }
    }

    /// <summary>GETs a collection, following @odata.nextLink until the last page.</summary>
    public async Task<List<T>> GetAllPagesAsync<T>(string url, CancellationToken ct)
    {
        var results = new List<T>();
        string? next = url;

        while (next is not null)
        {
            var page = await GetAsync<GraphCollection<T>>(next, ct);
            if (page?.Value is not null)
                results.AddRange(page.Value);
            next = page?.NextLink;
        }

        return results;
    }

    // ──────────────────────────────────────────────────────────
    //  Transport
    // ──────────────────────────────────────────────────────────

    private async Task<string?> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct, string? prefer = null)
    {
        var bodyJson = body is null ? null : JsonSerializer.Serialize(body, body.GetType(), JsonOpts);

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetTokenAsync(ct));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (prefer is not null)
                request.Headers.TryAddWithoutValidation("Prefer", prefer);
            if (bodyJson is not null)
                request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (Exception ex) when (attempt < _maxAttempts
                                       && (ex is HttpRequestException
                                           || (ex is TaskCanceledException && !ct.IsCancellationRequested)))
            {
                // Network failure, or HttpClient's own timeout (not our cancellation).
                var delay = Backoff(attempt);
                _logger.LogWarning("[Graph] {Method} {Url} failed ({Message}); retry {Attempt}/{Max} in {Delay}.",
                    method, url, ex.Message, attempt, _maxAttempts - 1, delay);
                await Task.Delay(delay, ct);
                continue;
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                    return string.IsNullOrWhiteSpace(text) ? null : text;

                if (IsTransient(response.StatusCode) && attempt < _maxAttempts)
                {
                    var delay = RetryDelay(response, attempt);
                    _logger.LogWarning("[Graph] {Method} {Url} → {Status}; retry {Attempt}/{Max} in {Delay}.",
                        method, url, (int)response.StatusCode, attempt, _maxAttempts - 1, delay);
                    await Task.Delay(delay, ct);
                    continue;
                }

                throw CreateException(response.StatusCode, text, method, url);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.BadGateway;

    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return delta;
        if (retryAfter?.Date is { } date && date > DateTimeOffset.UtcNow)
            return date - DateTimeOffset.UtcNow;
        return Backoff(attempt);
    }

    private TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));

    private static GraphRequestException CreateException(HttpStatusCode status, string? body, HttpMethod method, string url)
    {
        string? code = null;
        string? message = null;
        try
        {
            var envelope = string.IsNullOrWhiteSpace(body)
                ? null
                : JsonSerializer.Deserialize<GraphErrorEnvelope>(body, JsonOpts);
            code = envelope?.Error?.Code;
            message = envelope?.Error?.Message;
        }
        catch (JsonException)
        {
            // Not a Graph error body; the status code is all there is.
        }

        return new GraphRequestException(status, code,
            $"{method} {url} → {(int)status} {status}{(code is null ? "" : $": {code}")}{(message is null ? "" : $" – {message}")}");
    }

    private static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, JsonOpts);
}
