using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace GraphTaskSynchronizer;

/// <summary>
/// Keeps the <see cref="TaskStore"/> replica in step with the mailbox's To Do lists.
///
/// Work items
/// ──────────
/// Change notifications, reconcile requests and list changes share one queue with a single
/// consumer, so the replica is only ever touched by one piece of work at a time.
///
///   created / updated  → GET the task in its list, with PidLidPercentComplete expanded
///                          found     → Added / Updated (field diff) / no change — or Moved, when
///                                      the replica already holds its immutable ID under another
///                                      To Do ID (it came from another list)
///                          404       → it left the list; handled as for deleted
///
///   deleted            → the same GET confirms it before anything is dropped:
///                          found     → stale or out-of-order delete; refreshed instead
///                          404       → the immutable ID no longer resolves → Removed; it still
///                                      resolves → the item exists somewhere else (another list,
///                                      or Deleted Items), so a reconcile decides Moved or Removed
///
///   reconcile          → a todoTask delta round per list, from the delta link stored with the
///                        replica: only what changed since the last reconcile comes back (or,
///                        with no usable link, a full round of every task). Add / update what
///                        came back, re-key what moved (matched by immutable ID), remove what
///                        was @removed or, in a full round, isn't there. Runs at startup, every
///                        Sync:ReconcileIntervalMinutes, when lists come or go, and whenever a
///                        notification can't be settled on its own; a full round on request (F)
///                        and every Sync:FullReconcileIntervalHours. A list whose round fails
///                        is left alone that time.
///
///   lists changed      → the set of To Do lists (GraphSubscriptionService's list reconcile, every
///                        minute by default): renamed lists are renamed in the replica; new and
///                        deleted lists are reconciled.
///
/// Immutable IDs
/// ─────────────
/// A To Do task gets a new ID when it moves to another list, and the To Do API can't return
/// immutable IDs, so each new task ID is translated with translateExchangeIds
/// (restId → restImmutableEntryId) and stored with the task. The immutable ID survives the
/// move — that is how a task that appears in one list and disappears from another is
/// reported as one Moved change rather than Removed + Added. Without the User.Read.All
/// application permission translation is refused; it is retried every 30 minutes and moves
/// show as Removed + Added meanwhile.
///
/// Ordering guard
/// ──────────────
/// A snapshot whose lastModifiedDateTime is older than the replica's copy is ignored rather
/// than rolling the task back.
/// </summary>
public sealed class TaskSyncEngine
{
    // Users/{id}/todo/lists/{listId}/tasks/{taskId}, or the ('…') key form.
    private static readonly Regex ResourcePath = new(
        @"lists(?:\('|/)(?<list>[^/'()?]+)'?\)?/tasks(?:\('|/)(?<task>[^/'()?]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>After translateExchangeIds is refused (401/403), how long before trying again.</summary>
    internal static readonly TimeSpan TranslationPause = TimeSpan.FromMinutes(30);

    private static readonly IReadOnlyDictionary<string, string> NoIds = new Dictionary<string, string>();

    private readonly ILogger<TaskSyncEngine> _logger;
    private readonly TaskSyncOptions _options;
    private readonly ITaskGraphGateway _gateway;
    private readonly TaskStore _store;
    private readonly IReadOnlyList<ITaskChangeSink> _sinks;
    private readonly TimeProvider _time;

    // The To Do lists being synchronized; replaced as a whole when they change.
    private volatile ListSet _lists;

    // Subscription ID → the list it watches (maintained by GraphSubscriptionService).
    private readonly ConcurrentDictionary<string, TaskList> _subscriptionLists =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Channel<WorkItem> _queue =
        Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,   // exactly one consumer
            SingleWriter = false   // webhook requests, lifecycle handling and timers all enqueue
        });

    private long _enqueueSeq;
    private int _reconcileQueued;
    private int _fullReconcileRequested;
    private DateTimeOffset _lastFullReconcile;
    private DateTimeOffset _translationPausedUntil = DateTimeOffset.MinValue;

    // Whether todoTask delta honours the $expand of PidLidPercentComplete: null until a round
    // shows it, true once a value came back, false when it doesn't (then PercentComplete is
    // read with a GET per changed task, and a listing for full rounds).
    private bool? _deltaCarriesPercent;

    public TaskSyncEngine(
        TaskSyncOptions options,
        ILoggerFactory loggerFactory,
        ITaskGraphGateway gateway,
        TaskStore store,
        IReadOnlyList<TaskList> lists,
        IReadOnlyList<ITaskChangeSink> sinks,
        TimeProvider? time = null)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger<TaskSyncEngine>();
        _gateway = gateway;
        _store = store;
        _lists = ListSet.From(lists);
        _sinks = sinks;
        _time = time ?? TimeProvider.System;
        _lastFullReconcile = _time.GetUtcNow();
    }

    private sealed record ListSet(IReadOnlyList<TaskList> Lists, IReadOnlyDictionary<string, TaskList> ById)
    {
        public static ListSet From(IEnumerable<TaskList> lists)
        {
            var ordered = lists.DistinctBy(l => l.Id).ToList();
            return new ListSet(ordered, ordered.ToDictionary(l => l.Id, StringComparer.Ordinal));
        }
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    // ──────────────────────────────────────────────────────────
    //  Producers (any thread)
    // ──────────────────────────────────────────────────────────

    public void Enqueue(ChangeNotification notification)
    {
        var seq = Interlocked.Increment(ref _enqueueSeq);

        _logger.LogInformation(
            "[Webhook] ENQUEUE #{Seq} changeType={ChangeType} resourceData.id='{ResId}' subscription={Sub}",
            seq, notification.ChangeType, notification.ResourceData?.Id ?? "(null)",
            notification.SubscriptionId ?? "(null)");

        if (!_queue.Writer.TryWrite(new NotificationWork(notification)))
            _logger.LogWarning("[Webhook] Queue closed – notification #{Seq} dropped.", seq);
    }

    /// <summary>
    /// Queues a reconcile: a delta round per list, or with <paramref name="full"/> a full round
    /// (every task of every list). Requests are coalesced: while one is waiting in the queue,
    /// further requests are absorbed by it (a full request makes the queued one full).
    /// </summary>
    public void RequestReconcile(string reason, bool full = false)
    {
        if (full)
            Interlocked.Exchange(ref _fullReconcileRequested, 1);

        if (Interlocked.Exchange(ref _reconcileQueued, 1) == 1)
        {
            _logger.LogInformation("[Sync] Reconcile requested ({Reason}) – one is already queued.", reason);
            return;
        }

        _logger.LogInformation("[Sync] {Kind} queued ({Reason}).", full ? "Full reconcile" : "Reconcile", reason);
        if (!_queue.Writer.TryWrite(new ReconcileWork(reason)))
            Interlocked.Exchange(ref _reconcileQueued, 0);
    }

    /// <summary>True while a reconcile request is waiting in the queue.</summary>
    internal bool IsReconcileQueued => Volatile.Read(ref _reconcileQueued) == 1;

    /// <summary>
    /// Reads the To Do lists again and then requests the reconcile (reason, full): set to
    /// <see cref="GraphSubscriptionService.RequestListReconcile"/>, which subscribes any new
    /// list and passes the lists to <see cref="SetLists"/> first. Used where a list created
    /// since the last check could be the answer — above all a task that left a list but still
    /// exists, which may have moved into a new list. Without it the reconcile is requested
    /// straight away.
    /// </summary>
    public Action<string, bool>? ListCheck { get; set; }

    /// <summary>A reconcile that first picks up lists created since the last list check (see <see cref="ListCheck"/>).</summary>
    public void RequestReconcileAfterListCheck(string reason, bool full = false)
    {
        if (ListCheck is { } check)
            check(reason, full);
        else
            RequestReconcile(reason, full);
    }

    /// <summary>
    /// The To Do lists to synchronize from now on (lists were created, deleted or renamed).
    /// Applied on the consumer, in order with the notifications around it.
    /// </summary>
    public void SetLists(IReadOnlyList<TaskList> lists, string reason)
    {
        if (!_queue.Writer.TryWrite(new ListsWork(lists, reason)))
            _logger.LogWarning("[Sync] Queue closed – list change ({Reason}) dropped.", reason);
    }

    // ──────────────────────────────────────────────────────────
    //  Read access to the replica (task page, L key)
    // ──────────────────────────────────────────────────────────

    /// <summary>The To Do lists being synchronized.</summary>
    public IReadOnlyList<TaskList> Lists => _lists.Lists;

    /// <summary>A snapshot of every task in the replica.</summary>
    public IReadOnlyList<TaskRecord> GetSyncedTasks() => _store.GetAll();

    /// <summary>Full path of the replica file, or null for an in-memory replica.</summary>
    public string? ReplicaPath => _store.FilePath;

    public void RegisterSubscription(string subscriptionId, TaskList list) =>
        _subscriptionLists[subscriptionId] = list;

    public void UnregisterSubscription(string subscriptionId) =>
        _subscriptionLists.TryRemove(subscriptionId, out _);

    // ──────────────────────────────────────────────────────────
    //  Single sequential consumer
    // ──────────────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Sync] Consumer started – notifications and reconciles are processed one at a time.");

        var periodic = _options.ReconcileInterval > TimeSpan.Zero
            ? RunPeriodicReconcileAsync(ct)
            : Task.CompletedTask;

        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    switch (item)
                    {
                        case NotificationWork work:
                            await ProcessNotificationAsync(work.Notification, ct);
                            break;
                        case ReconcileWork work:
                            await ReconcileAsync(work.Reason, ct);
                            break;
                        case ListsWork work:
                            ApplyLists(work.Lists, work.Reason);
                            break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Sync] Error processing a work item – continuing.");
                }
                finally
                {
                    _store.SaveIfDirty();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Sync] Consumer cancelled.");
        }
        finally
        {
            _queue.Writer.TryComplete();
            _store.SaveIfDirty();
        }

        await periodic;
        _logger.LogInformation("[Sync] Consumer stopped.");
    }

    private async Task RunPeriodicReconcileAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.ReconcileInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                RequestReconcileAfterListCheck("periodic");
        }
        catch (OperationCanceledException) { }
    }

    // ──────────────────────────────────────────────────────────
    //  Notifications
    // ──────────────────────────────────────────────────────────

    internal async Task ProcessNotificationAsync(ChangeNotification notification, CancellationToken ct)
    {
        var changeType = notification.ChangeType?.ToLowerInvariant() ?? string.Empty;
        var (list, taskId) = Locate(notification);

        if (string.IsNullOrEmpty(taskId))
        {
            _logger.LogWarning("[Process] '{ChangeType}' notification without a task ID – skipped.", changeType);
            return;
        }

        if (list is null)
        {
            _logger.LogWarning(
                "[Process] '{ChangeType}' notification for task '{TaskId}' in a list this instance does not " +
                "synchronize (resource '{Resource}') – skipped.", changeType, taskId, notification.Resource);
            return;
        }

        _logger.LogInformation("[Process] BEGIN changeType={ChangeType} key='{Key}' list='{List}'",
            changeType, taskId, list.DisplayName);

        switch (changeType)
        {
            case "created":
            case "updated":
                await RefreshAsync(list, taskId, $"notification: {changeType}", ct);
                break;

            case "deleted":
                await ConfirmDeleteAsync(list, taskId, ct);
                break;

            default:
                _logger.LogInformation("[Process] changeType={ChangeType} not handled – skipped.", changeType);
                break;
        }
    }

    /// <summary>
    /// Works out which list and task a notification is about: the subscription's list when
    /// the subscription is registered, otherwise the list ID in the resource path (e.g. a
    /// subscription left over from a previous run that is still delivering).
    /// </summary>
    private (TaskList? List, string? TaskId) Locate(ChangeNotification notification)
    {
        var match = notification.Resource is null ? null : ResourcePath.Match(notification.Resource);
        var taskId = notification.ResourceData?.Id;
        if (string.IsNullOrEmpty(taskId) && match is { Success: true })
            taskId = Uri.UnescapeDataString(match.Groups["task"].Value);

        if (!string.IsNullOrEmpty(notification.SubscriptionId)
            && _subscriptionLists.TryGetValue(notification.SubscriptionId, out var subscribed))
            return (subscribed, taskId);

        if (match is { Success: true }
            && _lists.ById.TryGetValue(Uri.UnescapeDataString(match.Groups["list"].Value), out var fromPath))
            return (fromPath, taskId);

        return (null, taskId);
    }

    private async Task RefreshAsync(TaskList list, string taskId, string trigger, CancellationToken ct)
    {
        var lookup = await _gateway.GetTaskAsync(list.Id, taskId, ct);
        switch (lookup.Status)
        {
            case TaskLookupStatus.Found:
                var snapshot = lookup.Snapshot!;
                _logger.LogInformation(
                    "[Process] key='{Key}' PercentComplete={Percent} ({Where}) Status={Status} LastModified={Modified}",
                    snapshot.Id,
                    TaskFormat.Percent(snapshot.PercentComplete),
                    snapshot.PercentComplete is null ? "PidLidPercentComplete not set on the item" : "from singleValueExtendedProperties",
                    TaskFormat.Status(snapshot.Status),
                    TaskFormat.Timestamp(snapshot.LastModifiedDateTime));

                await ApplyNotifiedAsync(snapshot, list, trigger, ct);
                break;

            case TaskLookupStatus.NotFound:
                await LeftListAsync(list, taskId, $"no longer in '{list.DisplayName}' (deleted)", ct);
                break;

            default:
                _logger.LogWarning("[Process] key='{Key}': GET failed ({Error}) – scheduling a reconcile.",
                    taskId, lookup.Error);
                RequestReconcile("a task GET failed");
                break;
        }
    }

    private async Task ConfirmDeleteAsync(TaskList list, string taskId, CancellationToken ct)
    {
        // Notifications can arrive out of order, so a delete is confirmed before anything is
        // dropped: the task must really be gone from the list.
        var lookup = await _gateway.GetTaskAsync(list.Id, taskId, ct);
        switch (lookup.Status)
        {
            case TaskLookupStatus.NotFound:
                await LeftListAsync(list, taskId, $"deleted from '{list.DisplayName}'", ct);
                break;

            case TaskLookupStatus.Found:
                _logger.LogInformation(
                    "[Process] deleted key='{Key}', but the task is still in '{List}' (stale or out-of-order " +
                    "notification) – refreshing instead.", taskId, list.DisplayName);
                await ApplyNotifiedAsync(lookup.Snapshot!, list, "notification: deleted (task still present)", ct);
                break;

            default:
                _logger.LogWarning(
                    "[Process] deleted key='{Key}': could not confirm with a GET ({Error}) – scheduling a reconcile.",
                    taskId, lookup.Error);
                RequestReconcile("unconfirmed delete notification");
                break;
        }
    }

    /// <summary>
    /// A task its list returned. If the replica doesn't know this To Do ID but does know its
    /// immutable ID, the task has moved here from another list.
    /// </summary>
    private async Task ApplyNotifiedAsync(TaskSnapshot snapshot, TaskList list, string trigger, CancellationToken ct)
    {
        var detail = DescribeFetch(snapshot);
        var existing = _store.Get(snapshot.Id);
        var immutableId = existing?.ImmutableId ?? await ToImmutableIdAsync(snapshot.Id, ct);

        if (existing is null && immutableId is not null
            && _store.FindByImmutableId(immutableId) is { } previous && previous.Id != snapshot.Id)
        {
            await MoveAsync(previous, snapshot, list, immutableId, SnapshotSource.Notification, trigger, detail, ct);
            return;
        }

        await ApplyAsync(snapshot, list, immutableId, SnapshotSource.Notification, trigger, detail, ct);
    }

    /// <summary>
    /// The task is no longer in this list: deleted, or moved to another list. If its
    /// immutable ID no longer resolves, it's gone. If it still resolves, the item exists
    /// somewhere — another list, or Deleted Items — and a full listing settles which: its new
    /// ID turns up in a list (Moved) or doesn't (Removed). The "created" notification from the
    /// other list may well have settled it already, in which case nothing is left to do here.
    /// </summary>
    private async Task LeftListAsync(TaskList list, string taskId, string reason, CancellationToken ct)
    {
        var existing = _store.Get(taskId);
        if (existing is null)
        {
            _logger.LogInformation("[Process] key='{Key}' not in the replica – nothing to remove ({Reason}).", taskId, reason);
            return;
        }

        if (existing.ImmutableId is { } immutableId)
        {
            var (known, currentId) = await ResolveImmutableIdAsync(immutableId, ct);
            var mayHaveMoved = known ? currentId is not null : !TranslationPaused;
            if (mayHaveMoved)
            {
                // It may have moved into a list created since the last list check, so the lists
                // are read again first: the reconcile then covers that list too and reports Moved.
                _logger.LogInformation(
                    "[Process] key='{Key}' left '{List}', but {Why} – a reconcile will tell whether it moved to another list.",
                    taskId, list.DisplayName,
                    known ? "its immutable ID still resolves to an item" : "its immutable ID couldn't be checked");
                RequestReconcileAfterListCheck($"'{TaskFormat.Text(existing.Snapshot.Subject)}' left '{list.DisplayName}'");
                return;
            }
        }

        await RemoveAsync(existing, reason, SnapshotSource.Notification, ct);
    }

    private static string DescribeFetch(TaskSnapshot snapshot) =>
        snapshot.PercentComplete is null
            ? "GET after notification — PidLidPercentComplete is not set on this item"
            : "GET after notification — PercentComplete from singleValueExtendedProperties";

    // ──────────────────────────────────────────────────────────
    //  Reconcile
    // ──────────────────────────────────────────────────────────

    /// <summary>What one list's round of a reconcile brought back.</summary>
    private sealed class ListRound(TaskList list)
    {
        public TaskList List { get; } = list;

        /// <summary>Every task in the list came back (an initial delta round), not only changes.</summary>
        public bool Full { get; set; }

        public List<TaskSnapshot> Tasks { get; set; } = new();
        public List<string> RemovedIds { get; } = new();
        public string? NextDeltaLink { get; set; }
        public string Detail { get; set; } = "delta query (reconcile)";
        public bool Failed { get; set; }

        /// <summary>
        /// False when part of the round has to wait for the next reconcile: the list's delta
        /// link isn't advanced, so the same changes come back then (applying them is idempotent).
        /// </summary>
        public bool Commit { get; set; } = true;
    }

    /// <summary>
    /// Brings the replica in line with the mailbox, list by list, with todoTask delta. From the
    /// list's stored delta link only what changed since the last reconcile comes back: tasks
    /// added or changed, and @removed for tasks that left the list. Without one — first run, a
    /// new list, a link Graph no longer accepts, a full reconcile asked for or due — an initial
    /// delta round returns every task, and anything else the replica has for the list has gone.
    /// The round's new delta link is stored with the replica once the round has been applied.
    /// </summary>
    internal async Task<ReconcileSummary> ReconcileAsync(string reason, CancellationToken ct, bool full = false)
    {
        // Allow the next request to queue: anything that asks for a reconcile from here on
        // may have seen state this pass has already read past.
        Interlocked.Exchange(ref _reconcileQueued, 0);
        full |= Interlocked.Exchange(ref _fullReconcileRequested, 0) == 1;
        if (!full && _options.FullReconcileInterval > TimeSpan.Zero
                  && Now - _lastFullReconcile >= _options.FullReconcileInterval)
        {
            full = true;
            reason += $"; full, as one is due every {_options.FullReconcileInterval.TotalHours:0.#} h";
        }

        var lists = _lists;
        var stopwatch = Stopwatch.StartNew();
        var summary = new ReconcileSummary { Reason = reason, Lists = lists.Lists };
        var trigger = $"reconcile ({reason})";

        _logger.LogInformation("[Reconcile] BEGIN ({Reason}) – {Count} list(s){Full}.",
            reason, lists.Lists.Count, full ? ", full rounds" : "");

        // 1. A delta round per list: from its delta link, or a full round where there's none.
        var rounds = new List<ListRound>();
        foreach (var list in lists.Lists)
            rounds.Add(await FetchRoundAsync(list, full ? null : _store.GetDeltaLink(list.Id), ct));

        // 2. PercentComplete straight from the delta, once it's clear the delta carries it.
        await EnsurePercentCompleteAsync(rounds, ct);

        foreach (var round in rounds)
            (round.Failed ? summary.FailedLists : round.Full ? summary.FullLists : summary.DeltaLists).Add(round.List.DisplayName);

        var ok = rounds.Where(r => !r.Failed).ToList();
        var anyFailed = ok.Count < rounds.Count;
        var returned = ok.SelectMany(r => r.Tasks.Select(t => (Snapshot: t, Round: r))).ToList();
        var returnedIds = returned.Select(x => x.Snapshot.Id).ToHashSet(StringComparer.Ordinal);

        // 3. Immutable IDs for every task ID the replica has none for: new tasks, tasks that
        //    moved, and replicas written before immutable IDs were kept. One call per 1,000.
        var translated = await ToImmutableIdsAsync(
            returnedIds.Where(id => _store.Get(id)?.ImmutableId is null).ToList(), ct);

        // 4. Moved (known immutable ID under a new To Do ID), added, updated.
        foreach (var (snapshot, round) in returned)
        {
            var existing = _store.Get(snapshot.Id);
            var immutableId = existing?.ImmutableId ?? translated.GetValueOrDefault(snapshot.Id);

            if (existing is null && immutableId is not null
                && _store.FindByImmutableId(immutableId) is { } previous
                && previous.Id != snapshot.Id
                && !returnedIds.Contains(previous.Id))
            {
                await MoveAsync(previous, snapshot, round.List, immutableId, SnapshotSource.Reconcile, trigger, round.Detail, ct);
                summary.Moved++;
                continue;
            }

            switch (await ApplyAsync(snapshot, round.List, immutableId, SnapshotSource.Reconcile, trigger, round.Detail, ct))
            {
                case ApplyOutcome.Added: summary.Added++; break;
                case ApplyOutcome.Updated: summary.Updated++; break;
                default: summary.Unchanged++; break;
            }
        }

        // 5. Tasks that left a list: @removed in a delta round, or not returned by a full one.
        //    If a list couldn't be read, one of them may have moved into that list – so they stay
        //    until the next reconcile, and the delta link isn't advanced so they come back then.
        foreach (var round in ok)
        {
            var gone = round.Full
                ? _store.GetAll().Where(r => r.ListId == round.List.Id && !returnedIds.Contains(r.Id)).ToList()
                : round.RemovedIds.Select(_store.Get).OfType<TaskRecord>()
                    .Where(r => r.ListId == round.List.Id && !returnedIds.Contains(r.Id)).ToList();
            if (gone.Count == 0)
                continue;

            if (anyFailed)
            {
                round.Commit = false;
                _logger.LogWarning(
                    "[Reconcile] {Count} task(s) left '{List}', but not every list could be read – they stay until the next reconcile.",
                    gone.Count, round.List.DisplayName);
                continue;
            }

            foreach (var record in gone)
            {
                await RemoveAsync(record,
                    round.Full
                        ? $"no longer in '{round.List.DisplayName}' (full reconcile)"
                        : $"removed from '{round.List.DisplayName}' (delta)",
                    SnapshotSource.Reconcile, ct);
                summary.Removed++;
            }
        }

        // 6. Entries for lists that were deleted or are no longer synchronized.
        foreach (var record in _store.GetAll().Where(r => !lists.ById.ContainsKey(r.ListId)))
        {
            await RemoveAsync(record, $"its list '{record.ListName ?? record.ListId}' was deleted or is no longer synchronized",
                SnapshotSource.Reconcile, ct);
            summary.Removed++;
        }

        // 7. Where each list's next reconcile starts.
        foreach (var round in ok.Where(r => r.Commit && r.NextDeltaLink is not null))
            _store.SetDeltaLink(round.List.Id, round.NextDeltaLink);
        _store.RetainDeltaLinks(lists.ById.Keys);
        if (!anyFailed && ok.All(r => r.Full))
            _lastFullReconcile = Now;

        summary.TotalTasks = _store.Count;
        summary.Duration = stopwatch.Elapsed;

        _logger.LogInformation(
            "[Reconcile] END ({Reason}) added={Added} updated={Updated} moved={Moved} removed={Removed} " +
            "unchanged={Unchanged} deltaLists={Delta} fullLists={FullLists} failedLists={Failed} total={Total}",
            reason, summary.Added, summary.Updated, summary.Moved, summary.Removed, summary.Unchanged,
            summary.DeltaLists.Count, summary.FullLists.Count, summary.FailedLists.Count, summary.TotalTasks);

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.OnReconcileCompletedAsync(summary, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[Sync] Sink {Sink} failed on reconcile summary.", sink.GetType().Name);
            }
        }

        return summary;
    }

    private async Task<ListRound> FetchRoundAsync(TaskList list, string? deltaLink, CancellationToken ct)
    {
        var round = new ListRound(list);
        try
        {
            var page = await DeltaAsync(list, deltaLink, ct);
            round.Full = page.Initial;
            round.Tasks = page.Changed.ToList();
            round.RemovedIds.AddRange(page.RemovedIds);
            round.NextDeltaLink = page.DeltaLink;
            round.Detail = page.Initial ? "full delta round (reconcile)" : "delta query (reconcile)";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "[Reconcile] The delta round for '{List}' failed – its replica entries are left untouched this time.",
                list.DisplayName);
            round.Failed = true;
        }

        return round;
    }

    /// <summary>
    /// A round from the delta link; if Graph no longer accepts it, or there is none, an initial
    /// round. If Graph refuses the $expand of PidLidPercentComplete on delta, the initial round
    /// goes without it (and <see cref="EnsurePercentCompleteAsync"/> reads the values instead).
    /// </summary>
    private async Task<DeltaPage> DeltaAsync(TaskList list, string? deltaLink, CancellationToken ct)
    {
        if (deltaLink is not null)
        {
            try
            {
                return await _gateway.GetTaskDeltaAsync(list.Id, deltaLink, _deltaCarriesPercent != false, ct);
            }
            catch (DeltaResyncRequiredException ex)
            {
                _logger.LogInformation(
                    "[Reconcile] Graph no longer accepts the delta link for '{List}' ({Message}) – running a full round.",
                    list.DisplayName, ex.Message);
            }
        }

        try
        {
            return await _gateway.GetTaskDeltaAsync(list.Id, null, _deltaCarriesPercent != false, ct);
        }
        catch (GraphRequestException ex) when (_deltaCarriesPercent != false && ex.StatusCode == HttpStatusCode.BadRequest)
        {
            _deltaCarriesPercent = false;
            _logger.LogWarning(
                "[Reconcile] Graph refused todoTask delta with the $expand of PidLidPercentComplete ({Message}). Using delta " +
                "without it; PercentComplete is read with a GET per changed task (a listing for full rounds).", ex.Message);
            return await _gateway.GetTaskDeltaAsync(list.Id, null, expandPercent: false, ct);
        }
    }

    /// <summary>
    /// The delta carries PercentComplete when Graph honours the $expand of PidLidPercentComplete.
    /// That counts as shown once a delta returns a value. Until then — and for good if it turns
    /// out not to — the values are read the way that is known to work: a listing for a full
    /// round, a GET per changed task otherwise. If those find values the delta didn't return,
    /// the delta ignores the $expand.
    /// </summary>
    private async Task EnsurePercentCompleteAsync(List<ListRound> rounds, CancellationToken ct)
    {
        var withTasks = rounds.Where(r => !r.Failed && r.Tasks.Count > 0).ToList();
        if (withTasks.Count == 0 || _deltaCarriesPercent == true)
            return;

        if (_deltaCarriesPercent is null && withTasks.Any(r => r.Tasks.Any(t => t.PercentComplete is not null)))
        {
            _deltaCarriesPercent = true;
            _logger.LogInformation("[Reconcile] todoTask delta returns PidLidPercentComplete – values are taken from the delta.");
            return;
        }

        var verifying = _deltaCarriesPercent is null;
        var foundPercent = false;

        foreach (var round in withTasks)
        {
            if (round.Full)
            {
                // The delta round gave the next link; the listing (no older than it) gives the values.
                try
                {
                    round.Tasks = (await _gateway.ListTasksAsync(round.List.Id, ct)).ToList();
                    round.Detail = "list listing (reconcile)";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "[Reconcile] Listing '{List}' failed – its replica entries are left untouched this time.",
                        round.List.DisplayName);
                    round.Failed = true;
                    continue;
                }
            }
            else
            {
                var fetched = new List<TaskSnapshot>();
                foreach (var task in round.Tasks)
                {
                    var lookup = await _gateway.GetTaskAsync(round.List.Id, task.Id, ct);
                    switch (lookup.Status)
                    {
                        case TaskLookupStatus.Found:
                            fetched.Add(lookup.Snapshot!);
                            break;
                        case TaskLookupStatus.NotFound:
                            break;                  // gone since the delta: the next round reports it removed
                        default:
                            round.Commit = false;   // read it again next time
                            break;
                    }
                }

                round.Tasks = fetched;
                round.Detail = "GET after delta query (reconcile)";
            }

            foundPercent |= round.Tasks.Any(t => t.PercentComplete is not null);
        }

        if (verifying && foundPercent)
        {
            _deltaCarriesPercent = false;
            _logger.LogWarning(
                "[Reconcile] todoTask delta didn't return PidLidPercentComplete for tasks that have it – it ignores the " +
                "$expand here. PercentComplete is read with a GET per changed task (a listing for full rounds).");
        }
    }

    // ──────────────────────────────────────────────────────────
    //  List changes
    // ──────────────────────────────────────────────────────────

    private void ApplyLists(IReadOnlyList<TaskList> lists, string reason)
    {
        var previous = _lists;
        var next = ListSet.From(lists);

        var added = next.Lists.Where(l => !previous.ById.ContainsKey(l.Id)).ToList();
        var removed = previous.Lists.Where(l => !next.ById.ContainsKey(l.Id)).ToList();
        var renamed = next.Lists
            .Where(l => previous.ById.TryGetValue(l.Id, out var before) && before.DisplayName != l.DisplayName)
            .ToList();

        _lists = next;

        foreach (var list in renamed)
        {
            var records = _store.GetAll().Where(r => r.ListId == list.Id).ToList();
            foreach (var record in records)
                _store.Upsert(record with { ListName = list.DisplayName });

            _logger.LogInformation("[Lists] '{Old}' is now '{New}' – {Count} task(s) relabelled.",
                previous.ById[list.Id].DisplayName, list.DisplayName, records.Count);
        }

        // New lists need listing, deleted ones dropping; the reconcile does both.
        if (added.Count > 0 || removed.Count > 0)
            RequestReconcile(reason);
    }

    // ──────────────────────────────────────────────────────────
    //  Immutable IDs
    // ──────────────────────────────────────────────────────────

    private bool TranslationPaused => Now < _translationPausedUntil;

    private async Task<string?> ToImmutableIdAsync(string taskId, CancellationToken ct) =>
        (await ToImmutableIdsAsync(new[] { taskId }, ct)).GetValueOrDefault(taskId);

    /// <summary>Immutable IDs for these To Do task IDs; whatever can't be translated is left out.</summary>
    private async Task<IReadOnlyDictionary<string, string>> ToImmutableIdsAsync(IReadOnlyList<string> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0 || TranslationPaused)
            return NoIds;

        try
        {
            var translated = await _gateway.ToImmutableIdsAsync(taskIds, ct);
            if (translated.Count < taskIds.Count)
            {
                _logger.Log(taskIds.Count > 1 ? LogLevel.Information : LogLevel.Debug,
                    "[Ids] {Missing} of {Total} task ID(s) could not be translated to immutable IDs; moving those " +
                    "tasks between lists shows as Removed + Added.", taskIds.Count - translated.Count, taskIds.Count);
            }
            return translated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NoteTranslationFailure(ex);
            return NoIds;
        }
    }

    /// <summary>
    /// The item's current To Do ID for an immutable ID. Known=false when that couldn't be
    /// asked; CurrentId=null when the item no longer exists.
    /// </summary>
    private async Task<(bool Known, string? CurrentId)> ResolveImmutableIdAsync(string immutableId, CancellationToken ct)
    {
        if (TranslationPaused)
            return (false, null);

        try
        {
            return (true, await _gateway.FromImmutableIdAsync(immutableId, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NoteTranslationFailure(ex);
            return (false, null);
        }
    }

    private void NoteTranslationFailure(Exception ex)
    {
        if (ex is GraphRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized })
        {
            _translationPausedUntil = Now + TranslationPause;
            _logger.LogWarning(
                "[Ids] translateExchangeIds was refused: {Message}. It needs the User.Read.All application permission " +
                "(with admin consent). Until then a task moved between lists shows as Removed + Added. Trying again " +
                "in {Minutes} minutes.", ex.Message, TranslationPause.TotalMinutes);
        }
        else
        {
            _logger.LogWarning("[Ids] translateExchangeIds failed: {Message} – immutable IDs are filled in on a later pass.",
                ex.Message);
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Replica updates
    // ──────────────────────────────────────────────────────────

    internal enum ApplyOutcome
    {
        Added,
        Updated,
        Unchanged,
        Stale
    }

    private async Task<ApplyOutcome> ApplyAsync(
        TaskSnapshot snapshot,
        TaskList list,
        string? immutableId,
        SnapshotSource source,
        string trigger,
        string sourceDetail,
        CancellationToken ct)
    {
        var existing = _store.Get(snapshot.Id);
        snapshot = KeepKnownNotes(snapshot, existing);

        if (existing?.Snapshot.LastModifiedDateTime is { } known
            && snapshot.LastModifiedDateTime is { } incoming
            && incoming < known)
        {
            _logger.LogInformation(
                "[Process] key='{Key}' snapshot is older than the replica ({Incoming:O} < {Known:O}) – ignored.",
                snapshot.Id, incoming, known);
            return ApplyOutcome.Stale;
        }

        var record = new TaskRecord
        {
            Snapshot = snapshot,
            ListId = list.Id,
            ListName = list.DisplayName,
            ImmutableId = immutableId ?? existing?.ImmutableId,
            PreviousListName = existing?.PreviousListName,
            MovedAt = existing?.MovedAt,
            SyncedAt = Now,
            Source = source
        };

        if (existing is null)
        {
            _store.Upsert(record);
            await EmitAsync(new TaskChange
            {
                Kind = TaskChangeKind.Added,
                TaskId = snapshot.Id,
                After = record,
                Source = source,
                SourceDetail = sourceDetail,
                Reason = trigger
            }, ct);
            return ApplyOutcome.Added;
        }

        var changes = TaskDiff.Compare(existing, record);

        // Store it even when nothing tracked changed, so the etag/LastModified stay current
        // (and an immutable ID found since is kept).
        _store.Upsert(record);

        if (changes.Count == 0)
        {
            _logger.Log(source == SnapshotSource.Reconcile ? LogLevel.Debug : LogLevel.Information,
                "[Process] key='{Key}' – no tracked field changed.", snapshot.Id);
            return ApplyOutcome.Unchanged;
        }

        await EmitAsync(new TaskChange
        {
            Kind = TaskChangeKind.Updated,
            TaskId = snapshot.Id,
            Before = existing,
            After = record,
            Changes = changes,
            Source = source,
            SourceDetail = sourceDetail,
            Reason = trigger
        }, ct);
        return ApplyOutcome.Updated;
    }

    /// <summary>Re-keys a task that turned up under a new To Do ID (it moved to another list).</summary>
    private async Task MoveAsync(
        TaskRecord previous,
        TaskSnapshot snapshot,
        TaskList list,
        string immutableId,
        SnapshotSource source,
        string trigger,
        string sourceDetail,
        CancellationToken ct)
    {
        var now = Now;
        var changedList = previous.ListId != list.Id;
        var record = new TaskRecord
        {
            Snapshot = KeepKnownNotes(snapshot, previous),
            ListId = list.Id,
            ListName = list.DisplayName,
            ImmutableId = immutableId,
            PreviousListName = changedList ? previous.ListName ?? previous.ListId : previous.PreviousListName,
            MovedAt = changedList ? now : previous.MovedAt,
            SyncedAt = now,
            Source = source
        };

        _store.Remove(previous.Id);
        _store.Upsert(record);

        await EmitAsync(new TaskChange
        {
            Kind = TaskChangeKind.Moved,
            TaskId = snapshot.Id,
            Before = previous,
            After = record,
            Changes = TaskDiff.Compare(previous, record),
            Source = source,
            SourceDetail = sourceDetail,
            Reason = trigger
        }, ct);
    }

    /// <summary>
    /// A snapshot that came back without a body (Notes null: not known) keeps the notes on
    /// record rather than forgetting them. Every read asks for the body, so this is a safeguard.
    /// </summary>
    private static TaskSnapshot KeepKnownNotes(TaskSnapshot snapshot, TaskRecord? existing) =>
        snapshot.Notes is null && existing?.Snapshot.Notes is { } notes
            ? snapshot with { Notes = notes }
            : snapshot;

    private async Task RemoveAsync(TaskRecord existing, string reason, SnapshotSource source, CancellationToken ct)
    {
        _store.Remove(existing.Id);
        await EmitAsync(new TaskChange
        {
            Kind = TaskChangeKind.Removed,
            TaskId = existing.Id,
            Before = existing,
            Source = source,
            Reason = reason
        }, ct);
    }

    private async Task EmitAsync(TaskChange change, CancellationToken ct)
    {
        _logger.Log(
            change.Source == SnapshotSource.Reconcile && change.Kind == TaskChangeKind.Added
                ? LogLevel.Debug
                : LogLevel.Information,
            "[Sync] {Kind} key='{Key}' {Changes}",
            change.Kind, change.TaskId,
            string.Join("; ", change.Changes.Select(c => $"{c.Field}: {c.Before} → {c.After}")));

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.OnTaskChangedAsync(change, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[Sync] Sink {Sink} failed for key='{Key}'.", sink.GetType().Name, change.TaskId);
            }
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Work items
    // ──────────────────────────────────────────────────────────

    private abstract record WorkItem;
    private sealed record NotificationWork(ChangeNotification Notification) : WorkItem;
    private sealed record ReconcileWork(string Reason) : WorkItem;
    private sealed record ListsWork(IReadOnlyList<TaskList> Lists, string Reason) : WorkItem;

    /// <summary>For tests: applies a list change as the consumer would.</summary>
    internal void ApplyListsNow(IReadOnlyList<TaskList> lists, string reason) => ApplyLists(lists, reason);
}
