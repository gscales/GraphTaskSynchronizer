using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraphTaskSynchronizer.Tests;

/// <summary>A clock the tests move by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public sealed class GraphSubscriptionServiceTests : IDisposable
{
    private const string PublicUrl = "https://example.ngrok-free.app";
    private const string NotificationUrl = PublicUrl + "/api/notifications";
    private const string LifecycleUrl = PublicUrl + "/api/lifecycle";

    private readonly EngineHarness _h = new();
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-10-29T00:00:00Z"));

    // What "Graph" holds, and how it answers.
    private readonly Dictionary<string, SubscriptionDto> _subs = new(StringComparer.OrdinalIgnoreCase);
    private int _created;
    private HttpStatusCode? _reauthorizeFails;
    private HttpStatusCode? _renewFails;
    private bool _createFails;
    private string? _createFailsForList;
    private bool _validationFails;

    // What the public URL check reports (null = no check configured).
    private UrlCheckResult? _urlCheck;
    private int _urlChecks;

    // What a list refresh returns (null = the lists couldn't be read), and how often it ran.
    private IReadOnlyList<TaskList>? _latestLists;
    private int _listReads;

    // todoTaskList delta: changes to report per round after the first, what to throw, the links asked for.
    private readonly Queue<int> _listDeltaChanges = new();
    private Exception? _listDeltaFails;
    private bool _listDeltaResyncOnce;
    private readonly List<string?> _listDeltaCalls = new();

    private Task<(int Changes, string DeltaLink)> ListDelta(string? link, CancellationToken ct)
    {
        _listDeltaCalls.Add(link);
        if (_listDeltaFails is { } failure)
            throw failure;
        if (link is not null && _listDeltaResyncOnce)
        {
            _listDeltaResyncOnce = false;
            throw new DeltaResyncRequiredException("syncStateNotFound (simulated)");
        }

        // The initial round returns every list; later ones only what changed.
        var changes = link is null ? 1 : _listDeltaChanges.TryDequeue(out var n) ? n : 0;
        return Task.FromResult((changes, $"https://graph.microsoft.com/v1.0/users/x/todo/lists/delta?$deltatoken=round{_listDeltaCalls.Count}"));
    }

    public void Dispose() => _h.Dispose();

    // ── a minimal /subscriptions endpoint ────────────────────────────────────

    private HttpResponseMessage Respond(RecordedRequest r)
    {
        const string prefix = "/v1.0/subscriptions/";
        var rest = r.Url.AbsolutePath.StartsWith(prefix) ? Uri.UnescapeDataString(r.Url.AbsolutePath[prefix.Length..]) : null;
        var reauthorize = rest?.EndsWith("/reauthorize") == true;
        var id = reauthorize ? rest![..^"/reauthorize".Length] : rest;

        if (id is null && r.Method == HttpMethod.Get)
            return FakeGraphHandler.Json(JsonSerializer.Serialize(new { value = _subs.Values }));

        if (id is null && r.Method == HttpMethod.Post)
        {
            var sub = JsonSerializer.Deserialize<SubscriptionDto>(r.Body!)!;

            if (_validationFails)
                return FakeGraphHandler.Error(HttpStatusCode.BadRequest, "ValidationError",
                    "Subscription validation request failed. HTTP status code is 'NotFound'. Notification endpoint must respond with 200 OK to validation request.");

            if (_createFails || (_createFailsForList is { } failing && sub.Resource!.Contains($"/lists/{failing}/")))
                return FakeGraphHandler.Error(HttpStatusCode.InternalServerError, "InternalServerError", "simulated");

            sub.Id = $"sub-{++_created}";
            _subs[sub.Id] = sub;
            return FakeGraphHandler.Json(JsonSerializer.Serialize(sub), HttpStatusCode.Created);
        }

        if (id is null || !_subs.TryGetValue(id, out var existing))
            return FakeGraphHandler.Error(HttpStatusCode.NotFound, "ResourceNotFound", "subscription not found");

        if (reauthorize)
            return _reauthorizeFails is { } failure
                ? FakeGraphHandler.Error(failure, "ExtensionError", "simulated reauthorize failure")
                : new HttpResponseMessage(HttpStatusCode.NoContent);

        if (r.Method == HttpMethod.Get)
            return FakeGraphHandler.Json(JsonSerializer.Serialize(existing));

        if (r.Method == HttpMethod.Patch)
        {
            if (_renewFails is { } failure)
                return FakeGraphHandler.Error(failure, "ExtensionError", "simulated renew failure");

            using var body = JsonDocument.Parse(r.Body!);
            existing.ExpirationDateTime = body.RootElement.GetProperty("expirationDateTime").GetDateTimeOffset();
            return FakeGraphHandler.Json(JsonSerializer.Serialize(existing));
        }

        if (r.Method == HttpMethod.Delete)
        {
            _subs.Remove(id);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }

    private (GraphSubscriptionService Service, FakeGraphHandler Handler) Create(
        int expirationMinutes = 1440, IReadOnlyList<TaskList>? lists = null, bool refreshesLists = false, bool listDelta = false,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ngrok:PublicUrl"] = PublicUrl,
                ["Subscription:MailboxUserId"] = EngineHarness.Mailbox,
                ["Subscription:ClientState"] = "secret",
                ["Subscription:ExpirationMinutes"] = expirationMinutes.ToString()
            })
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var handler = new FakeGraphHandler(Respond);
        var service = new GraphSubscriptionService(config, NullLoggerFactory.Instance, TestGraph.Client(handler),
            lists ?? new[] { _h.List }, new WebhookServer(config, NullLoggerFactory.Instance, _h.Engine), _h.Engine, _time,
            refreshesLists
                ? _ =>
                {
                    _listReads++;
                    return Task.FromResult(_latestLists);
                }
                : null,
            (urls, _) =>
            {
                _urlChecks++;
                return Task.FromResult(_urlCheck ?? UrlCheckResult.Passed);
            },
            listDelta ? ListDelta : null);
        return (service, handler);
    }

    private SubscriptionDto Existing(string id, string? listId = null, string clientState = "secret",
        string notificationUrl = NotificationUrl, TimeSpan? expiresIn = null)
    {
        var sub = new SubscriptionDto
        {
            Id = id,
            // Graph may echo the resource back with different casing.
            Resource = $"Users/{EngineHarness.Mailbox}/todo/lists/{listId ?? EngineHarness.ListId}/tasks",
            ChangeType = "created,updated,deleted",
            NotificationUrl = notificationUrl,
            LifecycleNotificationUrl = LifecycleUrl,
            ExpirationDateTime = _time.Now + (expiresIn ?? TimeSpan.FromHours(5)),
            ClientState = clientState
        };
        _subs[id] = sub;
        return sub;
    }

    private Task Challenge(GraphSubscriptionService service, string subscriptionId) =>
        service.HandleLifecycleEventAsync(
            new ChangeNotification
            {
                SubscriptionId = subscriptionId,
                ClientState = "secret",
                LifecycleEvent = "reauthorizationRequired"
            },
            CancellationToken.None);

    private static List<RecordedRequest> Writes(FakeGraphHandler handler, int skip = 0) =>
        handler.Requests.Skip(skip).Where(r => r.Method != HttpMethod.Get).ToList();

    private static bool IsReauthorize(RecordedRequest r, string id) =>
        r.Method == HttpMethod.Post && r.Url.AbsolutePath.EndsWith($"/subscriptions/{id}/reauthorize");

    private static bool IsRenew(RecordedRequest r, string id) =>
        r.Method == HttpMethod.Patch && r.Url.AbsolutePath.EndsWith($"/subscriptions/{id}");

    private static bool IsCreate(RecordedRequest r) =>
        r.Method == HttpMethod.Post && r.Url.AbsolutePath.EndsWith("/subscriptions");

    private static bool IsDelete(RecordedRequest r, string id) =>
        r.Method == HttpMethod.Delete && r.Url.AbsolutePath.EndsWith($"/subscriptions/{id}");

    // ── startup ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Creates_one_basic_subscription_per_list()
    {
        var (service, handler) = Create();

        await service.EnsureSubscriptionsAsync(CancellationToken.None);

        var post = Assert.Single(handler.Requests, IsCreate);
        var sent = JsonSerializer.Deserialize<SubscriptionDto>(post.Body!)!;
        Assert.Equal($"users/{EngineHarness.Mailbox}/todo/lists/{EngineHarness.ListId}/tasks", sent.Resource);
        Assert.Equal("created,updated,deleted", sent.ChangeType);
        Assert.Equal(NotificationUrl, sent.NotificationUrl);
        Assert.Equal(LifecycleUrl, sent.LifecycleNotificationUrl);
        Assert.Equal("secret", sent.ClientState);
        Assert.DoesNotContain("includeResourceData", post.Body);   // not supported for todoTask
        Assert.Equal("sub-1", Assert.Single(service.ActiveSubscriptions).Key);
    }

    [Fact]
    public async Task A_live_subscription_from_an_earlier_run_is_reused_and_reauthorized_straight_away()
    {
        // 5 hours left of 24: past halfway, so the one call that answers any missed challenge
        // is a renewal (which reauthorizes too).
        Existing("existing-1");
        var (service, handler) = Create();

        await service.EnsureSubscriptionsAsync(CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, IsCreate);
        Assert.Single(handler.Requests, r => IsRenew(r, "existing-1"));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Equal("existing-1", Assert.Single(service.ActiveSubscriptions).Key);
    }

    [Fact]
    public async Task Leftover_subscriptions_of_ours_are_deleted_at_startup()
    {
        Existing("keep");                                                          // reused
        Existing("duplicate");                                                     // same list again
        Existing("old-secret", clientState: "an-earlier-secret");                  // notifications we'd reject
        Existing("other-list", listId: EngineHarness.OtherListId);                 // list no longer synchronized
        Existing("someone-else", notificationUrl: "https://elsewhere.example/hook"); // not ours
        var (service, handler) = Create();

        await service.EnsureSubscriptionsAsync(CancellationToken.None);

        Assert.Equal("keep", Assert.Single(service.ActiveSubscriptions).Key);
        Assert.Contains(handler.Requests, r => IsDelete(r, "duplicate"));
        Assert.Contains(handler.Requests, r => IsDelete(r, "old-secret"));
        Assert.Contains(handler.Requests, r => IsDelete(r, "other-list"));
        Assert.DoesNotContain(handler.Requests, r => IsDelete(r, "someone-else"));
        Assert.DoesNotContain(handler.Requests, r => IsDelete(r, "keep"));
    }

    // ── answering a challenge ───────────────────────────────────────────────

    [Fact]
    public async Task A_challenge_is_answered_with_reauthorize()
    {
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(30));
        var before = handler.Requests.Count;

        await Challenge(service, "sub-1");

        var call = Assert.Single(Writes(handler, before));
        Assert.True(IsReauthorize(call, "sub-1"), $"expected POST …/sub-1/reauthorize, got {call.Method} {call.Url}");
        Assert.Null(call.Body);                          // "Don't supply a request body for this method."
        Assert.False(_h.Engine.IsReconcileQueued);      // answered at once: nothing was missed
    }

    [Fact]
    public async Task A_challenge_past_the_halfway_point_renews_instead()
    {
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(13));           // 11 of 24 hours left
        var before = handler.Requests.Count;

        await Challenge(service, "sub-1");

        var call = Assert.Single(Writes(handler, before));
        Assert.True(IsRenew(call, "sub-1"));
        Assert.Contains("expirationDateTime", call.Body);
        Assert.Equal(_time.Now.AddHours(24), _subs["sub-1"].ExpirationDateTime);
    }

    [Fact]
    public async Task A_challenge_just_after_the_subscription_was_authorized_is_not_answered_twice()
    {
        // e.g. Graph redelivering challenges that failed while the synchronizer wasn't running
        Existing("existing-1");
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);   // renewed just now
        _time.Advance(TimeSpan.FromMinutes(1));
        var before = handler.Requests.Count;

        await Challenge(service, "existing-1");
        await Challenge(service, "existing-1");

        Assert.Empty(Writes(handler, before));
    }

    [Fact]
    public async Task For_ten_minutes_after_a_renewal_challenges_are_answered_with_renewals()
    {
        Existing("existing-1");                           // 5 h left: renewed at startup
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);

        // A fresh 24 h would normally mean reauthorize, but not within 10 minutes of a PATCH.
        _time.Advance(TimeSpan.FromMinutes(5));
        var before = handler.Requests.Count;
        await Challenge(service, "existing-1");
        Assert.True(IsRenew(Assert.Single(Writes(handler, before)), "existing-1"));

        _time.Advance(TimeSpan.FromMinutes(11));
        before = handler.Requests.Count;
        await Challenge(service, "existing-1");
        Assert.True(IsReauthorize(Assert.Single(Writes(handler, before)), "existing-1"));
    }

    [Fact]
    public async Task For_ten_minutes_after_a_reauthorize_challenges_are_answered_with_reauthorize()
    {
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);   // sub-1, 24 h

        _time.Advance(TimeSpan.FromMinutes(11 * 60 + 57));                 // 12 h 3 min left: reauthorize
        await Challenge(service, "sub-1");
        Assert.Single(handler.Requests, r => IsReauthorize(r, "sub-1"));

        _time.Advance(TimeSpan.FromMinutes(6));                            // past halfway, but 6 min later
        var before = handler.Requests.Count;
        await Challenge(service, "sub-1");
        Assert.True(IsReauthorize(Assert.Single(Writes(handler, before)), "sub-1"));

        _time.Advance(TimeSpan.FromMinutes(11));                           // window over: renew
        before = handler.Requests.Count;
        await Challenge(service, "sub-1");
        Assert.True(IsRenew(Assert.Single(Writes(handler, before)), "sub-1"));
    }

    [Fact]
    public async Task A_renewal_due_within_ten_minutes_of_a_reauthorize_waits()
    {
        // A 45-minute lifetime (Graph's minimum) is the one case where the two can come close.
        var (service, handler) = Create(expirationMinutes: 45);
        await service.EnsureSubscriptionsAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(22));          // 23 of 45 left: reauthorize
        await Challenge(service, "sub-1");
        Assert.Contains(handler.Requests, r => IsReauthorize(r, "sub-1"));

        _time.Advance(TimeSpan.FromMinutes(9));           // 14 left: renewal due, but 9 min after the reauthorize
        await service.MaintainAsync(CancellationToken.None);
        Assert.DoesNotContain(handler.Requests, r => IsRenew(r, "sub-1"));

        _time.Advance(TimeSpan.FromMinutes(2));           // 11 min after it: go
        await service.MaintainAsync(CancellationToken.None);
        Assert.Single(handler.Requests, r => IsRenew(r, "sub-1"));
    }

    // ── when the answer doesn't work ────────────────────────────────────────

    [Fact]
    public async Task A_failed_answer_is_retried_and_followed_by_a_reconcile()
    {
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(30));

        _reauthorizeFails = HttpStatusCode.InternalServerError;
        await Challenge(service, "sub-1");
        Assert.False(_h.Engine.IsReconcileQueued);

        _reauthorizeFails = null;
        _time.Advance(TimeSpan.FromMinutes(5));
        await service.MaintainAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count(r => IsReauthorize(r, "sub-1")));
        Assert.True(_h.Engine.IsReconcileQueued);        // delivery may have paused meanwhile
        Assert.Equal("sub-1", Assert.Single(service.ActiveSubscriptions).Key);
    }

    [Fact]
    public async Task A_challenge_unanswered_for_30_minutes_replaces_the_subscription()
    {
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(30));
        _reauthorizeFails = HttpStatusCode.InternalServerError;

        await Challenge(service, "sub-1");
        for (var i = 0; i < 6; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            await service.MaintainAsync(CancellationToken.None);
        }

        Assert.Equal("sub-2", Assert.Single(service.ActiveSubscriptions).Key);
        Assert.Contains(handler.Requests, r => IsDelete(r, "sub-1"));
        Assert.True(_h.Engine.IsReconcileQueued);
    }

    [Fact]
    public async Task When_Graph_refuses_reauthorize_the_subscription_is_replaced_without_a_gap()
    {
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(30));
        _reauthorizeFails = HttpStatusCode.Forbidden;
        var before = handler.Requests.Count;

        await Challenge(service, "sub-1");

        var writes = Writes(handler, before);
        var create = writes.FindIndex(IsCreate);
        var delete = writes.FindIndex(r => IsDelete(r, "sub-1"));
        Assert.True(create >= 0 && delete > create, "the new subscription is created before the old one is deleted");
        Assert.Equal("sub-2", Assert.Single(service.ActiveSubscriptions).Key);
        Assert.Contains(AuthorizationCall.Reauthorize, service.RefusedCalls);
        Assert.False(_h.Engine.IsReconcileQueued);      // no gap, nothing to catch up

        // Remembered: the next challenge uses the other call.
        _time.Advance(TimeSpan.FromMinutes(30));
        before = handler.Requests.Count;
        await Challenge(service, "sub-2");
        var next = Assert.Single(Writes(handler, before));
        Assert.True(IsRenew(next, "sub-2"));
    }

    [Fact]
    public async Task When_Graph_refuses_both_calls_challenges_and_renewals_replace_the_subscription()
    {
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        _reauthorizeFails = HttpStatusCode.Forbidden;
        _renewFails = HttpStatusCode.Forbidden;

        _time.Advance(TimeSpan.FromMinutes(30));
        await Challenge(service, "sub-1");                // reauthorize refused → sub-2
        _time.Advance(TimeSpan.FromMinutes(30));
        await Challenge(service, "sub-2");                // renew refused → sub-3
        Assert.Equal(2, service.RefusedCalls.Count);

        _time.Advance(TimeSpan.FromMinutes(30));
        var before = handler.Requests.Count;
        await Challenge(service, "sub-3");                // nothing left to try → straight to sub-4
        Assert.DoesNotContain(Writes(handler, before), r => IsReauthorize(r, "sub-3") || IsRenew(r, "sub-3"));
        Assert.Equal("sub-4", Assert.Single(service.ActiveSubscriptions).Key);

        _time.Advance(TimeSpan.FromHours(20));            // renewal due → replaced too
        before = handler.Requests.Count;
        await service.MaintainAsync(CancellationToken.None);
        Assert.DoesNotContain(Writes(handler, before), r => IsRenew(r, "sub-4"));
        Assert.Equal("sub-5", Assert.Single(service.ActiveSubscriptions).Key);
        Assert.Equal(new[] { "sub-5" }, _subs.Keys.ToArray());   // every old one deleted
    }

    [Fact]
    public async Task If_the_replacement_cannot_be_created_the_old_subscription_is_kept()
    {
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(30));
        _reauthorizeFails = HttpStatusCode.Forbidden;
        _createFails = true;

        await Challenge(service, "sub-1");

        Assert.Equal("sub-1", Assert.Single(service.ActiveSubscriptions).Key);
        Assert.DoesNotContain(handler.Requests, r => IsDelete(r, "sub-1"));

        _createFails = false;
        _time.Advance(TimeSpan.FromMinutes(5));
        await service.MaintainAsync(CancellationToken.None);

        Assert.Equal("sub-2", Assert.Single(service.ActiveSubscriptions).Key);
        Assert.Contains(handler.Requests, r => IsDelete(r, "sub-1"));
        Assert.True(_h.Engine.IsReconcileQueued);
    }

    [Fact]
    public async Task A_challenge_for_a_subscription_that_has_gone_recreates_it()
    {
        var (service, _) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(30));
        _subs.Remove("sub-1");

        await Challenge(service, "sub-1");

        Assert.Equal("sub-2", Assert.Single(service.ActiveSubscriptions).Key);
        Assert.True(_h.Engine.IsReconcileQueued);
    }

    [Fact]
    public async Task A_subscription_that_vanished_before_renewal_is_recreated_and_followed_by_a_reconcile()
    {
        var (service, _) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        _subs.Remove("sub-1");
        _time.Advance(TimeSpan.FromHours(20));            // 4 of 24 hours left: renewal due

        await service.MaintainAsync(CancellationToken.None);

        Assert.Equal("sub-2", Assert.Single(service.ActiveSubscriptions).Key);
        Assert.True(_h.Engine.IsReconcileQueued);
    }

    [Fact]
    public async Task Renewal_happens_when_a_fifth_of_the_lifetime_is_left()
    {
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromHours(19));            // 5 h left (> 4.8 h)
        await service.MaintainAsync(CancellationToken.None);
        Assert.DoesNotContain(handler.Requests, r => IsRenew(r, "sub-1"));

        _time.Advance(TimeSpan.FromMinutes(30));          // 4.5 h left
        await service.MaintainAsync(CancellationToken.None);
        Assert.Single(handler.Requests, r => IsRenew(r, "sub-1"));
        Assert.False(_h.Engine.IsReconcileQueued);
    }

    // ── challenges for subscriptions this instance doesn't track ────────────

    [Fact]
    public async Task A_challenge_for_a_leftover_duplicate_deletes_it()
    {
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);   // sub-1
        Existing("left-behind");                                          // appears later (e.g. a create that timed out)

        await Challenge(service, "left-behind");

        Assert.Contains(handler.Requests, r => IsDelete(r, "left-behind"));
        Assert.Equal("sub-1", Assert.Single(service.ActiveSubscriptions).Key);
    }

    [Fact]
    public async Task A_challenge_for_a_leftover_on_a_list_without_a_subscription_adopts_it()
    {
        _createFails = true;
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);   // nothing active
        Assert.Empty(service.ActiveSubscriptions);
        Existing("left-behind");

        await Challenge(service, "left-behind");

        Assert.Equal("left-behind", Assert.Single(service.ActiveSubscriptions).Key);
        Assert.Contains(handler.Requests, r => IsRenew(r, "left-behind") || IsReauthorize(r, "left-behind"));
        Assert.True(_h.Engine.IsReconcileQueued);
    }

    [Fact]
    public async Task Challenges_for_subscriptions_that_are_not_ours_or_no_longer_exist_are_ignored()
    {
        var (service, handler) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        Existing("someone-else", notificationUrl: "https://elsewhere.example/hook");
        var before = handler.Requests.Count;

        await Challenge(service, "someone-else");
        await Challenge(service, "never-existed");

        Assert.Empty(Writes(handler, before));
        Assert.Equal("sub-1", Assert.Single(service.ActiveSubscriptions).Key);
    }

    // ── lists created, deleted, renamed ─────────────────────────────────────

    private static bool IsCreateFor(RecordedRequest r, string listId) =>
        IsCreate(r) && JsonSerializer.Deserialize<SubscriptionDto>(r.Body!)!.Resource!.EndsWith($"/lists/{listId}/tasks");

    [Fact]
    public async Task A_new_list_is_subscribed_and_a_deleted_one_unsubscribed()
    {
        var (service, handler) = Create(refreshesLists: true);
        await service.EnsureSubscriptionsAsync(CancellationToken.None);          // sub-1: Tasks
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = _h.Engine.RunAsync(cts.Token);

        _latestLists = new[] { EngineHarness.TasksList, EngineHarness.GroceriesList };
        await service.MaintainAsync(CancellationToken.None);

        Assert.Single(handler.Requests, r => IsCreateFor(r, EngineHarness.OtherListId));
        Assert.Equal(new[] { "sub-1", "sub-2" }, service.ActiveSubscriptions.Keys.OrderBy(k => k).ToArray());
        await WaitUntilAsync(() => _h.Engine.Lists.Count == 2);

        _latestLists = new[] { EngineHarness.GroceriesList };                   // Tasks deleted
        await service.MaintainAsync(CancellationToken.None);

        Assert.Contains(handler.Requests, r => IsDelete(r, "sub-1"));
        Assert.Equal("sub-2", Assert.Single(service.ActiveSubscriptions).Key);
        await WaitUntilAsync(() => _h.Engine.Lists.Count == 1);

        cts.Cancel();
        await engine;
    }

    [Fact]
    public async Task A_renamed_list_keeps_its_subscription()
    {
        var (service, handler) = Create(refreshesLists: true);
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        var before = handler.Requests.Count;

        _latestLists = new[] { EngineHarness.TasksList with { DisplayName = "Work" } };
        await service.MaintainAsync(CancellationToken.None);

        Assert.Empty(Writes(handler, before));
        Assert.Equal("Work", Assert.Single(service.ActiveSubscriptions).Value.DisplayName);
    }

    [Fact]
    public async Task When_the_lists_cannot_be_read_nothing_changes()
    {
        var (service, handler) = Create(refreshesLists: true);
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        var before = handler.Requests.Count;

        _latestLists = null;
        await service.MaintainAsync(CancellationToken.None);
        _latestLists = Array.Empty<TaskList>();          // never acted on: it would empty everything
        await service.MaintainAsync(CancellationToken.None);

        Assert.Empty(Writes(handler, before));
        Assert.Equal("sub-1", Assert.Single(service.ActiveSubscriptions).Key);
    }

    [Fact]
    public async Task A_list_that_cannot_be_subscribed_is_retried_less_and_less_often()
    {
        _createFailsForList = EngineHarness.OtherListId;
        var (service, handler) = Create(lists: new[] { EngineHarness.TasksList, EngineHarness.GroceriesList });

        await service.EnsureSubscriptionsAsync(CancellationToken.None);          // attempt 1 at 0 min
        for (var minute = 5; minute <= 60; minute += 5)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            await service.MaintainAsync(CancellationToken.None);
        }

        // Waits of 5, 10, 20 and 40 minutes: attempts at 0, 5, 15 and 35.
        Assert.Equal(4, handler.Requests.Count(r => IsCreateFor(r, EngineHarness.OtherListId)));
        Assert.Equal("sub-1", Assert.Single(service.ActiveSubscriptions).Key);
    }

    // ── the list reconcile ──────────────────────────────────────────────────

    [Fact]
    public async Task The_lists_are_read_only_when_todoTaskList_delta_reports_a_change()
    {
        var (service, handler) = Create(refreshesLists: true, listDelta: true);
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = _h.Engine.RunAsync(cts.Token);
        _latestLists = new[] { EngineHarness.TasksList };

        Assert.True(await service.ReconcileListsAsync(readAll: false, CancellationToken.None));    // first round: read them
        Assert.False(await service.ReconcileListsAsync(readAll: false, CancellationToken.None));   // nothing changed
        Assert.Equal(1, _listReads);

        // A list is created in To Do: the next round reports it, and it is subscribed and synchronized.
        _latestLists = new[] { EngineHarness.TasksList, EngineHarness.GroceriesList };
        _listDeltaChanges.Enqueue(1);
        Assert.True(await service.ReconcileListsAsync(readAll: false, CancellationToken.None));

        Assert.Equal(2, _listReads);
        Assert.Single(handler.Requests, r => IsCreateFor(r, EngineHarness.OtherListId));
        await WaitUntilAsync(() => _h.Engine.Lists.Count == 2);
        Assert.Equal(new string?[] { null, ListDeltaLink(1), ListDeltaLink(2) }, _listDeltaCalls.ToArray());

        cts.Cancel();
        await engine;
    }

    /// <summary>The link the fake todoTaskList delta hands out after its nth call.</summary>
    private static string ListDeltaLink(int round) =>
        $"https://graph.microsoft.com/v1.0/users/x/todo/lists/delta?$deltatoken=round{round}";

    [Fact]
    public async Task A_list_found_before_subscribing_starts_is_subscribed_with_the_rest()
    {
        var (service, handler) = Create(refreshesLists: true);
        _latestLists = new[] { EngineHarness.TasksList, EngineHarness.GroceriesList };

        // Still waiting for the public URL check: the engine gets the list, Graph gets nothing yet.
        await service.ReconcileListsAsync(readAll: true, CancellationToken.None);
        Assert.DoesNotContain(handler.Requests, IsCreate);

        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        Assert.Equal(2, service.ActiveSubscriptions.Count);
    }

    [Fact]
    public async Task While_Graph_cannot_validate_the_webhook_a_new_list_waits_for_the_url_check()
    {
        var (service, handler) = Create(refreshesLists: true);
        _validationFails = true;
        await service.EnsureSubscriptionsAsync(CancellationToken.None);   // Tasks: validation fails
        var creates = handler.Requests.Count(IsCreate);

        _latestLists = new[] { EngineHarness.TasksList, EngineHarness.GroceriesList };
        await service.ReconcileListsAsync(readAll: true, CancellationToken.None);
        Assert.Equal(creates, handler.Requests.Count(IsCreate));          // not tried while the URL is suspect

        _validationFails = false;
        _time.Advance(TimeSpan.FromMinutes(5));
        await service.MaintainAsync(CancellationToken.None);              // URL check passes: both subscribed

        Assert.Equal(2, service.ActiveSubscriptions.Count);
    }

    [Fact]
    public async Task A_list_delta_link_Graph_no_longer_accepts_starts_a_new_round()
    {
        var (service, _) = Create(refreshesLists: true, listDelta: true);
        _latestLists = new[] { EngineHarness.TasksList };
        await service.ReconcileListsAsync(readAll: false, CancellationToken.None);

        _listDeltaResyncOnce = true;
        Assert.True(await service.ReconcileListsAsync(readAll: false, CancellationToken.None));   // read instead
        await service.ReconcileListsAsync(readAll: false, CancellationToken.None);

        Assert.Null(_listDeltaCalls[^1]);   // a new initial round
        Assert.Equal(3, _listReads);
    }

    [Fact]
    public async Task When_list_delta_fails_the_lists_are_read_in_full_until_it_is_tried_again()
    {
        var (service, _) = Create(refreshesLists: true, listDelta: true);
        _latestLists = new[] { EngineHarness.TasksList };
        _listDeltaFails = new GraphRequestException(HttpStatusCode.BadRequest, "BadRequest", "simulated");

        Assert.True(await service.ReconcileListsAsync(readAll: false, CancellationToken.None));
        Assert.True(await service.ReconcileListsAsync(readAll: false, CancellationToken.None));
        Assert.Single(_listDeltaCalls);     // paused after the failure
        Assert.Equal(2, _listReads);

        _listDeltaFails = null;
        _time.Advance(GraphSubscriptionService.ListDeltaPause);
        await service.ReconcileListsAsync(readAll: false, CancellationToken.None);
        Assert.Equal(2, _listDeltaCalls.Count);
    }

    [Fact]
    public async Task R_reads_the_lists_then_reconciles_so_a_new_list_is_synchronized()
    {
        var (service, handler) = Create(refreshesLists: true, listDelta: true);
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        _h.Engine.ListCheck = service.RequestListReconcile;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = _h.Engine.RunAsync(cts.Token);
        var lists = service.RunListReconcileAsync(cts.Token);

        _latestLists = new[] { EngineHarness.TasksList, EngineHarness.GroceriesList };
        _h.Gateway.Put(EngineHarness.OtherListId, Snapshots.Task("milk", "2026-10-29T00:00:00Z", null, subject: "Milk"));
        _h.Engine.RequestReconcileAfterListCheck("requested from the console");

        await WaitUntilAsync(() => _h.Store.Get("milk") is not null);
        Assert.Equal("Groceries", _h.Store.Get("milk")!.ListName);
        Assert.Single(handler.Requests, r => IsCreateFor(r, EngineHarness.OtherListId));

        cts.Cancel();
        await Task.WhenAll(engine, lists);
    }

    [Fact]
    public async Task The_service_picks_up_new_lists_from_the_start_even_before_it_can_subscribe()
    {
        var (service, handler) = Create(refreshesLists: true, listDelta: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = _h.Engine.RunAsync(cts.Token);
        var run = service.RunAsync(cts.Token);   // the webhook never gets ready here, so nothing is subscribed

        _latestLists = new[] { EngineHarness.TasksList, EngineHarness.GroceriesList };
        _h.Gateway.Put(EngineHarness.OtherListId, Snapshots.Task("milk", "2026-10-29T00:00:00Z", null, subject: "Milk"));
        service.RequestListReconcile("test");

        await WaitUntilAsync(() => _h.Store.Get("milk") is not null);
        Assert.Equal(2, _h.Engine.Lists.Count);
        Assert.DoesNotContain(handler.Requests, IsCreate);

        cts.Cancel();
        await Task.WhenAll(engine, run);
    }

    [Fact]
    public async Task R_with_no_list_changes_still_reconciles()
    {
        var (service, _) = Create(refreshesLists: true, listDelta: true);
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        _h.Engine.ListCheck = service.RequestListReconcile;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = _h.Engine.RunAsync(cts.Token);
        var lists = service.RunListReconcileAsync(cts.Token);

        _latestLists = new[] { EngineHarness.TasksList };   // the same lists as before
        _h.Put("x", 0.5);
        _h.Engine.RequestReconcileAfterListCheck("requested from the console");

        await WaitUntilAsync(() => _h.Store.Get("x") is not null);
        Assert.Equal(1, _listReads);

        cts.Cancel();
        await Task.WhenAll(engine, lists);
    }

    [Fact]
    public async Task A_task_moved_into_a_list_created_since_the_last_check_is_reported_as_moved()
    {
        var (service, _) = Create(refreshesLists: true, listDelta: true);
        await service.EnsureSubscriptionsAsync(CancellationToken.None);   // only Tasks so far
        _h.Engine.ListCheck = service.RequestListReconcile;
        _h.Put("a", 0.5);
        await _h.Engine.ReconcileAsync("startup", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var engine = _h.Engine.RunAsync(cts.Token);
        var lists = service.RunListReconcileAsync(cts.Token);

        // Groceries is created and the task dragged into it before any list check has run.
        _latestLists = new[] { EngineHarness.TasksList, EngineHarness.GroceriesList };
        _h.Gateway.Move("a", EngineHarness.OtherListId, "a-in-groceries");
        _h.Engine.Enqueue(_h.Notification("deleted", "a"));

        await WaitUntilAsync(() => _h.Sink.Changes.Any(c => c.Kind == TaskChangeKind.Moved));
        Assert.DoesNotContain(_h.Sink.Changes, c => c.Kind == TaskChangeKind.Removed);
        Assert.Equal("Groceries", Assert.Single(_h.Store.GetAll()).ListName);

        cts.Cancel();
        await Task.WhenAll(engine, lists);
    }

    [Fact]
    public void The_list_reconcile_interval_can_be_set_or_turned_off()
    {
        GraphSubscriptionService With(string? seconds) =>
            Create(settings: new Dictionary<string, string?> { ["Sync:ListReconcileIntervalSeconds"] = seconds }).Service;

        Assert.Equal(TimeSpan.FromSeconds(60), With(null).ListReconcileInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), With("30").ListReconcileInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), With("3").ListReconcileInterval);    // at least 10 s
        Assert.Equal(TimeSpan.Zero, With("0").ListReconcileInterval);               // off
    }

    // ── when Graph can't validate the webhook ───────────────────────────────

    private static readonly UrlCheckResult NgrokOffline = new(UrlCheckOutcome.Problem, NotificationUrl,
        "ngrok has no endpoint at example.ngrok-free.app (404 NotFound, ERR_NGROK_3200: the endpoint is offline).");

    [Fact]
    public async Task The_public_url_check_decides_whether_subscribing_can_start()
    {
        var (service, _) = Create();

        _urlCheck = NgrokOffline;
        Assert.False(await service.PublicUrlReachableAsync(CancellationToken.None));

        // Can't be told from here (e.g. this machine can't reach its own public URL): go ahead.
        _urlCheck = new UrlCheckResult(UrlCheckOutcome.Inconclusive, NotificationUrl, "couldn't connect");
        Assert.True(await service.PublicUrlReachableAsync(CancellationToken.None));

        _urlCheck = UrlCheckResult.Passed;
        Assert.True(await service.PublicUrlReachableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_validation_failure_holds_back_every_list_until_the_url_works_then_subscribes_them_all()
    {
        var lists = new[] { EngineHarness.TasksList, EngineHarness.GroceriesList };
        var (service, handler) = Create(lists: lists);

        // Graph can't validate the webhook (ngrok went away after startup's check).
        _validationFails = true;
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count(IsCreate));
        Assert.Empty(service.ActiveSubscriptions);

        // While the URL still doesn't work, no list is tried – checks only.
        _urlCheck = NgrokOffline;
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            await service.MaintainAsync(CancellationToken.None);
        }
        Assert.Equal(2, handler.Requests.Count(IsCreate));
        Assert.Equal(3, _urlChecks);

        // Fixed: both lists are subscribed at the next check, with no per-list backoff to sit out.
        _urlCheck = UrlCheckResult.Passed;
        _validationFails = false;
        _time.Advance(TimeSpan.FromMinutes(5));
        await service.MaintainAsync(CancellationToken.None);

        Assert.Equal(2, service.ActiveSubscriptions.Count);
        Assert.True(_h.Engine.IsReconcileQueued);
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

    // ── other lifecycle events ──────────────────────────────────────────────

    [Fact]
    public async Task SubscriptionRemoved_recreates_and_reconciles()
    {
        var (service, _) = Create();
        await service.EnsureSubscriptionsAsync(CancellationToken.None);
        _subs.Remove("sub-1");

        await service.HandleLifecycleEventAsync(
            new ChangeNotification { SubscriptionId = "sub-1", LifecycleEvent = "subscriptionRemoved" },
            CancellationToken.None);

        Assert.Equal("sub-2", Assert.Single(service.ActiveSubscriptions).Key);
        Assert.True(_h.Engine.IsReconcileQueued);
    }

    [Fact]
    public async Task Missed_queues_a_reconcile()
    {
        var (service, _) = Create();

        await service.HandleLifecycleEventAsync(
            new ChangeNotification { SubscriptionId = "sub-1", LifecycleEvent = "missed" },
            CancellationToken.None);

        Assert.True(_h.Engine.IsReconcileQueued);
    }
}
