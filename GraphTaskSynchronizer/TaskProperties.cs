using System.Globalization;

namespace GraphTaskSynchronizer;

/// <summary>
/// What the synchronizer asks Graph for on each todoTask: the typed To Do properties it
/// $selects, and — only for values To Do has no typed property for — MAPI properties via
/// $expand=singleValueExtendedProperties.
///
///   PidLidPercentComplete    Double     PSETID_Task 0x8102   0.0–1.0 (MS-OXOTASK 2.2.2.2.3)
///
/// Status, start/due/completed dates, importance and the notes (body) all have typed todoTask
/// properties, so they come from $select. Add an extended property here only when To Do has no typed
/// equivalent; it then flows into every GET and listing (surface it in
/// <see cref="TaskSnapshotFactory"/>).
/// </summary>
public static class TaskProperties
{
    /// <summary>PSETID_Task property set GUID.</summary>
    public const string PsetidTask = "{00062003-0000-0000-C000-000000000046}";

    /// <summary>PidLidPercentComplete — To Do has no typed percent-complete property.</summary>
    public const string PercentComplete = "Double " + PsetidTask + " Id 0x8102";

    public static IReadOnlyList<string> ExtendedPropertyIds { get; } =
    [
        PercentComplete
    ];

    /// <summary>
    /// Typed To Do properties requested with each task. "body" is the task's notes; it comes
    /// back as text, the same as from a delta query (which has no $select).
    /// </summary>
    public static IReadOnlyList<string> SelectFields { get; } =
    [
        "id",
        "status",
        "importance",
        "categories",
        "body",
        "createdDateTime",
        "lastModifiedDateTime",
        "startDateTime",
        "dueDateTime",
        "completedDateTime"
    ];

    /// <summary>$expand clause that pulls the extended properties above (unencoded).</summary>
    public static string ExpandClause { get; } =
        "singleValueExtendedProperties($filter=" +
        string.Join(" or ", ExtendedPropertyIds.Select(id => $"id eq '{id}'")) +
        ")";

    /// <summary>Query string for task GETs and listings, URL-encoded.</summary>
    public static string TaskQuery { get; } =
        "$select=" + string.Join(",", SelectFields) +
        "&$expand=" + Uri.EscapeDataString(ExpandClause);
}

/// <summary>
/// Compares Graph extended property IDs semantically. Graph does not always echo an ID
/// back in the exact form it was requested in (GUID case, hex width), so a plain string
/// comparison is fragile:
///
///   requested : Double {00062003-0000-0000-C000-000000000046} Id 0x8102
///   returned  : Double {00062003-0000-0000-c000-000000000046} Id 0x8102
///   also equal: Double {00062003-0000-0000-C000-000000000046} Id 0x00008102
/// </summary>
public static class ExtendedPropertyId
{
    public static bool AreEqual(string? a, string? b) =>
        a is not null && b is not null && Normalize(a) == Normalize(b);

    /// <summary>
    /// Canonical form: "type|tag" for tagged properties, "type|guid|id|0xNNNN" for named
    /// properties with a numeric ID, "type|guid|name|Name" for string-named properties.
    /// </summary>
    public static string Normalize(string id)
    {
        var parts = id.Trim().Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return string.Empty;

        var type = parts[0].ToLowerInvariant();

        // Tagged: "String 0x001A"
        if (parts.Length == 2)
            return $"{type}|{NormalizeHex(parts[1])}";

        // Named: "Double {guid} Id 0x8102" or "String {guid} Name Keywords"
        if (parts.Length == 4)
        {
            var guid = Guid.TryParse(parts[1], out var g)
                ? g.ToString("D")
                : parts[1].Trim('{', '}').ToLowerInvariant();

            if (parts[2].Equals("Id", StringComparison.OrdinalIgnoreCase))
                return $"{type}|{guid}|id|{NormalizeHex(parts[3])}";

            if (parts[2].Equals("Name", StringComparison.OrdinalIgnoreCase))
                return $"{type}|{guid}|name|{parts[3]}";
        }

        return string.Join(' ', parts).ToLowerInvariant();
    }

    private static string NormalizeHex(string token)
    {
        var digits = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
        return int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? $"0x{value:x4}"
            : token.ToLowerInvariant();
    }
}

/// <summary>
/// Extended property values indexed by normalised ID, so lookups tolerate the ID
/// variations described on <see cref="ExtendedPropertyId"/>.
/// </summary>
public sealed class ExtendedPropertyBag
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public ExtendedPropertyBag(IEnumerable<(string? Id, string? Value)>? properties)
    {
        if (properties is null) return;
        foreach (var (id, value) in properties)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            _values[ExtendedPropertyId.Normalize(id)] = value;
        }
    }

    public int Count => _values.Count;

    public bool Contains(string id) => _values.ContainsKey(ExtendedPropertyId.Normalize(id));

    public string? Get(string id) =>
        _values.TryGetValue(ExtendedPropertyId.Normalize(id), out var value) ? value : null;
}
