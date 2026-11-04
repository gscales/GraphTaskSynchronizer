using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace GraphTaskSynchronizer;

/// <summary>
/// The synced-tasks page the webhook server serves at "/": every task in the replica in one
/// table — subject and notes, the list it is in (with "from …" for a task moved in the last day), a
/// progress bar for PercentComplete, status, due date and last-modified time — with a filter
/// chip per list. It refreshes itself every <see cref="RefreshSeconds"/> seconds, so changes
/// arriving through notifications appear without reloading.
///
/// The webhook port is published to the internet through ngrok, so the page is served to
/// local requests only (see <see cref="IsLocalRequest"/>); anything else gets a 404.
/// </summary>
public static class TaskPage
{
    public const int RefreshSeconds = 10;

    /// <summary>How long a task shows where it moved from.</summary>
    public static readonly TimeSpan ShowMovesFor = TimeSpan.FromHours(24);

    /// <summary>Content-Security-Policy for the page: inline style/script only, fetches to itself.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; " +
        "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>
    /// True only for a browser on this machine. Requests through the ngrok tunnel also arrive
    /// from 127.0.0.1 (the agent runs here), but ngrok always adds X-Forwarded-For /
    /// X-Forwarded-Host / X-Forwarded-Proto and keeps the public host name, so either check
    /// rejects them.
    /// </summary>
    public static bool IsLocalRequest(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
            return false;
        if (remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();
        if (!IPAddress.IsLoopback(remote))
            return false;

        var headers = context.Request.Headers;
        if (headers.ContainsKey("X-Forwarded-For")
            || headers.ContainsKey("X-Forwarded-Host")
            || headers.ContainsKey("Forwarded"))
            return false;

        var host = context.Request.Host.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || host is "127.0.0.1" or "[::1]" or "::1";
    }

    // ──────────────────────────────────────────────────────────
    //  Rendering
    // ──────────────────────────────────────────────────────────

    public static string Render(
        IReadOnlyList<TaskRecord> records,
        IReadOnlyList<TaskList> lists,
        string? mailbox,
        string? replicaPath,
        DateTimeOffset now)
    {
        var html = new StringBuilder(16 * 1024);
        var title = string.IsNullOrEmpty(mailbox) ? "Synced tasks" : $"Synced tasks · {mailbox}";

        html.Append($$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{E(title)}}</title>
            <noscript><meta http-equiv="refresh" content="30"></noscript>
            <style>{{Css}}</style>
            </head>
            <body>
            <header>
              <h1>Synced tasks</h1>
              <div class="sub">{{E(mailbox ?? "")}}{{(string.IsNullOrEmpty(mailbox) ? "" : " · ")}}Microsoft To Do → local replica</div>
            </header>
            <main>
            """);

        // The synchronized lists in Graph's order, then any list the replica still has tasks
        // for (until the next reconcile drops them). A row's data-list is its list's key, which
        // stays the same when lists are added or removed around it – so a chosen filter
        // (#list=key) keeps showing the same list across refreshes.
        var listOrder = lists.Select(l => (l.Id, Name: l.DisplayName))
            .Concat(records.Where(r => lists.All(l => l.Id != r.ListId))
                .GroupBy(r => r.ListId)
                .Select(g => (Id: g.Key, Name: g.First().ListName ?? g.Key)))
            .DistinctBy(l => l.Id)
            .ToList();
        var listKey = ListKeys(listOrder.Select(l => l.Id));

        var completed = records.Count(r => r.Snapshot.Status == TodoTaskStatus.Completed);
        html.Append($$"""
            <div class="summary">
              <div class="stat"><b>{{records.Count}}</b>tasks</div>
              <div class="stat"><b>{{records.Count - completed}}</b>open</div>
              <div class="stat"><b>{{completed}}</b>completed</div>
              <div class="stat"><b>{{listOrder.Count}}</b>{{(listOrder.Count == 1 ? "list" : "lists")}}</div>
            </div>
            <nav class="chips" aria-label="Filter by list">
            <a class="chip on" href="#" data-list="">All lists <span>{{records.Count}}</span></a>
            """);

        foreach (var (id, name) in listOrder)
        {
            html.Append($"""<a class="chip" href="#list={listKey[id]}" data-list="{listKey[id]}">{E(name)} <span>{records.Count(r => r.ListId == id)}</span></a>""");
            html.Append('\n');
        }
        html.Append("</nav>\n<section>\n");

        if (records.Count == 0)
        {
            html.Append("""<div class="empty">No tasks synced yet — the startup reconcile fills the replica.</div>""");
        }
        else
        {
            html.Append("""
                <div class="rows"><table>
                <thead><tr><th>Task</th><th class="lst">List</th><th class="prog">Progress</th><th class="status">Status</th><th class="due">Due</th><th class="wide">Modified</th></tr></thead>
                <tbody>
                """);

            // Open tasks first (soonest due first, undated last), then completed ones.
            var ordered = records
                .OrderBy(t => t.Snapshot.Status == TodoTaskStatus.Completed)
                .ThenBy(t => t.Snapshot.DueDateTime is null)
                .ThenBy(t => t.Snapshot.DueDateTime?.DateTime, StringComparer.Ordinal)
                .ThenBy(t => t.Snapshot.Subject, StringComparer.OrdinalIgnoreCase);

            foreach (var task in ordered)
                AppendTask(html, task, listKey[task.ListId], now);

            html.Append("</tbody></table></div>\n");
            html.Append("""<div class="empty filtered" hidden>No tasks in this list.</div>""");
        }
        html.Append("</section>\n");

        var lastSync = records.Count == 0 ? (DateTimeOffset?)null : records.Max(r => r.SyncedAt);
        html.Append($$"""
            <p class="meta">Updated {{now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}}
            · refreshes every {{RefreshSeconds}} s
            · last change synced {{(lastSync is null ? "never" : Local(lastSync.Value, "d MMM HH:mm:ss"))}}{{(replicaPath is null ? "" : $"<br>Replica file: <code>{E(replicaPath)}</code>")}}</p>
            </main>
            <script>
            (() => {
              const selected = () => (/^#list=([0-9a-z-]+)$/.exec(location.hash) || [])[1] || "";
              const applyFilter = () => {
                let key = selected();
                // The chosen list is gone (deleted, or no longer synchronized): show them all.
                if (key !== "" && ![...document.querySelectorAll(".chip")].some(chip => chip.dataset.list === key)) {
                  key = "";
                  history.replaceState(null, "", location.pathname + location.search);
                }
                let shown = 0;
                document.querySelectorAll("tbody tr").forEach(row => {
                  row.hidden = key !== "" && row.dataset.list !== key;
                  if (!row.hidden) shown++;
                });
                document.querySelectorAll(".chip").forEach(chip => chip.classList.toggle("on", chip.dataset.list === key));
                const empty = document.querySelector(".filtered");
                if (empty) empty.hidden = shown > 0;
              };
              const refresh = async () => {
                try {
                  const response = await fetch(location.pathname, { cache: "no-store" });
                  if (!response.ok) return;
                  const html = await response.text();
                  const next = new DOMParser().parseFromString(html, "text/html").querySelector("main");
                  const current = document.querySelector("main");
                  if (next && current) {
                    // Notes the reader unfolded stay unfolded.
                    const open = new Set([...current.querySelectorAll("details.notes[open]")].map(d => d.dataset.key));
                    current.replaceWith(next);
                    document.querySelectorAll("details.notes").forEach(d => { if (open.has(d.dataset.key)) d.open = true; });
                    applyFilter();
                  }
                } catch { /* synchronizer stopped – keep showing the last view */ }
              };
              window.addEventListener("hashchange", applyFilter);
              applyFilter();
              setInterval(refresh, {{RefreshSeconds * 1000}});
            })();
            </script>
            </body>
            </html>
            """);

        return html.ToString();
    }

    /// <summary>
    /// A short key per list ID for chips, rows and the #list= filter: the first 10 hex digits of
    /// its SHA-256 (list IDs are long), with "-2", "-3"… on the off chance two keys collide.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ListKeys(IEnumerable<string> listIds)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in listIds)
        {
            if (keys.ContainsKey(id))
                continue;

            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)), 0, 5).ToLowerInvariant();
            var candidate = key;
            for (var n = 2; !used.Add(candidate); n++)
                candidate = $"{key}-{n}";
            keys[id] = candidate;
        }

        return keys;
    }

    private static void AppendTask(StringBuilder html, TaskRecord task, string listKey, DateTimeOffset now)
    {
        var s = task.Snapshot;
        var done = s.Status == TodoTaskStatus.Completed;
        var percent = s.PercentComplete is { } p ? Math.Clamp(p, 0, 1) * 100 : (double?)null;
        var width = (percent ?? 0).ToString("0.#", CultureInfo.InvariantCulture);
        var percentText = percent is null
            ? """<span class="muted" title="PidLidPercentComplete is not set on this task">—</span>"""
            : TaskFormat.Percent(s.PercentComplete);

        var statusKey = s.Status?.ToString() ?? "unknown";
        var statusClass = "s-" + char.ToLowerInvariant(statusKey[0]) + statusKey[1..];

        var modified = s.LastModifiedDateTime is { } m
            ? $"""<span title="{E(m.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}">{E(Local(m, "d MMM HH:mm"))}</span>"""
            : """<span class="muted">—</span>""";

        // Recently moved: say where from.
        var moved = task.PreviousListName is { } from && task.MovedAt is { } at && now - at < ShowMovesFor
            ? $"""<div class="moved" title="Moved {E(Local(at, "d MMM HH:mm"))}">from {E(from)}</div>"""
            : "";

        var ids = $"To Do ID: {s.Id}\nImmutable ID: {task.ImmutableId ?? "(not translated yet)"}";

        html.Append($$"""
            <tr data-list="{{listKey}}"{{(done ? " class=\"done\"" : "")}}>
              <td class="subject"><span class="title" title="{{E(ids)}}">{{E(TaskFormat.Text(s.Subject))}}</span>{{Categories(s.Categories)}}{{Notes(task)}}</td>
              <td class="lst">{{E(task.ListName ?? task.ListId)}}{{moved}}</td>
              <td class="prog"><div class="progress"><div class="bar"><span style="width:{{width}}%"></span></div><span class="pct">{{percentText}}</span></div></td>
              <td class="status"><span class="badge {{statusClass}}">{{E(TaskFormat.Status(s.Status))}}</span></td>
              <td class="{{(s.DueDateTime is null ? "due none" : "due")}}">{{Due(s.DueDateTime)}}</td>
              <td class="wide muted">{{modified}}</td>
            </tr>
            """);
    }

    /// <summary>
    /// A dueDateTime exactly as Graph returned it — date, then the time and zone it is
    /// expressed in — without converting it.
    /// </summary>
    private static string Due(TodoDateTime? due)
    {
        if (due is null)
            return """<span class="muted">—</span>""";

        if (DateTime.TryParse(due.DateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return $"""{value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} <span class="muted">{value.ToString("HH:mm", CultureInfo.InvariantCulture)} {E(due.TimeZone)}</span>""";
        }

        return E(TaskFormat.DateTime(due));
    }

    /// <summary>Notes up to this long, on one line, are shown in full; longer ones fold.</summary>
    internal const int InlineNotesLength = 140;

    /// <summary>
    /// The task's notes under its subject: in full when short, otherwise the first line with
    /// "more", which unfolds the rest (line breaks kept). An unfolded note stays open across
    /// the page's refreshes — data-key is how the script finds it again.
    /// </summary>
    private static string Notes(TaskRecord task)
    {
        var notes = task.Snapshot.Notes;
        if (string.IsNullOrEmpty(notes))
            return "";

        if (!notes.Contains('\n') && notes.Length <= InlineNotesLength)
            return $"""<div class="notes">{E(notes)}</div>""";

        var newline = notes.IndexOf('\n');
        var first = newline < 0 ? notes : notes[..newline];
        var rest = newline < 0 ? "" : notes[(newline + 1)..];

        return $"""<details class="notes" data-key="{E(task.ImmutableId ?? task.Id)}"><summary><span class="first">{E(first)}</span><span class="more"></span></summary>{(rest.Length == 0 ? "" : $"""<div class="rest">{E(rest)}</div>""")}</details>""";
    }

    private static string Categories(IReadOnlyList<string> categories) =>
        categories.Count == 0
            ? ""
            : " " + string.Concat(categories.Select(c => $"""<span class="cat">{E(c)}</span>"""));

    private static string Local(DateTimeOffset value, string format) =>
        value.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    private const string Css = """
        :root{--bg:#f5f6f8;--card:#fff;--text:#1d2330;--muted:#6b7280;--line:#e6e8ec;--track:#e9edf3;--fill:#2563eb;--done:#16a34a;--move:#7c3aed}
        @media (prefers-color-scheme:dark){:root{--bg:#0f1218;--card:#171b23;--text:#e6e9ef;--muted:#9aa3b2;--line:#262c38;--track:#262c38;--fill:#60a5fa;--done:#4ade80;--move:#c4b5fd}}
        *{box-sizing:border-box}
        [hidden]{display:none!important}
        body{margin:0;font:14px/1.45 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;background:var(--bg);color:var(--text)}
        header,main{max-width:1200px;margin:0 auto;padding:0 16px}
        header{padding-top:28px}
        h1{font-size:22px;margin:0 0 2px}
        .sub,.muted,.meta{color:var(--muted)}
        .summary{display:flex;flex-wrap:wrap;gap:12px;margin:20px 0 6px}
        .stat{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 16px;min-width:110px;color:var(--muted)}
        .stat b{display:block;font-size:22px;color:var(--text);font-variant-numeric:tabular-nums}
        .chips{display:flex;flex-wrap:wrap;gap:8px;margin:16px 0 0}
        .chip{display:inline-flex;align-items:center;gap:6px;padding:4px 11px;border:1px solid var(--line);border-radius:999px;background:var(--card);color:var(--text);text-decoration:none;font-size:13px}
        .chip span{color:var(--muted);font-variant-numeric:tabular-nums}
        .chip:hover{border-color:var(--muted)}
        .chip.on{border-color:var(--fill);color:var(--fill);font-weight:500}
        .chip.on span{color:inherit}
        section{background:var(--card);border:1px solid var(--line);border-radius:12px;margin:12px 0 18px}
        .rows{overflow-x:auto}
        table{width:100%;border-collapse:collapse}
        th{text-align:left;font-size:12px;font-weight:600;color:var(--muted);padding:10px 16px 8px}
        th.lst{width:150px}th.prog{width:170px}th.status{width:150px}th.due{width:185px}th.wide{width:120px}
        td{padding:10px 16px;border-top:1px solid var(--line);vertical-align:middle}
        .subject{font-weight:500;overflow-wrap:anywhere}
        .notes{margin-top:4px;padding-left:8px;border-left:2px solid var(--line);font-size:12.5px;font-weight:400;color:var(--muted);white-space:pre-wrap}
        details.notes{contain:inline-size}
        details.notes summary{display:flex;gap:6px;cursor:pointer;list-style:none;white-space:normal}
        details.notes summary::-webkit-details-marker{display:none}
        details.notes .first{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        details.notes .more{flex:none;color:var(--fill)}
        details.notes .more::before{content:"more"}
        details.notes[open] .first{white-space:pre-wrap}
        details.notes[open] .more::before{content:"less"}
        details.notes .rest{margin-top:2px}
        .lst{overflow-wrap:anywhere}
        .moved{font-size:12px;color:var(--move)}
        .cat{display:inline-block;margin-left:6px;font-size:11px;font-weight:400;color:var(--muted);border:1px solid var(--line);border-radius:4px;padding:0 5px}
        .progress{display:flex;align-items:center;gap:8px;min-width:120px}
        .bar{flex:1;height:8px;background:var(--track);border-radius:999px;overflow:hidden}
        .bar span{display:block;height:100%;background:var(--fill);border-radius:999px}
        .pct{width:46px;text-align:right;font-variant-numeric:tabular-nums}
        .badge{display:inline-block;font-size:12px;padding:2px 9px;border-radius:999px;white-space:nowrap}
        .s-notStarted{background:rgba(107,114,128,.14);color:#4b5563}
        .s-inProgress{background:rgba(37,99,235,.13);color:#1d4ed8}
        .s-waitingOnOthers{background:rgba(217,119,6,.15);color:#92400e}
        .s-deferred{background:rgba(124,58,237,.13);color:#6d28d9}
        .s-completed{background:rgba(22,163,74,.14);color:#166534}
        .s-unknown{background:rgba(107,114,128,.14);color:var(--muted)}
        @media (prefers-color-scheme:dark){.s-notStarted{color:#cbd5e1}.s-inProgress{color:#93c5fd}.s-waitingOnOthers{color:#fcd34d}.s-deferred{color:#c4b5fd}.s-completed{color:#86efac}}
        tr.done td{color:var(--muted)}
        tr.done .subject{font-weight:400}
        tr.done .subject .title{text-decoration:line-through}
        tr.done .bar span{background:var(--done)}
        .due{white-space:nowrap}
        .empty{padding:14px 16px;color:var(--muted)}
        .meta{font-size:12px;margin:8px 0 32px}
        code{font-size:12px;overflow-wrap:anywhere}
        @media (max-width:1080px){.wide{display:none}}
        @media (max-width:1000px){th.prog{width:130px}.progress{min-width:100px}}
        @media (max-width:880px){
          header{padding-top:20px}
          .summary{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:8px}
          .stat{min-width:0;padding:8px 10px;font-size:12px}
          .stat b{font-size:18px}
          thead{display:none}
          table,tbody{display:block}
          tr{display:grid;grid-template-columns:auto 1fr;align-items:center;gap:6px 12px;padding:12px 16px;border-top:1px solid var(--line)}
          tbody tr:first-child{border-top:0}
          td{padding:0;border:0}
          td.subject,td.lst,td.prog{grid-column:1/-1}
          td.lst{font-size:12px;color:var(--muted);margin-top:-4px}
          td.lst .moved{display:inline}
          td.lst .moved::before{content:" · "}
          .progress{min-width:0}
          td.due{justify-self:end;font-size:13px}
          td.due::before{content:"Due ";color:var(--muted)}
          td.due.none{display:none}
        }
        @media (max-width:360px){.summary{grid-template-columns:repeat(2,minmax(0,1fr))}}
        """;
}
