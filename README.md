# Graph Task Synchronizer

A C# (.NET 8) console application that keeps a local replica of a Microsoft 365 mailbox's
tasks — across **all of its To Do lists** — in sync using Microsoft Graph change notifications
on the **To Do API**. It reads **PidLidPercentComplete** for every task through
`singleValueExtendedProperties`, stores each task's **immutable ID** (from
`translateExchangeIds`) next to its To Do ID, and uses it to recognise a task that **moved
between lists**.

It is built from [DeletedItemWatcher](https://github.com/gscales/DeletedItemWatcher): the
Kestrel webhook, the subscription lifecycle and the single sequential notification queue
carry over; the deleted-item correlation is replaced by a task replica, a reconcile loop and
lifecycle-notification handling.

## Why the To Do API — and why there are no rich notifications

- **Tasks aren't messages.** IPM.Task items are not returned by the message endpoint, and
  the Outlook task API (`outlookTask`) has been retired, so tasks are reached through To Do
  (`todoTask`). Each To Do list is a task folder; the list whose `wellknownListName` is
  `defaultList` is the mailbox's Tasks folder.
- **todoTask subscriptions are basic only.** Graph supports change notifications on
  `users/{id}/todo/lists/{listId}/tasks`, but todoTask is not one of the resources that
  support `includeResourceData`. PercentComplete therefore can't travel *in* the
  notification: each notification is followed by a GET of the task.
- **PercentComplete is the only extended property.** To Do has no percent-complete field.
  todoTask does expose `singleValueExtendedProperties` through `$expand`, so the GET asks for
  `Double {00062003-0000-0000-C000-000000000046} Id 0x8102` (PidLidPercentComplete).
  Everything that has a typed todoTask property — status, start/due/completed dates,
  importance — is read from that property instead.
- **REST, not the Graph SDK.** The Graph .NET SDK's `TodoTask` model has no
  `SingleValueExtendedProperties` property (checked v5.56.0 through 5.105.0), so the app
  calls Graph over REST with small DTOs (`GraphModels.cs`) and Azure.Identity supplies the
  token. No encryption certificate is needed, since nothing arrives encrypted.

## How it works

One subscription per To Do list (every list in the mailbox by default), all pointed at the
same webhook:

```http
POST /subscriptions
{
  "resource": "users/{id}/todo/lists/{listId}/tasks",
  "changeType": "created,updated,deleted",
  "notificationUrl": "https://…/api/notifications",
  "lifecycleNotificationUrl": "https://…/api/lifecycle",
  "clientState": "…",
  "expirationDateTime": "…"            // todoTask maximum: 4,230 minutes
}
```

Every notification goes onto an in-process queue drained by a single consumer (as in
DeletedItemWatcher), alongside reconcile requests. For each one the task is fetched with:

```http
GET /users/{id}/todo/lists/{listId}/tasks/{taskId}
    ?$select=id,title,status,importance,categories,body,createdDateTime,lastModifiedDateTime,
             startDateTime,dueDateTime,completedDateTime
    &$expand=singleValueExtendedProperties($filter=
         id eq 'Double {00062003-0000-0000-C000-000000000046} Id 0x8102')     ← PidLidPercentComplete
```

A change made only to % complete in classic Outlook raises a todoTask `updated`
notification, so percent-complete edits are picked up like any other change.

```
created / updated                    deleted (or a GET that 404s)          reconcile
      │                                     │                                  │
 GET the task in its list             GET the task in its list           todoTask delta per list,
      │                                     │                            from its stored deltaLink
 found, known ID → diff:              found → stale delete, refreshed    (no link → full round)
   Updated / no change                404   → resolve its immutable ID:        │
 found, new ID → translateExchangeIds          gone      → Removed       translate unknown IDs
   immutable ID already in replica?            still     → reconcile     (one call per 1,000)
     yes → Moved (re-keyed)                     exists     decides             │
     no  → Added                                                        known ID → Updated
                                                                        known immutable ID
                                                                          → Moved
                                                                        neither → Added
                                                                        @removed (or, in a full
                                                                          round, missing) → Removed
                                                                        store the new deltaLink
      │
 ITaskChangeSink(s): console output, or wherever you push changes
```

### Where each value comes from

Typed todoTask properties wherever To Do has one; an extended property only where it doesn't:

| Value | Source |
|---|---|
| Percent complete | PidLidPercentComplete via `$expand` — To Do has no typed equivalent |
| Status | `status` (notStarted, inProgress, completed, waitingOnOthers, deferred) |
| Start / Due / Completed | `startDateTime` / `dueDateTime` / `completedDateTime` |
| Subject, importance, categories | `title`, `importance`, `categories` |
| Notes | `body` (itemBody), as plain text — see [Notes](#notes) |
| Created / modified, version | `createdDateTime`, `lastModifiedDateTime`, `@odata.etag` |

PidLidPercentComplete is `PtypFloating64`, 0.0–1.0 (MS-OXOTASK 2.2.2.2.3); it is shown as a
percentage. The `dateTimeTimeZone` values are kept exactly as Graph returns them — date-time
plus the zone it is expressed in — and shown that way, e.g. `2026-10-31T00:00:00 (UTC)`.

To add another value later, prefer a typed property in `TaskProperties.SelectFields`; add
to `TaskProperties.ExtendedPropertyIds` only when To Do has no typed equivalent.

Each reported change says where PercentComplete came from:

| Source line | Meaning |
|---|---|
| `GET after notification — PercentComplete from singleValueExtendedProperties` | Read from the expanded MAPI property. |
| `GET after notification — PidLidPercentComplete is not set on this item` | Graph returned no value for it (the item doesn't carry the property). |
| `delta query (reconcile)` / `full delta round (reconcile)` | Returned by the todoTask delta of a reconcile, with the PercentComplete `$expand`. |
| `list listing (reconcile)` / `GET after delta query (reconcile)` | Read by listing the list (full round) or a GET of the task, because the delta doesn't carry PercentComplete (see [Reconcile with delta queries](#reconcile-with-delta-queries)). |

`Sync:LogTaskJson=true` logs the raw JSON of every task GET, showing exactly what Graph
returns in `singleValueExtendedProperties`.

### Notes

A task's notes are its typed `body` property, tracked like any other field: an edit is
reported as an Updated change, and moves carry it under "Also changed".

- **Read as text everywhere.** To Do returns the body as text by default (`contentType:
  "text"`, line breaks as CRLF), from a GET, a listing and a delta query alike. The
  synchronizer keeps that default: it doesn't send `Prefer: outlook.body-content-type`, so all
  three read paths return the same notes. Line endings become `\n`, and trailing spaces and
  surrounding blank lines are dropped. If a body ever arrives as HTML, it is converted to the
  same text: blocks and `<br>` become line breaks, list items get `• `, and tags are removed.
  Formatting such as bold isn't kept; To Do's text form doesn't have it either.
- **What a change shows.** The console box lists the lines that went (`-`) and came (`+`)
  rather than two copies of the whole note. For a single edited line, both versions start just
  before the first changed character, so an edit near the end of a long line is still visible.
  The `FieldChange` that sinks and the log receive has one-line excerpts (`line · line`) that
  start the same way, and the `Before`/`After` records carry the full text.
- **Upgrading.** Replica entries saved before notes were tracked (formats 3–5) have no notes on
  record (`notes` is absent, not empty). Their delta links are dropped, so the startup
  reconcile is a full round. That round fills in every task's notes without reporting them as
  changes. After that, notes are compared like any other field.
- **Where you see them.** On the page, a short note sits under the subject, and a longer one
  folds to its first line with **more**/**less**. An unfolded note stays open across the
  page's 10-second refresh. The **L** listing shows the notes on one line under each task.

### Immutable IDs and moves between lists

A To Do task gets a **new ID when it moves to another list** (each list is a folder, and the
ID changes with the folder). The To Do API doesn't support `Prefer: IdType="ImmutableId"`, so
the synchronizer translates each task ID it hasn't seen before:

```http
POST /users/{id}/translateExchangeIds
{ "inputIds": [ "{todo-task-id}", … ], "sourceIdType": "restId", "targetIdType": "restImmutableEntryId" }
```

and stores the result as `immutableId` on the task's replica entry. An immutable ID stays the
same when the item moves to another folder in the mailbox, so:

- **The new list is notified first.** The `created` notification brings a task ID the replica
  doesn't know. Its immutable ID is already in the replica under the old ID, so the entry is
  re-keyed to the new ID and list and reported once, as **Moved**. The old list's `deleted`
  notification then finds nothing left to do.
- **The old list is notified first.** The GET in the old list returns 404. The stored
  immutable ID is translated back (`restImmutableEntryId` → `restId`). If it no longer
  resolves, the task was deleted: **Removed**. If it still resolves, the item exists
  somewhere: another list (a move) or Deleted Items (a delete). The entry is kept and a
  reconcile settles it, or the new list's `created` notification does if it comes first.
  Telling the two apart through the reconcile's list listings doesn't depend on whether a GET
  by ID checks the list in its URL.
- **A reconcile** translates every task ID it hasn't got an immutable ID for (one call per
  1,000 IDs; this also fills in replicas written before immutable IDs were kept). A task
  whose immutable ID the replica holds under another ID is reported as Moved.

A **Moved** change has the new ID as `TaskId` and the old entry as `Before`. Its changes list
`List: Tasks → Website relaunch`, plus any field edited at the same time. The entry
remembers `previousListName` and `movedAt`.

`translateExchangeIds` needs the **User.Read.All** application permission. Without it the
call is refused, the log says so, and moves show as Removed + Added. Translation is retried
every 30 minutes, so granting the permission takes effect without a restart. A task ID that
Graph can't translate on its own (it reports each ID separately) is simply stored without an
immutable ID.

### Lists

Every To Do list in the mailbox is synchronized by default, since a move can only be seen when
both lists are watched. `Tasks:Lists` can restrict that (see Configuration).

Graph has no change notifications for the lists themselves (only for the tasks in a list), so
a **list reconcile** runs every `Sync:ListReconcileIntervalSeconds` (60 by default):

```http
GET /users/{id}/todo/lists/delta          ← first round: every list, and a deltaLink
GET {the deltaLink of the last round}     ← afterwards: only lists added, changed or removed
```

When a round reports anything, or when there's no link yet, the lists are read in full
(`GET /users/{id}/todo/lists`, which is what `Tasks:Lists` is matched against) and applied:

- a **new list** is subscribed to, and the next reconcile runs a full round for it, so its tasks
  are synchronized right away. Its chip and count appear on the page and under **L**.
- a **deleted list's** subscription is deleted, and its tasks are removed from the replica
- a **renamed list** is renamed in the replica (no change is reported for its tasks)

The lists are also read straight away, before the reconcile, when you press **R** or **F**,
before the periodic task reconcile, and when a task leaves a list but still exists. That last
case is how a task dragged into a list created a moment ago is reported as **Moved** rather
than Removed + Added: the new list is picked up first, so the same reconcile sees the task in
it. The list reconcile runs from startup on, even while the public URL check is still waiting.
A list created then is synchronized at once and subscribed with the others. Every 5-minute
subscription check also reads the lists in full, as a safety net. If todoTaskList delta fails
(other than asking for a resync), the lists are read in full at each check for 30 minutes
before delta is tried again.

A list that can't be subscribed to (a list shared from another mailbox, for example) is
retried after 5, 10, 20, 40 and then every 60 minutes; the reconcile keeps it up to date
meanwhile.

### Keeping the replica correct

- **Subscribe, then snapshot.** On startup the subscriptions are created first and the
  reconcile runs after, so anything that changes while the reconcile is reading arrives as a
  notification queued behind it.
- **Every notification reads current state.** Because the task is fetched rather than read
  from the notification, out-of-order notifications can't apply stale values. Deletes are
  confirmed by a 404 before anything is removed, and a delete for a task that is still in
  the list is treated as stale.
- **Ordering guard.** A snapshot whose `lastModifiedDateTime` is older than the replica's copy
  is ignored rather than rolling the task back.
- **Moves between lists** are matched by immutable ID and reported once, as Moved (see
  above).
- **Reconcile** uses todoTask delta (see [Reconcile with delta queries](#reconcile-with-delta-queries))
  and runs at startup, every `Sync:ReconcileIntervalMinutes`, when the list reconcile finds
  lists created or deleted, when a task leaves a list but still exists (after reading the lists,
  in case it moved into a new one), whenever a subscription had to be
  recreated or adopted or a reauthorization challenge was answered late (see below), and on a
  `missed` lifecycle event. todoTask subscriptions never get `missed`,
  so the periodic reconcile is the safety net for dropped notifications. If a list can't be
  read, none of its replica entries are removed that round.
- **Lifecycle notifications.** For todoTask Graph sends `reauthorizationRequired` — see
  [Reauthorization](#reauthorization). `missed` and `subscriptionRemoved` (Outlook/Teams
  resources only) are handled too if they ever arrive.
- **Persistence.** The replica is written to `Sync:StorePath` (default `task-replica.json`)
  after each change, via a temp file and an atomic move. Each entry holds the task's To Do
  ID, `immutableId`, list, last known state and, after a move, `previousListName` and
  `movedAt`. The file also holds each list's `deltaLinks` entry (format 5) and each task's
  `notes` (format 6), so a restart carries on from where the last reconcile stopped. Formats
  3–5 are read as is and upgraded. Their delta links are dropped, so the startup reconcile is
  a full round, which fills in notes and immutable IDs. A replica for another mailbox or in an
  older format is rebuilt, and an unreadable one is copied aside and rebuilt.

Graph documents todoTask notification latency as under two minutes on average and up to 15
minutes at most.

### Reconcile with delta queries

A reconcile runs one [todoTask delta](https://learn.microsoft.com/graph/api/todotask-delta)
round per list, starting from that list's `@odata.deltaLink`, which is stored in the replica:

```http
GET /users/{id}/todo/lists/{listId}/tasks/delta
    ?$expand=singleValueExtendedProperties($filter=id eq 'Double {00062003-0000-0000-C000-000000000046} Id 0x8102')
Prefer: odata.maxpagesize=100
```

(that is the initial request; after that it is the stored link, which already carries the
query). Only what changed since the last reconcile comes back: tasks added or changed, and
`@removed` entries for tasks that left the list, whether deleted or moved to another list. The
pages are followed to the next `@odata.deltaLink`, which is stored once the round has been
applied.

- **Full rounds.** A list without a link runs an initial round, which returns every task;
  anything else the replica holds for that list has gone. That happens on the first run, for a
  new list, and when Graph no longer accepts a link: `syncStateNotFound` once the token has been
  evicted (To Do tokens have no fixed lifetime), or `410 Gone` after maintenance. A full round
  also runs every `Sync:FullReconcileIntervalHours` (24 by default) as a deep check, and when
  you press **F**.
- **No `$select`.** With one, Graph may leave out tasks whose only change was to a property
  that wasn't selected, such as a percent-only change. Without it the delta returns the whole
  task, `body` (the notes) included.
- **PercentComplete in the delta.** Graph documents `$expand` as supported on todoTask delta.
  The reconcile only takes PercentComplete from the delta once a delta has returned a value.
  Until then it reads the values the way known to work (a listing for a full round, a GET per
  changed task otherwise). If those find values the delta didn't return, the delta ignores the
  `$expand` and the values keep being read that way. If Graph refuses the `$expand` outright,
  delta runs without it on the same basis. The log says which applies.
- **Moves** arrive as `@removed` in one list's round and a new ID in another's. They are
  matched by immutable ID and reported as Moved, whatever order the lists are read in.
- **A list that can't be read.** Its entries are left alone. A task that left another list in
  the same reconcile could have moved into it, so that removal also waits. The other list's
  link isn't advanced either, so the removal comes back next time.
- **Idempotent.** A delta can repeat changes a notification already applied; they come back
  as unchanged and aren't reported again.

The reconcile summary shows how each list was reconciled, e.g.
`Lists : Tasks (delta), Website relaunch (full)`.

### Reauthorization

Graph sends a `reauthorizationRequired` lifecycle notification (a challenge) to
`/api/lifecycle` before the access token behind a subscription, or the subscription itself,
expires, and when consent is revoked. If nobody answers, notification delivery eventually
pauses and changes made in the meantime are never notified. After the clientState and tenant
checks, each challenge is answered like this:

| Situation | What the synchronizer does |
|---|---|
| More than half of the subscription's lifetime left | `POST /subscriptions/{id}/reauthorize` (no body → 204) |
| Past halfway | `PATCH /subscriptions/{id}` with a new `expirationDateTime`, which reauthorizes and renews in one call |
| A reauthorize or PATCH was sent for it in the last 10 minutes | Sends the same kind again; Graph says not to mix the two within 10 minutes |
| It was created, renewed or reauthorized in the last 2 minutes | Nothing. The challenge was raised before that (typically Graph redelivering one that failed while the app was down) |
| The subscription has gone (404) | Creates a new one, then reconciles |
| Graph refuses the call (400 / 401 / 403 / 405 / 501) | Replaces the subscription: creates the new one first, then deletes the old one, so delivery never stops. The refusal is remembered for the rest of the run |
| The call fails some other way | Retries every 5 minutes and reconciles once it succeeds. Still failing after 30 minutes → replaced, then reconciled |

Why replacement: the Graph permission tables list the application permission this app uses
(`Tasks.ReadWrite.All`) as supported for *creating* todoTask subscriptions, but "not
supported" for both [updating](https://learn.microsoft.com/graph/api/subscription-update)
and [reauthorize](https://learn.microsoft.com/graph/api/subscription-reauthorize). If your
tenant enforces that, the log says `Graph refused … From now on this synchronizer replaces
subscriptions instead` once per call, and from then on challenges and renewals are answered
by replacing the subscription.

Subscriptions are also renewed (or replaced) on their own when a fifth of their lifetime is
left, checked every five minutes. A list whose subscription couldn't be created is retried at
the same interval.

**Stopping and restarting.** Stop the synchronizer with **Ctrl+C** or by **closing its
window**: either way it deletes its subscriptions before it exits. Stopping the debugger or
killing the process can't be intercepted, so the subscriptions live on. Graph then keeps
sending notifications and challenges to the ngrok URL with nothing listening, and ngrok answers
each one with `502 Bad Gateway` until the subscription expires. The next start cleans up:

- a live subscription for a synchronized list is reused and reauthorized straight away (its
  challenges went unanswered), and the startup reconcile catches up on missed changes
- any other subscription pointing at this synchronizer is deleted: duplicates, ones with an old
  clientState or lifecycle URL, or ones for lists no longer synchronized
- a challenge for a subscription the running instance doesn't track gets the same treatment:
  it is adopted if its list has no subscription, otherwise deleted

### Pushing changes elsewhere

Every change to the replica goes to each registered `ITaskChangeSink`:

```csharp
public interface ITaskChangeSink
{
    Task OnTaskChangedAsync(TaskChange change, CancellationToken ct);           // Added / Updated / Removed
    Task OnReconcileCompletedAsync(ReconcileSummary summary, CancellationToken ct);
}
```

`TaskChange` carries the before/after `TaskRecord`, the field-level `Changes` (PercentComplete
first) and where the values came from. The console reporter is the only sink registered by
default; add yours to the list in `Program.cs`. Sinks run on the single consumer, in order.

### What changed from DeletedItemWatcher

| DeletedItemWatcher | Task Synchronizer |
|---|---|
| Message subscriptions with rich (encrypted) payloads | Basic todoTask subscriptions per To Do list; each notification triggers a GET |
| Graph .NET SDK | Graph REST (`GraphRestClient`: bearer auth, paging, retry with Retry-After) |
| `$expand` of LAPFID / submit / creation time | `$expand` of PercentComplete only; everything else from typed To Do properties |
| ItemCache, FolderCache, LAPFID resolution | TaskStore (persistent replica, ordering guard), TodoListResolver |
| Encryption certificate (PFX) | None needed |
| — | Reconcile with todoTask delta (startup / periodic / lifecycle; full rounds when needed) and lifecycle notifications |
| `translateExchangeIds` (its README listed Mail.ReadWrite for it) | `translateExchangeIds` for immutable task IDs, to recognise moves between lists — needs User.Read.All as an application permission |
| Notification handler returned `Task<IResult>` from a `(HttpContext)` lambda, which binds as a `RequestDelegate` — the result is discarded (ASP0016), so every POST got 200 and errors never became 500 | Handler sets 202 / 500 on the response directly |

---

## Prerequisites

| Tool | Notes |
|------|-------|
| .NET 8 SDK | `dotnet --version` |
| ngrok | `ngrok http 5000` |
| Azure App Registration | `Tasks.ReadWrite.All` and `User.Read.All` (application) |

## Azure App Registration

1. **Entra ID → App registrations → New registration**
2. Note **Tenant ID** and **Client ID**
3. **Certificates & secrets → New client secret** → copy value
4. **API permissions → Application permissions:**
   - `Tasks.ReadWrite.All` — the To Do lists, tasks and their subscriptions. Graph lists only
     ReadWrite permissions for todoTask subscriptions; the app itself never writes to tasks.
   - `User.Read.All` — `POST /users/{id}/translateExchangeIds`, for the immutable IDs. This is
     the least-privileged application permission Graph documents for it. Without it everything
     else works, but a move between lists shows as Removed + Added.
5. **Grant admin consent**

## Configuration

`appsettings.json` (every value can also come from environment variables, e.g.
`AzureAd__ClientSecret`):

```json
{
  "AzureAd": {
    "TenantId": "your-tenant-id",
    "ClientId": "your-client-id",
    "ClientSecret": "your-client-secret"
  },
  "Ngrok": {
    "PublicUrl": "https://abc123.ngrok-free.app"
  },
  "Subscription": {
    "MailboxUserId": "user@yourdomain.com",
    "ExpirationMinutes": 1440,
    "NotificationPath": "/api/notifications",
    "LifecycleNotificationPath": "/api/lifecycle",
    "ClientState": "pick-a-random-secret-string"
  },
  "Webhook": {
    "Port": 5000,
    "CheckPublicUrl": true
  },
  "Tasks": {
    "Lists": []
  },
  "Sync": {
    "StorePath": "task-replica.json",
    "ReconcileIntervalMinutes": 60,
    "FullReconcileIntervalHours": 24,
    "ListReconcileIntervalSeconds": 60,
    "LogTaskJson": false
  }
}
```

| Setting | Notes |
|---|---|
| `Subscription:ExpirationMinutes` | 5–4,230 (the todoTask maximum; Graph raises anything under 45 to 45). Renewed when a fifth is left. |
| `Tasks:Lists` | Empty (the default) = **every To Do list**, including lists created later. Entries restrict it to those lists: `"default"` (the Tasks folder), a list display name (case-insensitive) or a list ID. The available lists are logged at startup. A task moved to or from a list that isn't synchronized shows as Removed or Added. |
| `Sync:ReconcileIntervalMinutes` | Safety-net reconcile (a delta round per list). `0` disables it (startup and lifecycle reconciles still run). |
| `Sync:FullReconcileIntervalHours` | How often a reconcile runs full rounds instead of delta ones, as a deep check (default 24). `0` = only when needed: no delta link, or Graph no longer accepts one. |
| `Sync:ListReconcileIntervalSeconds` | How often the list reconcile asks todoTaskList delta whether a list was created, deleted or renamed (default 60, at least 10). `0` turns the periodic check off; R / F, the task reconcile and the 5-minute check still read the lists. |
| `Sync:LogTaskJson` | Log the raw JSON of every task GET. |
| `Webhook:CheckPublicUrl` | Check that the public URLs reach this app before subscribing (default `true`; see below). Turn it off only if this machine can't reach its own ngrok URL while Graph can. |

## Running

```bash
# Terminal 1
ngrok http 5000

# Terminal 2 — paste the ngrok HTTPS URL into appsettings.json first
cd GraphTaskSynchronizer
dotnet run
```

On startup you should see the mailbox's To Do lists (`[Lists] Available: 'Tasks' [defaultList] …`),
`[Lists] Synchronizing all N list(s).`, one "Subscription created" block per list, then the
startup reconcile listing every task with its list. The log line
`[Webhook] Synced tasks page: http://localhost:5000/` gives the address of the task page.

`ngrok http` must forward to the same port as `Webhook:Port` (5000 by default). If the ngrok
console shows `502 Bad Gateway` for `/api/notifications`, the two don't match and Graph's
validation request never reached the app.

**The public URL check.** Graph validates both URLs the moment a subscription is created. When
that fails it only says `Subscription validation request failed. HTTP status code is
'NotFound'` (or similar). So before subscribing, the synchronizer sends the same
`POST …?validationToken=` request to its own public URLs through ngrok. It checks that the
token comes back from *this* instance: every response carries an `X-Task-Synchronizer`
header with its instance ID. If something else answers, the log says what it was:

| The check found | Meaning |
|---|---|
| `ngrok has no endpoint at … (ERR_NGROK_3200: the endpoint is offline)` | ngrok isn't running, or is running with a different URL than `Ngrok:PublicUrl` |
| `ngrok is running but can't connect to the local port … (ERR_NGROK_8012)` | ngrok forwards to a port nothing listens on: run `ngrok http <Webhook:Port>` |
| `the request reached this synchronizer, but … answered 404` | `Ngrok:PublicUrl` has a path in it; it must be just `https://host` |
| `… from something other than this synchronizer (Server: …)` | ngrok forwards to another web server on that port |
| `another running copy of the synchronizer answered` | ngrok forwards to a different instance |

Until the check passes, nothing is subscribed and the check repeats every 30 seconds, so you
can start ngrok after the app. The replica is still reconciled meanwhile. If Graph later fails
to validate a subscription (ngrok stopped while the app was running), no list is put on its
retry delay. Every list is subscribed again as soon as the check passes (checked every five
minutes). If this machine simply can't reach the URL (no answer), the check doesn't hold
anything up.

Stop it with Ctrl+C or by closing the window, so that it deletes its subscriptions (see
[Reauthorization](#reauthorization) for what happens when it is killed instead).

## Viewing the synced tasks

While the synchronizer is running, open **http://localhost:5000/** (the `Webhook:Port`) in a
browser on the same machine. The default page lists every synced task from every list in one
table:

- a summary row (tasks / open / completed / lists) and a **filter chip per list** with its
  task count, including lists with no tasks yet. A list created in To Do appears within a
  minute (see [Lists](#lists)). Click a chip to show only that list. The choice is kept in the
  URL (`#list=` and a short key derived from the list ID), so it survives the refresh and
  keeps showing the same list when lists are added or removed around it. If that list is
  deleted, the page goes back to all lists
- per task: subject and categories, its **notes** (a short note in full; a longer one folds to
  its first line with **more**, and stays unfolded across the refresh), the **list it is in**,
  a **progress bar with the PercentComplete value**, a status badge, the due date (as Graph
  returned it, with its time zone) and when it was last modified
- a task moved between lists in the last 24 hours shows **from *old list*** under its list
- hovering over a subject shows its To Do ID and immutable ID
- open tasks first (soonest due first), completed tasks after them, with the subject struck
  through
- `—` instead of a percentage when PidLidPercentComplete isn't set on the task (hover for why)

The page updates itself every 10 seconds, so a change made in Outlook or To Do appears without
reloading once its notification has been processed. It follows the system light/dark setting
and switches to a stacked layout on a phone-width window.

The page is only served to this machine. The same port is published through ngrok, so any
request that arrives through the tunnel (or from another computer) gets a 404 — task subjects
are never exposed on the public URL.

| Other ways | What it shows |
|---|---|
| Press **L** in the running synchronizer | The replica as it is right now, with each task's list, and every synchronized list with its task count (a new list shows as `(0)` until it has tasks) |
| Press **R** in the running synchronizer | Runs a reconcile immediately: the lists are read first (a list you just created is subscribed and synchronized), then a delta round per list (useful after editing tasks while testing) |
| Press **F** in the running synchronizer | The same, with a full reconcile: every task of every list, and fresh delta links |
| `dotnet run -- --list` | The saved replica, then exits. Reads only the local file — no Graph calls, no subscriptions — so it is safe to run in a second window while the synchronizer is running |
| `task-replica.json` | The replica itself (JSON). It is written next to where the app runs; the full path is in the startup log (`[Store] …`) and under the L / `--list` table |

```
┌─ Synced tasks (3 in 2 lists) ─────────────────────────
│  Lists: Tasks (1) · Website relaunch (2)
│
│       %  Status             Due                         List                 Subject
│     75%  In progress        2026-10-31T00:00:00 (UTC)   Tasks                Draft Q4 report
│          └ notes: Numbers from finance by Wednesday · Then draft the slides · Send to Sam for review before…
│     80%  In progress        2026-11-07T00:00:00 (UTC)   Website relaunch     Content freeze
│          └ notes: Copy is frozen at 17:00 on Friday; later changes go to the post-launch backlog
│     50%  In progress        2026-11-15T00:00:00 (UTC)   Website relaunch     Migrate EWS scripts to Graph
│
│  Replica file      : C:\Dev\GraphTaskSynchronizer\GraphTaskSynchronizer\task-replica.json
└───────────────────────────────────────────────────────
```

Open tasks are listed before completed ones, soonest due first, with a task's notes on one
line under it. Changes are also printed as they happen (see below).

## Sample output

Produced by driving the engine and console reporter with test data through a fake To Do
gateway (log lines omitted). It shows the startup reconcile (a full round, since there are
no delta links yet), a PercentComplete edit, a notes edit (one line changed, one added), a
task moved from Tasks to Website relaunch with its percent changed on the way, a delete, and a
later reconcile that starts from the delta links, where only the changes come back (there,
notes were added to a task).

```
  + [   25%] In progress        Tasks                Draft Q4 report  due 2026-10-31T00:00:00 (UTC)
  + [    0%] Not started        Tasks                Renew test tenant licences
  + [   40%] In progress        Tasks                Migrate EWS scripts to Graph  due 2026-11-15T00:00:00 (UTC)
  + [   60%] In progress        Website relaunch     Content freeze  due 2026-11-07T00:00:00 (UTC)

┌─ Reconcile complete (startup) ────────────────────────
│  Lists             : Tasks (full), Website relaunch (full)
│  Tasks in replica  : 4
│  Added             : 4
│  Updated           : 0
│  Moved             : 0
│  Removed           : 0
│  Unchanged         : 0
│  Duration          : 0.1s
└───────────────────────────────────────────────────────

┌─ Task Updated ────────────────────────────────────────
│  Subject           : Draft Q4 report
│  List              : Tasks
│  Task ID           : AAMkADtask1
│  Immutable ID      : AAkALgAAAAAAHYQDEapmEc2byACqAC-EWg0AXq3N8zcQ1AAAAAA11AAA=
│  Detected at       : 2026-11-03T07:34:15.6676442+00:00
│
│  ── Changes ──────────────────────────────────────
│  Percent complete  : 25% → 75%
│
│  ── Current state ────────────────────────────────
│  Percent complete  : 75%
│  Status            : In progress
│  Start             : (none)
│  Due               : 2026-10-31T00:00:00 (UTC)
│  Importance        : normal
│  Notes             : Numbers from finance by Thursday
│                      Then draft the slides
│  Last modified     : 2026-10-29T02:23:45Z
│  Trigger           : notification: updated
│  Source            : GET after notification — PercentComplete from singleValueExtendedProperties
└───────────────────────────────────────────────────────

┌─ Task Updated ────────────────────────────────────────
│  Subject           : Draft Q4 report
│  List              : Tasks
│  Task ID           : AAMkADtask1
│  Immutable ID      : AAkALgAAAAAAHYQDEapmEc2byACqAC-EWg0AXq3N8zcQ1AAAAAA11AAA=
│  Detected at       : 2026-11-03T07:34:15.6742123+00:00
│
│  ── Changes ──────────────────────────────────────
│  Notes             : edited
│                      - Numbers from finance by Thursday
│                      + Numbers from finance by Wednesday
│                      + Send to Sam for review before Friday
│
│  ── Current state ────────────────────────────────
│  Percent complete  : 75%
│  Status            : In progress
│  Start             : (none)
│  Due               : 2026-10-31T00:00:00 (UTC)
│  Importance        : normal
│  Notes             : Numbers from finance by Wednesday
│                      Then draft the slides
│                      Send to Sam for review before Friday
│  Last modified     : 2026-10-29T02:31:05Z
│  Trigger           : notification: updated
│  Source            : GET after notification — PercentComplete from singleValueExtendedProperties
└───────────────────────────────────────────────────────

┌─ Task Moved ──────────────────────────────────────────
│  Subject           : Migrate EWS scripts to Graph
│  From list         : Tasks
│  To list           : Website relaunch
│  Task ID           : AAMkADtask3b
│  Previous task ID  : AAMkADtask3
│  Immutable ID      : AAkALgAAAAAAHYQDEapmEc2byACqAC-EWg0AXq3N8zcQ3AAAAAA11AAA=
│  Detected at       : 2026-11-03T07:34:15.6868040+00:00
│
│  ── Also changed ─────────────────────────────────
│  Percent complete  : 40% → 50%
│
│  ── Current state ────────────────────────────────
│  Percent complete  : 50%
│  Status            : In progress
│  Start             : (none)
│  Due               : 2026-11-15T00:00:00 (UTC)
│  Importance        : normal
│  Last modified     : 2026-10-29T02:41:12Z
│  Trigger           : notification: created
│  Source            : GET after notification — PercentComplete from singleValueExtendedProperties
│  Matched by        : immutable ID (translateExchangeIds)
└───────────────────────────────────────────────────────

┌─ Task Removed ────────────────────────────────────────
│  Subject           : Renew test tenant licences
│  List              : Tasks
│  Task ID           : AAMkADtask2
│  Immutable ID      : AAkALgAAAAAAHYQDEapmEc2byACqAC-EWg0AXq3N8zcQ2AAAAAA11AAA=
│  Detected at       : 2026-11-03T07:34:15.6952487+00:00
│
│  Reason            : deleted from 'Tasks'
│  Last percent      : 0%
│  Last status       : Not started
│  Source            : change notification, confirmed with a GET of the task
└───────────────────────────────────────────────────────

┌─ Task Updated ────────────────────────────────────────
│  Subject           : Content freeze
│  List              : Website relaunch
│  Task ID           : AAMkADtask4
│  Immutable ID      : AAkALgAAAAAAHYQDEapmEc2byACqAC-EWg0AXq3N8zcQ4AAAAAA11AAA=
│  Detected at       : 2026-11-03T07:34:15.7016212+00:00
│
│  ── Changes ──────────────────────────────────────
│  Percent complete  : 60% → 80%
│  Notes             : added
│                      + Copy is frozen at 17:00 on Friday; later changes go to the post-launch …
│
│  ── Current state ────────────────────────────────
│  Percent complete  : 80%
│  Status            : In progress
│  Start             : (none)
│  Due               : 2026-11-07T00:00:00 (UTC)
│  Importance        : normal
│  Notes             : Copy is frozen at 17:00 on Friday; later changes go to the post-launch …
│  Last modified     : 2026-11-03T05:10:00Z
│  Trigger           : reconcile (periodic)
│  Source            : delta query (reconcile)
└───────────────────────────────────────────────────────

┌─ Reconcile complete (periodic) ───────────────────────
│  Lists             : Tasks (delta), Website relaunch (delta)
│  Tasks in replica  : 3
│  Added             : 0
│  Updated           : 1
│  Moved             : 0
│  Removed           : 0
│  Unchanged         : 2
│  Duration          : 0.0s
└───────────────────────────────────────────────────────
```

## Tests

```bash
dotnet test
```

The xUnit suite runs without a tenant. The sync engine runs against an in-memory
`ITaskGraphGateway`; the REST layer, To Do gateway, list resolver and subscription service run
against a fake `HttpMessageHandler`. Coverage: extended-property ID matching (GUID case, hex
width), todoTask mapping (typed status and dateTimeTimeZone values, PercentComplete from the
extended property, invariant-culture doubles), bearer auth / paging / Retry-After / error
codes, the `$select`/`$expand` the gateway sends (`body` selected, PercentComplete the only
expand), list selection,
subscription create / reuse / leftover cleanup / 404-recreate, reauthorization (reauthorize
vs renew, Graph's 10-minute rule, redelivered challenges, retries, refusal → replacement
without a gap, adoption of leftovers), every created / updated / deleted path including stale
deletes, immutable IDs (the translateExchangeIds request, batches of 1,000, per-ID failures,
the reverse lookup, the pause after a 403), moves between lists (new list notified first, old
list notified first, found by a reconcile, deleted into Deleted Items vs moved), lists created
/ deleted / renamed / not subscribable, the list reconcile (todoTaskList delta paging and
links, reading the lists only when a round reports a change, a resync, falling back to full
reads while delta fails, lists found before subscribing starts or while Graph can't validate
the webhook, R reading the lists first, a task dragged into a brand-new list reported as
Moved, the service picking up lists from the start), the public URL check (ngrok offline / wrong port, a
path in the URL, another server or instance, no answer) and holding subscriptions back while
Graph can't validate the webhook, delta reconciles (full first round, rounds from the stored
link, @removed, moves, a link Graph no longer accepts, links kept across a restart, removals
held back while a list can't be read, full rounds on request and when due, PercentComplete
from the delta or by listing / GET when the delta ignores or refuses the $expand), the
delta request (expand, no $select, paging to the deltaLink, Prefer header, resync on 410 /
syncStateNotFound but not on 403), notes (text bodies with CRLF, Outlook and Word HTML bodies
read as the same text, an empty body vs no body, the same notes from GET / listing / delta
with no Prefer header, edits by notification and by delta, clearing, filling in notes for old
replica entries without reporting them, a body-less snapshot keeping the known notes, a notes
edit during a move, the line diff and the excerpts around a change), replica persistence and
the format 3 and 5 upgrades, the task page (list column, filter chips keyed by list so a
filter survives lists being added, the list count, "from" for moves,
progress bars, notes inline / folded, ordering, HTML encoding, local-only access) and an HTTP
round trip through
Kestrel (handshake, 202, the instance header, the public URL check against the real server,
clientState and tenant rejection, lifecycle routing, the page served
locally and a 404 through the tunnel).

## Troubleshooting

| Problem | Fix |
|---------|-----|
| `Could not read the To Do lists … Tasks.ReadWrite.All` | Add the `Tasks.ReadWrite.All` application permission and grant admin consent. |
| `No To Do list matches Tasks:Lists entry` | Copy the name or ID from the `[Lists] Available:` lines in the startup log. |
| `[Ids] translateExchangeIds was refused … User.Read.All` | Add the `User.Read.All` application permission and grant admin consent. It is picked up within 30 minutes, no restart needed; until then moves show as Removed + Added. |
| `[Ids] N of M task ID(s) could not be translated` | Graph rejected those IDs individually, typically tasks in a list shared from another mailbox. They synchronize normally, just without move detection. |
| A move still shows as Removed + Added | The task has no immutable ID (see the two rows above), or one of the two lists isn't synchronized (`Tasks:Lists`). |
| A list keeps logging `has no active subscription – retrying` | Graph refuses subscriptions for that list (e.g. a list shared with the mailbox). It is retried less and less often, up to hourly; the periodic reconcile keeps it up to date. To leave it out, name the lists you do want in `Tasks:Lists`. |
| `Subscription validation request failed. HTTP status code is 'NotFound'` | Graph's validation POST got a 404, almost always from ngrok: the endpoint in `Ngrok:PublicUrl` is offline (ngrok not running, or running with another URL). The `[Webhook] Graph won't be able to validate …` line just before it says exactly what answered; see [the public URL check](#running). |
| Subscription rejected | Check Graph's message in the log. Verify ngrok is running, `PublicUrl` is correct, and the permission is consented. |
| Every change says `PidLidPercentComplete is not set on this item` | Graph returned no value for the property on those tasks. Check with `Sync:LogTaskJson=true` and MFCMAPI. |
| Changes appear a few minutes late | todoTask notification latency (documented as under 2 minutes on average, up to 15). |
| A task moved between lists shows as Removed + Added | Expected: To Do task IDs change when a task moves. |
| Notifications not arriving | Check `ClientState` matches; `[Security] clientState mismatch` lines mean it doesn't. |
| The ngrok URL shows a 404 in a browser | Expected: the task page is only served to this machine. Open `http://localhost:5000/` instead. |
| ngrok shows `POST /api/lifecycle 502 Bad Gateway` (`reauthorizationRequired`) while the synchronizer isn't running | A subscription outlived the app, which was killed or the debugger was stopped instead of Ctrl+C or closing the window. Graph keeps sending challenges to a port nothing is listening on. Start the synchronizer: it reuses and reauthorizes the subscription, or deletes it if it's a duplicate. Otherwise the subscription simply expires. |
| `Graph refused POST /subscriptions/{id}/reauthorize` or `PATCH … (renew)` in the log | That call isn't allowed for app-only todoTask subscriptions in your tenant. Nothing to fix: subscriptions are replaced instead (new one created, then the old one deleted). |
| `Graph no longer accepts the delta link for '…' – running a full round` | Normal: the delta token was evicted (To Do tokens have no fixed lifetime) or Graph asked for a resync (410). That list gets a full round and a fresh link. |
| `todoTask delta didn't return PidLidPercentComplete … ignores the $expand` or `refused todoTask delta with the $expand` | Graph doesn't deliver the extended property through delta in your tenant. Nothing to fix: the reconcile reads PercentComplete with a listing (full rounds) or a GET per changed task. |
| `[Reconcile] N task(s) left '…', but not every list could be read` | Another list's round failed this time. The removal waits for the next reconcile, in case the task moved into that list. |
| Notes show without bold, links or other formatting | Expected: To Do returns the notes as text (see [Notes](#notes)), and that text is what is tracked. |
| After upgrading, the first reconcile is a full round | Expected once: replicas saved before notes were tracked lose their delta links so every task's notes get filled in. That round reports no changes for them. |
| A new list doesn't show up straight away | The list reconcile checks every `Sync:ListReconcileIntervalSeconds` (60 by default), so it can take up to a minute. Press **R** to read the lists now. |
| `[Lists] todoTaskList delta failed …` | Graph didn't answer the lists delta query (permissions, an outage). Nothing to fix: the lists are read in full at each check until delta works again (it is retried every 30 minutes). |
| `[Security] Notification for tenant … discarding it` | The notification's `tenantId` / `organizationId` isn't `AzureAd:TenantId`. Check the tenant ID in appsettings.json. |
| ngrok shows `502 Bad Gateway` (ERR_NGROK_8012) | ngrok is forwarding to a different local port than `Webhook:Port`. Run `ngrok http 5000` or change `Webhook:Port`, then restart the app (or wait — missing subscriptions are retried every 5 minutes). |
