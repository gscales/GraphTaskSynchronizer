using System.Net;
using Xunit;

namespace GraphTaskSynchronizer.Tests;

/// <summary>
/// Immutable IDs (translateExchangeIds) and recognising a task that moved between lists:
/// To Do gives it a new task ID in the new list, but its immutable ID stays the same.
/// </summary>
public sealed class MoveDetectionTests : IDisposable
{
    private const string Tasks = EngineHarness.ListId;
    private const string Groceries = EngineHarness.OtherListId;

    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-10-29T02:00:00Z"));
    private readonly EngineHarness _h;

    public MoveDetectionTests() => _h = new EngineHarness(bothLists: true, time: _time);

    public void Dispose() => _h.Dispose();

    private static GraphRequestException Forbidden() =>
        new(HttpStatusCode.Forbidden, "ErrorAccessDenied", "POST users/x/translateExchangeIds → 403 Forbidden");

    // ── storing the immutable ID ─────────────────────────────────────────────

    [Fact]
    public async Task A_new_task_is_stored_with_its_immutable_id()
    {
        await _h.Created("task-1", percent: 0.25);

        Assert.Equal("imm-task-1", _h.Store.Get("task-1")!.ImmutableId);
        Assert.Equal(new[] { "task-1" }, Assert.Single(_h.Gateway.ToImmutableCalls).ToArray());
        Assert.Equal("imm-task-1", _h.Sink.Changes[0].After!.ImmutableId);
    }

    [Fact]
    public async Task A_known_task_is_not_translated_again()
    {
        await _h.Created("task-1", percent: 0.25);
        _h.Put("task-1", percent: 0.5, lastModified: "2026-10-28T00:20:00Z");

        await _h.Process(_h.Notification("updated", "task-1"));

        Assert.Single(_h.Gateway.ToImmutableCalls);
        Assert.Equal("imm-task-1", _h.Store.Get("task-1")!.ImmutableId);
    }

    // ── moves seen through notifications ─────────────────────────────────────

    [Fact]
    public async Task A_move_is_one_change_when_the_new_list_is_notified_first()
    {
        await _h.Created("task-1", percent: 0.4);
        _h.Gateway.Move("task-1", Groceries, "task-1-in-groceries");

        await _h.Process(_h.GroceriesNotification("created", "task-1-in-groceries"));
        await _h.Process(_h.Notification("deleted", "task-1"));      // the old list's notification, later

        Assert.Equal(new[] { TaskChangeKind.Added, TaskChangeKind.Moved }, _h.Sink.Changes.Select(c => c.Kind).ToArray());
        var move = _h.Sink.Changes[1];
        Assert.Equal("task-1-in-groceries", move.TaskId);
        Assert.Equal("task-1", move.Before!.Id);
        Assert.Equal(Tasks, move.Before.ListId);
        Assert.Equal(Groceries, move.After!.ListId);
        var list = Assert.Single(move.Changes);
        Assert.Equal((TaskDiff.ListField, "Tasks", "Groceries"), (list.Field, list.Before, list.After));

        Assert.Null(_h.Store.Get("task-1"));
        var record = _h.Store.Get("task-1-in-groceries")!;
        Assert.Equal("imm-task-1", record.ImmutableId);
        Assert.Equal("Groceries", record.ListName);
        Assert.Equal("Tasks", record.PreviousListName);
        Assert.Equal(_time.Now, record.MovedAt);
        Assert.False(_h.Engine.IsReconcileQueued);
    }

    [Fact]
    public async Task A_move_is_one_change_when_the_old_list_is_notified_first()
    {
        await _h.Created("task-1", percent: 0.4);
        _h.Gateway.Move("task-1", Groceries, "task-1-in-groceries");

        // The task is gone from Tasks, but its immutable ID still resolves: it may have moved.
        await _h.Process(_h.Notification("deleted", "task-1"));
        Assert.Single(_h.Sink.Changes);
        Assert.NotNull(_h.Store.Get("task-1"));
        Assert.True(_h.Engine.IsReconcileQueued);

        // Whichever comes next settles it – here, the new list's notification.
        await _h.Process(_h.GroceriesNotification("created", "task-1-in-groceries"));
        var summary = await _h.Engine.ReconcileAsync("left a list", CancellationToken.None);

        Assert.Equal(new[] { TaskChangeKind.Added, TaskChangeKind.Moved }, _h.Sink.Changes.Select(c => c.Kind).ToArray());
        Assert.Equal(0, summary.Moved + summary.Removed + summary.Added);
        Assert.Equal("task-1-in-groceries", Assert.Single(_h.Store.GetAll()).Id);
    }

