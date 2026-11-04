using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraphTaskSynchronizer.Tests;

/// <summary>Reading the todoTask body as plain-text notes, and the helpers that show changes to it.</summary>
public sealed class TaskNotesTests
{
    [Fact]
    public void Text_notes_keep_their_lines_with_CRLF_as_LF_and_nothing_trailing()
    {
        var notes = TaskNotes.FromBody("Numbers from finance  \r\n\r\nThen draft the slides\r\n", "text");

        Assert.Equal("Numbers from finance\n\nThen draft the slides", notes);
    }

    [Fact]
    public void An_empty_body_is_no_notes()
    {
        Assert.Equal("", TaskNotes.FromBody(null, "text"));
        Assert.Equal("", TaskNotes.FromBody("", "text"));
        Assert.Equal("", TaskNotes.FromBody("\r\n  \r\n", "text"));
    }

    [Fact]
    public void An_Outlook_HTML_body_reads_as_the_same_text()
    {
        const string html =
            "<html><head>\r\n<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">" +
            "<style type=\"text/css\" style=\"display:none\">P {margin-top:0;margin-bottom:0;}</style></head>\r\n" +
            "<body dir=\"ltr\">\r\n<div class=\"elementToProof\" style=\"font-family: Aptos\">Numbers from finance</div>\r\n" +
            "<div class=\"elementToProof\"><br></div>\r\n" +
            "<div class=\"elementToProof\">Then draft the <b>slides</b> &amp; send</div>\r\n</body></html>";

        Assert.Equal("Numbers from finance\n\nThen draft the slides & send", TaskNotes.FromBody(html, "html"));
    }

    [Fact]
    public void A_Word_style_paragraph_of_nbsp_is_an_empty_line()
    {
        const string html =
            "<p class=MsoNormal>Line one<o:p></o:p></p>\r\n" +
            "<p class=MsoNormal><o:p>&nbsp;</o:p></p>\r\n" +
            "<p class=MsoNormal>Line two<o:p></o:p></p>";

        Assert.Equal("Line one\n\nLine two", TaskNotes.FromBody(html, "HTML"));
    }

    [Fact]
    public void Breaks_list_items_and_entities_become_text()
    {
        var notes = TaskNotes.FromBody("Shopping:<br>\r\n<ul><li>Milk</li><li>Eggs &lt;6&gt;</li></ul>Done", "html");

        Assert.Equal("Shopping:\n• Milk\n• Eggs <6>\nDone", notes);
    }

    [Fact]
    public void The_same_notes_compare_equal_whether_read_as_text_or_HTML()
    {
        var text = TaskNotes.FromBody("Call Bob\r\nthen email Sue", "text");
        var html = TaskNotes.FromBody("<html><body><div>Call Bob</div><div>then email Sue</div></body></html>", "html");

        Assert.Equal(text, html);
    }

    [Fact]
    public void On_one_line_the_lines_are_joined_and_blank_ones_dropped()
    {
        Assert.Equal("Numbers from finance · Then draft the slides",
            TaskNotes.OneLine("Numbers from finance\n\nThen draft the slides"));
        Assert.Equal("", TaskNotes.OneLine(null));
    }

    [Fact]
    public void A_change_near_the_end_of_long_notes_is_still_visible()
    {
        var start = new string('x', 100);

        var (before, after) = TaskNotes.ChangeWindow(start + " call Bob", start + " call Sue", 40);

        Assert.StartsWith("…", before);
        Assert.EndsWith("call Bob", before);
        Assert.EndsWith("call Sue", after);
        Assert.True(before.Length <= 40 && after.Length <= 40);
        Assert.Equal(("a", "b"), TaskNotes.ChangeWindow("a", "b", 40));   // short: as is
    }

    [Fact]
    public void The_line_diff_lists_only_what_went_and_came()
    {
        var diff = TaskNotes.DiffLines("Milk\nEggs\nBread", "Milk\nOat milk\nBread\nButter");

        Assert.Equal(new[] { "- Eggs", "+ Oat milk", "+ Butter" },
            diff.Select(d => (d.Change == TaskNotes.LineChange.Removed ? "- " : "+ ") + d.Line).ToArray());
    }

    [Fact]
    public void A_change_is_described_as_added_removed_or_edited()
    {
        Assert.Equal("added", TaskNotes.Describe("", "x"));
        Assert.Equal("removed", TaskNotes.Describe("x", ""));
        Assert.Equal("edited", TaskNotes.Describe("x", "y"));
    }
}

