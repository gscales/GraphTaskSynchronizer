namespace GraphTaskSynchronizer;

/// <summary>
/// Receives every change the synchronizer makes to its replica. This is the extension
/// point for pushing task changes somewhere else (another mailbox, To Do, a database,
/// a queue): implement it and add the instance to the sink list in Program.cs.
///
/// Sinks are called on the engine's single consumer, in order, one change at a time.
/// A sink that throws is logged and skipped; the replica is already updated.
/// </summary>
public interface ITaskChangeSink
{
    Task OnTaskChangedAsync(TaskChange change, CancellationToken ct);

    Task OnReconcileCompletedAsync(ReconcileSummary summary, CancellationToken ct);
}

/// <summary>Writes changes to the console in the same boxed style as DeletedItemWatcher.</summary>
public sealed class ConsoleTaskChangeReporter : ITaskChangeSink
{
    private const int BoxWidth = 56;
    private const int MaxReconcileLines = 100;

    private static readonly object ConsoleLock = new();

    private int _reconcileLinesPrinted;
    private int _reconcileLinesSuppressed;

    public Task OnTaskChangedAsync(TaskChange change, CancellationToken ct)
    {
        lock (ConsoleLock)
        {
            // A first reconcile can add hundreds of tasks — one line each, not a box each.
            if (change.Source == SnapshotSource.Reconcile && change.Kind == TaskChangeKind.Added)
                WriteReconcileLine(change);
            else
                WriteChangeBox(change);
        }

        return Task.CompletedTask;
    }

