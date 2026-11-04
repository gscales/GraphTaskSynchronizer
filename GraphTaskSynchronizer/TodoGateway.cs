using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GraphTaskSynchronizer;

/// <summary>
/// <see cref="ITaskGraphGateway"/> over the To Do API:
///
///   GET users/{id}/todo/lists/{listId}/tasks/{taskId}
///       ?$select=id,title,status,…
///       &amp;$expand=singleValueExtendedProperties($filter=id eq 'Double {00062003-…} Id 0x8102' or …)
///
/// The listing uses the same query on users/{id}/todo/lists/{listId}/tasks, paged.
///
/// Immutable IDs come from
///
///   POST users/{id}/translateExchangeIds
///   { "inputIds": [ … ], "sourceIdType": "restId", "targetIdType": "restImmutableEntryId" }
///
/// (and the reverse), because the To Do API doesn't honour Prefer: IdType="ImmutableId".
/// Up to 1,000 IDs go in one call; each ID succeeds or fails on its own.
/// </summary>
public sealed class TodoGateway : ITaskGraphGateway
{
    private const int PageSize = 100;

    /// <summary>translateExchangeIds accepts at most 1,000 input IDs.</summary>
    internal const int MaxIdsPerTranslation = 1000;

    private readonly GraphRestClient _graph;
    private readonly string _mailboxUserId;
    private readonly bool _logTaskJson;
    private readonly ILogger<TodoGateway> _logger;

    public TodoGateway(GraphRestClient graph, string mailboxUserId, bool logTaskJson, ILoggerFactory loggerFactory)
    {
        _graph = graph;
        _mailboxUserId = mailboxUserId;
        _logTaskJson = logTaskJson;
        _logger = loggerFactory.CreateLogger<TodoGateway>();
    }

    public async Task<TaskLookupResult> GetTaskAsync(string listId, string taskId, CancellationToken ct)
    {
        var url = TaskUrl(listId, taskId);
        try
        {
            var json = await _graph.GetStringAsync(url, ct);

            if (_logTaskJson)
                _logger.LogInformation("[Graph] GET {Url}\n{Json}", url, json);

            var task = JsonSerializer.Deserialize<TodoTaskDto>(json, GraphRestClient.JsonOpts);
            return string.IsNullOrEmpty(task?.Id)
                ? TaskLookupResult.NotFound()
                : TaskLookupResult.Found(TaskSnapshotFactory.FromTodoTask(task));
        }
        catch (GraphRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return TaskLookupResult.NotFound();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[Graph] GET task {TaskId} in list {ListId} failed.", taskId, listId);
            return TaskLookupResult.Failed(ex.Message);
        }
    }

    public async Task<IReadOnlyList<TaskSnapshot>> ListTasksAsync(string listId, CancellationToken ct)
    {
        var tasks = await _graph.GetAllPagesAsync<TodoTaskDto>(TasksUrl(listId), ct);

        _logger.LogDebug("[Graph] Listed {Count} task(s) in list {ListId}.", tasks.Count, listId);
        return tasks
            .Where(t => !string.IsNullOrEmpty(t.Id))
            .Select(TaskSnapshotFactory.FromTodoTask)
            .ToList();
    }

