using System.Text.Json.Serialization;

namespace GraphTaskSynchronizer;

// ──────────────────────────────────────────────────────────────
//  Wire shapes for the Graph REST calls the synchronizer makes
// ──────────────────────────────────────────────────────────────

public sealed class GraphCollection<T>
{
    [JsonPropertyName("value")]
    public List<T>? Value { get; set; }

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }

    /// <summary>On the last page of a delta round: the link that starts the next round.</summary>
    [JsonPropertyName("@odata.deltaLink")]
    public string? DeltaLink { get; set; }
}

public sealed class GraphErrorEnvelope
{
    [JsonPropertyName("error")]
    public GraphError? Error { get; set; }
}

public sealed class GraphError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>users/{id}/todo/lists/{id}</summary>
public sealed class TodoTaskListDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>"none", "defaultList" (the mailbox's Tasks folder) or "flaggedEmails".</summary>
    [JsonPropertyName("wellknownListName")]
    public string? WellknownListName { get; set; }

    [JsonPropertyName("isOwner")]
    public bool? IsOwner { get; set; }

    [JsonPropertyName("isShared")]
    public bool? IsShared { get; set; }
}

/// <summary>
/// users/{id}/todo/lists/{id}/tasks/{id}, including the singleValueExtendedProperties that
/// $expand returns — the part the Graph SDK's TodoTask model has no property for.
/// </summary>
public sealed class TodoTaskDto
{
    [JsonPropertyName("@odata.etag")]
    public string? ODataEtag { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>notStarted, inProgress, completed, waitingOnOthers, deferred.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("importance")]
    public string? Importance { get; set; }

    [JsonPropertyName("categories")]
    public string[]? Categories { get; set; }

    [JsonPropertyName("createdDateTime")]
    public DateTimeOffset? CreatedDateTime { get; set; }

    [JsonPropertyName("lastModifiedDateTime")]
    public DateTimeOffset? LastModifiedDateTime { get; set; }

    [JsonPropertyName("startDateTime")]
    public DateTimeTimeZoneDto? StartDateTime { get; set; }

    [JsonPropertyName("dueDateTime")]
    public DateTimeTimeZoneDto? DueDateTime { get; set; }

    [JsonPropertyName("completedDateTime")]
    public DateTimeTimeZoneDto? CompletedDateTime { get; set; }

    /// <summary>The task's notes. Text unless the request asked for HTML (it doesn't).</summary>
    [JsonPropertyName("body")]
    public ItemBodyDto? Body { get; set; }

    [JsonPropertyName("singleValueExtendedProperties")]
    public SingleValueExtendedProperty[]? SingleValueExtendedProperties { get; set; }

    /// <summary>In a delta response: the task left the list (deleted, or moved to another list).</summary>
    [JsonPropertyName("@removed")]
    public RemovedDto? Removed { get; set; }
}

/// <summary>The @removed annotation of a delta response; reason is "changed" or "deleted".</summary>
public sealed class RemovedDto
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>itemBody: the content and whether it is "text" or "html".</summary>
public sealed class ItemBodyDto
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }
}

public sealed class DateTimeTimeZoneDto
{
    [JsonPropertyName("dateTime")]
    public string? DateTime { get; set; }

    [JsonPropertyName("timeZone")]
    public string? TimeZone { get; set; }
}

public sealed class SingleValueExtendedProperty
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

/// <summary>One entry of the users/{id}/translateExchangeIds response (convertIdResult).</summary>
public sealed class ConvertIdResultDto
{
    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    /// <summary>Absent when the conversion failed.</summary>
    [JsonPropertyName("targetId")]
    public string? TargetId { get; set; }

    /// <summary>Why the conversion failed; absent when it succeeded.</summary>
    [JsonPropertyName("errorDetails")]
    public GraphError? ErrorDetails { get; set; }
}

/// <summary>A Graph subscription, as sent to and returned by /subscriptions.</summary>
public sealed class SubscriptionDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("resource")]
    public string? Resource { get; set; }

    [JsonPropertyName("changeType")]
    public string? ChangeType { get; set; }

    [JsonPropertyName("notificationUrl")]
    public string? NotificationUrl { get; set; }

    [JsonPropertyName("lifecycleNotificationUrl")]
    public string? LifecycleNotificationUrl { get; set; }

    [JsonPropertyName("expirationDateTime")]
    public DateTimeOffset? ExpirationDateTime { get; set; }

    [JsonPropertyName("clientState")]
    public string? ClientState { get; set; }

    [JsonPropertyName("latestSupportedTlsVersion")]
    public string? LatestSupportedTlsVersion { get; set; }
}
