using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GraphTaskSynchronizer;

/// <summary>The two calls that answer a reauthorizationRequired challenge.</summary>
internal enum AuthorizationCall
{
    /// <summary>POST /subscriptions/{id}/reauthorize — reauthorizes, leaves the expiry alone.</summary>
    Reauthorize,

    /// <summary>PATCH /subscriptions/{id} with a new expirationDateTime — reauthorizes and renews.</summary>
    Renew
}

/// <summary>
/// Manages the Graph change-notification subscriptions — one per synchronized To Do list:
///
///   POST /subscriptions
///   {
///     "resource": "users/{id}/todo/lists/{listId}/tasks",
///     "changeType": "created,updated,deleted",
///     "notificationUrl": "…/api/notifications",
///     "lifecycleNotificationUrl": "…/api/lifecycle",
///     …
///   }
///
/// todoTask subscriptions do not support includeResourceData, so these are basic
/// notifications: the engine GETs each changed task (with PercentComplete expanded).
/// Maximum lifetime is 4,230 minutes.
///
/// Keeping a subscription authorized
/// ─────────────────────────────────
/// Graph sends a reauthorizationRequired lifecycle notification (a "challenge") before the
/// access token behind a subscription, or the subscription itself, expires, and when consent
/// is revoked. If nobody answers, notification delivery eventually pauses and changes made in
/// the meantime are never notified. Each challenge is answered with
///
///   POST  /subscriptions/{id}/reauthorize   while more than half the subscription's lifetime is left
///   PATCH /subscriptions/{id}               once it is past halfway: a new expirationDateTime, which
///                                           reauthorizes and renews in one call
///
/// and the two are never sent for one subscription within 10 minutes of each other (Graph's
/// rule). The Graph permission tables list application permissions as "not supported" for
/// both calls on todoTask (only for creating), so if Graph refuses one, that is remembered
/// and the subscription is replaced instead: a new one is created first, then the old one is
/// deleted, so delivery never stops. A challenge that still can't be answered after 30 minutes
/// also leads to a replacement, followed by a reconcile for whatever went unnotified.
///
/// Lists
/// ─────
/// Graph has no change notifications for the To Do lists themselves, so a list reconcile runs
/// every Sync:ListReconcileIntervalSeconds (60 by default), from startup on: a todoTaskList
/// delta round says whether any list was added, changed or removed since the last one, and
/// only then are the lists read in full (which applies Tasks:Lists). A new list is subscribed
/// and handed to the engine (<see cref="TaskSyncEngine.SetLists"/>), whose reconcile then reads
/// all of its tasks; a deleted list's subscription is deleted. R / F in the console, the engine's
/// periodic reconcile and a task that left a list but still exists read the lists first too
/// (<see cref="RequestListReconcile"/>), and every 5-minute check reads them in full as a
/// safety net. A list that can't be subscribed (shared lists, for example) is retried with a
/// growing delay, up to an hour; the reconcile keeps it up to date meanwhile.
///
/// Subscriptions are renewed (or replaced) when 20% of their lifetime is left. At startup a
/// live subscription from an earlier run is reused and reauthorized at once (its challenges
/// may have gone unanswered while nothing was listening); any other subscription of ours —
/// duplicates, an old clientState or lifecycle URL, a list no longer synchronized — is
/// deleted. A challenge for a subscription this instance doesn't track gets the same check:
/// ours for a list without a subscription → adopted; a duplicate → deleted.
/// </summary>
public sealed class GraphSubscriptionService
{
    private static readonly TimeSpan MaintenanceTick = TimeSpan.FromMinutes(5);

    /// <summary>Graph: don't send POST /reauthorize and PATCH for one subscription within 10 minutes of each other.</summary>
    internal static readonly TimeSpan ReauthorizeRenewGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A challenge arriving this soon after the subscription was created, renewed or reauthorized
    /// was raised before that — typically Graph redelivering one that failed while the
    /// synchronizer wasn't running — so there is nothing left to answer.
    /// </summary>
    internal static readonly TimeSpan ChallengeDebounce = TimeSpan.FromMinutes(2);

    /// <summary>A challenge still unanswered after this long: replace the subscription.</summary>
    internal static readonly TimeSpan ReplaceAfter = TimeSpan.FromMinutes(30);

    /// <summary>Longest wait between attempts to subscribe a list that keeps failing.</summary>
    internal static readonly TimeSpan MaxCreateBackoff = TimeSpan.FromHours(1);

    /// <summary>How often the public URL is checked again while it doesn't reach this synchronizer.</summary>
    internal static readonly TimeSpan UrlRecheck = TimeSpan.FromSeconds(30);

    /// <summary>The list reconcile's interval when Sync:ListReconcileIntervalSeconds isn't set.</summary>
    internal static readonly TimeSpan DefaultListReconcileInterval = TimeSpan.FromSeconds(60);

    /// <summary>After todoTaskList delta fails (other than asking for a resync), how long the lists are just read in full.</summary>
    internal static readonly TimeSpan ListDeltaPause = TimeSpan.FromMinutes(30);

    private readonly IConfiguration _config;
    private readonly ILogger<GraphSubscriptionService> _logger;
    private readonly GraphRestClient _graph;
    private readonly WebhookServer _webhookServer;
    private readonly TaskSyncEngine _engine;
    private readonly TimeProvider _time;
    private readonly Func<CancellationToken, Task<IReadOnlyList<TaskList>?>>? _refreshLists;
    private readonly Func<string?, CancellationToken, Task<(int Changes, string DeltaLink)>>? _listChanges;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<UrlCheckResult>>? _checkPublicUrl;

    // The lists to keep subscribed; replaced when lists are created, deleted or renamed.
    private IReadOnlyList<TaskList> _lists;

