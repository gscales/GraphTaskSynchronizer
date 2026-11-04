using Microsoft.Extensions.Configuration;

namespace GraphTaskSynchronizer;

/// <summary>The Sync:* settings.</summary>
public sealed record TaskSyncOptions
{
    /// <summary>
    /// Safety-net full reconcile interval. Notifications are best-effort and todoTask
    /// subscriptions get no 'missed' lifecycle event, so a periodic reconcile bounds how long
    /// a silently dropped change can go unnoticed. Zero disables it (startup and lifecycle
    /// reconciles still run).
    /// </summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Reconciles use delta queries. This often, one runs a full round instead (every task of
    /// every list, as on a first run), as a deep check that the replica and the mailbox agree.
    /// Zero turns that off (full rounds still run when there's no delta link or Graph drops one).
    /// </summary>
    public TimeSpan FullReconcileInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Log the raw JSON of every task GET — shows exactly what Graph returns in
    /// singleValueExtendedProperties.
    /// </summary>
    public bool LogTaskJson { get; init; }

    public static TaskSyncOptions FromConfiguration(IConfiguration config) => new()
    {
        ReconcileInterval = TimeSpan.FromMinutes(
            int.TryParse(config["Sync:ReconcileIntervalMinutes"], out var minutes) ? Math.Max(0, minutes) : 60),
        FullReconcileInterval = TimeSpan.FromHours(
            int.TryParse(config["Sync:FullReconcileIntervalHours"], out var hours) ? Math.Max(0, hours) : 24),
        LogTaskJson = bool.TryParse(config["Sync:LogTaskJson"], out var log) && log
    };
}
