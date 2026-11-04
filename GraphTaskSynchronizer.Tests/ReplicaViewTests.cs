using Xunit;

namespace GraphTaskSynchronizer.Tests;

public sealed class ReplicaViewTests
{
    private static TaskRecord Record(string id, string list, double? percent, TodoTaskStatus status, string subject,
        TodoDateTime? due = null) => new()
    {
        Snapshot = Snapshots.Task(id, "2026-10-28T00:10:00Z", percent, status, subject) with { DueDateTime = due },
        ListId = "id-" + list,
        ListName = list
    };

    [Fact]
    public void Every_task_shows_its_list_open_tasks_first()
    {
        var rows = ConsoleTaskChangeReporter.RenderReplica(new[]
        {
            Record("1", "Tasks", 1.0, TodoTaskStatus.Completed, "Review FTS import results"),
            Record("2", "Tasks", 0.25, TodoTaskStatus.InProgress, "Draft Q4 report", new TodoDateTime("2026-10-31T00:00:00", "UTC")),
            Record("3", "Groceries", null, TodoTaskStatus.NotStarted, "Milk")
        });

        var text = rows.Select(r => r.Text).ToList();
        var header = text.FindIndex(t => t.Contains("Subject"));
        var draft = text.FindIndex(t => t.Contains("Draft Q4 report"));
        var milk = text.FindIndex(t => t.Contains("Milk"));
        var review = text.FindIndex(t => t.Contains("Review FTS import results"));

        Assert.Contains(text, t => t.Contains("Lists: Groceries (1) · Tasks (2)"));
        Assert.Contains("List", text[header]);
        Assert.True(header < draft && draft < milk && milk < review,
            "open tasks (soonest due first) before completed ones");
        Assert.Contains("Tasks", text[draft]);
        Assert.Contains("Groceries", text[milk]);
        Assert.Contains("25%", text[draft]);
        Assert.Contains("In progress", text[draft]);
        Assert.Contains("2026-10-31T00:00:00 (UTC)", text[draft]);
        Assert.Contains("(none)", text[milk]);
        Assert.Equal(ConsoleColor.DarkGray, rows[review].Color);
    }

    [Fact]
    public void Long_subjects_and_list_names_are_shortened()
    {
        var rows = ConsoleTaskChangeReporter.RenderReplica(new[]
        {
            Record("1", new string('L', 40), 0.5, TodoTaskStatus.InProgress, new string('x', 200))
        });

        var row = rows.Single(r => r.Text.Contains("xxx")).Text;
        Assert.EndsWith(new string('x', 59) + "…", row);
        Assert.Contains(new string('L', 19) + "…", row);
    }

    [Fact]
    public void A_synchronized_list_without_tasks_is_counted_too()
    {
        var lists = new[]
        {
            new TaskList("id-Tasks", "Tasks", "defaultList", "test"),
            new TaskList("id-My new list", "My new list", "none", "test")
        };

        var rows = ConsoleTaskChangeReporter.RenderReplica(new[]
        {
            Record("1", "Tasks", 0.5, TodoTaskStatus.InProgress, "Draft Q4 report")
        }, lists);

        Assert.Contains(rows, r => r.Text.Contains("Lists: My new list (0) · Tasks (1)"));
        Assert.Equal(2, ConsoleTaskChangeReporter.ListCounts(Array.Empty<TaskRecord>(), lists).Count);
    }

    [Fact]
    public void An_empty_replica_says_so()
    {
        var row = Assert.Single(ConsoleTaskChangeReporter.RenderReplica(Array.Empty<TaskRecord>()));

        Assert.Contains("no tasks yet", row.Text);
    }

    [Fact]
    public void A_reconcile_line_names_the_list()
    {
        var line = ConsoleTaskChangeReporter.FormatReconcileLine(
            Record("1", "Website relaunch", 0.6, TodoTaskStatus.InProgress, "Content freeze",
                new TodoDateTime("2026-11-07T00:00:00", "UTC")));

        Assert.Contains("60%", line);
        Assert.Contains("Website relaunch", line);
        Assert.Contains("Content freeze", line);
        Assert.Contains("due 2026-11-07T00:00:00 (UTC)", line);
    }
}
