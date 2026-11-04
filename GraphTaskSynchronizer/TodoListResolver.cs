using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GraphTaskSynchronizer;

/// <summary>
/// Works out which To Do lists to synchronize from GET users/{id}/todo/lists.
///
/// By default that is every list in the mailbox: a move between lists can only be
/// recognised when both lists are watched. Tasks:Lists restricts it; each entry is one of:
///   "default"        the defaultList (the mailbox's Tasks folder)
///   a list ID        matched exactly
///   a display name   matched case-insensitively
///
/// Called at startup (which logs every available list, so names and IDs are easy to copy)
/// and afterwards by <see cref="GraphSubscriptionService"/>'s list reconcile, quietly, to pick
/// up lists that were created, deleted or renamed. Graph has no change notifications for the
/// lists themselves, so the list reconcile asks todoTaskList delta
/// (<see cref="GetListChangesAsync"/>) every minute whether anything changed, and only then
/// reads the lists in full.
/// </summary>
public sealed class TodoListResolver
{
    private readonly IConfiguration _config;
    private readonly ILogger<TodoListResolver> _logger;
    private readonly GraphRestClient _graph;

    public TodoListResolver(IConfiguration config, ILoggerFactory loggerFactory, GraphRestClient graph)
    {
        _config = config;
        _logger = loggerFactory.CreateLogger<TodoListResolver>();
        _graph = graph;
    }

    /// <summary>The lists to synchronize, or null when the lists couldn't be read.</summary>
    public async Task<IReadOnlyList<TaskList>?> ResolveAsync(string mailboxUserId, CancellationToken ct, bool quiet = false)
    {
        List<TodoTaskListDto> available;
        try
        {
            available = await _graph.GetAllPagesAsync<TodoTaskListDto>(
                $"users/{Uri.EscapeDataString(mailboxUserId)}/todo/lists", ct);
        }
        catch (GraphRequestException ex)
        {
            _logger.Log(quiet ? LogLevel.Warning : LogLevel.Error,
                "[Lists] Could not read the To Do lists for {Mailbox}: {Message}. The app registration needs the " +
                "Tasks.ReadWrite.All application permission (with admin consent).", mailboxUserId, ex.Message);
            return null;
        }
        catch (Exception ex) when (quiet && ex is not OperationCanceledException)
        {
            _logger.LogWarning("[Lists] Could not read the To Do lists for {Mailbox}: {Message}", mailboxUserId, ex.Message);
            return null;
        }

        if (!quiet)
        {
            foreach (var list in available)
            {
                _logger.LogInformation("[Lists] Available: '{Name}' [{Wellknown}] id={Id}",
                    list.DisplayName, list.WellknownListName ?? "none", list.Id);
            }
        }

        var entries = _config.GetSection("Tasks:Lists").GetChildren()
            .Select(c => c.Value?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToList();

        if (entries.Count == 0)
        {
            var all = available
                .Where(l => !string.IsNullOrEmpty(l.Id))
                .Select(l => new TaskList(l.Id!, l.DisplayName ?? l.Id!, l.WellknownListName, "all lists"))
                .ToList();

            if (!quiet)
                _logger.LogInformation("[Lists] Synchronizing all {Count} list(s).", all.Count);
            return all;
        }

        var lists = new List<TaskList>();
        foreach (var entry in entries)
        {
            var (match, how) = Find(available, entry);
            if (match?.Id is null)
            {
                if (!quiet)
                    _logger.LogError("[Lists] No To Do list matches Tasks:Lists entry '{Entry}' – skipped.", entry);
                continue;
            }

            if (lists.Any(l => l.Id == match.Id))
                continue;

            var list = new TaskList(match.Id, match.DisplayName ?? match.Id, match.WellknownListName, how);
            lists.Add(list);
            if (!quiet)
                _logger.LogInformation("[Lists] Synchronizing '{Name}' ({How}).", list.DisplayName, how);
        }

        if (!quiet && lists.Count < available.Count)
        {
            _logger.LogInformation(
                "[Lists] Tasks:Lists leaves out {Count} list(s): a task moved to one of them shows as Removed, and one " +
                "moved from one of them as Added.", available.Count - lists.Count);
        }

        return lists;
    }

    /// <summary>
    /// One round of todoTaskList delta:
    ///
    ///   GET users/{id}/todo/lists/delta              (no link yet: every list, plus the first link)
    ///   GET {the @odata.deltaLink of the last round}  (afterwards: only lists added, changed or removed)
    ///
    /// paged to the round's @odata.deltaLink. Returns how many lists came back and the link for
    /// the next round. A link Graph no longer accepts (410 Gone, syncStateNotFound) throws
    /// <see cref="DeltaResyncRequiredException"/>.
    /// </summary>
    public async Task<(int Changes, string DeltaLink)> GetListChangesAsync(string mailboxUserId, string? deltaLink, CancellationToken ct)
    {
        try
        {
            var (lists, next) = await _graph.GetDeltaAsync<TodoTaskListDto>(
                deltaLink ?? $"users/{Uri.EscapeDataString(mailboxUserId)}/todo/lists/delta", prefer: null, ct);
            return (lists.Count, next);
        }
        catch (GraphRequestException ex) when (deltaLink is not null && TodoGateway.RejectsDeltaLink(ex))
        {
            throw new DeltaResyncRequiredException(ex.Message, ex);
        }
    }

    private static (TodoTaskListDto? Match, string How) Find(List<TodoTaskListDto> available, string entry)
    {
        if (entry.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return (available.FirstOrDefault(l =>
                    string.Equals(l.WellknownListName, "defaultList", StringComparison.OrdinalIgnoreCase)),
                "default list — the mailbox's Tasks folder");
        }

        var byId = available.FirstOrDefault(l => l.Id == entry);
        if (byId is not null)
            return (byId, "configured by ID");

        return (available.FirstOrDefault(l =>
                string.Equals(l.DisplayName, entry, StringComparison.OrdinalIgnoreCase)),
            "configured by name");
    }
}
