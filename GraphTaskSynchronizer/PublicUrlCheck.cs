using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GraphTaskSynchronizer;

public enum UrlCheckOutcome
{
    /// <summary>This synchronizer answered the validation request through the public URL.</summary>
    Ok,

    /// <summary>Something else answered: Graph's validation would fail the same way.</summary>
    Problem,

    /// <summary>
    /// No answer from here (can't connect, timed out). Not conclusive: this machine may not be
    /// able to reach its own public URL while Graph can, so subscribing goes ahead.
    /// </summary>
    Inconclusive
}

public sealed record UrlCheckResult(UrlCheckOutcome Outcome, string? Url = null, string? Problem = null)
{
    public static readonly UrlCheckResult Passed = new(UrlCheckOutcome.Ok);
}

/// <summary>
/// Sends the request Graph sends to validate a subscription — POST {url}?validationToken=… —
/// to the public notification and lifecycle URLs, through ngrok, and checks that this
/// synchronizer echoes the token. When Graph's validation fails it only says something like
/// "Subscription validation request failed. HTTP status code is 'NotFound'"; this says what
/// answered instead:
///
///   echoed by this instance                → fine
///   ngrok ERR_NGROK_3200 (404)             → ngrok has no endpoint at that URL: not running,
///                                            or running with a different URL
///   ngrok ERR_NGROK_8012 (502)             → ngrok forwards to a port nothing listens on
///   this synchronizer, but not an echo     → a path it doesn't serve (Ngrok:PublicUrl with a path?)
///   another copy of the synchronizer       → ngrok forwards to a different running instance
///   anything else                          → ngrok forwards to some other web server
///   no answer / can't connect              → inconclusive
///
/// Every response from <see cref="WebhookServer"/> carries <see cref="WebhookServer.InstanceHeader"/>,
/// which is how "this instance" is recognised.
/// </summary>
public sealed class PublicUrlCheck
{
    /// <summary>Validation tokens of self-checks start with this (the webhook logs them as such).</summary>
    public const string TokenPrefix = "self-check-";

    private static readonly Regex NgrokErrorCode = new(@"ERR_NGROK_\d+", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly string _instanceId;
    private readonly int _localPort;

    /// <param name="http">A client with a short timeout; requests go to absolute public URLs.</param>
    /// <param name="instanceId">This synchronizer's <see cref="WebhookServer.InstanceId"/>.</param>
    /// <param name="localPort">Webhook:Port, for the hints.</param>
    public PublicUrlCheck(HttpClient http, string instanceId, int localPort)
    {
        _http = http;
        _instanceId = instanceId;
        _localPort = localPort;
    }

    /// <summary>Checks each URL in turn; the first that isn't fine decides the result.</summary>
    public async Task<UrlCheckResult> CheckAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        foreach (var url in urls.Distinct(StringComparer.Ordinal))
        {
            var result = await CheckOneAsync(url, ct);
            if (result.Outcome != UrlCheckOutcome.Ok)
                return result;
        }

        return UrlCheckResult.Passed;
    }

    private async Task<UrlCheckResult> CheckOneAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Problem(url, $"'{url}' isn't a valid URL. Set Ngrok:PublicUrl to the https URL on ngrok's Forwarding line.");

        var token = TokenPrefix + Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{url}{(string.IsNullOrEmpty(uri.Query) ? "?" : "&")}validationToken={Uri.EscapeDataString(token)}")
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain")
        };
        // Free ngrok domains put a warning page in front of browsers; Graph never sees it.
        request.Headers.TryAddWithoutValidation("ngrok-skip-browser-warning", "1");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Inconclusive(url, $"no answer from {uri.Host} within {_http.Timeout.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException ex)
        {
            return Inconclusive(url, $"couldn't connect to {uri.Host} ({ex.Message})");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var status = $"{(int)response.StatusCode} {response.StatusCode}";
            var instance = response.Headers.TryGetValues(WebhookServer.InstanceHeader, out var instances)
                ? instances.FirstOrDefault()
                : null;

            if (instance == _instanceId)
            {
                if (response.StatusCode == HttpStatusCode.OK && body == token)
                    return UrlCheckResult.Passed;

                return Problem(url,
                    $"the request reached this synchronizer, but {uri.AbsolutePath} answered {status}. Ngrok:PublicUrl " +
                    "should be just the scheme and host, with no path: the paths come from Subscription:NotificationPath " +
                    "and Subscription:LifecycleNotificationPath.");
            }

            if (instance is not null)
            {
                return Problem(url,
                    $"another running copy of the synchronizer answered (instance {instance}; this one is {_instanceId}). " +
                    $"ngrok is forwarding to that one. Stop it, or point ngrok at this one with `ngrok http {_localPort}`.");
            }

            var ngrokCode = response.Headers.TryGetValues("ngrok-error-code", out var codes)
                ? codes.FirstOrDefault()
                : NgrokErrorCode.Match(body) is { Success: true } match ? match.Value : null;

            switch (ngrokCode)
            {
                case "ERR_NGROK_3200":
                    return Problem(url,
                        $"ngrok has no endpoint at {uri.Host} ({status}, ERR_NGROK_3200: the endpoint is offline). " +
                        $"Start it with `ngrok http {_localPort}`; if ngrok is already running, set Ngrok:PublicUrl to " +
                        "the https URL on its Forwarding line.");

                case "ERR_NGROK_8012":
                    return Problem(url,
                        $"ngrok is running but can't connect to the local port it forwards to ({status}, ERR_NGROK_8012). " +
                        $"This synchronizer listens on port {_localPort} (Webhook:Port): run `ngrok http {_localPort}`.");

                case "ERR_NGROK_6024":
                    // The browser warning page: shown to browsers only, so not what Graph gets.
                    return Inconclusive(url, "ngrok answered with its browser warning page");

                case not null:
                    return Problem(url,
                        $"ngrok answered {status} with {ngrokCode} (https://ngrok.com/docs/errors/{ngrokCode.ToLowerInvariant()}).");
            }

            var server = response.Headers.Server.ToString();
            return Problem(url,
                $"{status} from something other than this synchronizer{(server.Length > 0 ? $" (Server: {server})" : "")}. " +
                $"{uri.Host} is forwarding to another web server: run `ngrok http {_localPort}`.");
        }
    }

    private static UrlCheckResult Problem(string url, string problem) => new(UrlCheckOutcome.Problem, url, problem);

    private static UrlCheckResult Inconclusive(string url, string problem) => new(UrlCheckOutcome.Inconclusive, url, problem);
}
