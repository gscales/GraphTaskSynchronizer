using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraphTaskSynchronizer.Tests;

public sealed class GraphRestClientTests
{
    [Fact]
    public async Task Sends_the_bearer_token_and_resolves_relative_urls()
    {
        var handler = new FakeGraphHandler(_ => FakeGraphHandler.Json("{\"value\":[]}"));

        await TestGraph.Client(handler).GetAllPagesAsync<TodoTaskListDto>("users/someone/todo/lists", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://graph.microsoft.com/v1.0/users/someone/todo/lists", request.Url.ToString());
        Assert.Equal("Bearer test-token", request.Authorization);
    }

    [Fact]
    public async Task Follows_nextLink_until_the_last_page()
    {
        var handler = new FakeGraphHandler(r => r.Url.Query.Contains("skiptoken")
            ? FakeGraphHandler.Json("{\"value\":[{\"id\":\"3\"}]}")
            : FakeGraphHandler.Json(
                "{\"value\":[{\"id\":\"1\"},{\"id\":\"2\"}]," +
                "\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users/someone/todo/lists?$skiptoken=abc\"}"));

        var lists = await TestGraph.Client(handler).GetAllPagesAsync<TodoTaskListDto>("users/someone/todo/lists", CancellationToken.None);

        Assert.Equal(new[] { "1", "2", "3" }, lists.Select(l => l.Id).ToArray());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Throttling_is_retried_after_the_Retry_After_interval()
    {
        var calls = 0;
        var handler = new FakeGraphHandler(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                var throttled = FakeGraphHandler.Error(HttpStatusCode.TooManyRequests, "TooManyRequests", "slow down");
                throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return throttled;
            }
            return FakeGraphHandler.Json("{\"id\":\"sub-1\"}");
        });

        var result = await TestGraph.Client(handler).GetAsync<SubscriptionDto>("subscriptions/sub-1", CancellationToken.None);

        Assert.Equal("sub-1", result!.Id);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Gives_up_after_the_last_attempt()
    {
        var handler = new FakeGraphHandler(_ => FakeGraphHandler.Error(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", "busy"));

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() =>
            TestGraph.Client(handler).GetStringAsync("subscriptions", CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_404_carries_the_Graph_error_code()
    {
        var handler = new FakeGraphHandler(_ => FakeGraphHandler.Error(HttpStatusCode.NotFound, "ErrorItemNotFound", "The specified object was not found in the store."));

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() =>
            TestGraph.Client(handler).GetStringAsync("users/someone/todo/lists/l/tasks/t", CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("ErrorItemNotFound", ex.ErrorCode);
        Assert.Single(handler.Requests);   // not retried
    }

    [Fact]
    public async Task Request_bodies_leave_out_null_properties()
    {
        var handler = new FakeGraphHandler(_ => FakeGraphHandler.Json("{\"id\":\"sub-1\"}", HttpStatusCode.Created));

        await TestGraph.Client(handler).PostAsync<SubscriptionDto>("subscriptions",
            new SubscriptionDto { Resource = "users/someone/todo/lists/l/tasks", ChangeType = "updated" }, CancellationToken.None);

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("\"resource\":\"users/someone/todo/lists/l/tasks\"", body);
        Assert.DoesNotContain("\"id\"", body);
        Assert.DoesNotContain("notificationUrl", body);
    }
}

public sealed class TodoGatewayTests
{
    private const string Mailbox = "user@contoso.com";
    private const string ListId = "AAMkADTasksList=";

    [Fact]
    public async Task GetTask_asks_for_PercentComplete_through_expand_and_maps_the_task()
    {
        var handler = new FakeGraphHandler(_ => FakeGraphHandler.Json(TodoJson.Task("task-1", percent: 0.75)));
        var gateway = new TodoGateway(TestGraph.Client(handler), Mailbox, logTaskJson: false, NullLoggerFactory.Instance);

        var result = await gateway.GetTaskAsync(ListId, "task-1", CancellationToken.None);

        Assert.Equal(TaskLookupStatus.Found, result.Status);
        Assert.Equal(0.75, result.Snapshot!.PercentComplete);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/v1.0/users/user%40contoso.com/todo/lists/{Uri.EscapeDataString(ListId)}/tasks/task-1", request.Url.AbsolutePath);
        Assert.Contains("$select=id,status,", request.DecodedQuery);
        Assert.Contains("dueDateTime", request.DecodedQuery);
        Assert.EndsWith(
            "$expand=singleValueExtendedProperties($filter=id eq 'Double {00062003-0000-0000-C000-000000000046} Id 0x8102')",
            request.DecodedQuery);
    }

    [Fact]
    public async Task GetTask_404_is_NotFound()
    {
        var handler = new FakeGraphHandler(_ => FakeGraphHandler.Error(HttpStatusCode.NotFound, "ErrorItemNotFound", "gone"));
        var gateway = new TodoGateway(TestGraph.Client(handler), Mailbox, false, NullLoggerFactory.Instance);

        var result = await gateway.GetTaskAsync(ListId, "task-1", CancellationToken.None);

        Assert.Equal(TaskLookupStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task GetTask_other_errors_are_Failed()
    {
        var handler = new FakeGraphHandler(_ => FakeGraphHandler.Error(HttpStatusCode.BadRequest, "BadRequest", "Invalid $expand"));
        var gateway = new TodoGateway(TestGraph.Client(handler), Mailbox, false, NullLoggerFactory.Instance);

        var result = await gateway.GetTaskAsync(ListId, "task-1", CancellationToken.None);

        Assert.Equal(TaskLookupStatus.Failed, result.Status);
        Assert.Contains("Invalid $expand", result.Error);
    }

    [Fact]
    public async Task ListTasks_pages_through_the_list()
    {
        var handler = new FakeGraphHandler(r => r.Url.Query.Contains("skiptoken")
            ? FakeGraphHandler.Json(TodoJson.Collection(new object[] { TodoJson.TaskObject("c", percent: 1.0) }))
            : FakeGraphHandler.Json(TodoJson.Collection(
                new object[] { TodoJson.TaskObject("a", percent: 0.25), TodoJson.TaskObject("b") },
                nextLink: $"https://graph.microsoft.com/v1.0/users/x/todo/lists/{ListId}/tasks?$skiptoken=next")));
        var gateway = new TodoGateway(TestGraph.Client(handler), Mailbox, false, NullLoggerFactory.Instance);

        var tasks = await gateway.ListTasksAsync(ListId, CancellationToken.None);

        Assert.Equal(new[] { "a", "b", "c" }, tasks.Select(t => t.Id).ToArray());
        Assert.Equal(0.25, tasks[0].PercentComplete);
        Assert.Null(tasks[1].PercentComplete);
        Assert.Contains("$top=100", handler.Requests[0].DecodedQuery);
    }

    // ── delta ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_initial_delta_round_expands_PercentComplete_and_pages_to_the_delta_link()
    {
        var deltaLink = $"https://graph.microsoft.com/v1.0/users/x/todo/lists/{ListId}/tasks/delta?$deltatoken=next-round";
        var handler = new FakeGraphHandler(r => r.Url.Query.Contains("skiptoken")
            ? FakeGraphHandler.Json(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["value"] = new object[] { TodoJson.TaskObject("b", percent: 1.0) },
                ["@odata.deltaLink"] = deltaLink
            }))
            : FakeGraphHandler.Json(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["value"] = new object[] { TodoJson.TaskObject("a", percent: 0.25) },
                ["@odata.nextLink"] = $"https://graph.microsoft.com/v1.0/users/x/todo/lists/{ListId}/tasks/delta?$skiptoken=page2"
            })));
        var gateway = new TodoGateway(TestGraph.Client(handler), Mailbox, false, NullLoggerFactory.Instance);

        var page = await gateway.GetTaskDeltaAsync(ListId, null, expandPercent: true, CancellationToken.None);

        Assert.True(page.Initial);
        Assert.Equal(new[] { "a", "b" }, page.Changed.Select(t => t.Id).ToArray());
        Assert.Equal(0.25, page.Changed[0].PercentComplete);
        Assert.Equal(deltaLink, page.DeltaLink);

        var first = handler.Requests[0];
        Assert.Equal($"/v1.0/users/user%40contoso.com/todo/lists/{Uri.EscapeDataString(ListId)}/tasks/delta", first.Url.AbsolutePath);
        Assert.Equal(
            "?$expand=singleValueExtendedProperties($filter=id eq 'Double {00062003-0000-0000-C000-000000000046} Id 0x8102')",
            first.DecodedQuery);
        Assert.DoesNotContain("$select", first.DecodedQuery);   // a $select could hide %-only changes
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_delta_round_from_a_link_reports_changes_and_removals()
    {
        var link = $"https://graph.microsoft.com/v1.0/users/x/todo/lists/{ListId}/tasks/delta?$deltatoken=t1";
        var handler = new FakeGraphHandler(_ => FakeGraphHandler.Json(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["value"] = new object[]
            {
                TodoJson.TaskObject("a", percent: 0.5),
                new Dictionary<string, object?> { ["id"] = "gone", ["@removed"] = new { reason = "deleted" } },
                TodoJson.TaskObject("a", percent: 0.75)            // the same task again: the later entry wins
            },
            ["@odata.deltaLink"] = link.Replace("t1", "t2")
        })));
        var gateway = new TodoGateway(TestGraph.Client(handler), Mailbox, false, NullLoggerFactory.Instance);

        var page = await gateway.GetTaskDeltaAsync(ListId, link, expandPercent: true, CancellationToken.None);

        Assert.False(page.Initial);
        Assert.Equal(0.75, Assert.Single(page.Changed).PercentComplete);
        Assert.Equal("gone", Assert.Single(page.RemovedIds));
        Assert.EndsWith("$deltatoken=t2", page.DeltaLink);
        Assert.Equal(link, Assert.Single(handler.Requests).Url.ToString());
        Assert.Contains("Prefer", handler.RequestHeaders[0]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, "resyncRequired")]
    [InlineData(HttpStatusCode.BadRequest, "syncStateNotFound")]
    public async Task A_delta_link_Graph_no_longer_accepts_asks_for_a_full_round(HttpStatusCode status, string code)
    {
        var handler = new FakeGraphHandler(_ => FakeGraphHandler.Error(status, code, "The sync state is no longer valid."));
        var gateway = new TodoGateway(TestGraph.Client(handler), Mailbox, false, NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<DeltaResyncRequiredException>(() =>
            gateway.GetTaskDeltaAsync(ListId, "https://graph.microsoft.com/v1.0/x/tasks/delta?$deltatoken=old", true, CancellationToken.None));
    }

    [Fact]
    public async Task A_permission_error_on_a_delta_link_is_not_a_resync()
    {
        var handler = new FakeGraphHandler(_ => FakeGraphHandler.Error(HttpStatusCode.Forbidden, "ErrorAccessDenied", "Access is denied."));
        var gateway = new TodoGateway(TestGraph.Client(handler), Mailbox, false, NullLoggerFactory.Instance);

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() =>
            gateway.GetTaskDeltaAsync(ListId, "https://graph.microsoft.com/v1.0/x/tasks/delta?$deltatoken=old", true, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    // ── translateExchangeIds ─────────────────────────────────────────────────

    private static string Translated(params (string Source, string? Target)[] results) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            value = results.Select(r => r.Target is null
                ? (object)new { sourceId = r.Source, errorDetails = new { code = "ErrorInvalidIdMalformed", message = "Id is malformed." } }
                : new { sourceId = r.Source, targetId = r.Target })
        });

    [Fact]
    public async Task Task_ids_are_translated_to_immutable_ids()
    {
        var handler = new FakeGraphHandler(_ => FakeGraphHandler.Json(Translated(("id-1", "imm-1"), ("id-2", null))));
        var gateway = new TodoGateway(TestGraph.Client(handler), Mailbox, false, NullLoggerFactory.Instance);

        var ids = await gateway.ToImmutableIdsAsync(new[] { "id-1", "id-2" }, CancellationToken.None);

        Assert.Equal("imm-1", ids["id-1"]);
        Assert.False(ids.ContainsKey("id-2"));   // that one failed on its own

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1.0/users/user%40contoso.com/translateExchangeIds", request.Url.AbsolutePath);
        using var body = System.Text.Json.JsonDocument.Parse(request.Body!);
        Assert.Equal(new[] { "id-1", "id-2" },
            body.RootElement.GetProperty("inputIds").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("restId", body.RootElement.GetProperty("sourceIdType").GetString());
        Assert.Equal("restImmutableEntryId", body.RootElement.GetProperty("targetIdType").GetString());
    }

    [Fact]
    public async Task More_than_1000_ids_go_in_several_calls()
    {
        var handler = new FakeGraphHandler(r =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(r.Body!);
            var inputs = body.RootElement.GetProperty("inputIds").EnumerateArray().Select(e => e.GetString()!).ToArray();
            return FakeGraphHandler.Json(Translated(inputs.Select(i => (i, (string?)("imm-" + i))).ToArray()));
        });
        var gateway = new TodoGateway(TestGraph.Client(handler), Mailbox, false, NullLoggerFactory.Instance);
        var input = Enumerable.Range(0, 2500).Select(i => $"id-{i}").ToArray();

        var ids = await gateway.ToImmutableIdsAsync(input, CancellationToken.None);

        Assert.Equal(2500, ids.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("imm-id-2499", ids["id-2499"]);
    }

    [Fact]
    public async Task An_immutable_id_resolves_to_the_current_task_id_or_to_nothing()
    {
        var handler = new FakeGraphHandler(r => r.Body!.Contains("imm-gone")
            ? FakeGraphHandler.Json(Translated(("imm-gone", null)))
            : FakeGraphHandler.Json(Translated(("imm-1", "id-now"))));
        var gateway = new TodoGateway(TestGraph.Client(handler), Mailbox, false, NullLoggerFactory.Instance);

        Assert.Equal("id-now", await gateway.FromImmutableIdAsync("imm-1", CancellationToken.None));
        Assert.Null(await gateway.FromImmutableIdAsync("imm-gone", CancellationToken.None));

        using var body = System.Text.Json.JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("restImmutableEntryId", body.RootElement.GetProperty("sourceIdType").GetString());
        Assert.Equal("restId", body.RootElement.GetProperty("targetIdType").GetString());
    }

    [Fact]
    public async Task A_refused_translation_throws()
    {
        var handler = new FakeGraphHandler(_ =>
            FakeGraphHandler.Error(HttpStatusCode.Forbidden, "ErrorAccessDenied", "Access is denied."));
        var gateway = new TodoGateway(TestGraph.Client(handler), Mailbox, false, NullLoggerFactory.Instance);

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() =>
            gateway.ToImmutableIdsAsync(new[] { "id-1" }, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }
}

public sealed class TodoListResolverTests
{
    private const string Lists =
        "{\"value\":[" +
        "{\"id\":\"list-tasks\",\"displayName\":\"Tasks\",\"wellknownListName\":\"defaultList\",\"isOwner\":true,\"isShared\":false}," +
        "{\"id\":\"list-flagged\",\"displayName\":\"Flagged Emails\",\"wellknownListName\":\"flaggedEmails\"}," +
        "{\"id\":\"list-groceries\",\"displayName\":\"Groceries\",\"wellknownListName\":\"none\"}]}";

    private static IConfiguration Config(params string[] lists) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(lists.Select((v, i) => new KeyValuePair<string, string?>($"Tasks:Lists:{i}", v)))
            .Build();

    private static Task<IReadOnlyList<TaskList>?> Resolve(IConfiguration config, FakeGraphHandler handler, bool quiet = false) =>
        new TodoListResolver(config, NullLoggerFactory.Instance, TestGraph.Client(handler))
            .ResolveAsync("user@contoso.com", CancellationToken.None, quiet);

    [Fact]
    public async Task With_no_configuration_every_list_is_synchronized()
    {
        var lists = await Resolve(Config(), new FakeGraphHandler(_ => FakeGraphHandler.Json(Lists)));

        Assert.Equal(new[] { "list-tasks", "list-flagged", "list-groceries" }, lists!.Select(l => l.Id).ToArray());
        Assert.Equal("defaultList", lists![0].WellknownListName);
        Assert.Equal("Groceries", lists[2].DisplayName);
    }

    [Fact]
    public async Task Lists_can_be_named_or_given_by_id()
    {
        var lists = await Resolve(Config("groceries", "list-flagged", "default"),
            new FakeGraphHandler(_ => FakeGraphHandler.Json(Lists)));

        Assert.Equal(new[] { "list-groceries", "list-flagged", "list-tasks" }, lists!.Select(l => l.Id).ToArray());
    }

    [Fact]
    public async Task An_unknown_entry_is_skipped()
    {
        var lists = await Resolve(Config("Holiday plans", "default"), new FakeGraphHandler(_ => FakeGraphHandler.Json(Lists)));

        Assert.Equal("list-tasks", Assert.Single(lists!).Id);
    }

    [Fact]
    public async Task Lists_that_cannot_be_read_give_null_rather_than_none()
    {
        var forbidden = await Resolve(Config(), new FakeGraphHandler(_ =>
            FakeGraphHandler.Error(HttpStatusCode.Forbidden, "ErrorAccessDenied", "Access is denied.")));
        var offline = await Resolve(Config(), new FakeGraphHandler(_ => throw new HttpRequestException("network down")),
            quiet: true);

        Assert.Null(forbidden);
        Assert.Null(offline);   // a refresh doesn't take the synchronizer down
    }

    // ── todoTaskList delta ───────────────────────────────────────────────────

    private static TodoListResolver Resolver(FakeGraphHandler handler) =>
        new(Config(), NullLoggerFactory.Instance, TestGraph.Client(handler));

    [Fact]
    public async Task List_delta_pages_to_the_delta_link_and_counts_what_changed()
    {
        var handler = new FakeGraphHandler(r => r.Url.Query.Contains("skiptoken")
            ? FakeGraphHandler.Json(
                "{\"value\":[{\"id\":\"list-new\",\"displayName\":\"My new list\"}]," +
                "\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/users/x/todo/lists/delta?$deltatoken=next\"}")
            : FakeGraphHandler.Json(
                "{\"value\":[{\"id\":\"list-tasks\",\"displayName\":\"Tasks\"},{\"id\":\"list-gone\",\"@removed\":{\"reason\":\"deleted\"}}]," +
                "\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users/x/todo/lists/delta?$skiptoken=page2\"}"));

        var (changes, link) = await Resolver(handler).GetListChangesAsync("user@contoso.com", null, CancellationToken.None);

        Assert.Equal(3, changes);   // added, changed and removed alike
        Assert.EndsWith("$deltatoken=next", link);
        Assert.Equal("/v1.0/users/user%40contoso.com/todo/lists/delta", handler.Requests[0].Url.AbsolutePath);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_later_list_delta_round_starts_from_the_link()
    {
        const string link = "https://graph.microsoft.com/v1.0/users/x/todo/lists/delta?$deltatoken=round1";
        var handler = new FakeGraphHandler(_ => FakeGraphHandler.Json(
            "{\"value\":[],\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/users/x/todo/lists/delta?$deltatoken=round2\"}"));

        var (changes, next) = await Resolver(handler).GetListChangesAsync("user@contoso.com", link, CancellationToken.None);

        Assert.Equal(0, changes);
        Assert.EndsWith("round2", next);
        Assert.Equal(link, Assert.Single(handler.Requests).Url.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, "resyncRequired")]
    [InlineData(HttpStatusCode.BadRequest, "syncStateNotFound")]
    public async Task A_list_delta_link_Graph_no_longer_accepts_asks_for_a_new_round(HttpStatusCode status, string code)
    {
        var handler = new FakeGraphHandler(_ => FakeGraphHandler.Error(status, code, "The sync state is no longer valid."));

        await Assert.ThrowsAsync<DeltaResyncRequiredException>(() => Resolver(handler).GetListChangesAsync(
            "user@contoso.com", "https://graph.microsoft.com/v1.0/users/x/todo/lists/delta?$deltatoken=old", CancellationToken.None));
    }
}
