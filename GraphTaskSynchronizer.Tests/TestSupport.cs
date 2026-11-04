using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphTaskSynchronizer.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tasksync-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

// ──────────────────────────────────────────────────────────────
//  Engine fakes
// ──────────────────────────────────────────────────────────────

/// <summary>
/// In-memory stand-in for the To Do lists as Graph would report them, including the
/// immutable IDs translateExchangeIds would return: a task keeps its immutable ID when it
/// moves to another list (and gets a new task ID).
/// </summary>
internal sealed class FakeGateway : ITaskGraphGateway
{
    /// <summary>Task ID → the list it is in and its current state.</summary>
    public Dictionary<string, (string ListId, TaskSnapshot Task)> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>Task ID → immutable ID, for the tasks in <see cref="Items"/>.</summary>
    public Dictionary<string, string> ImmutableIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Immutable IDs of deleted items that still exist (e.g. in Deleted Items) → their current ID there.</summary>
    public Dictionary<string, string> InDeletedItems { get; } = new(StringComparer.Ordinal);

    public HashSet<string> FailingTaskIds { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Untranslatable { get; } = new(StringComparer.Ordinal);
    public bool FailListing { get; set; }

    /// <summary>Thrown by both translation calls when set (e.g. a 403 without User.Read.All).</summary>
    public Exception? TranslationFailure { get; set; }

    public List<(string ListId, string TaskId)> GetCalls { get; } = new();
    public List<IReadOnlyCollection<string>> ToImmutableCalls { get; } = new();
    public List<string> FromImmutableCalls { get; } = new();
    public List<string> ListCalls { get; } = new();

    // ── delta ──
    /// <summary>Each delta call: the list, and the link it started from (null = initial round).</summary>
    public List<(string ListId, string? DeltaLink)> DeltaCalls { get; } = new();

    /// <summary>Stored links Graph "no longer accepts" (→ DeltaResyncRequiredException).</summary>
    public HashSet<string> ExpiredDeltaLinks { get; } = new(StringComparer.Ordinal);

    /// <summary>Delta returns no PercentComplete even when asked to expand it.</summary>
    public bool DeltaIgnoresExpand { get; set; }

    /// <summary>Delta refuses the $expand outright (400).</summary>
    public bool DeltaRejectsExpand { get; set; }

    public HashSet<string> FailingDeltaLists { get; } = new(StringComparer.Ordinal);

    private long _version;
    private int _linkSequence;
    private readonly Dictionary<string, long> _changedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ListId, long Version, HashSet<string> TaskIds, bool Expanded)> _issuedLinks = new(StringComparer.Ordinal);

    public void Put(string listId, TaskSnapshot task)
    {
        Items[task.Id] = (listId, task);
        ImmutableIds.TryAdd(task.Id, "imm-" + task.Id);
        _changedAt[task.Id] = ++_version;
    }

    /// <summary>Moves a task to another list the way To Do does: a new task ID, the same immutable ID.</summary>
    public void Move(string taskId, string toListId, string newTaskId)
    {
        var (_, task) = Items[taskId];
        Items.Remove(taskId);
        Items[newTaskId] = (toListId, task with { Id = newTaskId });
        ImmutableIds[newTaskId] = ImmutableIds[taskId];
        ImmutableIds.Remove(taskId);
        _changedAt[newTaskId] = ++_version;
    }

