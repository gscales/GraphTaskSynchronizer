using System.Net;
using System.Text;
using System.Web;
using Xunit;

namespace GraphTaskSynchronizer.Tests;

public sealed class PublicUrlCheckTests
{
    private const string Instance = "a1b2c3d4e5f6";
    private const string Url = "https://example.ngrok-free.dev/api/notifications";

    private static (PublicUrlCheck Check, FakeGraphHandler Handler) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new FakeGraphHandler(respond);
        return (new PublicUrlCheck(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) }, Instance, 5000), handler);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body = "", string? instance = null,
        string? ngrokHeader = null, string? server = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
        if (instance is not null)
            response.Headers.Add(WebhookServer.InstanceHeader, instance);
        if (ngrokHeader is not null)
            response.Headers.Add("ngrok-error-code", ngrokHeader);
        if (server is not null)
            response.Headers.TryAddWithoutValidation("Server", server);
        return response;
    }

    private static string TokenOf(RecordedRequest r) => HttpUtility.ParseQueryString(r.Url.Query)["validationToken"]!;

    [Fact]
    public async Task Passes_when_this_synchronizer_echoes_the_token()
    {
        var (check, handler) = Create(r => Answer(HttpStatusCode.OK, TokenOf(r), Instance));

        var result = await check.CheckAsync(new[] { Url, "https://example.ngrok-free.dev/api/lifecycle" }, CancellationToken.None);

        Assert.Equal(UrlCheckOutcome.Ok, result.Outcome);
        Assert.Equal(2, handler.Requests.Count);
        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.StartsWith(PublicUrlCheck.TokenPrefix, TokenOf(request));   // the same request Graph sends
    }

    [Fact]
    public async Task An_offline_ngrok_endpoint_is_named()
    {
        // What Graph's "validation request failed … NotFound" usually is.
        var (check, _) = Create(_ => Answer(HttpStatusCode.NotFound,
            "<html>… ERR_NGROK_3200 … The endpoint example.ngrok-free.dev is offline.</html>"));

        var result = await check.CheckAsync(new[] { Url }, CancellationToken.None);

        Assert.Equal(UrlCheckOutcome.Problem, result.Outcome);
        Assert.Contains("ERR_NGROK_3200", result.Problem);
        Assert.Contains("offline", result.Problem);
        Assert.Contains("ngrok http 5000", result.Problem);
    }

    [Fact]
    public async Task The_ngrok_error_header_is_understood_too()
    {
        var (check, _) = Create(_ => Answer(HttpStatusCode.NotFound, "Not Found", ngrokHeader: "ERR_NGROK_3200"));

        var result = await check.CheckAsync(new[] { Url }, CancellationToken.None);

        Assert.Contains("the endpoint is offline", result.Problem);
    }

    [Fact]
    public async Task Ngrok_forwarding_to_the_wrong_port_is_named()
    {
        var (check, _) = Create(_ => Answer(HttpStatusCode.BadGateway, "… ERR_NGROK_8012 … dial tcp [::1]:8080: connection refused"));

        var result = await check.CheckAsync(new[] { Url }, CancellationToken.None);

        Assert.Equal(UrlCheckOutcome.Problem, result.Outcome);
        Assert.Contains("ERR_NGROK_8012", result.Problem);
        Assert.Contains("port 5000", result.Problem);
    }

    [Fact]
    public async Task A_url_with_a_path_this_app_does_not_serve_is_named()
    {
        var (check, _) = Create(_ => Answer(HttpStatusCode.NotFound, "", Instance));

        var result = await check.CheckAsync(new[] { "https://example.ngrok-free.dev/hooks/api/notifications" }, CancellationToken.None);

        Assert.Equal(UrlCheckOutcome.Problem, result.Outcome);
        Assert.Contains("reached this synchronizer", result.Problem);
        Assert.Contains("/hooks/api/notifications answered 404", result.Problem);
        Assert.Contains("no path", result.Problem);
    }

    [Fact]
    public async Task Another_copy_of_the_synchronizer_is_named()
    {
        var (check, _) = Create(r => Answer(HttpStatusCode.OK, TokenOf(r), instance: "0ther1nstanc"));

        var result = await check.CheckAsync(new[] { Url }, CancellationToken.None);

        Assert.Equal(UrlCheckOutcome.Problem, result.Outcome);
        Assert.Contains("another running copy", result.Problem);
    }

    [Fact]
    public async Task Another_web_server_is_named()
    {
        var (check, _) = Create(_ => Answer(HttpStatusCode.NotFound, "<h1>404 - File or directory not found.</h1>",
            server: "Microsoft-IIS/10.0"));

        var result = await check.CheckAsync(new[] { Url }, CancellationToken.None);

        Assert.Equal(UrlCheckOutcome.Problem, result.Outcome);
        Assert.Contains("something other than this synchronizer", result.Problem);
        Assert.Contains("Microsoft-IIS/10.0", result.Problem);
    }

    [Fact]
    public async Task No_answer_is_inconclusive()
    {
        var (check, _) = Create(_ => throw new HttpRequestException("No such host is known."));

        var result = await check.CheckAsync(new[] { Url }, CancellationToken.None);

        Assert.Equal(UrlCheckOutcome.Inconclusive, result.Outcome);
        Assert.Contains("No such host", result.Problem);
    }

    [Fact]
    public async Task The_first_failing_url_decides()
    {
        var (check, handler) = Create(r => r.Url.AbsolutePath.EndsWith("/lifecycle")
            ? Answer(HttpStatusCode.NotFound, "", Instance)
            : Answer(HttpStatusCode.OK, TokenOf(r), Instance));

        var result = await check.CheckAsync(
            new[] { Url, "https://example.ngrok-free.dev/api/lifecycle", "https://example.ngrok-free.dev/never-asked" },
            CancellationToken.None);

        Assert.Equal("https://example.ngrok-free.dev/api/lifecycle", result.Url);
        Assert.Equal(2, handler.Requests.Count);
    }
}
