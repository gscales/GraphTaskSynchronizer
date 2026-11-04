using System.Net;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace GraphTaskSynchronizer.Tests;

public sealed class TaskPageTests
{
    private static readonly TaskList Tasks = new("list-tasks", "Tasks", "defaultList", "test");
    private static readonly TaskList Groceries = new("list-groceries", "Groceries", "none", "test");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T12:34:56+10:00");

    private static TaskRecord Record(string id, TaskList list, double? percent, TodoTaskStatus status, string subject,
        TodoDateTime? due = null) => new()
    {
        Snapshot = Snapshots.Task(id, "2026-10-28T00:10:00Z", percent, status, subject) with { DueDateTime = due },
        ListId = list.Id,
        ListName = list.DisplayName,
        ImmutableId = "imm-" + id,
        SyncedAt = DateTimeOffset.Parse("2026-10-28T02:00:00Z")
    };

    private static string Render(params TaskRecord[] records) =>
        TaskPage.Render(records, new[] { Tasks, Groceries }, "user@contoso.com", @"C:\sync\task-replica.json", Now);

    [Fact]
    public void Shows_each_task_with_its_list_progress_status_and_due_date()
    {
        var html = Render(Record("1", Tasks, 0.75, TodoTaskStatus.InProgress, "Draft Q4 report",
            new TodoDateTime("2026-10-31T00:00:00", "UTC")));

        Assert.Contains("Draft Q4 report", html);
        Assert.Contains("<td class=\"lst\">Tasks</td>", html);
        Assert.Contains("style=\"width:75%\"", html);
        Assert.Contains(">75%<", html);
        Assert.Contains("class=\"badge s-inProgress\">In progress<", html);
        Assert.Contains("<td class=\"due\">2026-10-31 <span class=\"muted\">00:00 UTC</span>", html);
        Assert.Contains("user@contoso.com", html);
    }

    [Fact]
    public void There_is_a_filter_chip_per_list_with_its_count()
    {
        var html = Render(
            Record("1", Tasks, 0.25, TodoTaskStatus.InProgress, "Draft Q4 report"),
            Record("2", Tasks, 1.0, TodoTaskStatus.Completed, "Review FTS import results"),
            Record("3", Groceries, null, TodoTaskStatus.NotStarted, "Milk"));

        var tasks = Key(Tasks);
        var groceries = Key(Groceries);
        Assert.Contains("data-list=\"\">All lists <span>3</span>", html);
        Assert.Contains($"href=\"#list={tasks}\" data-list=\"{tasks}\">Tasks <span>2</span>", html);
        Assert.Contains($"href=\"#list={groceries}\" data-list=\"{groceries}\">Groceries <span>1</span>", html);
        Assert.Contains($"<tr data-list=\"{groceries}\">", html);                // Milk, in Groceries
        Assert.Contains($"<tr data-list=\"{tasks}\" class=\"done\">", html);  // the completed task in Tasks
    }

    private static string Key(TaskList list) => TaskPage.ListKeys(new[] { list.Id })[list.Id];

    [Fact]
    public void A_list_keeps_its_key_when_lists_are_added_around_it()
    {
        // The filter in the URL (#list=key) must keep meaning the same list after a refresh
        // in which a new list appears before it.
        var groceries = Record("3", Groceries, null, TodoTaskStatus.NotStarted, "Milk");
        var created = new TaskList("list-created", "A new list", "none", "test");

        var before = TaskPage.Render(new[] { groceries }, new[] { Tasks, Groceries }, null, null, Now);
        var after = TaskPage.Render(new[] { groceries }, new[] { Tasks, created, Groceries }, null, null, Now);

        var key = Key(Groceries);
        Assert.Contains($"<tr data-list=\"{key}\">", before);
        Assert.Contains($"<tr data-list=\"{key}\">", after);
        Assert.Contains($"data-list=\"{Key(created)}\">A new list <span>0</span>", after);
        Assert.Contains("<b>3</b>lists", after);
    }