    [Fact]
    public async Task A_move_found_by_the_reconcile_after_the_old_list_was_notified()
    {
        await _h.Created("task-1", percent: 0.4);
        _h.Gateway.Move("task-1", Groceries, "task-1-in-groceries");

        await _h.Process(_h.Notification("deleted", "task-1"));
        var summary = await _h.Engine.ReconcileAsync("left a list", CancellationToken.None);

        Assert.Equal(1, summary.Moved);
        Assert.Equal(0, summary.Removed + summary.Added);
        var move = _h.Sink.Changes[^1];
        Assert.Equal(TaskChangeKind.Moved, move.Kind);
        Assert.Equal(SnapshotSource.Reconcile, move.Source);

        // The new list's notification arrives afterwards: nothing left to do.
        await _h.Process(_h.GroceriesNotification("created", "task-1-in-groceries"));
        Assert.Equal(2, _h.Sink.Changes.Count);
    }

    [Fact]
    public async Task A_task_deleted_into_Deleted_Items_is_removed_by_the_reconcile()
    {
        await _h.Created("task-1", percent: 0.4);
        _h.Gateway.Delete("task-1", toDeletedItems: true);    // still resolves, but in no list

        await _h.Process(_h.Notification("deleted", "task-1"));
        Assert.True(_h.Engine.IsReconcileQueued);
        var summary = await _h.Engine.ReconcileAsync("left a list", CancellationToken.None);

        Assert.Equal(1, summary.Removed);
        Assert.Equal(TaskChangeKind.Removed, _h.Sink.Changes[^1].Kind);
        Assert.Empty(_h.Store.GetAll());
    }

    [Fact]
    public async Task A_task_that_no_longer_exists_is_removed_straight_away()
    {
        await _h.Created("task-1", percent: 0.4);
        _h.Gateway.Delete("task-1");

        await _h.Process(_h.Notification("deleted", "task-1"));

        Assert.Equal(TaskChangeKind.Removed, _h.Sink.Changes[^1].Kind);
        Assert.Equal("imm-task-1", Assert.Single(_h.Gateway.FromImmutableCalls));
        Assert.False(_h.Engine.IsReconcileQueued);
    }

    // ── moves seen by a reconcile ────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_reports_a_move_between_lists()
    {
        await _h.Created("task-1", percent: 0.4);
        _h.Gateway.Move("task-1", Groceries, "task-1-in-groceries");   // no notifications seen

        var summary = await _h.Engine.ReconcileAsync("periodic", CancellationToken.None);

        Assert.Equal(1, summary.Moved);
        Assert.Equal(0, summary.Added + summary.Removed);
        Assert.Equal("Groceries", Assert.Single(_h.Store.GetAll()).ListName);
    }

    [Fact]
    public async Task Reconcile_fills_in_missing_immutable_ids_without_reporting_changes()
    {
        // A replica written before immutable IDs were kept.
        _h.Put("task-1", percent: 0.5);
        _h.Store.Upsert(new TaskRecord
        {
            Snapshot = _h.Gateway.Items["task-1"].Task,
            ListId = Tasks,
            ListName = "Tasks"
        });

        var summary = await _h.Engine.ReconcileAsync("startup", CancellationToken.None);

        Assert.Equal("imm-task-1", _h.Store.Get("task-1")!.ImmutableId);
        Assert.Empty(_h.Sink.Changes);
        Assert.Equal(1, summary.Unchanged);
        Assert.Equal(new[] { "task-1" }, Assert.Single(_h.Gateway.ToImmutableCalls).ToArray());
    }

    [Fact]
    public async Task Reconcile_translates_all_unknown_ids_in_one_call()
    {
        _h.Put("a", percent: 0.1);
        _h.Put("b", percent: 0.2);
        _h.Gateway.Put(Groceries, Snapshots.Task("c", "2026-10-28T00:10:00Z", 0.3));

        await _h.Engine.ReconcileAsync("startup", CancellationToken.None);

        Assert.Equal(new[] { "a", "b", "c" }, Assert.Single(_h.Gateway.ToImmutableCalls).OrderBy(x => x).ToArray());
        Assert.True(_h.Store.GetAll().All(r => r.ImmutableId is not null));
    }

