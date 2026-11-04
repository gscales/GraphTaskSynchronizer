using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraphTaskSynchronizer.Tests;

/// <summary>
/// Reconciles through todoTask delta: a full round when a list has no delta link, otherwise
/// only what changed since the last one.
/// </summary>
public sealed class DeltaReconcileTests : IDisposable
{
    private const string Tasks = EngineHarness.ListId;
    private const string Groceries = EngineHarness.OtherListId;

    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-11-03T05:00:00Z"));
    private readonly EngineHarness _h;

    public DeltaReconcileTests() => _h = new EngineHarness(bothLists: true, time: _time);

    public void Dispose() => _h.Dispose();

    private void PutIn(string listId, string id, double? percent, string modified = "2026-11-03T01:00:00Z") =>
        _h.Gateway.Put(listId, Snapshots.Task(id, modified, percent, subject: "Task " + id));

    private Task<ReconcileSummary> Reconcile(bool full = false) =>
        _h.Engine.ReconcileAsync("test", CancellationToken.None, full);

    private (string ListId, string? DeltaLink)[] DeltaCallsSince(int count) => _h.Gateway.DeltaCalls.Skip(count).ToArray();

    private static void AllMatch<T>(IEnumerable<T> items, Func<T, bool> condition) =>
        Assert.True(items.All(condition), "not every item satisfies the condition");

    // ── rounds ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_first_reconcile_is_a_full_round_and_keeps_a_delta_link_per_list()
    {
        PutIn(Tasks, "a", 0.25);
        PutIn(Groceries, "b", 0.5);

        var summary = await Reconcile();

        Assert.Equal(2, summary.Added);
        Assert.Equal(new[] { "Tasks", "Groceries" }, summary.FullLists.ToArray());
        AllMatch(_h.Gateway.DeltaCalls, c => c.DeltaLink is null);
        Assert.NotNull(_h.Store.GetDeltaLink(Tasks));
        Assert.NotNull(_h.Store.GetDeltaLink(Groceries));
    }

    [Fact]
    public async Task The_next_reconcile_starts_from_the_link_and_sees_only_what_changed()
    {
        PutIn(Tasks, "a", 0.25);
        PutIn(Tasks, "b", 0.5);
        await Reconcile();
        var tasksLink = _h.Store.GetDeltaLink(Tasks);
        var calls = _h.Gateway.DeltaCalls.Count;

        PutIn(Tasks, "a", 0.75, modified: "2026-11-03T02:00:00Z");   // a missed notification
        var summary = await Reconcile();

        Assert.Equal(new[] { "Tasks", "Groceries" }, summary.DeltaLists.ToArray());
        Assert.Equal((Tasks, tasksLink), DeltaCallsSince(calls)[0]);
        Assert.Equal(1, summary.Updated);
        Assert.Equal(0, summary.Unchanged);                         // b didn't come back at all
        Assert.Equal(0.75, _h.Store.Get("a")!.Snapshot.PercentComplete);
        Assert.NotEqual(tasksLink, _h.Store.GetDeltaLink(Tasks));    // moved on
    }

    [Fact]
    public async Task A_task_deleted_between_reconciles_is_removed_by_the_delta()
    {
        PutIn(Tasks, "a", 0.25);
        await Reconcile();

        _h.Gateway.Delete("a");
        var summary = await Reconcile();

        Assert.Equal(1, summary.Removed);
        Assert.Contains("(delta)", _h.Sink.Changes[^1].Reason);
        Assert.Empty(_h.Store.GetAll());
    }

    [Fact]
    public async Task A_move_between_reconciles_is_one_Moved_change()
    {
        PutIn(Tasks, "a", 0.25);
        await Reconcile();

        _h.Gateway.Move("a", Groceries, "a-in-groceries");   // Tasks reports @removed, Groceries the new ID
        var summary = await Reconcile();

        Assert.Equal(1, summary.Moved);
        Assert.Equal(0, summary.Added + summary.Removed);
        var move = _h.Sink.Changes[^1];
        Assert.Equal(TaskChangeKind.Moved, move.Kind);
        Assert.Equal("delta query (reconcile)", move.SourceDetail);
        Assert.Equal("Groceries", Assert.Single(_h.Store.GetAll()).ListName);
    }

