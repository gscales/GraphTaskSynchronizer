using System.Globalization;

namespace GraphTaskSynchronizer;

/// <summary>Field-level comparison of two replica entries.</summary>
public static class TaskDiff
{
    public const string PercentCompleteField = "Percent complete";
    public const string NotesField = "Notes";
    public const string ListField = "List";

    /// <summary>Width of the one-line notes shown in a <see cref="FieldChange"/>.</summary>
    public const int NotesChangeWidth = 60;

    /// <summary>
    /// The tracked fields that differ between <paramref name="before"/> and
    /// <paramref name="after"/>, PercentComplete first. ChangeKey and LastModified are
    /// deliberately not compared — they move on every save, even when nothing a task
    /// consumer cares about changed.
    ///
    /// Notes are compared in full, but only when both sides know them: a replica entry saved
    /// before notes were tracked has none on record, and filling them in isn't a change.
    /// The <see cref="FieldChange"/> carries one-line excerpts that start just before the
    /// first difference; the records in the <see cref="TaskChange"/> have the full text.
    /// </summary>
    public static IReadOnlyList<FieldChange> Compare(TaskRecord before, TaskRecord after)
    {
        var a = before.Snapshot;
        var b = after.Snapshot;
        var changes = new List<FieldChange>();

        void Add(string field, string oldValue, string newValue)
        {
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                changes.Add(new FieldChange(field, oldValue, newValue));
        }

        if (!PercentEquals(a.PercentComplete, b.PercentComplete))
            changes.Add(new FieldChange(PercentCompleteField,
                TaskFormat.Percent(a.PercentComplete), TaskFormat.Percent(b.PercentComplete)));

        Add("Status", TaskFormat.Status(a.Status), TaskFormat.Status(b.Status));
        Add("Subject", TaskFormat.Text(a.Subject), TaskFormat.Text(b.Subject));
        Add("Start", TaskFormat.DateTime(a.StartDateTime), TaskFormat.DateTime(b.StartDateTime));
        Add("Due", TaskFormat.DateTime(a.DueDateTime), TaskFormat.DateTime(b.DueDateTime));
        Add("Completed", TaskFormat.DateTime(a.CompletedDateTime), TaskFormat.DateTime(b.CompletedDateTime));
        Add("Importance", TaskFormat.Text(a.Importance), TaskFormat.Text(b.Importance));
        Add("Categories", TaskFormat.Categories(a.Categories), TaskFormat.Categories(b.Categories));

        if (a.Notes is { } oldNotes && b.Notes is { } newNotes
            && !string.Equals(oldNotes, newNotes, StringComparison.Ordinal))
        {
            var (oldLine, newLine) = TaskNotes.ChangeWindow(
                TaskNotes.OneLine(oldNotes), TaskNotes.OneLine(newNotes), NotesChangeWidth);
            changes.Add(new FieldChange(NotesField, TaskFormat.Text(oldLine), TaskFormat.Text(newLine)));
        }

        if (!string.Equals(before.ListId, after.ListId, StringComparison.Ordinal))
            changes.Add(new FieldChange(ListField,
                before.ListName ?? before.ListId, after.ListName ?? after.ListId));

        return changes;
    }

    private static bool PercentEquals(double? x, double? y)
    {
        if (x is null || y is null)
            return x is null && y is null;
        return Math.Abs(x.Value - y.Value) < 1e-9;
    }
}

/// <summary>Display formatting shared by the diff and the console output.</summary>
public static class TaskFormat
{
    public const string None = "(none)";

    /// <summary>PidLidPercentComplete is 0.0–1.0; shown as a percentage.</summary>
    public static string Percent(double? value) =>
        value is null
            ? None
            : (value.Value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    public static string Status(TodoTaskStatus? status) => status switch
    {
        null => None,
        TodoTaskStatus.NotStarted => "Not started",
        TodoTaskStatus.InProgress => "In progress",
        TodoTaskStatus.Completed => "Completed",
        TodoTaskStatus.WaitingOnOthers => "Waiting on others",
        TodoTaskStatus.Deferred => "Deferred",
        _ => $"Unknown ({(int)status})"
    };

    /// <summary>A To Do dateTimeTimeZone as Graph returned it, e.g. "2026-10-31T00:00:00 (UTC)".</summary>
    public static string DateTime(TodoDateTime? value) =>
        value is null ? None : $"{value.DateTime} ({value.TimeZone})";

    public static string Text(string? value) => string.IsNullOrEmpty(value) ? None : value;

    public static string Categories(IReadOnlyList<string> categories) =>
        categories.Count == 0 ? None : string.Join(", ", categories);

    public static string Timestamp(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? None;
}
