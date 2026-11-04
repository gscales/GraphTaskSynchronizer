using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraphTaskSynchronizer.Tests;

public sealed class TaskSyncEngineTests : IDisposable
{
    private const string ListId = EngineHarness.ListId;

    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ── created / updated ────────────────────────────────────────────────────

    [Fact]
    public async Task Created_GETs_the_task_and_adds_it_with_PercentComplete()
    {
        await _h.Created("task-1", percent: 0.25);

        var change = Assert.Single(_h.Sink.Changes);
        Assert.Equal(TaskChangeKind.Added, change.Kind);
        Assert.Equal(SnapshotSource.Notification, change.Source);
        Assert.Equal(0.25, change.After!.Snapshot.PercentComplete);
        Assert.Equal(ListId, change.After.ListId);
        Assert.Contains("singleValueExtendedProperties", change.SourceDetail);
        Assert.Equal((ListId, "task-1"), Assert.Single(_h.Gateway.GetCalls));
    }

    [Fact]
    public async Task Updated_reports_the_PercentComplete_change()
    {
        await _h.Created("task-1", percent: 0.25);

        _h.Put("task-1", percent: 0.75, lastModified: "2026-10-28T00:20:00Z");
        await _h.Process(_h.Notification("updated", "task-1"));

        Assert.Equal(2, _h.Sink.Changes.Count);
        var update = _h.Sink.Changes[1];
        Assert.Equal(TaskChangeKind.Updated, update.Kind);

        var field = Assert.Single(update.Changes);
        Assert.Equal(TaskDiff.PercentCompleteField, field.Field);
        Assert.Equal("25%", field.Before);
        Assert.Equal("75%", field.After);
        Assert.Equal(0.75, _h.Store.Get("task-1")!.Snapshot.PercentComplete);
    }

    [Fact]
    public async Task Update_with_no_tracked_change_is_stored_but_not_reported()
    {
        await _h.Created("task-1", percent: 0.25);

        _h.Put("task-1", percent: 0.25, lastModified: "2026-10-28T00:20:00Z");
        await _h.Process(_h.Notification("updated", "task-1"));

        Assert.Single(_h.Sink.Changes);
        Assert.Equal(DateTimeOffset.Parse("2026-10-28T00:20:00Z"), _h.Store.Get("task-1")!.Snapshot.LastModifiedDateTime);
    }

    [Fact]
    public async Task A_task_without_PercentComplete_is_added_and_says_so()
    {
        await _h.Created("task-1", percent: null);

        var change = Assert.Single(_h.Sink.Changes);
        Assert.Null(change.After!.Snapshot.PercentComplete);
        Assert.Contains("not set", change.SourceDetail);
    }

    [Fact]
    public async Task Created_for_a_task_that_is_already_gone_adds_nothing()
    {
        await _h.Process(_h.Notification("created", "task-1"));

        Assert.Empty(_h.Sink.Changes);
        Assert.Null(_h.Store.Get("task-1"));
    }

    [Fact]
    public async Task Updated_for_a_task_that_left_the_list_removes_it()
    {
        await _h.Created("task-1", percent: 0.25);
        _h.Gateway.Items.Remove("task-1");

        await _h.Process(_h.Notification("updated", "task-1"));

        var removed = _h.Sink.Changes[^1];
        Assert.Equal(TaskChangeKind.Removed, removed.Kind);
        Assert.Contains("no longer in 'Tasks'", removed.Reason);
        Assert.Null(_h.Store.Get("task-1"));
    }

    [Fact]
    public async Task A_failed_GET_leaves_the_replica_and_queues_a_reconcile()
    {
        await _h.Created("task-1", percent: 0.25);
        _h.Gateway.FailingTaskIds.Add("task-1");

        await _h.Process(_h.Notification("updated", "task-1"));

        Assert.Single(_h.Sink.Changes);
        Assert.Equal(0.25, _h.Store.Get("task-1")!.Snapshot.PercentComplete);
        Assert.True(_h.Engine.IsReconcileQueued);
    }

    // ── locating the list ────────────────────────────────────────────────────

    [Fact]
    public async Task An_unregistered_subscription_falls_back_to_the_list_in_the_resource_path()
    {
        _h.Put("task-1", percent: 0.5);

        await _h.Process(_h.Notification("created", "task-1", subscriptionId: "left-over-from-a-previous-run"));

        Assert.Equal(TaskChangeKind.Added, Assert.Single(_h.Sink.Changes).Kind);
    }

    [Fact]
    public async Task The_key_form_of_the_resource_path_is_understood()
    {
        _h.Put("task-1", percent: 0.5);
        var notification = _h.Notification("created", "task-1", subscriptionId: "unknown",
            resource: $"Users('00000000-0000-0000-0000-000000000001')/todo/lists('{ListId}')/tasks('task-1')");
        notification.ResourceData = null;   // task ID from the path too

        await _h.Process(notification);

        Assert.Equal(TaskChangeKind.Added, Assert.Single(_h.Sink.Changes).Kind);
    }