    // Set once the startup subscriptions are being made. Before that a new list is only handed
    // to the engine: EnsureSubscriptionsAsync subscribes every list, new ones included.
    private bool _subscribing;

    // The list reconcile: requests (R / F, the engine, the periodic tick) and the todoTaskList
    // delta link of the last round. Only the list reconcile loop touches the link.
    private sealed record ListRequest(string Reason, bool ReadAll, bool ThenReconcile, bool FullReconcile);

    private readonly Channel<ListRequest> _listRequests =
        Channel.CreateUnbounded<ListRequest>(new UnboundedChannelOptions { SingleReader = true });

    private string? _listDeltaLink;
    private DateTimeOffset _listDeltaPausedUntil = DateTimeOffset.MinValue;

    // Set when Graph couldn't validate the webhook: no list gets a subscription until the
    // public URL check passes again, and then they all do at once.
    private bool _urlSuspect;
    private string? _lastUrlProblem;

    // Lists whose subscription couldn't be created: failures so far, and when to try again.
    private readonly Dictionary<string, (int Failures, DateTimeOffset NextAttempt)> _createBackoff =
        new(StringComparer.Ordinal);

    // Serialises create / renew / reauthorize / delete between maintenance and lifecycle handling.
    private readonly SemaphoreSlim _opsLock = new(1, 1);

