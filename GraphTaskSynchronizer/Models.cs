using System.Text.Json.Serialization;

namespace GraphTaskSynchronizer;

// ──────────────────────────────────────────────────────────────
//  Graph change notification payload (change + lifecycle)
// ──────────────────────────────────────────────────────────────

public sealed class ChangeNotificationPayload
{
    [JsonPropertyName("value")]
    public ChangeNotification[] Value { get; set; } = [];
}

/// <summary>
/// A basic (no resource data) change notification. For todoTask subscriptions, Graph does
/// not support includeResourceData, so a notification carries only the task's ID and the
/// resource path — the synchronizer GETs the task to learn what changed.
/// </summary>
public sealed class ChangeNotification
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }

    [JsonPropertyName("subscriptionExpirationDateTime")]
    public DateTimeOffset? SubscriptionExpirationDateTime { get; set; }

    [JsonPropertyName("changeType")]
    public string? ChangeType { get; set; }

    /// <summary>e.g. Users/{userId}/todo/lists/{listId}/tasks/{taskId}</summary>
    [JsonPropertyName("resource")]
    public string? Resource { get; set; }

    [JsonPropertyName("resourceData")]
    public ResourceData? ResourceData { get; set; }

    [JsonPropertyName("clientState")]
    public string? ClientState { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    /// <summary>The tenant, as lifecycle notifications report it.</summary>
    [JsonPropertyName("organizationId")]
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Set only on lifecycle notifications. For todoTask subscriptions Graph sends
    /// "reauthorizationRequired"; "missed" and "subscriptionRemoved" are Outlook
    /// message/event/contact (and Teams) only, but are handled if they arrive.
    /// </summary>
    [JsonPropertyName("lifecycleEvent")]
    public string? LifecycleEvent { get; set; }
}

public sealed class ResourceData
{
    [JsonPropertyName("@odata.type")]
    public string? ODataType { get; set; }

    [JsonPropertyName("@odata.id")]
    public string? ODataId { get; set; }