    [Fact]
    public void List_keys_are_short_and_distinct()
    {
        var keys = TaskPage.ListKeys(new[] { "AQMkADAwATM0MDAAMS1iNTcwLWI2NTEtMDACLTAwCgAuAAAD", "AQMkADAwATM0MDAAMS1iNTcwLWI2NTEtMDACLTAwCgAuAAAE", "x" });

        Assert.Equal(3, keys.Values.Distinct().Count());
        Assert.True(keys.Values.All(k => k.Length == 10 && k.All(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c))));
    }

    [Fact]
    public void A_filter_for_a_list_that_is_gone_falls_back_to_all_lists()
    {
        var html = Render(Record("1", Tasks, 0.25, TodoTaskStatus.InProgress, "Draft Q4 report"));

        Assert.Contains("chip.dataset.list === key)) {", html);
        Assert.Contains("history.replaceState(null, \"\", location.pathname + location.search);", html);
    }

    [Fact]
    public void Open_tasks_come_first_soonest_due_first_then_completed_ones()
    {
        var html = Render(
            Record("1", Tasks, 1.0, TodoTaskStatus.Completed, "Review FTS import results"),
            Record("2", Groceries, null, TodoTaskStatus.NotStarted, "Milk"),
            Record("3", Tasks, 0.25, TodoTaskStatus.InProgress, "Draft Q4 report", new TodoDateTime("2026-10-31T00:00:00", "UTC")));

        var draft = html.IndexOf("Draft Q4 report", StringComparison.Ordinal);
        var milk = html.IndexOf("Milk", StringComparison.Ordinal);
        var review = html.IndexOf("Review FTS import results", StringComparison.Ordinal);

        Assert.True(draft < milk && milk < review);
    }

    [Fact]
    public void A_task_moved_in_the_last_day_says_where_from()
    {
        var recent = Record("1", Groceries, 0.5, TodoTaskStatus.InProgress, "Recently moved") with
        {
            PreviousListName = "Tasks",
            MovedAt = Now.AddHours(-2)
        };
        var old = Record("2", Groceries, 0.5, TodoTaskStatus.InProgress, "Moved last week") with
        {
            PreviousListName = "Website relaunch",
            MovedAt = Now.AddDays(-7)
        };

        var html = Render(recent, old);

        Assert.Contains(">from Tasks</div>", html);
        Assert.DoesNotContain("from Website relaunch", html);
    }

    [Fact]
    public void The_ids_are_in_the_subject_tooltip()
    {
        var html = Render(Record("task-1", Tasks, 0.5, TodoTaskStatus.InProgress, "Draft Q4 report"));

        Assert.Contains("To Do ID: task-1\nImmutable ID: imm-task-1", html);
    }

    [Fact]
    public void A_task_without_PercentComplete_says_so()
    {
        var html = Render(Record("1", Tasks, null, TodoTaskStatus.NotStarted, "Milk"));

        Assert.Contains("PidLidPercentComplete is not set", html);
        Assert.Contains("style=\"width:0%\"", html);
        // No due date: "—" on the desktop table, nothing at all on the phone layout.
        Assert.Contains("<td class=\"due none\">", html);
    }

    [Fact]
    public void Task_text_and_list_names_are_HTML_encoded()
    {
        var list = new TaskList("list-x", "<b>Ops</b> & co", "none", "test");
        var html = TaskPage.Render(
            new[] { Record("1", list, 0.5, TodoTaskStatus.InProgress, "<script>alert('x')</script> & more") },
            new[] { list }, null, null, Now);

        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; &amp; more", html);
        Assert.DoesNotContain("<b>Ops</b>", html);
        Assert.Contains("&lt;b&gt;Ops&lt;/b&gt; &amp; co", html);
    }

    [Fact]
    public void An_empty_replica_is_explained()
    {
        var html = Render();

        Assert.Contains("No tasks synced yet", html);
        Assert.Contains("last change synced never", html);
        Assert.Contains("Tasks <span>0</span>", html);
    }

    [Fact]
    public void Tasks_of_a_list_no_longer_synchronized_still_get_a_chip()
    {
        var gone = new TaskList("list-gone", "Old list", "none", "test");
        var html = TaskPage.Render(new[] { Record("1", gone, 0.5, TodoTaskStatus.InProgress, "Leftover") },
            new[] { Tasks }, null, null, Now);

        Assert.Contains($"data-list=\"{Key(gone)}\">Old list <span>1</span>", html);
        Assert.Contains($"<tr data-list=\"{Key(gone)}\">", html);
    }

    // ── who may see it ───────────────────────────────────────────────────────

    private static HttpContext Request(string remoteIp, string host, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        context.Request.Host = new HostString(host);
        if (forwardedFor is not null)
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        return context;
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost:5000")]
    [InlineData("127.0.0.1", "127.0.0.1:5000")]
    [InlineData("::1", "[::1]:5000")]
    [InlineData("::ffff:127.0.0.1", "localhost:5000")]
    public void A_browser_on_this_machine_may_see_the_page(string remoteIp, string host)
    {
        Assert.True(TaskPage.IsLocalRequest(Request(remoteIp, host)));
    }

    [Fact]
    public void A_request_through_the_ngrok_tunnel_may_not()
    {
        // The ngrok agent connects from 127.0.0.1, but adds X-Forwarded-For and keeps the public host.
        Assert.False(TaskPage.IsLocalRequest(Request("127.0.0.1", "example.ngrok-free.dev", forwardedFor: "203.0.113.7")));
        Assert.False(TaskPage.IsLocalRequest(Request("127.0.0.1", "localhost:5000", forwardedFor: "203.0.113.7")));
        Assert.False(TaskPage.IsLocalRequest(Request("127.0.0.1", "example.ngrok-free.dev")));
    }

    [Fact]
    public void A_request_from_another_machine_may_not()
    {
        Assert.False(TaskPage.IsLocalRequest(Request("192.168.1.20", "localhost:5000")));
    }
}