    // ── without translation ──────────────────────────────────────────────────

    [Fact]
    public async Task Without_translation_a_move_shows_as_removed_and_added()
    {
        _h.Gateway.TranslationFailure = Forbidden();

        await _h.Created("task-1", percent: 0.4);
        Assert.Null(_h.Store.Get("task-1")!.ImmutableId);

        _h.Gateway.Move("task-1", Groceries, "task-1-in-groceries");
        await _h.Process(_h.Notification("deleted", "task-1"));
        await _h.Process(_h.GroceriesNotification("created", "task-1-in-groceries"));

        Assert.Equal(new[] { TaskChangeKind.Added, TaskChangeKind.Removed, TaskChangeKind.Added },
            _h.Sink.Changes.Select(c => c.Kind).ToArray());
        // Refused once, then paused rather than asked again for every task.
        Assert.Single(_h.Gateway.ToImmutableCalls);
    }

    [Fact]
    public async Task Translation_is_tried_again_after_the_pause()
    {
        _h.Gateway.TranslationFailure = Forbidden();
        await _h.Created("task-1", percent: 0.4);

        _h.Gateway.TranslationFailure = null;
        _time.Advance(TaskSyncEngine.TranslationPause + TimeSpan.FromMinutes(1));
        await _h.Engine.ReconcileAsync("periodic", CancellationToken.None);

        Assert.Equal(2, _h.Gateway.ToImmutableCalls.Count);
        Assert.Equal("imm-task-1", _h.Store.Get("task-1")!.ImmutableId);
    }

    [Fact]
    public async Task If_the_old_task_cannot_be_looked_up_it_is_kept_and_a_reconcile_decides()
    {
        await _h.Created("task-1", percent: 0.4);
        _h.Gateway.Delete("task-1");
        _h.Gateway.TranslationFailure = new HttpRequestException("network down");

        await _h.Process(_h.Notification("deleted", "task-1"));

        Assert.NotNull(_h.Store.Get("task-1"));
        Assert.True(_h.Engine.IsReconcileQueued);

        _h.Gateway.TranslationFailure = null;
        await _h.Engine.ReconcileAsync("left a list", CancellationToken.None);
        Assert.Null(_h.Store.Get("task-1"));
    }

    // ── the lists themselves ─────────────────────────────────────────────────

    [Fact]
    public async Task A_renamed_list_relabels_its_tasks_quietly()
    {
        await _h.Created("task-1", percent: 0.4);

        _h.Engine.ApplyListsNow(new[] { EngineHarness.TasksList with { DisplayName = "Work" }, EngineHarness.GroceriesList }, "renamed");

        Assert.Equal("Work", _h.Store.Get("task-1")!.ListName);
        Assert.Single(_h.Sink.Changes);
        Assert.False(_h.Engine.IsReconcileQueued);
        Assert.Equal("Work", _h.Engine.Lists[0].DisplayName);
    }

    [Fact]
    public async Task A_deleted_list_takes_its_tasks_with_it_at_the_next_reconcile()
    {
        await _h.Created("task-1", percent: 0.4);

        _h.Engine.ApplyListsNow(new[] { EngineHarness.GroceriesList }, "Tasks deleted");
        Assert.True(_h.Engine.IsReconcileQueued);

        var summary = await _h.Engine.ReconcileAsync("Tasks deleted", CancellationToken.None);

        Assert.Equal(1, summary.Removed);
        Assert.Contains("was deleted or is no longer synchronized", _h.Sink.Changes[^1].Reason);
    }

    [Fact]
    public async Task A_new_list_is_reconciled_and_its_notifications_understood()
    {
        using var single = new EngineHarness();
        single.Gateway.Put(Groceries, Snapshots.Task("milk", "2026-10-28T00:10:00Z", null, TodoTaskStatus.NotStarted, "Milk"));

        single.Engine.ApplyListsNow(new[] { EngineHarness.TasksList, EngineHarness.GroceriesList }, "Groceries created");
        Assert.True(single.Engine.IsReconcileQueued);

        await single.Process(single.Notification("created", "milk", subscriptionId: "not-registered-yet",
            resource: $"Users/x/todo/lists/{Groceries}/tasks/milk"));

        Assert.Equal("Groceries", Assert.Single(single.Sink.Changes).After!.ListName);
    }
}