    /// <summary>
    /// GET users/{id}/todo/lists/{listId}/tasks/delta?$expand=…PidLidPercentComplete…, or the
    /// stored @odata.deltaLink (which already carries the query), paged to the next deltaLink.
    /// There is deliberately no $select: with one, Graph may leave out tasks whose change was
    /// to a property that wasn't selected.
    /// </summary>
    public async Task<DeltaPage> GetTaskDeltaAsync(string listId, string? deltaLink, bool expandPercent, CancellationToken ct)
    {
        List<TodoTaskDto> items;
        string nextLink;
        try
        {
            (items, nextLink) = await _graph.GetDeltaAsync<TodoTaskDto>(
                deltaLink ?? DeltaUrl(listId, expandPercent), $"odata.maxpagesize={PageSize}", ct);
        }
        catch (GraphRequestException ex) when (deltaLink is not null && RejectsDeltaLink(ex))
        {
            throw new DeltaResyncRequiredException(ex.Message, ex);
        }

        // A task can appear more than once in a round; the last entry is its latest state.
        var latest = new Dictionary<string, TodoTaskDto>(StringComparer.Ordinal);
        foreach (var item in items.Where(i => !string.IsNullOrEmpty(i.Id)))
        {
            latest.Remove(item.Id!);
            latest[item.Id!] = item;
        }

        var changed = latest.Values.Where(t => t.Removed is null).Select(TaskSnapshotFactory.FromTodoTask).ToList();
        var removed = latest.Values.Where(t => t.Removed is not null).Select(t => t.Id!).ToList();

        _logger.LogDebug("[Graph] Delta for list {ListId}: {Changed} changed, {Removed} removed ({Kind}).",
            listId, changed.Count, removed.Count, deltaLink is null ? "initial round" : "from the delta link");
        return new DeltaPage(changed, removed, nextLink, Initial: deltaLink is null);
    }

    /// <summary>
    /// 410 Gone (Graph wants a fresh start), or another client error for a stored link — e.g.
    /// syncStateNotFound once the token has been evicted. Auth and throttling errors aren't.
    /// </summary>
    internal static bool RejectsDeltaLink(GraphRequestException ex) =>
        ex.StatusCode == HttpStatusCode.Gone
        || ((int)ex.StatusCode is >= 400 and < 500
            && ex.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests));

    internal string DeltaUrl(string listId, bool expandPercent) =>
        $"{ListPath(listId)}/tasks/delta" +
        (expandPercent ? "?$expand=" + Uri.EscapeDataString(TaskProperties.ExpandClause) : "");

    public Task<IReadOnlyDictionary<string, string>> ToImmutableIdsAsync(IReadOnlyCollection<string> taskIds, CancellationToken ct) =>
        TranslateAsync(taskIds, "restId", "restImmutableEntryId", ct);

    public async Task<string?> FromImmutableIdAsync(string immutableId, CancellationToken ct) =>
        (await TranslateAsync(new[] { immutableId }, "restImmutableEntryId", "restId", ct))
        .GetValueOrDefault(immutableId);

    private async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyCollection<string> ids, string sourceIdType, string targetIdType, CancellationToken ct)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var batch in ids.Distinct(StringComparer.Ordinal).Chunk(MaxIdsPerTranslation))
        {
            var response = await _graph.PostAsync<GraphCollection<ConvertIdResultDto>>(
                $"users/{Uri.EscapeDataString(_mailboxUserId)}/translateExchangeIds",
                new { inputIds = batch, sourceIdType, targetIdType },
                ct);

            foreach (var result in response?.Value ?? [])
            {
                if (result.SourceId is not null && !string.IsNullOrEmpty(result.TargetId))
                {
                    results[result.SourceId] = result.TargetId;
                }
                else
                {
                    _logger.LogDebug("[Ids] {Source} → {Target} failed for {Id}: {Code} {Message}",
                        sourceIdType, targetIdType, result.SourceId, result.ErrorDetails?.Code, result.ErrorDetails?.Message);
                }
            }
        }

        return results;
    }

    internal string TaskUrl(string listId, string taskId) =>
        $"{ListPath(listId)}/tasks/{Uri.EscapeDataString(taskId)}?{TaskProperties.TaskQuery}";

    internal string TasksUrl(string listId) =>
        $"{ListPath(listId)}/tasks?{TaskProperties.TaskQuery}&$top={PageSize}";

    private string ListPath(string listId) =>
        $"users/{Uri.EscapeDataString(_mailboxUserId)}/todo/lists/{Uri.EscapeDataString(listId)}";
}
