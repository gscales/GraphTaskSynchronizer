using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace GraphTaskSynchronizer;

/// <summary>
/// The synchronized replica: the last known state of every task in the synchronized
/// To Do lists, keyed by task ID and persisted to a JSON file so a restart picks up
/// where it left off (the startup reconcile then catches anything that changed while the
/// synchronizer was not running).
///
/// All mutations come from <see cref="TaskSyncEngine"/>'s single consumer, so ordering
/// is already serialised; the lock just keeps readers safe.
/// </summary>
public sealed class TaskStore
{
    // 1: folder-based layout. 2: To Do lists. 3: typed To Do status and dateTimeTimeZone
    // values (MAPI dates/flags dropped). 4: immutableId, previousListName and movedAt.
    // 5: deltaLinks. 6: notes (todoTask body). Formats 3–5 are read as is: the new fields start
    // empty, and their delta links are dropped so the startup reconcile is a full round, which
    // fills in notes (and immutable IDs) for every task. Older formats are rebuilt.
    private const int FormatVersion = 6;
    private const int OldestReadableVersion = 3;

    /// <summary>The first format whose entries carry notes; delta links from older ones are dropped.</summary>
    private const int NotesSinceVersion = 6;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, TaskRecord> _tasks = new(StringComparer.Ordinal);

    // Immutable ID → current To Do task ID, for recognising a task that moved between lists.
    private readonly Dictionary<string, string> _byImmutableId = new(StringComparer.Ordinal);

    // List ID → the @odata.deltaLink the next reconcile of that list starts from. Saved with
    // the tasks, in the same file, so a link always matches the replica it was applied to.
    private readonly Dictionary<string, string> _deltaLinks = new(StringComparer.Ordinal);
    private readonly ILogger<TaskStore> _logger;
    private readonly string _mailboxUserId;
    private bool _dirty;

    /// <summary>Full path of the replica file, or null for an in-memory store.</summary>
    public string? FilePath { get; }