    [JsonPropertyName("@odata.etag")]
    public string? ODataEtag { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

// ──────────────────────────────────────────────────────────────
//  Normalised task state
// ──────────────────────────────────────────────────────────────

/// <summary>todoTask status (To Do's taskStatus enum).</summary>
public enum TodoTaskStatus
{
    NotStarted,
    InProgress,
    Completed,
    WaitingOnOthers,
    Deferred
}

/// <summary>
/// A To Do dateTimeTimeZone value, kept exactly as Graph returned it (date-time plus the
/// zone it is expressed in) rather than reinterpreted.
/// </summary>
/// <param name="DateTime">ISO 8601 local date-time, normalised to yyyy-MM-ddTHH:mm:ss.</param>
/// <param name="TimeZone">The zone the value is in, e.g. "UTC".</param>
public sealed record TodoDateTime(string DateTime, string TimeZone);

/// <summary>
/// One task as the synchronizer understands it, built from a todoTask (GET or listing) by
/// <see cref="TaskSnapshotFactory"/>. Everything comes from To Do's typed properties except
/// PercentComplete, which To Do doesn't expose and is read from PidLidPercentComplete.
/// </summary>
public sealed record TaskSnapshot
{
    /// <summary>To Do task ID — the replica key. Changes if the task moves to another list.</summary>
    public required string Id { get; init; }

    /// <summary>todoTask title.</summary>
    public string? Subject { get; init; }

    /// <summary>todoTask status.</summary>
    public TodoTaskStatus? Status { get; init; }

    /// <summary>PidLidPercentComplete (singleValueExtendedProperties): 0.0–1.0. Null when the item does not carry it.</summary>
    public double? PercentComplete { get; init; }

    /// <summary>todoTask startDateTime.</summary>
    public TodoDateTime? StartDateTime { get; init; }

    /// <summary>todoTask dueDateTime.</summary>
    public TodoDateTime? DueDateTime { get; init; }

    /// <summary>todoTask completedDateTime.</summary>
    public TodoDateTime? CompletedDateTime { get; init; }

    public string? Importance { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>
    /// The task's notes — todoTask body, as plain text (<see cref="TaskNotes"/>); "" when the
    /// task has none. Null when not known: a replica entry saved before notes were tracked.
    /// </summary>
    public string? Notes { get; init; }

    public DateTimeOffset? CreatedDateTime { get; init; }
    public DateTimeOffset? LastModifiedDateTime { get; init; }

    /// <summary>The todoTask @odata.etag.</summary>
    public string? ChangeKey { get; init; }
}

/// <summary>What produced a change.</summary>
public enum SnapshotSource
{
    /// <summary>A change notification, followed by a GET of the task.</summary>
    Notification,

    /// <summary>A full listing of the list during a reconcile.</summary>
    Reconcile
}

/// <summary>A replica entry: the task's last known state plus the list it is in.</summary>
public sealed record TaskRecord
{
    public required TaskSnapshot Snapshot { get; init; }

    /// <summary>ID of the To Do list the task is in.</summary>
    public required string ListId { get; init; }

    public string? ListName { get; init; }

    /// <summary>
    /// The task's immutable ID (restImmutableEntryId), from translateExchangeIds — the To Do
    /// API can't return it. Unlike the To Do task ID it stays the same when the task moves to
    /// another list, which is how a move is recognised. Null until it could be translated.
    /// </summary>
    public string? ImmutableId { get; init; }

    /// <summary>The list the task was in before its last move between lists; null if it never moved.</summary>
    public string? PreviousListName { get; init; }

    /// <summary>When the last move between lists was detected.</summary>
    public DateTimeOffset? MovedAt { get; init; }

    public DateTimeOffset SyncedAt { get; init; }
    public SnapshotSource Source { get; init; }

    /// <summary>The current To Do task ID (it changes when the task moves to another list).</summary>
    [JsonIgnore]
    public string Id => Snapshot.Id;
}

public enum TaskChangeKind
{
    Added,
    Updated,

    /// <summary>
    /// The task moved to another list: it has a new To Do ID (<see cref="TaskChange.TaskId"/>;
    /// the old one is <c>Before.Id</c>) but the same immutable ID.
    /// </summary>
    Moved,

    Removed
}

public sealed record FieldChange(string Field, string Before, string After);

/// <summary>A change to the replica, handed to every <see cref="ITaskChangeSink"/>.</summary>
public sealed class TaskChange
{
    public required TaskChangeKind Kind { get; init; }
    public required string TaskId { get; init; }
    public TaskRecord? Before { get; init; }
    public TaskRecord? After { get; init; }
    public IReadOnlyList<FieldChange> Changes { get; init; } = [];
    public required SnapshotSource Source { get; init; }

    /// <summary>How the values were obtained, in words — in particular where PercentComplete came from.</summary>
    public string? SourceDetail { get; init; }

    /// <summary>What caused it, e.g. "notification: updated" or "reconcile (startup)".</summary>
    public string? Reason { get; init; }
}

public sealed class ReconcileSummary
{
    public required string Reason { get; init; }
    public IReadOnlyList<TaskList> Lists { get; init; } = [];
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Moved { get; set; }
    public int Unchanged { get; set; }
    public int Removed { get; set; }
    public List<string> FailedLists { get; } = new();

    /// <summary>Lists reconciled from their delta link: only what changed came back.</summary>
    public List<string> DeltaLists { get; } = new();

    /// <summary>Lists reconciled with a full round: every task came back.</summary>
    public List<string> FullLists { get; } = new();

    public int TotalTasks { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>A To Do list whose tasks are synchronized.</summary>
/// <param name="Id">todoTaskList ID.</param>
/// <param name="DisplayName">List name.</param>
/// <param name="WellknownListName">"defaultList" for the mailbox's Tasks folder, "flaggedEmails", or "none".</param>
/// <param name="Source">How the list was selected (for the startup log).</param>
public sealed record TaskList(string Id, string DisplayName, string? WellknownListName, string Source);