    public Task<DeltaPage> GetTaskDeltaAsync(string listId, string? deltaLink, bool expandPercent, CancellationToken ct)
    {
        DeltaCalls.Add((listId, deltaLink));

        if (FailListing || FailingDeltaLists.Contains(listId))
            throw new HttpRequestException("simulated delta failure");
        if (deltaLink is not null && ExpiredDeltaLinks.Contains(deltaLink))
            throw new DeltaResyncRequiredException("syncStateNotFound (simulated)");
        if (deltaLink is null && expandPercent && DeltaRejectsExpand)
            throw new GraphRequestException(System.Net.HttpStatusCode.BadRequest, "BadRequest", "$expand isn't supported on delta (simulated)");

        var current = Items.Where(i => i.Value.ListId == listId).ToDictionary(i => i.Key, i => i.Value.Task, StringComparer.Ordinal);
        List<TaskSnapshot> changed;
        var removed = new List<string>();
        bool expanded;

        if (deltaLink is null)
        {
            changed = current.Values.ToList();
            expanded = expandPercent;
        }
        else
        {
            var (_, since, before, linkExpanded) = _issuedLinks[deltaLink];
            expanded = linkExpanded;
            changed = current
                .Where(i => !before.Contains(i.Key) || _changedAt.GetValueOrDefault(i.Key) > since)
                .Select(i => i.Value)
                .ToList();
            removed = before.Where(id => !current.ContainsKey(id)).ToList();
        }

        // A stored link carries the $expand it was created with; Graph might still not honour it.
        if (DeltaIgnoresExpand || !expanded)
            changed = changed.Select(t => t with { PercentComplete = null }).ToList();

        var link = $"https://graph.microsoft.com/v1.0/fake-delta/{listId}/{++_linkSequence}";
        _issuedLinks[link] = (listId, _version, current.Keys.ToHashSet(StringComparer.Ordinal), expanded);
        return Task.FromResult(new DeltaPage(changed, removed, link, Initial: deltaLink is null));
    }

    /// <summary>Deletes a task; with <paramref name="toDeletedItems"/> its immutable ID still resolves.</summary>
    public void Delete(string taskId, bool toDeletedItems = false)
    {
        Items.Remove(taskId);
        if (ImmutableIds.Remove(taskId, out var immutableId) && toDeletedItems)
            InDeletedItems[immutableId] = "deleted-items-" + taskId;
    }

    public Task<IReadOnlyDictionary<string, string>> ToImmutableIdsAsync(IReadOnlyCollection<string> taskIds, CancellationToken ct)
    {
        ToImmutableCalls.Add(taskIds.ToList());
        if (TranslationFailure is not null)
            throw TranslationFailure;

        // Only items that exist can be translated.
        IReadOnlyDictionary<string, string> result = taskIds
            .Where(id => !Untranslatable.Contains(id) && Items.ContainsKey(id) && ImmutableIds.ContainsKey(id))
            .Distinct()
            .ToDictionary(id => id, id => ImmutableIds[id], StringComparer.Ordinal);
        return Task.FromResult(result);
    }

    public Task<string?> FromImmutableIdAsync(string immutableId, CancellationToken ct)
    {
        FromImmutableCalls.Add(immutableId);
        if (TranslationFailure is not null)
            throw TranslationFailure;

        var current = Items.Keys.FirstOrDefault(id => ImmutableIds.GetValueOrDefault(id) == immutableId)
                      ?? InDeletedItems.GetValueOrDefault(immutableId);
        return Task.FromResult(current);
    }

    public Task<TaskLookupResult> GetTaskAsync(string listId, string taskId, CancellationToken ct)
    {
        GetCalls.Add((listId, taskId));

        if (FailingTaskIds.Contains(taskId))
            return Task.FromResult(TaskLookupResult.Failed("simulated failure"));

        return Task.FromResult(Items.TryGetValue(taskId, out var item) && item.ListId == listId
            ? TaskLookupResult.Found(item.Task)
            : TaskLookupResult.NotFound());
    }

    public Task<IReadOnlyList<TaskSnapshot>> ListTasksAsync(string listId, CancellationToken ct)
    {
        ListCalls.Add(listId);
        if (FailListing)
            throw new HttpRequestException("simulated listing failure");

        IReadOnlyList<TaskSnapshot> listing = Items.Values.Where(i => i.ListId == listId).Select(i => i.Task).ToList();
        return Task.FromResult(listing);
    }
}

internal sealed class RecordingSink : ITaskChangeSink
{
    public List<TaskChange> Changes { get; } = new();
    public List<ReconcileSummary> Reconciles { get; } = new();

    public Task OnTaskChangedAsync(TaskChange change, CancellationToken ct)
    {
        lock (Changes) Changes.Add(change);
        return Task.CompletedTask;
    }

