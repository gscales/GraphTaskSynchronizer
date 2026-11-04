using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraphTaskSynchronizer.Tests;

/// <summary>
/// End to end over HTTP: Kestrel → clientState check → queue → GET → replica → sink.
/// </summary>
public sealed class WebhookServerTests : IDisposable
{
    private const string Tenant = "1c3a18bf-da31-4f6c-a404-2c06c9cf5ae4";

    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Handshake_notifications_and_lifecycle_events_are_routed()
    {
        var port = FreePort();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Subscription:ClientState"] = "secret",
                ["AzureAd:TenantId"] = Tenant,
                ["Webhook:Port"] = port.ToString()
            })
            .Build();

        var server = new WebhookServer(config, NullLoggerFactory.Instance, _h.Engine);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var serverTask = server.RunAsync(cts.Token);
        var engineTask = _h.Engine.RunAsync(cts.Token);
        await server.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(20));

        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}")
        };

        // 1. The validation handshake echoes the decoded token as text/plain.
        var validation = await http.PostAsync("/api/notifications?validationToken=abc%20123", content: null);
        Assert.Equal(HttpStatusCode.OK, validation.StatusCode);
        Assert.Equal("abc 123", await validation.Content.ReadAsStringAsync());

        // Every response says which synchronizer sent it – a 404 included.
        Assert.Equal(server.InstanceId, validation.Headers.GetValues(WebhookServer.InstanceHeader).Single());
        var unknown = await http.PostAsync("/no-such-path?validationToken=x", content: null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(server.InstanceId, unknown.Headers.GetValues(WebhookServer.InstanceHeader).Single());

        // The public URL check, run against this server directly: the real paths pass, a
        // public URL with a path in it is caught.
        var check = new PublicUrlCheck(new HttpClient(new HttpClientHandler { UseProxy = false }), server.InstanceId, port);
        var good = await check.CheckAsync(new[] { $"http://127.0.0.1:{port}/api/notifications", $"http://127.0.0.1:{port}/api/lifecycle" },
            cts.Token);
        Assert.Equal(UrlCheckOutcome.Ok, good.Outcome);
        var withPath = await check.CheckAsync(new[] { $"http://127.0.0.1:{port}/api/notifications/api/notifications" }, cts.Token);
        Assert.Equal(UrlCheckOutcome.Problem, withPath.Outcome);
        Assert.Contains("no path", withPath.Problem);

        // 2. A notification is acknowledged with 202; the task is fetched and lands in the replica.
        _h.Put("task-1", percent: 0.4);
        var accepted = await http.PostAsync("/api/notifications", Json(_h.Notification("created", "task-1")));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        await WaitUntilAsync(() => _h.Sink.ChangeCount == 1);
        Assert.Equal(0.4, _h.Store.Get("task-1")!.Snapshot.PercentComplete);

        // 3. A notification with the wrong clientState is acknowledged but discarded.
        _h.Put("task-2", percent: 0.9);
        var forged = _h.Notification("created", "task-2");
        forged.ClientState = "not-the-secret";
        var forgedResponse = await http.PostAsync("/api/notifications", Json(forged));
        Assert.Equal(HttpStatusCode.Accepted, forgedResponse.StatusCode);

        // 4. Lifecycle events go to the lifecycle channel, not the sync engine – but only from
        //    our own tenant (lifecycle notifications report it as organizationId).
        var otherTenant = new ChangeNotification
        {
            SubscriptionId = "from-another-tenant",
            ClientState = "secret",
            OrganizationId = "99999999-9999-4999-8999-999999999999",
            LifecycleEvent = "reauthorizationRequired"
        };
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsync("/api/lifecycle", Json(otherTenant))).StatusCode);

        var lifecycle = new ChangeNotification
        {
            SubscriptionId = EngineHarness.SubscriptionId,
            ClientState = "secret",
            OrganizationId = Tenant,
            LifecycleEvent = "reauthorizationRequired"
        };
        var lifecycleResponse = await http.PostAsync("/api/lifecycle", Json(lifecycle));
        Assert.Equal(HttpStatusCode.Accepted, lifecycleResponse.StatusCode);

        var evt = await server.LifecycleEvents.ReadAsync(cts.Token);
        Assert.Equal("reauthorizationRequired", evt.LifecycleEvent);
        Assert.Equal(EngineHarness.SubscriptionId, evt.SubscriptionId);
        Assert.False(server.LifecycleEvents.TryRead(out _));

        await Task.Delay(250);
        Assert.Null(_h.Store.Get("task-2"));
        Assert.Equal(1, _h.Sink.ChangeCount);

        // 5. The default page lists the synced tasks for a local browser...
        var page = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Draft Q4 report", html);
        Assert.Contains("style=\"width:40%\"", html);

        // ...but not for a request that came through the ngrok tunnel.
        using var tunnelled = new HttpRequestMessage(HttpMethod.Get, "/");
        tunnelled.Headers.Add("X-Forwarded-For", "203.0.113.7");
        tunnelled.Headers.Host = "example.ngrok-free.dev";
        var hidden = await http.SendAsync(tunnelled);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.DoesNotContain("Draft Q4 report", await hidden.Content.ReadAsStringAsync());

        cts.Cancel();
        await Task.WhenAll(serverTask, engineTask);
    }

    private static StringContent Json(ChangeNotification notification) =>
        new(JsonSerializer.Serialize(new ChangeNotificationPayload { Value = new[] { notification } }),
            Encoding.UTF8, "application/json");

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within 15s.");
            await Task.Delay(25);
        }
    }
}