    public TaskStore(string? filePath, string mailboxUserId, ILoggerFactory loggerFactory)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        _mailboxUserId = mailboxUserId;
        _logger = loggerFactory.CreateLogger<TaskStore>();
        Load();
    }

    public int Count
    {
        get { lock (_sync) return _tasks.Count; }
    }

    public TaskRecord? Get(string id)
    {
        lock (_sync)
            return _tasks.TryGetValue(id, out var record) ? record : null;
    }

    public IReadOnlyList<TaskRecord> GetAll()
    {
        lock (_sync)
            return _tasks.Values.ToList();
    }

    /// <summary>The task with this immutable ID, whatever its To Do ID is now.</summary>
    public TaskRecord? FindByImmutableId(string immutableId)
    {
        lock (_sync)
        {
            return _byImmutableId.TryGetValue(immutableId, out var id) && _tasks.TryGetValue(id, out var record)
                ? record
                : null;
        }
    }

    public void Upsert(TaskRecord record)
    {
        lock (_sync)
        {
            if (_tasks.TryGetValue(record.Id, out var previous))
                Unindex(previous);

            _tasks[record.Id] = record;
            Index(record);
            _dirty = true;
        }
    }

    public bool Remove(string id)
    {
        lock (_sync)
        {
            if (!_tasks.Remove(id, out var removed))
                return false;

            Unindex(removed);
            _dirty = true;
            return true;
        }
    }

    /// <summary>The delta link the next reconcile of this list starts from, if there is one.</summary>
    public string? GetDeltaLink(string listId)
    {
        lock (_sync)
            return _deltaLinks.TryGetValue(listId, out var link) ? link : null;
    }

    /// <summary>Stores (or, with null, forgets) the delta link for a list.</summary>
    public void SetDeltaLink(string listId, string? deltaLink)
    {
        lock (_sync)
        {
            var changed = deltaLink is null
                ? _deltaLinks.Remove(listId)
                : !_deltaLinks.TryGetValue(listId, out var current) || current != deltaLink;
            if (deltaLink is not null)
                _deltaLinks[listId] = deltaLink;
            _dirty |= changed;
        }
    }

    /// <summary>Forgets the delta links of lists that aren't synchronized any more.</summary>
    public void RetainDeltaLinks(IEnumerable<string> listIds)
    {
        var keep = listIds.ToHashSet(StringComparer.Ordinal);
        lock (_sync)
        {
            foreach (var listId in _deltaLinks.Keys.Where(id => !keep.Contains(id)).ToList())
            {
                _deltaLinks.Remove(listId);
                _dirty = true;
            }
        }
    }

    private void Index(TaskRecord record)
    {
        if (record.ImmutableId is not null)
            _byImmutableId[record.ImmutableId] = record.Id;
    }

    private void Unindex(TaskRecord record)
    {
        if (record.ImmutableId is not null
            && _byImmutableId.TryGetValue(record.ImmutableId, out var id)
            && id == record.Id)
            _byImmutableId.Remove(record.ImmutableId);
    }

    /// <summary>
    /// Writes the replica if anything changed since the last save. Written to a temp file
    /// and moved over the original so a crash mid-write never leaves a truncated file.
    /// </summary>
    public void SaveIfDirty()
    {
        if (FilePath is null)
            return;

        StoreFile file;
        lock (_sync)
        {
            if (!_dirty)
                return;

            file = new StoreFile
            {
                FormatVersion = FormatVersion,
                MailboxUserId = _mailboxUserId,
                SavedAt = DateTimeOffset.UtcNow,
                DeltaLinks = new Dictionary<string, string>(_deltaLinks, StringComparer.Ordinal),
                Tasks = _tasks.Values
                    .OrderBy(t => t.Snapshot.Subject, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(t => t.Id, StringComparer.Ordinal)
                    .ToList()
            };
            _dirty = false;
        }

        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(file, JsonOpts));
            File.Move(tempPath, FilePath, overwrite: true);

            _logger.LogDebug("[Store] Saved {Count} task(s) to {Path}.", file.Tasks.Count, FilePath);
        }
        catch (Exception ex)
        {
            lock (_sync) _dirty = true;   // try again after the next work item
            _logger.LogError(ex, "[Store] Failed to save the replica to {Path}.", FilePath);
        }
    }

    private void Load()
    {
        if (FilePath is null)
        {
            _logger.LogInformation("[Store] In-memory replica (no Sync:StorePath).");
            return;
        }

        if (!File.Exists(FilePath))
        {
            _logger.LogInformation("[Store] No replica at {Path} yet – the startup reconcile will build it.", FilePath);
            return;
        }

        try
        {
            var json = File.ReadAllText(FilePath);

            // Check the header first: a replica written in an older format (e.g. the
            // folder-based one) is simply rebuilt rather than half-read.
            var header = JsonSerializer.Deserialize<StoreHeader>(json, JsonOpts);
            if (header is null || header.FormatVersion < OldestReadableVersion || header.FormatVersion > FormatVersion)
            {
                _logger.LogInformation(
                    "[Store] {Path} is format {Found}, not {Expected} – starting from an empty replica; the " +
                    "startup reconcile will rebuild it.", FilePath, header?.FormatVersion, FormatVersion);
                return;
            }

            var file = JsonSerializer.Deserialize<StoreFile>(json, JsonOpts);
            if (file is null)
                return;

            if (!string.Equals(file.MailboxUserId, _mailboxUserId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "[Store] {Path} holds tasks for '{Other}', not '{Mailbox}' – starting from an empty " +
                    "replica (the file is replaced on the next save).",
                    FilePath, file.MailboxUserId, _mailboxUserId);
                return;
            }

            foreach (var record in file.Tasks)
            {
                _tasks[record.Id] = record;
                Index(record);
            }

            // A delta round from an older replica's link would only return what changed since,
            // leaving every other task without notes; without a link the next round is full.
            if (file.FormatVersion >= NotesSinceVersion)
            {
                foreach (var (listId, link) in file.DeltaLinks ?? new Dictionary<string, string>())
                    _deltaLinks[listId] = link;
            }

            _logger.LogInformation("[Store] Loaded {Count} task(s) from {Path} (saved {SavedAt:O}).",
                _tasks.Count, FilePath, file.SavedAt);

            if (file.FormatVersion < FormatVersion)
            {
                _logger.LogInformation(
                    "[Store] {Path} is format {Found}; it is upgraded to {Expected} on the next save. The startup " +
                    "reconcile is a full round, which fills in notes (and immutable IDs) for every task without " +
                    "reporting them as changes.", FilePath, file.FormatVersion, FormatVersion);
            }
        }
        catch (Exception ex)
        {
            var backup = FilePath + ".unreadable";
            try { File.Copy(FilePath, backup, overwrite: true); } catch { /* best effort */ }

            _logger.LogError(ex,
                "[Store] Could not read {Path} (copied to {Backup}) – starting from an empty replica; " +
                "the startup reconcile will rebuild it.", FilePath, backup);
        }
    }

    private sealed class StoreHeader
    {
        public int FormatVersion { get; set; }
    }

    private sealed class StoreFile
    {
        public int FormatVersion { get; set; }
        public string? MailboxUserId { get; set; }
        public DateTimeOffset SavedAt { get; set; }
        public Dictionary<string, string>? DeltaLinks { get; set; }
        public List<TaskRecord> Tasks { get; set; } = new();
    }
}