    public Task OnReconcileCompletedAsync(ReconcileSummary summary, CancellationToken ct)
    {
        lock (Reconciles) Reconciles.Add(summary);
        return Task.CompletedTask;
    }

    public int ChangeCount
    {
        get { lock (Changes) return Changes.Count; }
    }
}

internal static class Snapshots
{
    public static TaskSnapshot Task(
        string id,
        string lastModified,
        double? percent,
        TodoTaskStatus? status = TodoTaskStatus.InProgress,
        string subject = "Draft Q4 report",
        string? notes = "") => new()
    {
        Id = id,
        Subject = subject,
        Status = status,
        PercentComplete = percent,
        Importance = "normal",
        Notes = notes,
        LastModifiedDateTime = DateTimeOffset.Parse(lastModified, CultureInfo.InvariantCulture)
    };
}

/// <summary>An engine wired to fakes, plus helpers to raise notifications for it.</summary>
internal sealed class EngineHarness : IDisposable
{
    public const string ListId = "AAMkADTasksList=";
    public const string OtherListId = "AAMkADGroceriesList=";
    public const string SubscriptionId = "0f6a4c1e-4b1f-4f5e-9d38-6a2b1c0d9e11";
    public const string Mailbox = "user@contoso.com";

    public const string OtherSubscriptionId = "5d0c2e7b-0d9a-4a4b-8f1e-2c1d0b9e8f22";

    public static readonly TaskList TasksList = new(ListId, "Tasks", "defaultList", "test");
    public static readonly TaskList GroceriesList = new(OtherListId, "Groceries", "none", "test");

    private readonly TempDirectory _temp = new();

    public TaskList List => TasksList;
    public FakeGateway Gateway { get; } = new();
    public TaskStore Store { get; }
    public RecordingSink Sink { get; } = new();
    public TaskSyncEngine Engine { get; }

    /// <param name="bothLists">Synchronize Groceries as well as Tasks (for moves between lists).</param>
    public EngineHarness(TaskSyncOptions? options = null, bool bothLists = false, TimeProvider? time = null)
    {
        Store = new TaskStore(_temp.File("replica.json"), Mailbox, NullLoggerFactory.Instance);

        Engine = new TaskSyncEngine(
            options ?? new TaskSyncOptions { ReconcileInterval = TimeSpan.Zero },
            NullLoggerFactory.Instance,
            Gateway,
            Store,
            bothLists ? new[] { TasksList, GroceriesList } : new[] { TasksList },
            new ITaskChangeSink[] { Sink },
            time);

        Engine.RegisterSubscription(SubscriptionId, TasksList);
        if (bothLists)
            Engine.RegisterSubscription(OtherSubscriptionId, GroceriesList);
    }

    /// <summary>A notification for a task in Groceries, from that list's subscription.</summary>
    public ChangeNotification GroceriesNotification(string changeType, string taskId) =>
        Notification(changeType, taskId, OtherSubscriptionId,
            $"Users/00000000-0000-0000-0000-000000000001/todo/lists/{OtherListId}/tasks/{taskId}");

    public ChangeNotification Notification(
        string changeType,
        string taskId,
        string subscriptionId = SubscriptionId,
        string? resource = null) => new()
    {
        SubscriptionId = subscriptionId,
        ChangeType = changeType,
        ClientState = "secret",
        Resource = resource ?? $"Users/00000000-0000-0000-0000-000000000001/todo/lists/{ListId}/tasks/{taskId}",
        ResourceData = new ResourceData { Id = taskId, ODataType = "#Microsoft.Graph.todoTask" }
    };

    public Task Process(ChangeNotification notification) =>
        Engine.ProcessNotificationAsync(notification, CancellationToken.None);

    /// <summary>Sets the task's state in the mailbox (the fake gateway).</summary>
    public void Put(string id, double? percent, string lastModified = "2026-10-28T00:10:00Z",
        TodoTaskStatus? status = TodoTaskStatus.InProgress, string? notes = "") =>
        Gateway.Put(ListId, Snapshots.Task(id, lastModified, percent, status, notes: notes));

