using System.Globalization;

namespace GraphTaskSynchronizer;

/// <summary>
/// Turns a todoTask (from a GET or a listing) into a <see cref="TaskSnapshot"/>, so
/// notification refreshes and reconciles compare like-for-like.
///
/// Every value comes from To Do's typed properties except PercentComplete, which To Do
/// doesn't expose and is read from the expanded PidLidPercentComplete.
/// </summary>
public static class TaskSnapshotFactory
{
    public static TaskSnapshot FromTodoTask(TodoTaskDto task)
    {
        if (string.IsNullOrEmpty(task.Id))
            throw new ArgumentException("todoTask has no id.", nameof(task));

        var props = new ExtendedPropertyBag(task.SingleValueExtendedProperties?.Select(p => (p.Id, p.Value)));

        return new TaskSnapshot
        {
            Id = task.Id,
            Subject = task.Title,
            Status = ParseTodoStatus(task.Status),
            PercentComplete = ParseDouble(props.Get(TaskProperties.PercentComplete)),
            StartDateTime = ParseTodoDateTime(task.StartDateTime),
            DueDateTime = ParseTodoDateTime(task.DueDateTime),
            CompletedDateTime = ParseTodoDateTime(task.CompletedDateTime),
            Importance = task.Importance?.ToLowerInvariant(),
            Categories = task.Categories ?? [],
            Notes = task.Body is null ? null : TaskNotes.FromBody(task.Body.Content, task.Body.ContentType),
            CreatedDateTime = task.CreatedDateTime,
            LastModifiedDateTime = task.LastModifiedDateTime,
            ChangeKey = task.ODataEtag
        };
    }

    /// <summary>Graph renders PT_DOUBLE as an invariant-culture string, e.g. "0.75".</summary>
    internal static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    internal static TodoTaskStatus? ParseTodoStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "notstarted" => TodoTaskStatus.NotStarted,
        "inprogress" => TodoTaskStatus.InProgress,
        "completed" => TodoTaskStatus.Completed,
        "waitingonothers" => TodoTaskStatus.WaitingOnOthers,
        "deferred" => TodoTaskStatus.Deferred,
        _ => null
    };

    /// <summary>
    /// Keeps a dateTimeTimeZone as Graph returned it — the date-time in the zone Graph states —
    /// trimming the 7-digit fraction so equal values compare equal however they were written.
    /// </summary>
    internal static TodoDateTime? ParseTodoDateTime(DateTimeTimeZoneDto? value)
    {
        if (string.IsNullOrWhiteSpace(value?.DateTime))
            return null;

        var dateTime = DateTime.TryParse(value.DateTime, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)
            ? parsed.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            : value.DateTime;

        return new TodoDateTime(dateTime, string.IsNullOrWhiteSpace(value.TimeZone) ? "UTC" : value.TimeZone);
    }
}
