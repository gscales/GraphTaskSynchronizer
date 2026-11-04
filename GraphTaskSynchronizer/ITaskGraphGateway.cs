namespace GraphTaskSynchronizer;

public enum TaskLookupStatus
{
    Found,
    NotFound,
    Failed
}

public sealed record TaskLookupResult(TaskLookupStatus Status, TaskSnapshot? Snapshot = null, string? Error = null)
{
    public static TaskLookupResult Found(TaskSnapshot snapshot) => new(TaskLookupStatus.Found, snapshot);
    public static TaskLookupResult NotFound() => new(TaskLookupStatus.NotFound);
    public static TaskLookupResult Failed(string error) => new(TaskLookupStatus.Failed, Error: error);
}

/// <summary>One round of todoTask delta for a list.</summary>
/// <param name="Changed">
/// Tasks added or changed since the delta link — every task in the list on an initial round.
/// </param>
/// <param name="RemovedIds">Tasks that left the list since the delta link (@removed).</param>
/// <param name="DeltaLink">Where the next round starts.</param>
/// <param name="Initial">True when the round started without a delta link (a full round).</param>
public sealed record DeltaPage(
    IReadOnlyList<TaskSnapshot> Changed,
    IReadOnlyList<string> RemovedIds,
    string DeltaLink,
    bool Initial);

/// <summary>
/// Graph no longer accepts a stored delta link (expired, or a 410 Gone after maintenance):
/// the list needs a full round, starting without a link.
/// </summary>
public sealed class DeltaResyncRequiredException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The Graph reads the sync engine needs. Kept behind an interface so the sync logic can
/// be exercised without a tenant (see the test project).
/// </summary>
public interface ITaskGraphGateway
{
    /// <summary>
    /// GETs one task from a list, with PercentComplete and the other task properties
    /// expanded. <see cref="TaskLookupStatus.NotFound"/> when the task is not in that list
    /// (deleted, or moved to another list — To Do task IDs change when a task moves);
    /// <see cref="TaskLookupStatus.Failed"/> for anything else.
    /// </summary>
    Task<TaskLookupResult> GetTaskAsync(string listId, string taskId, CancellationToken ct);

    /// <summary>Lists every task in a list, with the same $select/$expand. Throws on failure.</summary>
    Task<IReadOnlyList<TaskSnapshot>> ListTasksAsync(string listId, CancellationToken ct);

    /// <summary>
    /// One delta round for a list: from <paramref name="deltaLink"/> when given, otherwise an
    /// initial round that returns every task. With <paramref name="expandPercent"/> the initial
    /// request expands PidLidPercentComplete. Throws <see cref="DeltaResyncRequiredException"/>
    /// when Graph rejects the delta link, and other exceptions when the round fails.
    /// </summary>
    Task<DeltaPage> GetTaskDeltaAsync(string listId, string? deltaLink, bool expandPercent, CancellationToken ct);

    /// <summary>
    /// Translates To Do task IDs (restId) to immutable IDs (restImmutableEntryId) with
    /// translateExchangeIds. IDs Graph can't translate are left out of the result. Throws when
    /// the call itself fails (e.g. 403 without the User.Read.All application permission).
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ToImmutableIdsAsync(IReadOnlyCollection<string> taskIds, CancellationToken ct);

    /// <summary>
    /// The item's current restId for an immutable ID, or null when it can't be translated
    /// (the item no longer exists). Throws when the call itself fails.
    /// </summary>
    Task<string?> FromImmutableIdAsync(string immutableId, CancellationToken ct);
}