    /// <summary>Puts the task in the mailbox and delivers a 'created' notification for it.</summary>
    public Task Created(string id, double? percent, string lastModified = "2026-10-28T00:10:00Z")
    {
        Put(id, percent, lastModified);
        return Process(Notification("created", id));
    }

    public void Dispose() => _temp.Dispose();
}

// ──────────────────────────────────────────────────────────────
//  HTTP fakes for the REST layer
// ──────────────────────────────────────────────────────────────

internal sealed record RecordedRequest(HttpMethod Method, Uri Url, string? Body, string? Authorization)
{
    /// <summary>The query string with percent-encoding removed, for readable assertions.</summary>
    public string DecodedQuery => Uri.UnescapeDataString(Url.Query);
}

/// <summary>Records every request and answers with whatever the test's responder returns.</summary>
internal sealed class FakeGraphHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, HttpResponseMessage> _respond;

    public FakeGraphHandler(Func<RecordedRequest, HttpResponseMessage> respond) => _respond = respond;

    public List<RecordedRequest> Requests { get; } = new();

    /// <summary>The request headers of each request, as text, in the same order as <see cref="Requests"/>.</summary>
    public List<string> RequestHeaders { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString());
        lock (Requests)
        {
            Requests.Add(recorded);
            RequestHeaders.Add(request.Headers.ToString());
        }
        return _respond(recorded);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        Json(JsonSerializer.Serialize(new { error = new { code, message } }), status);
}

internal sealed class StaticTokenProvider : IGraphTokenProvider
{
    public Task<string> GetTokenAsync(CancellationToken ct) => Task.FromResult("test-token");
}

internal static class TestGraph
{
    public static GraphRestClient Client(FakeGraphHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = GraphRestClient.DefaultBaseAddress },
            new StaticTokenProvider(), NullLoggerFactory.Instance,
            maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(1));
}

/// <summary>todoTask JSON shaped like a Graph response (extended property IDs echoed lower-case).</summary>
internal static class TodoJson
{
    public const string TaskGuid = "{00062003-0000-0000-c000-000000000046}";

    public static string Task(
        string id,
        string lastModified = "2026-10-28T00:23:45.1234567Z",
        string status = "inProgress",
        double? percent = null,
        string? dueDate = null,
        string? completedDate = null,
        string title = "Draft Q4 report",
        string? notes = null,
        string notesType = "text") =>
        JsonSerializer.Serialize(TaskObject(id, lastModified, status, percent, dueDate, completedDate, title, notes, notesType));

    public static Dictionary<string, object?> TaskObject(
        string id,
        string lastModified = "2026-10-28T00:23:45.1234567Z",
        string status = "inProgress",
        double? percent = null,
        string? dueDate = null,
        string? completedDate = null,
        string title = "Draft Q4 report",
        string? notes = null,
        string notesType = "text")
    {
        var task = new Dictionary<string, object?>
        {
            ["@odata.etag"] = "W/\"vVxzmGwEk0GWDaBVxDVVJQAAAhFAAg==\"",
            ["importance"] = "Normal",
            ["isReminderOn"] = false,
            ["status"] = status,
            ["title"] = title,
            ["createdDateTime"] = "2026-10-27T22:10:00.0000000Z",
            ["lastModifiedDateTime"] = lastModified,
            ["categories"] = new[] { "Finance" },
            ["id"] = id
        };

        if (dueDate is not null)
            task["dueDateTime"] = new { dateTime = dueDate, timeZone = "UTC" };
        if (completedDate is not null)
            task["completedDateTime"] = new { dateTime = completedDate, timeZone = "UTC" };
        if (notes is not null)
            task["body"] = new { content = notes, contentType = notesType };

        if (percent is not null)
        {
            task["singleValueExtendedProperties"] = new[]
            {
                new { id = $"Double {TaskGuid} Id 0x8102", value = percent.Value.ToString(CultureInfo.InvariantCulture) }
            };
        }

        return task;
    }

    public static string Collection(IEnumerable<object> items, string? nextLink = null)
    {
        var page = new Dictionary<string, object?> { ["value"] = items.ToList() };
        if (nextLink is not null)
            page["@odata.nextLink"] = nextLink;
        return JsonSerializer.Serialize(page);
    }
}