    [Fact]
    public async Task Changes_a_notification_already_brought_in_are_not_reported_again()
    {
        PutIn(Tasks, "a", 0.25);
        await Reconcile();

        _h.Put("a", percent: 0.5, lastModified: "2026-11-03T02:00:00Z");
        await _h.Process(_h.Notification("updated", "a"));
        var changes = _h.Sink.Changes.Count;
        var summary = await Reconcile();

        Assert.Equal(1, summary.Unchanged);
        Assert.Equal(changes, _h.Sink.Changes.Count);
    }

    // ── when a link can't be used ────────────────────────────────────────────

    [Fact]
    public async Task A_link_Graph_no_longer_accepts_is_replaced_by_a_full_round()
    {
        PutIn(Tasks, "a", 0.25);
        PutIn(Tasks, "b", 0.5);
        await Reconcile();
        var expired = _h.Store.GetDeltaLink(Tasks)!;
        _h.Gateway.ExpiredDeltaLinks.Add(expired);
        _h.Gateway.Items.Remove("b");   // gone while the link was useless

        var summary = await Reconcile();

        Assert.Contains("Tasks", summary.FullLists);
        Assert.Contains("Groceries", summary.DeltaLists);
        Assert.Equal(1, summary.Removed);   // full round: b simply isn't there
        Assert.Contains("(full reconcile)", _h.Sink.Changes[^1].Reason);
        Assert.NotEqual(expired, _h.Store.GetDeltaLink(Tasks));
    }

    [Fact]
    public async Task The_delta_links_survive_a_restart()
    {
        PutIn(Tasks, "a", 0.25);
        await Reconcile();
        _h.Store.SaveIfDirty();

        var reloaded = new TaskStore(_h.Store.FilePath, EngineHarness.Mailbox, NullLoggerFactory.Instance);
        var restarted = new TaskSyncEngine(new TaskSyncOptions { ReconcileInterval = TimeSpan.Zero }, NullLoggerFactory.Instance,
            _h.Gateway, reloaded, new[] { EngineHarness.TasksList, EngineHarness.GroceriesList }, Array.Empty<ITaskChangeSink>(), _time);
        var calls = _h.Gateway.DeltaCalls.Count;

        var summary = await restarted.ReconcileAsync("startup", CancellationToken.None);

        Assert.Equal(new[] { "Tasks", "Groceries" }, summary.DeltaLists.ToArray());
        AllMatch(DeltaCallsSince(calls), c => c.DeltaLink is not null);
    }

    [Fact]
    public async Task While_a_list_cannot_be_read_tasks_that_left_other_lists_wait()
    {
        PutIn(Tasks, "a", 0.25);
        await Reconcile();
        var tasksLink = _h.Store.GetDeltaLink(Tasks);

        // Moved into Groceries – whose round fails this time.
        _h.Gateway.Move("a", Groceries, "a-in-groceries");
        _h.Gateway.FailingDeltaLists.Add(Groceries);
        var first = await Reconcile();

        Assert.Equal("Groceries", Assert.Single(first.FailedLists));
        Assert.Equal(0, first.Removed);
        Assert.NotNull(_h.Store.Get("a"));                    // kept: it may have moved into Groceries
        Assert.Equal(tasksLink, _h.Store.GetDeltaLink(Tasks)); // not advanced, so the removal comes back

        _h.Gateway.FailingDeltaLists.Clear();
        var second = await Reconcile();

        Assert.Equal(1, second.Moved);
        Assert.Equal(0, second.Removed + second.Added);
    }

    // ── full rounds ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_full_reconcile_can_be_asked_for()
    {
        PutIn(Tasks, "a", 0.25);
        await Reconcile();
        var calls = _h.Gateway.DeltaCalls.Count;

        _h.Engine.RequestReconcile("F key", full: true);
        var summary = await Reconcile();

        Assert.Equal(new[] { "Tasks", "Groceries" }, summary.FullLists.ToArray());
        AllMatch(DeltaCallsSince(calls), c => c.DeltaLink is null);
    }