/// <summary>Where the notes come from: the typed body, on every read path.</summary>
public sealed class NotesMappingTests
{
    private static TodoTaskDto Parse(string json) =>
        JsonSerializer.Deserialize<TodoTaskDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void Notes_come_from_the_typed_body()
    {
        var snapshot = TaskSnapshotFactory.FromTodoTask(Parse(TodoJson.Task("t", notes: "Line one\r\nLine two")));

        Assert.Equal("Line one\nLine two", snapshot.Notes);
    }

    [Fact]
    public void An_empty_body_is_no_notes_and_a_missing_one_is_unknown()
    {
        Assert.Equal("", TaskSnapshotFactory.FromTodoTask(Parse(TodoJson.Task("t", notes: ""))).Notes);
        Assert.Null(TaskSnapshotFactory.FromTodoTask(Parse(TodoJson.Task("t"))).Notes);
    }

    [Fact]
    public void The_task_query_selects_the_body()
    {
        Assert.Contains("body", TaskProperties.SelectFields);
        Assert.Contains(",body,", Uri.UnescapeDataString(TaskProperties.TaskQuery));
    }

    [Fact]
    public async Task GET_listing_and_delta_all_read_the_notes_as_text()
    {
        var handler = new FakeGraphHandler(r => r.Url.AbsolutePath.EndsWith("/delta")
            ? FakeGraphHandler.Json(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["value"] = new object[] { TodoJson.TaskObject("d", notes: "From delta\r\nline 2") },
                ["@odata.deltaLink"] = "https://graph.microsoft.com/v1.0/x/tasks/delta?$deltatoken=t"
            }))
            : r.Url.AbsolutePath.EndsWith("/tasks")
                ? FakeGraphHandler.Json(TodoJson.Collection(new object[] { TodoJson.TaskObject("l", notes: "From listing") }))
                : FakeGraphHandler.Json(TodoJson.Task("g", notes: "From GET")));
        var gateway = new TodoGateway(TestGraph.Client(handler), "user@contoso.com", false, NullLoggerFactory.Instance);

        var got = await gateway.GetTaskAsync("list", "g", CancellationToken.None);
        var listed = await gateway.ListTasksAsync("list", CancellationToken.None);
        var delta = await gateway.GetTaskDeltaAsync("list", null, true, CancellationToken.None);

        Assert.Equal("From GET", got.Snapshot!.Notes);
        Assert.Equal("From listing", Assert.Single(listed).Notes);
        Assert.Equal("From delta\nline 2", Assert.Single(delta.Changed).Notes);
        Assert.Contains(",body,", handler.Requests[0].DecodedQuery);
        // Text is To Do's default on all three; asking only some of them for HTML would make them disagree.
        Assert.DoesNotContain(handler.RequestHeaders, h => h.Contains("outlook.body-content-type"));
    }
}

/// <summary>Notes as a tracked field: reported when they change, filled in quietly when first learned.</summary>
public sealed class NotesTrackingTests : IDisposable
{
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-11-03T05:00:00Z"));
    private readonly EngineHarness _h;

    public NotesTrackingTests() => _h = new EngineHarness(bothLists: true, time: _time);

    public void Dispose() => _h.Dispose();

    private Task<ReconcileSummary> Reconcile() => _h.Engine.ReconcileAsync("test", CancellationToken.None);

    [Fact]
    public async Task A_notes_edit_arriving_by_notification_is_reported()
    {
        _h.Put("a", 0.5, notes: "Numbers from finance");
        await Reconcile();

        _h.Put("a", 0.5, lastModified: "2026-10-28T01:00:00Z", notes: "Numbers from finance\nThen draft the slides");
        await _h.Process(_h.Notification("updated", "a"));

        var change = _h.Sink.Changes[^1];
        Assert.Equal(TaskChangeKind.Updated, change.Kind);
        var notes = Assert.Single(change.Changes);
        Assert.Equal(TaskDiff.NotesField, notes.Field);
        Assert.Equal("Numbers from finance", notes.Before);
        Assert.Equal("Numbers from finance · Then draft the slides", notes.After);
        Assert.Equal("Numbers from finance\nThen draft the slides", _h.Store.Get("a")!.Snapshot.Notes);
    }

    [Fact]
    public async Task A_notes_edit_found_by_a_delta_reconcile_is_reported()
    {
        _h.Put("a", 0.5, notes: "Call Bob");
        await Reconcile();

        _h.Put("a", 0.5, lastModified: "2026-10-28T01:00:00Z", notes: "Call Sue");
        var summary = await Reconcile();

        Assert.Equal(1, summary.Updated);
        Assert.Contains("Tasks", summary.DeltaLists);
        var change = Assert.Single(_h.Sink.Changes[^1].Changes);
        Assert.Equal(("Call Bob", "Call Sue"), (change.Before, change.After));
    }