    public Task OnReconcileCompletedAsync(ReconcileSummary summary, CancellationToken ct)
    {
        lock (ConsoleLock)
        {
            WriteSummaryBox(summary, _reconcileLinesSuppressed);
            _reconcileLinesPrinted = 0;
            _reconcileLinesSuppressed = 0;
        }

        return Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────
    //  Change box
    // ──────────────────────────────────────────────────────────

    private static void WriteChangeBox(TaskChange change)
    {
        var (title, color) = change.Kind switch
        {
            TaskChangeKind.Added => ("Task Added", ConsoleColor.Green),
            TaskChangeKind.Updated => ("Task Updated", ConsoleColor.Yellow),
            TaskChangeKind.Moved => ("Task Moved", ConsoleColor.Magenta),
            _ => ("Task Removed", ConsoleColor.Red)
        };

        var record = change.After ?? change.Before!;
        var s = record.Snapshot;

        Header(title, color);
        Line("Subject", TaskFormat.Text(s.Subject));
        if (change.Kind == TaskChangeKind.Moved)
        {
            Line("From list", ListName(change.Before!), ConsoleColor.Magenta);
            Line("To list", ListName(change.After!), ConsoleColor.Magenta);
            Line("Task ID", change.TaskId);
            Line("Previous task ID", change.Before!.Id);
        }
        else
        {
            Line("List", ListName(record));
            Line("Task ID", change.TaskId);
        }
        Line("Immutable ID", record.ImmutableId ?? "(not translated)",
            record.ImmutableId is null ? ConsoleColor.DarkYellow : null);
        Line("Detected at", DateTimeOffset.UtcNow.ToString("O"));

        // For a move the list change is already shown above.
        var fieldChanges = change.Changes
            .Where(c => !(change.Kind == TaskChangeKind.Moved && c.Field == TaskDiff.ListField))
            .ToList();
        if ((change.Kind is TaskChangeKind.Updated or TaskChangeKind.Moved) && fieldChanges.Count > 0)
        {
            Blank();
            Section(change.Kind == TaskChangeKind.Moved ? "Also changed" : "Changes");
            foreach (var c in fieldChanges)
            {
                if (c.Field == TaskDiff.NotesField
                    && change.Before?.Snapshot.Notes is { } oldNotes
                    && change.After?.Snapshot.Notes is { } newNotes)
                {
                    // The lines that went and came, rather than two excerpts of the whole text.
                    Line(c.Field, TaskNotes.Describe(oldNotes, newNotes), ConsoleColor.Yellow);
                    foreach (var (text, lineColor) in RenderNotesChange(oldNotes, newNotes))
                        Line("", text, lineColor);
                    continue;
                }

                var highlight = c.Field == TaskDiff.PercentCompleteField
                    ? ConsoleColor.Green
                    : ConsoleColor.Yellow;
                Line(c.Field, $"{c.Before} → {c.After}", highlight);
            }
        }

        Blank();
        if (change.Kind == TaskChangeKind.Removed)
        {
            Line("Reason", TaskFormat.Text(change.Reason), ConsoleColor.Red);
            Line("Last percent", TaskFormat.Percent(s.PercentComplete));
            Line("Last status", TaskFormat.Status(s.Status));
        }
        else
        {
            Section("Current state");
            Line("Percent complete", TaskFormat.Percent(s.PercentComplete), ConsoleColor.Cyan);
            Line("Status", TaskFormat.Status(s.Status));
            Line("Start", TaskFormat.DateTime(s.StartDateTime));
            Line("Due", TaskFormat.DateTime(s.DueDateTime));
            if (s.CompletedDateTime is not null)
                Line("Completed", TaskFormat.DateTime(s.CompletedDateTime));
            Line("Importance", TaskFormat.Text(s.Importance));
            if (s.Categories.Count > 0)
                Line("Categories", TaskFormat.Categories(s.Categories));
            var notes = RenderNotesPreview(s.Notes);
            for (var i = 0; i < notes.Count; i++)
                Line(i == 0 ? "Notes" : "", notes[i], i == 0 ? null : ConsoleColor.Gray);
            Line("Last modified", TaskFormat.Timestamp(s.LastModifiedDateTime));
            Line("Trigger", TaskFormat.Text(change.Reason));
        }

        Line("Source", change.SourceDetail ?? DefaultSourceDetail(change.Source),
            change.Kind != TaskChangeKind.Removed && s.PercentComplete is null
                ? ConsoleColor.DarkYellow
                : ConsoleColor.Green);
        if (change.Kind == TaskChangeKind.Moved)
            Line("Matched by", "immutable ID (translateExchangeIds)", ConsoleColor.Magenta);

        Footer(color);
    }

    private static string ListName(TaskRecord record) => record.ListName ?? record.ListId;

    // ── notes ──

    private const int NotesWidth = 72;
    private const int NotesPreviewLines = 3;
    private const int NotesChangeLines = 8;

    /// <summary>
    /// The first lines of a task's notes for the "Current state" section, each shortened to
    /// fit, then how many more there are. Nothing when the task has no notes.
    /// </summary>
    internal static IReadOnlyList<string> RenderNotesPreview(string? notes)
    {
        var lines = TaskNotes.Lines(notes).Where(l => l.Trim().Length > 0).ToList();
        var shown = lines.Take(NotesPreviewLines).Select(l => Fit(l.Trim(), NotesWidth)).ToList();
        if (lines.Count > NotesPreviewLines)
            shown.Add($"(+{lines.Count - NotesPreviewLines} more line{(lines.Count - NotesPreviewLines == 1 ? "" : "s")})");
        return shown;
    }

    /// <summary>
    /// What changed in a task's notes, a line per removed ("-") or added ("+") line. A single
    /// line that was edited is shown from just before the first character that differs, so a
    /// change near the end of a long line is still visible.
    /// </summary>
    internal static IReadOnlyList<(string Text, ConsoleColor Color)> RenderNotesChange(string before, string after)
    {
        var diff = TaskNotes.DiffLines(before, after);
        var rows = new List<(string, ConsoleColor)>();

        if (diff.Count == 2 && diff[0].Change == TaskNotes.LineChange.Removed && diff[1].Change == TaskNotes.LineChange.Added)
        {
            var (removed, added) = TaskNotes.ChangeWindow(diff[0].Line.Trim(), diff[1].Line.Trim(), NotesWidth);
            rows.Add(("- " + Shown(removed), ConsoleColor.Red));
            rows.Add(("+ " + Shown(added), ConsoleColor.Green));
            return rows;
        }

        foreach (var (lineChange, line) in diff.Take(NotesChangeLines))
        {
            rows.Add(lineChange == TaskNotes.LineChange.Removed
                ? ("- " + Shown(Fit(line.Trim(), NotesWidth)), ConsoleColor.Red)
                : ("+ " + Shown(Fit(line.Trim(), NotesWidth)), ConsoleColor.Green));
        }

        if (diff.Count > NotesChangeLines)
            rows.Add(($"… {diff.Count - NotesChangeLines} more changed line(s)", ConsoleColor.DarkGray));
        return rows;

        static string Shown(string line) => line.Length == 0 ? "(blank line)" : line;
    }

    private static string DefaultSourceDetail(SnapshotSource source) => source switch
    {
        SnapshotSource.Notification => "change notification, confirmed with a GET of the task",
        _ => "list listing (reconcile)"
    };

    // ──────────────────────────────────────────────────────────
    //  Reconcile output
    // ──────────────────────────────────────────────────────────

    private void WriteReconcileLine(TaskChange change)
    {
        if (_reconcileLinesPrinted >= MaxReconcileLines)
        {
            _reconcileLinesSuppressed++;
            return;
        }

        _reconcileLinesPrinted++;
        Console.ForegroundColor = ConsoleColor.DarkGreen;
        Console.WriteLine(FormatReconcileLine(change.After!));
        Console.ResetColor();
    }

    /// <summary>One line per task added by a reconcile: percent, status, list, subject, due.</summary>
    internal static string FormatReconcileLine(TaskRecord record)
    {
        var s = record.Snapshot;
        var due = s.DueDateTime is null ? string.Empty : $"  due {TaskFormat.DateTime(s.DueDateTime)}";
        return $"  + [{TaskFormat.Percent(s.PercentComplete),6}] {TaskFormat.Status(s.Status),-18} " +
               $"{Fit(ListName(record), ListWidth),-ListWidth} {TaskFormat.Text(s.Subject)}{due}";
    }

    private static void WriteSummaryBox(ReconcileSummary summary, int suppressed)
    {
        var color = summary.FailedLists.Count > 0 ? ConsoleColor.Red : ConsoleColor.Cyan;

        Header($"Reconcile complete ({summary.Reason})", color);
        Line("Lists", string.Join(", ", summary.Lists.Select(l =>
            summary.FullLists.Contains(l.DisplayName) ? $"{l.DisplayName} (full)"
            : summary.DeltaLists.Contains(l.DisplayName) ? $"{l.DisplayName} (delta)"
            : summary.FailedLists.Contains(l.DisplayName) ? $"{l.DisplayName} (failed)"
            : l.DisplayName)));
        Line("Tasks in replica", summary.TotalTasks.ToString());
        Line("Added", summary.Added.ToString());
        Line("Updated", summary.Updated.ToString());
        Line("Moved", summary.Moved.ToString());
        Line("Removed", summary.Removed.ToString());
        Line("Unchanged", summary.Unchanged.ToString());
        if (suppressed > 0)
            Line("Not listed above", $"{suppressed} more added task(s)");
        if (summary.FailedLists.Count > 0)
        {
            Line("Failed lists", string.Join(", ", summary.FailedLists), ConsoleColor.Red);
            Line("", "(their replica entries were left untouched)", ConsoleColor.DarkGray);
        }
        Line("Duration", $"{summary.Duration.TotalSeconds:0.0}s");
        Footer(color);
    }

    // ──────────────────────────────────────────────────────────
    //  The whole replica (L key / --list)
    // ──────────────────────────────────────────────────────────

    private const int SubjectWidth = 60;
    private const int ListWidth = 20;
    private const int NotesListWidth = 90;

    /// <summary>
    /// Prints every synced task with its list — open tasks first (soonest due first), then
    /// completed ones — and, under a task that has notes, the notes on one line.
    /// <paramref name="lists"/> are the lists being synchronized, so one without tasks (a list
    /// just created, say) is counted too; null when only the saved replica is known (--list).
    /// </summary>
    public static void PrintReplica(IReadOnlyList<TaskRecord> records, IReadOnlyList<TaskList>? lists, string? replicaPath)
    {
        lock (ConsoleLock)
        {
            var listCount = ListCounts(records, lists).Count;
            Header($"Synced tasks ({records.Count} in {listCount} list{(listCount == 1 ? "" : "s")})", ConsoleColor.Cyan);

            foreach (var (text, color) in RenderReplica(records, lists))
            {
                if (color is { } c)
                    Console.ForegroundColor = c;
                Console.WriteLine(text);
                Console.ResetColor();
            }

            if (replicaPath is not null)
                Line("Replica file", replicaPath, ConsoleColor.DarkGray);
            Footer(ConsoleColor.Cyan);
        }
    }

    /// <summary>
    /// Every list with its task count, by name: the synchronized lists (with or without tasks)
    /// and any list the replica still holds tasks for.
    /// </summary>
    internal static IReadOnlyList<(string Name, int Count)> ListCounts(IReadOnlyList<TaskRecord> records, IReadOnlyList<TaskList>? lists)
    {
        var byId = new Dictionary<string, (string Name, int Count)>(StringComparer.Ordinal);
        foreach (var list in lists ?? [])
            byId.TryAdd(list.Id, (list.DisplayName, 0));
        foreach (var record in records)
        {
            var (name, count) = byId.TryGetValue(record.ListId, out var known) ? known : (ListName(record), 0);
            byId[record.ListId] = (name, count + 1);
        }

        return byId.Values.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The table rows <see cref="PrintReplica"/> writes, with their colours.</summary>
    internal static IReadOnlyList<(string Text, ConsoleColor? Color)> RenderReplica(
        IReadOnlyList<TaskRecord> records, IReadOnlyList<TaskList>? lists = null)
    {
        var rows = new List<(string, ConsoleColor?)>();
        var counts = ListCounts(records, lists);
        if (counts.Count > 0)
            rows.Add(($"│  Lists: {string.Join(" · ", counts.Select(l => $"{l.Name} ({l.Count})"))}", ConsoleColor.Cyan));

        if (records.Count == 0)
        {
            rows.Add(("│  (no tasks yet — the startup reconcile fills the replica)", ConsoleColor.DarkGray));
            return rows;
        }

        rows.Add(("│", null));
        rows.Add(($"│  {"%",6}  {"Status",-18} {"Due",-27} {"List",-ListWidth} Subject", ConsoleColor.DarkGray));

        var ordered = records
            .OrderBy(r => r.Snapshot.Status == TodoTaskStatus.Completed)
            .ThenBy(r => r.Snapshot.DueDateTime is null)
            .ThenBy(r => r.Snapshot.DueDateTime?.DateTime, StringComparer.Ordinal)
            .ThenBy(r => r.Snapshot.Subject, StringComparer.OrdinalIgnoreCase);

        foreach (var record in ordered)
        {
            var s = record.Snapshot;
            rows.Add((
                $"│  {TaskFormat.Percent(s.PercentComplete),6}  {TaskFormat.Status(s.Status),-18} " +
                $"{TaskFormat.DateTime(s.DueDateTime),-27} {Fit(ListName(record), ListWidth),-ListWidth} " +
                Fit(TaskFormat.Text(s.Subject), SubjectWidth),
                s.Status == TodoTaskStatus.Completed ? ConsoleColor.DarkGray : null));

            // Notes, on one line under the task.
            var notes = TaskNotes.OneLine(s.Notes);
            if (notes.Length > 0)
                rows.Add(($"│  {"",8}└ notes: {Fit(notes, NotesListWidth)}", ConsoleColor.DarkGray));
        }

        rows.Add(("│", null));
        return rows;
    }

    /// <summary>Shortens text to a column width, ending in "…".</summary>
    private static string Fit(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "…";

    // ──────────────────────────────────────────────────────────
    //  Box drawing
    // ──────────────────────────────────────────────────────────

    private static void Header(string title, ConsoleColor color)
    {
        var head = $"┌─ {title} ";
        Console.ForegroundColor = color;
        Console.WriteLine();
        Console.WriteLine(head.Length >= BoxWidth ? head : head + new string('─', BoxWidth - head.Length));
        Console.ResetColor();
    }

    private static void Section(string name)
    {
        var head = $"│  ── {name} ";
        Console.WriteLine(head + new string('─', Math.Max(0, BoxWidth - 4 - head.Length)));
    }

    private static void Line(string label, string value, ConsoleColor? color = null)
    {
        if (color is { } c)
            Console.ForegroundColor = c;
        Console.WriteLine(label.Length == 0 ? $"│  {"",-18}  {value}" : $"│  {label,-18}: {value}");
        Console.ResetColor();
    }

    private static void Blank() => Console.WriteLine("│");

    private static void Footer(ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine("└" + new string('─', BoxWidth - 1));
        Console.ResetColor();
        Console.WriteLine();
    }
}