    [Fact]
    public async Task A_notification_for_a_list_that_is_not_synchronized_is_skipped()
    {
        await _h.Process(_h.Notification("created", "task-9", subscriptionId: "someone-else",
            resource: $"Users/x/todo/lists/{EngineHarness.OtherListId}/tasks/task-9"));

        Assert.Empty(_h.Sink.Changes);
        Assert.Empty(_h.Gateway.GetCalls);
    }

    // ── deleted ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_confirmed_by_a_404_removes_the_task()
    {
        await _h.Created("task-1", percent: 0.25);
        _h.Gateway.Items.Remove("task-1");

        await _h.Process(_h.Notification("deleted", "task-1"));

        var removed = _h.Sink.Changes[^1];
        Assert.Equal(TaskChangeKind.Removed, removed.Kind);
        Assert.Contains("deleted from 'Tasks'", removed.Reason);
        Assert.Null(_h.Store.Get("task-1"));
    }

    [Fact]
    public async Task A_stale_delete_for_a_task_still_in_the_list_keeps_it()
    {
        await _h.Created("task-1", percent: 0.25);

        await _h.Process(_h.Notification("deleted", "task-1"));

        Assert.Single(_h.Sink.Changes);
        Assert.NotNull(_h.Store.Get("task-1"));
    }

    [Fact]
    public async Task A_delete_that_cannot_be_confirmed_leaves_the_task_and_queues_a_reconcile()
    {
        await _h.Created("task-1", percent: 0.25);
        _h.Gateway.FailingTaskIds.Add("task-1");

        await _h.Process(_h.Notification("deleted", "task-1"));

        Assert.NotNull(_h.Store.Get("task-1"));
        Assert.True(_h.Engine.IsReconcileQueued);
    }

    // ── reconcile ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_adds_updates_and_removes_against_the_list()
    {
        await _h.Created("a", percent: 0.25);
        await _h.Created("b", percent: 0.5);

        _h.Put("a", percent: 1.0, lastModified: "2026-10-28T01:00:00Z", status: TodoTaskStatus.Completed);
        _h.Put("c", percent: 0.0, lastModified: "2026-10-28T01:00:00Z", status: TodoTaskStatus.NotStarted);
        _h.Gateway.Items.Remove("b");

        var summary = await _h.Engine.ReconcileAsync("test", CancellationToken.None);

        Assert.Equal(1, summary.Added);
        Assert.Equal(1, summary.Updated);
        Assert.Equal(1, summary.Removed);
        Assert.Equal(2, summary.TotalTasks);
        Assert.Null(_h.Store.Get("b"));
        Assert.Equal(1.0, _h.Store.Get("a")!.Snapshot.PercentComplete);
        Assert.NotNull(_h.Store.Get("c"));
        Assert.Single(_h.Sink.Reconciles);
    }

    [Fact]
    public async Task An_older_snapshot_never_rolls_a_task_back()
    {
        await _h.Created("a", percent: 0.75, lastModified: "2026-10-28T00:20:00Z");
        _h.Put("a", percent: 0.25, lastModified: "2026-10-28T00:10:00Z");

        await _h.Engine.ReconcileAsync("test", CancellationToken.None);

        Assert.Equal(0.75, _h.Store.Get("a")!.Snapshot.PercentComplete);
        Assert.Single(_h.Sink.Changes);
    }

    [Fact]
    public async Task Reconcile_with_a_failed_listing_removes_nothing()
    {
        await _h.Created("a", percent: 0.25);
        _h.Gateway.FailListing = true;

        var summary = await _h.Engine.ReconcileAsync("test", CancellationToken.None);

        Assert.NotNull(_h.Store.Get("a"));
        Assert.Equal(0, summary.Removed);
        Assert.Equal("Tasks", Assert.Single(summary.FailedLists));
    }

    [Fact]
    public async Task Reconcile_drops_tasks_from_lists_that_are_no_longer_synchronized()
    {
        _h.Store.Upsert(new TaskRecord
        {
            Snapshot = Snapshots.Task("old", "2026-10-01T00:00:00Z", 0.5),
            ListId = "list-from-an-earlier-configuration",
            ListName = "Old list"
        });

        var summary = await _h.Engine.ReconcileAsync("test", CancellationToken.None);

        Assert.Equal(1, summary.Removed);
        Assert.Null(_h.Store.Get("old"));
    }

    [Fact]
    public async Task Reconcile_requests_are_coalesced_while_one_is_queued()
    {
        _h.Engine.RequestReconcile("first");
        _h.Engine.RequestReconcile("second");
        Assert.True(_h.Engine.IsReconcileQueued);

        await _h.Engine.ReconcileAsync("first", CancellationToken.None);
        Assert.False(_h.Engine.IsReconcileQueued);
    }

    // ── persistence ──────────────────────────────────────────────────────────

    [Fact]
    public async Task The_replica_survives_a_restart()
    {
        await _h.Created("task-1", percent: 0.25);
        _h.Store.SaveIfDirty();

        var reloaded = new TaskStore(_h.Store.FilePath, EngineHarness.Mailbox, NullLoggerFactory.Instance);

        var record = reloaded.Get("task-1");
        Assert.NotNull(record);
        Assert.Equal(0.25, record!.Snapshot.PercentComplete);
        Assert.Equal(TodoTaskStatus.InProgress, record.Snapshot.Status);
        Assert.Equal(ListId, record.ListId);
    }
}
