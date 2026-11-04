using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GraphTaskSynchronizer;

/// <summary>
/// Minimal HTTP server for the Graph webhook lifecycle.
///
///   {NotificationPath}  change notifications → <see cref="TaskSyncEngine.Enqueue"/>
///   {LifecyclePath}     lifecycle notifications (missed / subscriptionRemoved /
///                       reauthorizationRequired) → <see cref="LifecycleEvents"/>, consumed by
///                       <see cref="GraphSubscriptionService"/>
///
///   /                   the synced-tasks page (<see cref="TaskPage"/>) — local requests only;
///                       anything that comes in through the ngrok tunnel gets a 404
///
/// Both webhook paths answer Graph's validationToken handshake. Every notification's
/// clientState is checked before it is accepted. Processing happens off the request thread
/// so Graph gets its 202 quickly.
/// </summary>
public sealed class WebhookServer
{
    public const string DefaultNotificationPath = "/api/notifications";
    public const string DefaultLifecyclePath = "/api/lifecycle";

    /// <summary>
    /// Set on every response (including 404s), so <see cref="PublicUrlCheck"/> can tell this
    /// synchronizer's answers from ngrok's and from other web servers'.
    /// </summary>
    public const string InstanceHeader = "X-Task-Synchronizer";

    /// <summary>Identifies this run of the synchronizer in <see cref="InstanceHeader"/>.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N")[..12];

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<WebhookServer> _logger;
    private readonly TaskSyncEngine _engine;
    private readonly string _clientState;
    private readonly string _notificationPath;
    private readonly string _lifecyclePath;
    private readonly string? _mailboxUserId;
    private readonly Guid? _tenantId;
    private readonly int _port;

    // Signals the subscription service that Kestrel is bound and accepting connections.
    // Graph sends its validation request immediately after POST /subscriptions.
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Channel<ChangeNotification> _lifecycleEvents =
        Channel.CreateUnbounded<ChangeNotification>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public WebhookServer(IConfiguration config, ILoggerFactory loggerFactory, TaskSyncEngine engine)
    {
        _logger = loggerFactory.CreateLogger<WebhookServer>();
        _engine = engine;
        _clientState = config["Subscription:ClientState"]
            ?? throw new InvalidOperationException("Subscription:ClientState missing");
        _notificationPath = NotificationPathFrom(config);
        _lifecyclePath = LifecyclePathFrom(config);
        _mailboxUserId = config["Subscription:MailboxUserId"];
        // Only a GUID can be compared; a tenant given as a domain name skips the check.
        _tenantId = Guid.TryParse(config["AzureAd:TenantId"], out var tenant) ? tenant : null;
        _port = int.TryParse(config["Webhook:Port"], out var p) ? p : 5000;
    }

    public static string NotificationPathFrom(IConfiguration config) =>
        config["Subscription:NotificationPath"] ?? DefaultNotificationPath;

    public static string LifecyclePathFrom(IConfiguration config) =>
        config["Subscription:LifecycleNotificationPath"] ?? DefaultLifecyclePath;

    /// <summary>Lifecycle notifications, in arrival order.</summary>
    public ChannelReader<ChangeNotification> LifecycleEvents => _lifecycleEvents.Reader;

    /// <summary>Completes once Kestrel is accepting connections (faults if it failed to start).</summary>
    public Task WaitForReadyAsync() => _ready.Task;

    public async Task RunAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.Urls.Add($"http://0.0.0.0:{_port}");

        app.Use((HttpContext ctx, RequestDelegate next) =>
        {
            ctx.Response.Headers[InstanceHeader] = InstanceId;
            return next(ctx);
        });

        app.MapGet(_notificationPath, (HttpContext ctx) => HandleValidationAsync(ctx));
        app.MapPost(_notificationPath, (HttpContext ctx) => HandlePostAsync(ctx));

        if (!string.Equals(_lifecyclePath, _notificationPath, StringComparison.OrdinalIgnoreCase))
        {
            app.MapGet(_lifecyclePath, (HttpContext ctx) => HandleValidationAsync(ctx));
            app.MapPost(_lifecyclePath, (HttpContext ctx) => HandlePostAsync(ctx));
        }