    // Subscriptions this instance owns, by subscription ID.
    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.OrdinalIgnoreCase);

    // Calls Graph has refused for this app's todoTask subscriptions (see the class summary).
    private readonly HashSet<AuthorizationCall> _refused = new();

    public GraphSubscriptionService(
        IConfiguration config,
        ILoggerFactory loggerFactory,
        GraphRestClient graph,
        IReadOnlyList<TaskList> lists,
        WebhookServer webhookServer,
        TaskSyncEngine engine,
        TimeProvider? time = null,
        Func<CancellationToken, Task<IReadOnlyList<TaskList>?>>? refreshLists = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<UrlCheckResult>>? checkPublicUrl = null,
        Func<string?, CancellationToken, Task<(int Changes, string DeltaLink)>>? listChanges = null)
    {
        _config = config;
        _logger = loggerFactory.CreateLogger<GraphSubscriptionService>();
        _graph = graph;
        _lists = lists;
        _webhookServer = webhookServer;
        _engine = engine;
        _time = time ?? TimeProvider.System;
        _refreshLists = refreshLists;
        _checkPublicUrl = checkPublicUrl;
        _listChanges = listChanges;
    }

    /// <summary>What this instance knows about one of its subscriptions.</summary>
    private sealed class Tracked(TaskList list)
    {
        public TaskList List { get; set; } = list;
        public DateTimeOffset? ExpiresAt { get; set; }

        /// <summary>When it was last created, renewed or reauthorized successfully.</summary>
        public DateTimeOffset? LastAuthorized { get; set; }

        /// <summary>The last reauthorize / renew request sent (successful or not) — for the 10-minute rule.</summary>
        public (AuthorizationCall Call, DateTimeOffset At)? LastCall { get; set; }

        /// <summary>Set while a challenge is unanswered because a call failed; cleared once one succeeds.</summary>
        public DateTimeOffset? PendingSince { get; set; }
    }

    private enum CallOutcome { Done, Gone, Refused, Failed }

    internal IReadOnlyDictionary<string, TaskList> ActiveSubscriptions =>
        _tracked.ToDictionary(kv => kv.Key, kv => kv.Value.List, StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyCollection<AuthorizationCall> RefusedCalls => _refused;

    // ──────────────────────────────────────────────────────────
    //  Entry point
    // ──────────────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        // Stopped before the subscriptions are deleted (it may be creating one for a new list),
        // and whatever ends this method – not only Ctrl+C.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Lists created while running are picked up from the start, also while the webhook
        // can't be validated yet: they are synchronized then, and subscribed once it can.
        var listLoop = RunListReconcileAsync(stop.Token);
        var lifecycleLoop = Task.CompletedTask;

        try
        {
            // Graph validates notificationUrl and lifecycleNotificationUrl straight after
            // POST /subscriptions, so nothing is created until Kestrel is accepting requests.
            _logger.LogInformation("[Subscription] Waiting for webhook server to be ready...");
            await _webhookServer.WaitForReadyAsync().WaitAsync(ct);

            // Graph validates both URLs as soon as a subscription is created, and when that fails
            // all it says is "NotFound" or the like. Check them first, and wait until they work.
            if (!await PublicUrlReachableAsync(ct))
            {
                _engine.RequestReconcile("startup (not subscribed yet)");
                do
                {
                    await Task.Delay(UrlRecheck, _time, ct);
                }
                while (!await PublicUrlReachableAsync(ct));
            }

            _logger.LogInformation("[Subscription] Webhook ready — creating subscriptions.");

            await WithOpsLockAsync(() => EnsureSubscriptionsAsync(ct), ct);

            // Subscribe first, then take the full snapshot: anything that changes while the
            // reconcile is listing arrives as a notification queued behind it.
            _engine.RequestReconcile("startup");

            lifecycleLoop = ConsumeLifecycleEventsAsync(stop.Token);

            using var timer = new PeriodicTimer(MaintenanceTick);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await WithOpsLockAsync(() => MaintainAsync(ct), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Never let one bad check end the service: the app would stop listening
                    // and Graph's notifications and challenges would go unanswered.
                    _logger.LogError(ex, "[Subscription] Maintenance check failed – trying again in {Minutes} minutes.",
                        MaintenanceTick.TotalMinutes);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            stop.Cancel();
            await lifecycleLoop;
            await listLoop;
            await DeleteAllAsync();
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Startup: reuse where possible, delete leftovers, else create
    // ──────────────────────────────────────────────────────────

    internal async Task EnsureSubscriptionsAsync(CancellationToken ct)
    {
        _subscribing = true;
        _logger.LogInformation("[Subscription] Target lists ({Count}):", _lists.Count);
        foreach (var list in _lists)
            _logger.LogInformation("[Subscription]   • {List}: {Resource}", list.DisplayName, BuildResource(list));

        var existing = await ListAllSubscriptionsAsync(ct);
        var ours = existing.Where(IsOurs).ToList();
        _logger.LogInformation(
            "[Subscription] Found {Count} existing subscription(s) for this app, {Ours} of them pointing at this synchronizer.",
            existing.Count, ours.Count);

        var reused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in _lists)
        {
            if (await EnsureOneAsync(list, ours, ct) is { } id)
                reused.Add(id);
        }

        // Anything else of ours is left over from an earlier run that didn't shut down cleanly:
        // a duplicate, an old clientState or lifecycle URL, or a list no longer synchronized.
        // Left alone it keeps sending notifications and challenges until it expires.
        foreach (var sub in ours.Where(s => !reused.Contains(s.Id!) && !_tracked.ContainsKey(s.Id!)))
        {
            _logger.LogInformation("[Subscription] Deleting leftover subscription {Id} ({Resource}).", sub.Id, sub.Resource);
            await DeleteQuietlyAsync(sub.Id!, ct);
        }

        _logger.LogInformation("[Subscription] {Count} active subscription(s) owned by this synchronizer.", _tracked.Count);
    }

    /// <summary>Reuses a live subscription of ours for the list (returning its ID), or creates one (returning null).</summary>
    private async Task<string?> EnsureOneAsync(TaskList list, List<SubscriptionDto> ours, CancellationToken ct)
    {
        var resource = BuildResource(list);
        var lifecycleUrl = BuildUrl(LifecyclePath);

        foreach (var sub in ours)
        {
            if (sub.LifecycleNotificationUrl != lifecycleUrl) continue;
            if (!SameResource(sub.Resource, resource)) continue;
            if (sub.ExpirationDateTime is null || sub.ExpirationDateTime <= Now) continue;
            if (_tracked.ContainsKey(sub.Id!)) continue;

            // The list response doesn't reliably include clientState; read the subscription itself.
            var full = await GetSubscriptionAsync(sub.Id!, ct);
            if (full is null || full.ClientState != ClientState) continue;

            _logger.LogInformation(
                "[Subscription] Reusing {Id} for '{List}' – reauthorizing it now, in case challenges went unanswered while nothing was listening.",
                sub.Id, list.DisplayName);
            var tracked = Track(sub.Id!, list, full.ExpirationDateTime ?? sub.ExpirationDateTime, authorizedNow: false);
            PrintSubscriptionInfo(full, list, reused: true);
            await AuthorizeOrReplaceAsync(sub.Id!, tracked, "reused at startup", ct);
            return sub.Id;
        }

        await CreateOneAsync(list, ct);
        return null;
    }

    // ──────────────────────────────────────────────────────────
    //  Create / replace / delete
    // ──────────────────────────────────────────────────────────

    private async Task<string?> CreateOneAsync(TaskList list, CancellationToken ct, string? replaces = null)
    {
        var request = new SubscriptionDto
        {
            ChangeType = "created,updated,deleted",
            NotificationUrl = BuildUrl(NotificationPath),
            LifecycleNotificationUrl = BuildUrl(LifecyclePath),
            Resource = BuildResource(list),
            ExpirationDateTime = Now + Lifetime,
            ClientState = ClientState,
            LatestSupportedTlsVersion = "v1_2"
        };

        _logger.LogInformation("[Subscription] Creating for '{List}' -> {Resource}", list.DisplayName, request.Resource);

        try
        {
            var created = await _graph.PostAsync<SubscriptionDto>("subscriptions", request, ct);
            if (created?.Id is null)
            {
                _logger.LogWarning("[Subscription] Graph returned no subscription ID for '{List}'.", list.DisplayName);
                NoteCreateFailure(list);
                return null;
            }

            _createBackoff.Remove(list.Id);
            Track(created.Id, list, created.ExpirationDateTime ?? request.ExpirationDateTime, authorizedNow: true);
            PrintSubscriptionInfo(created, list, reused: false, replaces);
            return created.Id;
        }
        catch (GraphRequestException ex) when (ex.ErrorCode == "ValidationError")
        {
            // Graph couldn't validate the webhook. That's the URL, not this list: hold back every
            // list until the public URL check passes (see CreateMissingAsync), without a backoff.
            _logger.LogError(
                "[Subscription] Graph couldn't validate the webhook for '{List}': {Message}", list.DisplayName, ex.Message);
            if (!_urlSuspect)
            {
                _urlSuspect = true;
                _lastUrlProblem = null;   // so the next check logs what it finds
            }
            return null;
        }
        catch (GraphRequestException ex)
        {
            _logger.LogError(
                "[Subscription] Graph rejected the subscription for '{List}': {Message}. Check the ngrok URL is " +
                "reachable, the Tasks.ReadWrite.All application permission, and MailboxUserId.",
                list.DisplayName, ex.Message);
            NoteCreateFailure(list);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[Subscription] Failed to create the subscription for '{List}'.", list.DisplayName);
            NoteCreateFailure(list);
            return null;
        }
    }

    /// <summary>Waits 5, 10, 20, 40, then 60 minutes between attempts.</summary>
    private void NoteCreateFailure(TaskList list)
    {
        var failures = _createBackoff.TryGetValue(list.Id, out var previous) ? previous.Failures + 1 : 1;
        var wait = TimeSpan.FromMinutes(Math.Min(
            MaxCreateBackoff.TotalMinutes, MaintenanceTick.TotalMinutes * Math.Pow(2, failures - 1)));
        _createBackoff[list.Id] = (failures, Now + wait);
    }

    /// <summary>Retries any list whose subscription could not be created earlier, when its wait is over.</summary>
    private async Task CreateMissingAsync(CancellationToken ct)
    {
        var missing = _lists.Where(l => _tracked.Values.All(t => t.List.Id != l.Id)).ToList();
        if (missing.Count == 0)
            return;

        if (_urlSuspect)
        {
            if (!await PublicUrlReachableAsync(ct))
                return;

            // Working again: every list gets its subscription now, not after its backoff.
            _urlSuspect = false;
            _createBackoff.Clear();
        }

        foreach (var list in missing)
        {
            if (_createBackoff.TryGetValue(list.Id, out var backoff) && backoff.NextAttempt > Now)
                continue;

            _logger.LogInformation(
                "[Subscription] '{List}' has no active subscription – retrying (until it has one, only the periodic " +
                "reconcile keeps it up to date).", list.DisplayName);
            if (await CreateOneAsync(list, ct) is not null)
                _engine.RequestReconcile($"subscription for '{list.DisplayName}' created late");
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Public URL check
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Checks that the notification and lifecycle URLs reach this synchronizer (see
    /// <see cref="PublicUrlCheck"/>). True when they do, or when that can't be told from here;
    /// false when something else answers. A problem is logged when it appears or changes.
    /// </summary>
    internal async Task<bool> PublicUrlReachableAsync(CancellationToken ct)
    {
        if (_checkPublicUrl is null)
            return true;

        var urls = new[] { BuildUrl(NotificationPath), BuildUrl(LifecyclePath) }.Distinct().ToList();
        UrlCheckResult result;
        try
        {
            result = await _checkPublicUrl(urls, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new UrlCheckResult(UrlCheckOutcome.Inconclusive, urls[0], ex.Message);
        }

        switch (result.Outcome)
        {
            case UrlCheckOutcome.Ok:
                _logger.LogInformation(
                    _lastUrlProblem is null
                        ? "[Webhook] Public URL check passed: {Urls} reach this synchronizer."
                        : "[Webhook] Public URL check passes now: {Urls} reach this synchronizer – subscribing.",
                    string.Join(" and ", urls));
                _lastUrlProblem = null;
                return true;

            case UrlCheckOutcome.Inconclusive:
                _logger.LogWarning(
                    "[Webhook] Couldn't check {Url} from here: {Problem}. Subscribing anyway – Graph says if it can't reach it.",
                    result.Url, result.Problem);
                _lastUrlProblem = null;
                return true;

            default:
                if (result.Problem != _lastUrlProblem)
                {
                    _logger.LogError("[Webhook] Graph won't be able to validate {Url}: {Problem}", result.Url, result.Problem);
                    _logger.LogWarning(
                        "[Subscription] Not subscribing until it does – checking again every {Seconds} seconds at startup, " +
                        "every {Minutes} minutes later on. (Webhook:CheckPublicUrl=false turns this check off.)",
                        UrlRecheck.TotalSeconds, MaintenanceTick.TotalMinutes);
                }
                _lastUrlProblem = result.Problem;
                return false;
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Lists created, deleted or renamed
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the To Do lists again and brings the subscriptions and the engine in line: new
    /// lists are subscribed (before the engine reconciles them, so nothing slips between the
    /// listing and the subscription), deleted lists lose their subscription.
    /// </summary>
    internal async Task RefreshListsAsync(CancellationToken ct)
    {
        if (_refreshLists is null)
            return;

        IReadOnlyList<TaskList>? latest;
        try
        {
            latest = await _refreshLists(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("[Lists] Could not refresh the To Do lists: {Message}", ex.Message);
            return;
        }

        // Couldn't read them this time: keep what we have. An empty answer is treated the same
        // way: every mailbox has at least its default list, and acting on it would unsubscribe
        // everything and empty the replica.
        if (latest is null || latest.Count == 0)
            return;

        var current = _lists;
        var added = latest.Where(l => current.All(c => c.Id != l.Id)).ToList();
        var removed = current.Where(c => latest.All(l => l.Id != c.Id)).ToList();
        var renamed = latest.Where(l => current.Any(c => c.Id == l.Id && c.DisplayName != l.DisplayName)).ToList();
        if (added.Count == 0 && removed.Count == 0 && renamed.Count == 0)
            return;

        // Before the startup subscriptions, or while Graph can't validate the webhook, a new list
        // is subscribed later (EnsureSubscriptionsAsync / CreateMissingAsync); it is
        // synchronized by the reconcile either way.
        var subscribeNow = _subscribing && !_urlSuspect;
        foreach (var list in added)
        {
            _logger.LogInformation(subscribeNow
                    ? "[Lists] New list '{List}' – subscribing to it and synchronizing its tasks."
                    : "[Lists] New list '{List}' – synchronizing its tasks; it is subscribed once the webhook can be validated.",
                list.DisplayName);
        }
        foreach (var list in removed)
            _logger.LogInformation("[Lists] '{List}' is gone – deleting its subscription.", list.DisplayName);
        foreach (var list in renamed)
            _logger.LogInformation("[Lists] '{Old}' was renamed '{New}'.",
                current.First(c => c.Id == list.Id).DisplayName, list.DisplayName);

        _lists = latest;

        foreach (var (id, tracked) in _tracked.ToList())
        {
            if (removed.Any(l => l.Id == tracked.List.Id))
            {
                Untrack(id);
                await DeleteQuietlyAsync(id, ct);
            }
            else if (latest.FirstOrDefault(l => l.Id == tracked.List.Id) is { } updated && updated != tracked.List)
            {
                tracked.List = updated;
                _engine.RegisterSubscription(id, updated);
            }
        }

        foreach (var list in removed)
            _createBackoff.Remove(list.Id);

        if (subscribeNow)
        {
            foreach (var list in added)
                await CreateOneAsync(list, ct);
        }

        _engine.SetLists(latest, "To Do lists changed: " + string.Join(", ",
            added.Select(l => $"'{l.DisplayName}' added")
                .Concat(removed.Select(l => $"'{l.DisplayName}' deleted"))
                .Concat(renamed.Select(l => $"renamed '{l.DisplayName}'"))));
        _logger.LogInformation("[Lists] Synchronizing {Count} list(s): {Lists}.",
            latest.Count, string.Join(", ", latest.Select(l => $"'{l.DisplayName}'")));
    }

    // ──────────────────────────────────────────────────────────
    //  List reconcile
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the To Do lists now (subscribing a new one and passing them to the engine), then
    /// requests a task reconcile — full if <paramref name="full"/>. This is
    /// <see cref="TaskSyncEngine.ListCheck"/>, and what R / F in the console run. Requests made
    /// while one is running are combined into the next.
    /// </summary>
    public void RequestListReconcile(string reason, bool full = false)
    {
        if (!_listRequests.Writer.TryWrite(new ListRequest(reason, ReadAll: true, ThenReconcile: true, FullReconcile: full)))
            _engine.RequestReconcile(reason, full);
    }

    /// <summary>Sync:ListReconcileIntervalSeconds: 0 turns the periodic check off (at least 10 otherwise).</summary>
    internal TimeSpan ListReconcileInterval =>
        int.TryParse(_config["Sync:ListReconcileIntervalSeconds"], out var seconds)
            ? seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(10, seconds))
            : DefaultListReconcileInterval;

    /// <summary>Handles list requests one at a time, plus a periodic delta check of the lists.</summary>
    internal async Task RunListReconcileAsync(CancellationToken ct)
    {
        var interval = ListReconcileInterval;
        var ticks = interval > TimeSpan.Zero ? TickListReconcileAsync(interval, ct) : Task.CompletedTask;

        try
        {
            while (await _listRequests.Reader.WaitToReadAsync(ct))
            {
                var batch = new List<ListRequest>();
                while (_listRequests.Reader.TryRead(out var request))
                    batch.Add(request);

                try
                {
                    await ReconcileListsAsync(readAll: batch.Any(r => r.ReadAll), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "[Lists] The list reconcile failed – trying again at the next check.");
                }

                // The reconcile is requested after the lists were passed on (SetLists is queued
                // first), so it covers a new list – whether or not the lists could be read.
                var reconcile = batch.Where(r => r.ThenReconcile).ToList();
                if (reconcile.Count > 0 && !ct.IsCancellationRequested)
                {
                    _engine.RequestReconcile(string.Join("; ", reconcile.Select(r => r.Reason).Distinct()),
                        full: reconcile.Any(r => r.FullReconcile));
                }
            }
        }
        catch (OperationCanceledException) { }

        await ticks;
    }

    private async Task TickListReconcileAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, _time, ct);
                _listRequests.Writer.TryWrite(new ListRequest("periodic", ReadAll: false, ThenReconcile: false, FullReconcile: false));
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// One list reconcile: a todoTaskList delta round, then — if it reported a change, or with
    /// <paramref name="readAll"/> — the lists read in full and applied. True when they were read.
    /// The delta round comes first, so a list created after it is in the next round.
    /// </summary>
    internal async Task<bool> ReconcileListsAsync(bool readAll, CancellationToken ct)
    {
        var changed = await ListsChangedAsync(ct);
        if (!changed && !readAll)
            return false;

        await WithOpsLockAsync(() => RefreshListsAsync(ct), ct);
        return true;
    }

    /// <summary>
    /// Whether todoTaskList delta reports a list added, changed or removed since the last round.
    /// Without a delta link (first round, or after Graph asked for a resync), or while delta
    /// isn't working, the answer is "read them".
    /// </summary>
    private async Task<bool> ListsChangedAsync(CancellationToken ct)
    {
        if (_listChanges is null || Now < _listDeltaPausedUntil)
            return true;

        var initial = _listDeltaLink is null;
        try
        {
            var (changes, link) = await _listChanges(_listDeltaLink, ct);
            _listDeltaLink = link;
            if (!initial && changes > 0)
                _logger.LogInformation("[Lists] todoTaskList delta: {Count} list(s) added, changed or removed – reading the lists.", changes);
            return initial || changes > 0;
        }
        catch (DeltaResyncRequiredException ex)
        {
            _logger.LogInformation("[Lists] Graph no longer accepts the todoTaskList delta link ({Message}) – reading the lists, then a new delta round.", ex.Message);
            _listDeltaLink = null;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _listDeltaLink = null;
            _listDeltaPausedUntil = Now + ListDeltaPause;
            _logger.LogWarning(
                "[Lists] todoTaskList delta failed: {Message}. The lists are read in full at each check instead; delta is tried " +
                "again in {Minutes} minutes.", ex.Message, ListDeltaPause.TotalMinutes);
            return true;
        }
    }

    /// <summary>
    /// A new subscription for the same list, then the old one deleted — in that order, so
    /// notifications never stop (the overlap only means the odd duplicate, which is harmless).
    /// If the new one can't be created, the old one is kept and retried at the next check.
    /// </summary>
    private async Task ReplaceAsync(string oldId, Tracked old, string why, bool reconcile, CancellationToken ct)
    {
        _logger.LogWarning("[Subscription] Replacing {Id} ('{List}'): {Why}.", oldId, old.List.DisplayName, why);

        if (await CreateOneAsync(old.List, ct, replaces: oldId) is null)
        {
            old.PendingSince ??= Now;
            _logger.LogWarning("[Subscription] Keeping {Id} for now; trying again in {Minutes} minutes.",
                oldId, MaintenanceTick.TotalMinutes);
            return;
        }

        Untrack(oldId);
        await DeleteQuietlyAsync(oldId, ct);

        if (reconcile)
            _engine.RequestReconcile($"subscription for '{old.List.DisplayName}' replaced after reauthorization failed");
    }

    private async Task DeleteAllAsync()
    {
        foreach (var id in _tracked.Keys.ToList())
        {
            await DeleteQuietlyAsync(id, CancellationToken.None);
            Untrack(id);
        }
    }

    private async Task DeleteQuietlyAsync(string id, CancellationToken ct)
    {
        try
        {
            await _graph.DeleteAsync($"subscriptions/{Uri.EscapeDataString(id)}", ct);
            _logger.LogInformation("[Subscription] Deleted {Id}.", id);
        }
        catch (GraphRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("[Subscription] {Id} was already gone.", id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("[Subscription] Could not delete {Id}: {Message}", id, ex.Message);
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Reauthorize / renew
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// The call that answers a challenge now, or null when Graph won't accept either and
    /// only a replacement will do.
    /// </summary>
    private AuthorizationCall? ChooseCall(Tracked t, DateTimeOffset now)
    {
        // Graph's 10-minute rule: within the window, only the kind already sent may be sent again.
        if (t.LastCall is { } last && now - last.At < ReauthorizeRenewGap)
            return _refused.Contains(last.Call) ? null : last.Call;

        // Fresh enough: just reauthorize. Past halfway: renew, which reauthorizes as well.
        var remaining = (t.ExpiresAt ?? now) - now;
        var preferred = remaining > Lifetime / 2 ? AuthorizationCall.Reauthorize : AuthorizationCall.Renew;
        if (!_refused.Contains(preferred))
            return preferred;

        var other = preferred == AuthorizationCall.Reauthorize ? AuthorizationCall.Renew : AuthorizationCall.Reauthorize;
        return _refused.Contains(other) ? null : other;
    }

    /// <summary>
    /// Brings a subscription back to "authorized": reauthorize or renew it, recreate it if it
    /// has gone, replace it if Graph refuses, or leave it pending for the next check.
    /// </summary>
    private async Task AuthorizeOrReplaceAsync(string id, Tracked t, string why, CancellationToken ct)
    {
        var call = ChooseCall(t, Now);
        if (call is null)
        {
            await ReplaceAsync(id, t, $"{why} – Graph refuses both reauthorizing and renewing it",
                reconcile: t.PendingSince is not null, ct);
            return;
        }

        switch (await CallAsync(id, t, call.Value, ct))
        {
            case CallOutcome.Done:
                if (t.PendingSince is { } since)
                {
                    // Delivery may have paused while the challenge was unanswered.
                    t.PendingSince = null;
                    _engine.RequestReconcile(
                        $"subscription for '{t.List.DisplayName}' reauthorized after failing since {Local(since)}");
                }
                break;

            case CallOutcome.Gone:
                Untrack(id);
                if (await CreateOneAsync(t.List, ct) is not null)
                    _engine.RequestReconcile($"subscription for '{t.List.DisplayName}' was recreated");
                break;

            case CallOutcome.Refused:
                await ReplaceAsync(id, t, $"{why} – Graph refused {Describe(call.Value)}",
                    reconcile: t.PendingSince is not null, ct);
                break;

            case CallOutcome.Failed:
                t.PendingSince ??= Now;
                _logger.LogWarning("[Subscription] {Id} ('{List}') is still waiting to be reauthorized; trying again in {Minutes} minutes.",
                    id, t.List.DisplayName, MaintenanceTick.TotalMinutes);
                break;
        }
    }

    private async Task<CallOutcome> CallAsync(string id, Tracked t, AuthorizationCall call, CancellationToken ct)
    {
        var url = $"subscriptions/{Uri.EscapeDataString(id)}";
        var sentAt = Now;
        t.LastCall = (call, sentAt);

        try
        {
            if (call == AuthorizationCall.Reauthorize)
            {
                await _graph.PostAsync($"{url}/reauthorize", ct);
                t.LastAuthorized = Now;
                _logger.LogInformation("[Subscription] Reauthorized {Id} ('{List}') – expires {Expiry}.",
                    id, t.List.DisplayName, Utc(t.ExpiresAt));
            }
            else
            {
                var expiry = sentAt + Lifetime;
                var renewed = await _graph.PatchAsync<SubscriptionDto>(url, new { expirationDateTime = expiry }, ct);
                t.ExpiresAt = renewed?.ExpirationDateTime ?? expiry;
                t.LastAuthorized = Now;
                _logger.LogInformation("[Subscription] Renewed and reauthorized {Id} ('{List}') – expires {Expiry}.",
                    id, t.List.DisplayName, Utc(t.ExpiresAt));
            }

            return CallOutcome.Done;
        }
        catch (GraphRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("[Subscription] {Id} ('{List}') no longer exists.", id, t.List.DisplayName);
            return CallOutcome.Gone;
        }
        catch (GraphRequestException ex) when (IsRefusal(ex.StatusCode))
        {
            if (_refused.Add(call))
            {
                _logger.LogWarning(
                    "[Subscription] Graph refused {Call} for {Id}: {Message}. (The Graph permission tables list application " +
                    "permissions as not supported for this on todoTask subscriptions.) From now on this synchronizer " +
                    "replaces subscriptions instead of sending it.",
                    Describe(call), id, ex.Message);
            }
            return CallOutcome.Refused;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("[Subscription] {Call} failed for {Id} ('{List}'): {Message}",
                Describe(call), id, t.List.DisplayName, ex.Message);
            return CallOutcome.Failed;
        }
    }

    /// <summary>Statuses that mean "not for this subscription / not with this permission", not "try later".</summary>
    private static bool IsRefusal(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.NotImplemented;

    private static string Describe(AuthorizationCall call) => call == AuthorizationCall.Reauthorize
        ? "POST /subscriptions/{id}/reauthorize"
        : "PATCH /subscriptions/{id} (renew)";

    // ──────────────────────────────────────────────────────────
    //  Periodic check (every 5 minutes)
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Picks up list changes, retries unanswered challenges, renews what is due, and recreates
    /// what is missing.
    /// </summary>
    internal async Task MaintainAsync(CancellationToken ct)
    {
        await RefreshListsAsync(ct);

        foreach (var (id, t) in _tracked.ToList())
        {
            if (!_tracked.ContainsKey(id))
                continue;

            var now = Now;

            if (t.PendingSince is { } since)
            {
                if (now - since >= ReplaceAfter)
                    await ReplaceAsync(id, t, $"it has needed reauthorizing since {Local(since)}", reconcile: true, ct);
                else
                    await AuthorizeOrReplaceAsync(id, t, "retrying the reauthorization", ct);
                continue;
            }

            if ((t.ExpiresAt ?? now) - now > RenewalLead)
                continue;

            if (_refused.Contains(AuthorizationCall.Renew))
            {
                await ReplaceAsync(id, t, "it is due for renewal, which Graph refuses for these subscriptions", reconcile: false, ct);
                continue;
            }

            // Graph's 10-minute rule: no PATCH straight after a POST /reauthorize. Renewal is
            // due well before expiry, so the next check is soon enough.
            if (t.LastCall is { Call: AuthorizationCall.Reauthorize } last && now - last.At < ReauthorizeRenewGap)
                continue;

            switch (await CallAsync(id, t, AuthorizationCall.Renew, ct))
            {
                case CallOutcome.Gone:
                    Untrack(id);
                    if (await CreateOneAsync(t.List, ct) is not null)
                        _engine.RequestReconcile($"subscription for '{t.List.DisplayName}' was recreated");
                    break;

                case CallOutcome.Refused:
                    await ReplaceAsync(id, t, "Graph refused renewing it", reconcile: false, ct);
                    break;

                // Done: nothing more. Failed: logged; still due at the next check.
            }
        }

        await CreateMissingAsync(ct);
    }

    // ──────────────────────────────────────────────────────────
    //  Lifecycle notifications
    // ──────────────────────────────────────────────────────────

    private async Task ConsumeLifecycleEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _webhookServer.LifecycleEvents.ReadAllAsync(ct))
            {
                try
                {
                    await WithOpsLockAsync(() => HandleLifecycleEventAsync(evt, ct), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Lifecycle] Failed to handle '{Event}' for {Id}.",
                        evt.LifecycleEvent, evt.SubscriptionId);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task HandleLifecycleEventAsync(ChangeNotification evt, CancellationToken ct)
    {
        var id = evt.SubscriptionId ?? string.Empty;
        var kind = evt.LifecycleEvent ?? string.Empty;
        _tracked.TryGetValue(id, out var t);

        _logger.LogInformation("[Lifecycle] {Event} for subscription {Id} {Owner} – subscription expires {Expiry}.",
            kind, id, t is null ? "(not tracked by this instance)" : $"('{t.List.DisplayName}')",
            Utc(evt.SubscriptionExpirationDateTime));

        if (t is not null && t.ExpiresAt is null)
            t.ExpiresAt = evt.SubscriptionExpirationDateTime;

        switch (kind.ToLowerInvariant())
        {
            case "reauthorizationrequired":
                if (t is null)
                    await HandleUntrackedChallengeAsync(id, ct);
                else
                    await AnswerChallengeAsync(id, t, ct);
                break;

            case "subscriptionremoved":
                if (t is null) break;

                Untrack(id);
                await CreateOneAsync(t.List, ct);
                _engine.RequestReconcile("lifecycle: subscriptionRemoved");
                break;

            case "missed":
                _engine.RequestReconcile("lifecycle: missed");
                break;

            default:
                _logger.LogWarning("[Lifecycle] Unknown lifecycle event '{Event}' – ignored.", kind);
                break;
        }
    }

    private async Task AnswerChallengeAsync(string id, Tracked t, CancellationToken ct)
    {
        if (t.PendingSince is null && t.LastAuthorized is { } at && Now - at < ChallengeDebounce)
        {
            _logger.LogInformation(
                "[Lifecycle] {Id} ('{List}') was authorized at {At} – this challenge was raised before that; nothing to do.",
                id, t.List.DisplayName, Local(at));
            return;
        }

        await AuthorizeOrReplaceAsync(id, t, "reauthorizationRequired", ct);
    }

    /// <summary>
    /// A challenge for a subscription this instance doesn't track — usually one left behind by
    /// an earlier run that was killed rather than stopped. If it's ours and its list has no
    /// subscription here, adopt it; if the list already has one, delete it.
    /// </summary>
    private async Task HandleUntrackedChallengeAsync(string id, CancellationToken ct)
    {
        var sub = await GetSubscriptionAsync(id, ct);
        if (sub?.Id is null)
        {
            _logger.LogInformation("[Lifecycle] {Id} no longer exists (or can't be read) – nothing to do.", id);
            return;
        }

        if (!IsOurs(sub))
        {
            _logger.LogInformation("[Lifecycle] {Id} ({Resource}) isn't one of this synchronizer's subscriptions – ignored.",
                id, sub.Resource);
            return;
        }

        var list = _lists.FirstOrDefault(l => SameResource(sub.Resource, BuildResource(l)));
        if (list is null || _tracked.Values.Any(t => t.List.Id == list.Id))
        {
            _logger.LogInformation("[Lifecycle] {Id} is left over from an earlier run – deleting it.", id);
            await DeleteQuietlyAsync(id, ct);
            return;
        }

        _logger.LogInformation("[Lifecycle] Adopting {Id} for '{List}' (left over from an earlier run).", id, list.DisplayName);
        var tracked = Track(id, list, sub.ExpirationDateTime, authorizedNow: false);
        await AuthorizeOrReplaceAsync(id, tracked, "adopted after a challenge", ct);

        // Its challenges went unanswered for a while, so delivery may have paused.
        _engine.RequestReconcile($"subscription for '{list.DisplayName}' adopted");
    }

    // ──────────────────────────────────────────────────────────
    //  Read
    // ──────────────────────────────────────────────────────────

    private async Task<SubscriptionDto?> GetSubscriptionAsync(string id, CancellationToken ct)
    {
        try
        {
            return await _graph.GetAsync<SubscriptionDto>($"subscriptions/{Uri.EscapeDataString(id)}", ct);
        }
        catch (GraphRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[Subscription] Failed to GET subscription {Id}.", id);
            return null;
        }
    }

    private async Task<List<SubscriptionDto>> ListAllSubscriptionsAsync(CancellationToken ct)
    {
        try
        {
            return await _graph.GetAllPagesAsync<SubscriptionDto>("subscriptions", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[Subscription] Failed to list subscriptions – will create fresh.");
            return [];
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────

    private Tracked Track(string id, TaskList list, DateTimeOffset? expires, bool authorizedNow)
    {
        var tracked = new Tracked(list)
        {
            ExpiresAt = expires,
            LastAuthorized = authorizedNow ? Now : null
        };
        _tracked[id] = tracked;
        _engine.RegisterSubscription(id, list);
        return tracked;
    }

    private void Untrack(string id)
    {
        _tracked.Remove(id);
        _engine.UnregisterSubscription(id);
    }

    private async Task WithOpsLockAsync(Func<Task> action, CancellationToken ct)
    {
        await _opsLock.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            _opsLock.Release();
        }
    }

    internal string BuildResource(TaskList list) => $"users/{MailboxUserId}/todo/lists/{list.Id}/tasks";

    /// <summary>
    /// A subscription this synchronizer made: our notification URL, on a To Do list of this
    /// mailbox. (GET /subscriptions only returns this app registration's subscriptions.)
    /// </summary>
    private bool IsOurs(SubscriptionDto sub) =>
        sub.Id is not null
        && sub.NotificationUrl == BuildUrl(NotificationPath)
        && sub.Resource is not null
        && Regex.IsMatch(sub.Resource.TrimStart('/'),
            $"^users/{Regex.Escape(MailboxUserId)}/todo/lists/[^/]+/tasks$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Graph may echo the resource back with different casing or a leading slash.</summary>
    private static bool SameResource(string? a, string b) =>
        a is not null && string.Equals(a.TrimStart('/'), b.TrimStart('/'), StringComparison.OrdinalIgnoreCase);

    private string BuildUrl(string path) => $"{Required("Ngrok:PublicUrl").TrimEnd('/')}{path}";

    private string MailboxUserId => Required("Subscription:MailboxUserId");
    private string ClientState => Required("Subscription:ClientState");
    private string NotificationPath => WebhookServer.NotificationPathFrom(_config);
    private string LifecyclePath => WebhookServer.LifecyclePathFrom(_config);

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>todoTask subscriptions can live up to 4,230 minutes.</summary>
    private TimeSpan Lifetime =>
        TimeSpan.FromMinutes(int.TryParse(_config["Subscription:ExpirationMinutes"], out var m) ? Math.Clamp(m, 5, 4230) : 1440);

    /// <summary>Renew when this much is left: 20% of the lifetime, but never less than 15 minutes (three checks).</summary>
    private TimeSpan RenewalLead
    {
        get
        {
            var fifth = Lifetime / 5;
            return fifth > TimeSpan.FromMinutes(15) ? fifth : TimeSpan.FromMinutes(15);
        }
    }

    private static string Utc(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "(unknown)";

    private static string Local(DateTimeOffset value) =>
        value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private void PrintSubscriptionInfo(SubscriptionDto sub, TaskList list, bool reused, string? replaces = null)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine();
        Console.WriteLine(reused
            ? $"  Subscription reused — {list.DisplayName}"
            : $"  Subscription created — {list.DisplayName}");
        Console.WriteLine($"     ID              : {sub.Id}");
        if (replaces is not null)
            Console.WriteLine($"     Replaces        : {replaces}");
        Console.WriteLine($"     Mailbox         : {MailboxUserId}");
        Console.WriteLine($"     Resource        : {sub.Resource}");
        Console.WriteLine($"     Change type     : {sub.ChangeType}");
        Console.WriteLine($"     Expires         : {sub.ExpirationDateTime:O}");
        Console.WriteLine($"     Webhook         : {sub.NotificationUrl}");
        Console.WriteLine($"     Lifecycle URL   : {sub.LifecycleNotificationUrl ?? "(none)"}");
        Console.WriteLine();
        Console.ResetColor();
    }

    private string Required(string key) =>
        _config[key] ?? throw new InvalidOperationException($"Configuration key '{key}' is required but missing.");
}