    [Fact]
    public async Task A_full_round_runs_once_its_interval_is_up()
    {
        using var h = new EngineHarness(new TaskSyncOptions { ReconcileInterval = TimeSpan.Zero, FullReconcileInterval = TimeSpan.FromHours(24) },
            bothLists: true, time: _time);
        h.Gateway.Put(Tasks, Snapshots.Task("a", "2026-11-03T01:00:00Z", 0.25));
        await h.Engine.ReconcileAsync("startup", CancellationToken.None);   // full: no links yet

        _time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(2, (await h.Engine.ReconcileAsync("periodic", CancellationToken.None)).DeltaLists.Count);

        _time.Advance(TimeSpan.FromHours(2));
        var due = await h.Engine.ReconcileAsync("periodic", CancellationToken.None);
        Assert.Equal(2, due.FullLists.Count);
        Assert.Contains("full", due.Reason);
    }

    [Fact]
    public async Task A_deleted_list_loses_its_delta_link()
    {
        PutIn(Groceries, "b", 0.5);
        await Reconcile();
        Assert.NotNull(_h.Store.GetDeltaLink(Groceries));

        _h.Engine.ApplyListsNow(new[] { EngineHarness.TasksList }, "Groceries deleted");
        await Reconcile();

        Assert.Null(_h.Store.GetDeltaLink(Groceries));
        Assert.Empty(_h.Store.GetAll());
    }

    // ── PercentComplete in the delta ─────────────────────────────────────────

    [Fact]
    public async Task Once_the_delta_has_shown_PercentComplete_it_is_taken_from_the_delta()
    {
        PutIn(Tasks, "a", 0.25);
        await Reconcile();
        PutIn(Tasks, "a", 0.75, modified: "2026-11-03T02:00:00Z");

        await Reconcile();

        Assert.Equal(0.75, _h.Store.Get("a")!.Snapshot.PercentComplete);
        Assert.Empty(_h.Gateway.GetCalls);    // no GET per task
        Assert.Empty(_h.Gateway.ListCalls);   // no listing
    }

    [Fact]
    public async Task If_the_delta_ignores_the_expand_PercentComplete_is_read_by_listing_and_GET()
    {
        _h.Gateway.DeltaIgnoresExpand = true;
        PutIn(Tasks, "a", 0.4);
        PutIn(Tasks, "b", 0.6);

        await Reconcile();                    // full round: values from a listing instead
        Assert.Equal(0.4, _h.Store.Get("a")!.Snapshot.PercentComplete);
        Assert.Contains(Tasks, _h.Gateway.ListCalls);

        PutIn(Tasks, "a", 0.9, modified: "2026-11-03T02:00:00Z");
        await Reconcile();                    // delta round: a GET for the changed task

        Assert.Equal((Tasks, "a"), Assert.Single(_h.Gateway.GetCalls));
        Assert.Equal(0.9, _h.Store.Get("a")!.Snapshot.PercentComplete);
        Assert.DoesNotContain(_h.Sink.Changes, c => c.After is { } after && after.Snapshot.PercentComplete is null);
    }

    [Fact]
    public async Task If_the_delta_refuses_the_expand_it_runs_without_it()
    {
        _h.Gateway.DeltaRejectsExpand = true;
        PutIn(Tasks, "a", 0.4);

        var summary = await Reconcile();

        Assert.Empty(summary.FailedLists);
        Assert.Equal(0.4, _h.Store.Get("a")!.Snapshot.PercentComplete);
        Assert.NotNull(_h.Store.GetDeltaLink(Tasks));
    }

    [Fact]
    public async Task Tasks_without_PercentComplete_are_never_reported_as_losing_it()
    {
        PutIn(Tasks, "a", null);
        await Reconcile();
        PutIn(Tasks, "a", null, modified: "2026-11-03T02:00:00Z");

        await Reconcile();

        Assert.Single(_h.Sink.Changes);       // just the Added
        Assert.Null(_h.Store.Get("a")!.Snapshot.PercentComplete);
    }
}