    [Fact]
    public async Task Clearing_the_notes_is_reported()
    {
        _h.Put("a", 0.5, notes: "Call Bob");
        await Reconcile();

        _h.Put("a", 0.5, lastModified: "2026-10-28T01:00:00Z", notes: "");
        await _h.Process(_h.Notification("updated", "a"));

        var change = Assert.Single(_h.Sink.Changes[^1].Changes);
        Assert.Equal(("Call Bob", TaskFormat.None), (change.Before, change.After));
    }

    [Fact]
    public async Task Notes_filled_in_for_an_entry_saved_before_notes_were_tracked_are_not_a_change()
    {
        // An entry from an older replica: everything known except the notes.
        _h.Store.Upsert(new TaskRecord
        {
            Snapshot = Snapshots.Task("a", "2026-10-28T00:10:00Z", 0.5, notes: null),
            ListId = EngineHarness.ListId,
            ListName = "Tasks",
            ImmutableId = "imm-a"
        });
        _h.Put("a", 0.5, notes: "Numbers from finance");

        var summary = await Reconcile();

        Assert.Equal(1, summary.Unchanged);
        Assert.Empty(_h.Sink.Changes);
        Assert.Equal("Numbers from finance", _h.Store.Get("a")!.Snapshot.Notes);
    }

    [Fact]
    public async Task A_snapshot_without_a_body_keeps_the_notes_on_record()
    {
        _h.Put("a", 0.5, notes: "Call Bob");
        await Reconcile();

        _h.Put("a", 0.75, lastModified: "2026-10-28T01:00:00Z", notes: null);   // body not returned
        await _h.Process(_h.Notification("updated", "a"));

        var change = _h.Sink.Changes[^1];
        Assert.Equal(TaskDiff.PercentCompleteField, Assert.Single(change.Changes).Field);
        Assert.Equal("Call Bob", _h.Store.Get("a")!.Snapshot.Notes);
    }

    [Fact]
    public async Task Notes_edited_while_a_task_moved_list_show_under_the_move()
    {
        _h.Put("a", 0.5, notes: "Call Bob");
        await Reconcile();

        _h.Gateway.Move("a", EngineHarness.OtherListId, "a-in-groceries");
        _h.Gateway.Put(EngineHarness.OtherListId,
            Snapshots.Task("a-in-groceries", "2026-10-28T01:00:00Z", 0.5, notes: "Call Bob\nand Sue"));
        var summary = await Reconcile();

        Assert.Equal(1, summary.Moved);
        var move = _h.Sink.Changes[^1];
        Assert.Equal(TaskChangeKind.Moved, move.Kind);
        Assert.Contains(move.Changes, c => c.Field == TaskDiff.NotesField);
        Assert.Equal("Call Bob\nand Sue", _h.Store.Get("a-in-groceries")!.Snapshot.Notes);
    }
}

public sealed class NotesDiffTests
{
    private static TaskRecord Record(string? notes) => new()
    {
        Snapshot = Snapshots.Task("t", "2026-10-28T00:10:00Z", 0.5, notes: notes),
        ListId = "l"
    };

    [Fact]
    public void Notes_are_compared_only_when_both_sides_know_them()
    {
        Assert.Empty(TaskDiff.Compare(Record(null), Record("Call Bob")));
        Assert.Empty(TaskDiff.Compare(Record("Call Bob"), Record("Call Bob")));
        Assert.Equal(TaskDiff.NotesField, Assert.Single(TaskDiff.Compare(Record(""), Record("Call Bob"))).Field);
    }

    [Fact]
    public void A_notes_change_carries_excerpts_that_start_near_the_difference()
    {
        var plan = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"Step {i} of the plan"));

        var change = Assert.Single(TaskDiff.Compare(Record(plan + "\nCall Bob"), Record(plan + "\nCall Sue")));

        Assert.StartsWith("…", change.Before);
        Assert.EndsWith("Call Bob", change.Before);
        Assert.EndsWith("Call Sue", change.After);
        Assert.True(change.After.Length <= TaskDiff.NotesChangeWidth);
    }
}

public sealed class NotesDisplayTests
{
    // ── console ──

    [Fact]
    public void The_current_state_shows_the_first_lines_of_the_notes()
    {
        var preview = ConsoleTaskChangeReporter.RenderNotesPreview("One\n\nTwo\nThree\nFour\nFive");

        Assert.Equal(new[] { "One", "Two", "Three", "(+2 more lines)" }, preview.ToArray());
        Assert.Empty(ConsoleTaskChangeReporter.RenderNotesPreview(""));
        Assert.Empty(ConsoleTaskChangeReporter.RenderNotesPreview(null));
    }