        var servesTaskPage = _notificationPath != "/" && _lifecyclePath != "/";
        if (servesTaskPage)
            app.MapGet("/", (HttpContext ctx) => HandleTaskPageAsync(ctx));

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            _logger.LogInformation(
                "[Webhook] Kestrel ready on http://localhost:{Port} ({Notifications}, {Lifecycle}) — signalling subscription service.",
                _port, _notificationPath, _lifecyclePath);
            if (servesTaskPage)
                _logger.LogInformation("[Webhook] Synced tasks page: http://localhost:{Port}/ (this machine only)", _port);
            _ready.TrySetResult();
        });

        using var stopOnCancel = ct.Register(() => app.Lifetime.StopApplication());

        try
        {
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            // e.g. the port is already in use – don't leave the subscription service waiting.
            _ready.TrySetException(ex);
            throw;
        }
        finally
        {
            _lifecycleEvents.Writer.TryComplete();
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Synced-tasks page
    // ──────────────────────────────────────────────────────────

    private async Task HandleTaskPageAsync(HttpContext ctx)
    {
        // The port is published through ngrok; task subjects aren't for the internet.
        if (!TaskPage.IsLocalRequest(ctx))
        {
            _logger.LogWarning("[Webhook] Task page requested from outside this machine ({Remote}) – 404.",
                ctx.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } forwarded
                    ? forwarded
                    : ctx.Connection.RemoteIpAddress?.ToString());
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var html = TaskPage.Render(_engine.GetSyncedTasks(), _engine.Lists, _mailboxUserId,
            _engine.ReplicaPath, DateTimeOffset.Now);

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Response.Headers.ContentSecurityPolicy = TaskPage.ContentSecurityPolicy;
        await ctx.Response.WriteAsync(html, Encoding.UTF8);
    }

    // ──────────────────────────────────────────────────────────
    //  Validation handshake (GET and POST with ?validationToken)
    // ──────────────────────────────────────────────────────────

    private async Task HandleValidationAsync(HttpContext ctx)
    {
        if (await TryEchoValidationTokenAsync(ctx))
            return;

        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Expected validationToken query parameter.");
    }

    private async Task<bool> TryEchoValidationTokenAsync(HttpContext ctx)
    {
        if (!ctx.Request.Query.TryGetValue("validationToken", out var rawToken))
            return false;

        var decoded = HttpUtility.UrlDecode(rawToken.ToString());
        if (decoded.StartsWith(PublicUrlCheck.TokenPrefix, StringComparison.Ordinal))
            _logger.LogInformation("[Handshake] {Path}: self-check arrived through the public URL – echoing.", ctx.Request.Path);
        else
            _logger.LogInformation("[Handshake] {Method} {Path} validation – echoing token (len={Len}).",
                ctx.Request.Method, ctx.Request.Path, decoded.Length);

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync(decoded, Encoding.UTF8);
        return true;
    }

    // ──────────────────────────────────────────────────────────
    //  Notifications
    // ──────────────────────────────────────────────────────────

    // Returns plain Task and sets the status code itself: a (HttpContext) lambda returning
    // Task<IResult> binds as a RequestDelegate and the IResult would be silently discarded.
    private async Task HandlePostAsync(HttpContext ctx)
    {
        if (await TryEchoValidationTokenAsync(ctx))
            return;

        try
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);

            _logger.LogDebug("[Notification] Raw body: {Body}", body);

            var payload = JsonSerializer.Deserialize<ChangeNotificationPayload>(body, JsonOpts);
            if (payload?.Value is null || payload.Value.Length == 0)
            {
                _logger.LogWarning("[Notification] Empty or unparseable payload.");
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            foreach (var notification in payload.Value)
            {
                if (notification.ClientState != _clientState)
                {
                    _logger.LogWarning("[Security] clientState mismatch – discarding notification.");
                    continue;
                }

                if (!IsFromOurTenant(notification))
                {
                    _logger.LogWarning("[Security] Notification for tenant {Tenant}, not AzureAd:TenantId – discarding it.",
                        notification.TenantId ?? notification.OrganizationId);
                    continue;
                }

                // Lifecycle notifications can arrive on either URL (Graph allows the same
                // URL for both), so route by content rather than by path.
                if (!string.IsNullOrEmpty(notification.LifecycleEvent))
                {
                    _logger.LogInformation("[Webhook] LIFECYCLE {Event} subscription={Id}",
                        notification.LifecycleEvent, notification.SubscriptionId);
                    _lifecycleEvents.Writer.TryWrite(notification);
                    continue;
                }

                _engine.Enqueue(notification);
            }

            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Notification] Unhandled error processing payload.");
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    /// <summary>
    /// Change notifications carry tenantId and lifecycle notifications organizationId; when
    /// either is present it must be the app's tenant.
    /// </summary>
    private bool IsFromOurTenant(ChangeNotification notification)
    {
        if (_tenantId is null)
            return true;

        foreach (var value in new[] { notification.TenantId, notification.OrganizationId })
        {
            if (!string.IsNullOrEmpty(value) && (!Guid.TryParse(value, out var tenant) || tenant != _tenantId))
                return false;
        }

        return true;
    }
}
