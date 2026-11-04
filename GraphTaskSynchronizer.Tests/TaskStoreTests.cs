using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraphTaskSynchronizer.Tests;

public sealed class TaskStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Round_trips_every_field_through_the_file()
    {
        var path = _temp.File("replica.json");
        var store = new TaskStore(path, "user@contoso.com", NullLoggerFactory.Instance);

        store.Upsert(new TaskRecord
        {
            Snapshot = new TaskSnapshot
            {
                Id = "task-1",
                Subject = "Draft Q4 report",
                Status = TodoTaskStatus.WaitingOnOthers,
                PercentComplete = 0.5,
                StartDateTime = new TodoDateTime("2026-10-27T00:00:00", "UTC"),
                DueDateTime = new TodoDateTime("2026-10-31T00:00:00", "UTC"),
                Importance = "high",
                Categories = new[] { "Finance", "Q4" },
                Notes = "Numbers from finance\n\nThen draft the slides",
                LastModifiedDateTime = DateTimeOffset.Parse("2026-10-28T00:10:00Z"),
                ChangeKey = "W/\"etag\""
            },
            ListId = "list-tasks",
            ListName = "Tasks",
            ImmutableId = "AAkALgAAAAAAHYQDEapmEc2byACqAC-EWg0A",
            PreviousListName = "Website relaunch",
            MovedAt = DateTimeOffset.Parse("2026-10-29T01:30:00Z"),
            SyncedAt = DateTimeOffset.UtcNow,
            Source = SnapshotSource.Notification
        });
        store.SaveIfDirty();

        var reloaded = new TaskStore(path, "user@contoso.com", NullLoggerFactory.Instance);
        var record = reloaded.Get("task-1")!;
        var s = record.Snapshot;

        Assert.Equal("AAkALgAAAAAAHYQDEapmEc2byACqAC-EWg0A", record.ImmutableId);
        Assert.Equal("Website relaunch", record.PreviousListName);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T01:30:00Z"), record.MovedAt);
        Assert.Same(record, reloaded.FindByImmutableId("AAkALgAAAAAAHYQDEapmEc2byACqAC-EWg0A"));
        Assert.Contains("\"formatVersion\": 6", File.ReadAllText(path));

        Assert.Equal(0.5, s.PercentComplete);
        Assert.Equal(TodoTaskStatus.WaitingOnOthers, s.Status);
        Assert.Equal(new TodoDateTime("2026-10-31T00:00:00", "UTC"), s.DueDateTime);
        Assert.Equal(new TodoDateTime("2026-10-27T00:00:00", "UTC"), s.StartDateTime);
        Assert.Null(s.CompletedDateTime);
        Assert.Equal("Finance, Q4", string.Join(", ", s.Categories));
        Assert.Equal("Numbers from finance\n\nThen draft the slides", s.Notes);
        Assert.Equal(DateTimeOffset.Parse("2026-10-28T00:10:00Z"), s.LastModifiedDateTime);
        Assert.Equal("list-tasks", record.ListId);
        Assert.Equal(SnapshotSource.Notification, record.Source);
    }

    [Fact]
    public void A_replica_for_another_mailbox_is_not_loaded()
    {
        var path = _temp.File("replica.json");
        var store = new TaskStore(path, "someone@contoso.com", NullLoggerFactory.Instance);
        store.Upsert(new TaskRecord { Snapshot = new TaskSnapshot { Id = "x" }, ListId = "l" });
        store.SaveIfDirty();

        var other = new TaskStore(path, "user@contoso.com", NullLoggerFactory.Instance);

        Assert.Equal(0, other.Count);
    }

    [Fact]
    public void A_replica_in_an_older_format_is_rebuilt_rather_than_read()
    {
        var path = _temp.File("replica.json");
        File.WriteAllText(path,
            "{\"formatVersion\":2,\"mailboxUserId\":\"user@contoso.com\",\"tasks\":[" +
            "{\"snapshot\":{\"id\":\"t\",\"status\":\"complete\",\"dueDate\":\"2026-10-31\"},\"listId\":\"l\"}]}");

        var store = new TaskStore(path, "user@contoso.com", NullLoggerFactory.Instance);

        Assert.Equal(0, store.Count);
        Assert.False(File.Exists(path + ".unreadable"));
    }

    [Fact]
    public void A_format_3_replica_is_read_and_upgraded_on_the_next_save()
    {
        var path = _temp.File("replica.json");
        File.WriteAllText(path,
            "{\"formatVersion\":3,\"mailboxUserId\":\"user@contoso.com\",\"tasks\":[" +
            "{\"snapshot\":{\"id\":\"t\",\"subject\":\"Draft\",\"status\":\"inProgress\"},\"listId\":\"l\",\"listName\":\"Tasks\"}]}");

        var store = new TaskStore(path, "user@contoso.com", NullLoggerFactory.Instance);

        var record = store.Get("t")!;
        Assert.Equal("Tasks", record.ListName);
        Assert.Null(record.ImmutableId);

        store.Upsert(record with { ImmutableId = "imm-t" });
        store.SaveIfDirty();
        Assert.Contains("\"formatVersion\": 6", File.ReadAllText(path));
        Assert.Contains("\"immutableId\": \"imm-t\"", File.ReadAllText(path));
    }

    [Fact]
    public void The_immutable_id_index_follows_a_task_to_its_new_id()
    {
        var store = new TaskStore(null, "user@contoso.com", NullLoggerFactory.Instance);
        var before = new TaskRecord { Snapshot = new TaskSnapshot { Id = "old" }, ListId = "a", ImmutableId = "imm" };
        store.Upsert(before);
        Assert.Same(before, store.FindByImmutableId("imm"));

        // Moved: the old ID goes, the new one takes the immutable ID.
        store.Remove("old");
        Assert.Null(store.FindByImmutableId("imm"));
        var after = before with { Snapshot = new TaskSnapshot { Id = "new" }, ListId = "b" };
        store.Upsert(after);
        Assert.Same(after, store.FindByImmutableId("imm"));

        // Replacing a record without its immutable ID drops it from the index.
        store.Upsert(after with { ImmutableId = null });
        Assert.Null(store.FindByImmutableId("imm"));
    }

    [Fact]
    public void Delta_links_are_saved_with_the_tasks()
    {
        var path = _temp.File("replica.json");
        var store = new TaskStore(path, "user@contoso.com", NullLoggerFactory.Instance);
        store.SetDeltaLink("list-a", "https://graph.microsoft.com/v1.0/users/x/todo/lists/list-a/tasks/delta?$deltatoken=aaa");
        store.SetDeltaLink("list-b", "https://graph.microsoft.com/v1.0/users/x/todo/lists/list-b/tasks/delta?$deltatoken=bbb");
        store.SaveIfDirty();

        var reloaded = new TaskStore(path, "user@contoso.com", NullLoggerFactory.Instance);
        Assert.EndsWith("$deltatoken=aaa", reloaded.GetDeltaLink("list-a"));

        // A list that is no longer synchronized loses its link; null forgets one.
        reloaded.RetainDeltaLinks(new[] { "list-a" });
        Assert.Null(reloaded.GetDeltaLink("list-b"));
        reloaded.SetDeltaLink("list-a", null);
        Assert.Null(reloaded.GetDeltaLink("list-a"));
    }

    [Fact]
    public void A_format_4_replica_has_no_delta_links_so_the_next_reconcile_is_full()
    {
        var path = _temp.File("replica.json");
        File.WriteAllText(path,
            "{\"formatVersion\":4,\"mailboxUserId\":\"user@contoso.com\",\"tasks\":[" +
            "{\"snapshot\":{\"id\":\"t\"},\"listId\":\"l\",\"immutableId\":\"imm-t\"}]}");

        var store = new TaskStore(path, "user@contoso.com", NullLoggerFactory.Instance);

        Assert.Equal("imm-t", store.Get("t")!.ImmutableId);
        Assert.Null(store.GetDeltaLink("l"));
    }

    [Fact]
    public void A_format_5_replica_loses_its_delta_links_so_a_full_round_fills_in_notes()
    {
        var path = _temp.File("replica.json");
        File.WriteAllText(path,
            "{\"formatVersion\":5,\"mailboxUserId\":\"user@contoso.com\"," +
            "\"deltaLinks\":{\"l\":\"https://graph.microsoft.com/v1.0/users/x/todo/lists/l/tasks/delta?$deltatoken=old\"}," +
            "\"tasks\":[{\"snapshot\":{\"id\":\"t\",\"subject\":\"Draft\"},\"listId\":\"l\",\"immutableId\":\"imm-t\"}]}");

        var store = new TaskStore(path, "user@contoso.com", NullLoggerFactory.Instance);

        var record = store.Get("t")!;
        Assert.Null(record.Snapshot.Notes);   // not known yet – not "no notes"
        Assert.Equal("imm-t", record.ImmutableId);
        Assert.Null(store.GetDeltaLink("l"));
    }

    [Fact]
    public void A_task_with_no_notes_is_saved_as_empty_not_unknown()
    {
        var path = _temp.File("replica.json");
        var store = new TaskStore(path, "user@contoso.com", NullLoggerFactory.Instance);
        store.Upsert(new TaskRecord { Snapshot = new TaskSnapshot { Id = "t", Notes = "" }, ListId = "l" });
        store.SetDeltaLink("l", "https://graph.microsoft.com/v1.0/users/x/todo/lists/l/tasks/delta?$deltatoken=new");
        store.SaveIfDirty();

        var reloaded = new TaskStore(path, "user@contoso.com", NullLoggerFactory.Instance);

        Assert.Equal("", reloaded.Get("t")!.Snapshot.Notes);
        Assert.NotNull(reloaded.GetDeltaLink("l"));   // current format: the link is kept
    }

    [Fact]
    public void An_unreadable_file_is_kept_aside_and_the_replica_starts_empty()
    {
        var path = _temp.File("replica.json");
        File.WriteAllText(path, "{ not json");

        var store = new TaskStore(path, "user@contoso.com", NullLoggerFactory.Instance);

        Assert.Equal(0, store.Count);
        Assert.True(File.Exists(path + ".unreadable"));
    }

    [Fact]
    public void Nothing_is_written_until_something_changes()
    {
        var path = _temp.File("replica.json");
        var store = new TaskStore(path, "user@contoso.com", NullLoggerFactory.Instance);

        store.SaveIfDirty();

        Assert.False(File.Exists(path));
    }
}