    [Fact]
    public void A_notes_change_shows_the_lines_that_went_and_came()
    {
        var rows = ConsoleTaskChangeReporter.RenderNotesChange("Milk\nEggs\nBread", "Milk\nOat milk\nBread\nButter");

        Assert.Equal(new[] { "- Eggs", "+ Oat milk", "+ Butter" }, rows.Select(r => r.Text).ToArray());
        Assert.Equal(ConsoleColor.Red, rows[0].Color);
        Assert.Equal(ConsoleColor.Green, rows[2].Color);
    }

    [Fact]
    public void An_edit_inside_a_long_line_is_shown_where_it_changed()
    {
        var start = new string('x', 120);

        var rows = ConsoleTaskChangeReporter.RenderNotesChange(start + " call Bob", start + " call Sue");

        Assert.Equal(2, rows.Count);
        Assert.StartsWith("- …", rows[0].Text);
        Assert.EndsWith("call Bob", rows[0].Text);
        Assert.EndsWith("call Sue", rows[1].Text);
    }

    [Fact]
    public void An_added_blank_line_says_so()
    {
        var rows = ConsoleTaskChangeReporter.RenderNotesChange("One\nTwo", "One\n\nTwo");

        Assert.Equal("+ (blank line)", Assert.Single(rows).Text);
    }

    [Fact]
    public void A_long_notes_change_is_cut_short()
    {
        var after = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"Line {i}"));

        var rows = ConsoleTaskChangeReporter.RenderNotesChange("", after);

        Assert.Equal(9, rows.Count);
        Assert.Equal("… 4 more changed line(s)", rows[^1].Text);
    }

    [Fact]
    public void The_listing_shows_notes_on_one_line_under_the_task()
    {
        var rows = ConsoleTaskChangeReporter.RenderReplica(new[]
        {
            new TaskRecord
            {
                Snapshot = Snapshots.Task("1", "2026-10-28T00:10:00Z", 0.5, subject: "Draft Q4 report",
                    notes: "Numbers from finance\n\nThen draft the slides"),
                ListId = "l",
                ListName = "Tasks"
            },
            new TaskRecord { Snapshot = Snapshots.Task("2", "2026-10-28T00:10:00Z", null, subject: "Milk"), ListId = "l", ListName = "Tasks" }
        });

        var text = rows.Select(r => r.Text).ToList();
        var task = text.FindIndex(t => t.Contains("Draft Q4 report"));
        Assert.Contains("└ notes: Numbers from finance · Then draft the slides", text[task + 1]);
        Assert.Equal(ConsoleColor.DarkGray, rows[task + 1].Color);
        Assert.Single(text, t => t.Contains("notes:"));   // Milk has none
    }

    // ── page ──

    private static string Page(params TaskRecord[] records) =>
        TaskPage.Render(records, new[] { EngineHarness.TasksList }, "user@contoso.com", null,
            DateTimeOffset.Parse("2026-11-03T05:00:00Z"));

    private static TaskRecord WithNotes(string? notes, string id = "task-1") => new()
    {
        Snapshot = Snapshots.Task(id, "2026-10-28T00:10:00Z", 0.5, subject: "Draft Q4 report", notes: notes),
        ListId = EngineHarness.ListId,
        ListName = "Tasks",
        ImmutableId = "imm-" + id
    };

    [Fact]
    public void Short_notes_show_under_the_subject()
    {
        var html = Page(WithNotes("Numbers from <finance>"));

        Assert.Contains("<div class=\"notes\">Numbers from &lt;finance&gt;</div>", html);
        Assert.Contains("<span class=\"title\" title=\"To Do ID: task-1", html);
    }

    [Fact]
    public void Longer_notes_fold_to_their_first_line_and_stay_open_across_refreshes()
    {
        var html = Page(WithNotes("Numbers from finance\n\nThen draft the slides"));

        Assert.Contains("<details class=\"notes\" data-key=\"imm-task-1\">", html);
        Assert.Contains("<span class=\"first\">Numbers from finance</span>", html);
        Assert.Contains("<div class=\"rest\">\nThen draft the slides</div>", html);
        Assert.Contains("querySelectorAll(\"details.notes[open]\")", html);   // the refresh re-opens it
    }

    [Fact]
    public void One_long_line_folds_too()
    {
        var line = new string('x', TaskPage.InlineNotesLength + 1);

        var html = Page(WithNotes(line));

        Assert.Contains($"<span class=\"first\">{line}</span><span class=\"more\"></span></summary></details>", html);
    }

    [Fact]
    public void A_task_without_notes_has_no_notes_element()
    {
        var html = Page(WithNotes(""), WithNotes(null, "task-2"));

        Assert.DoesNotContain("class=\"notes\"", html);
    }
}
