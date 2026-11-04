using GraphTaskSynchronizer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

using var loggerFactory = LoggerFactory.Create(b =>
    b.AddConsole().SetMinimumLevel(LogLevel.Information));

var logger = loggerFactory.CreateLogger<Program>();

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("""

  ╔══════════════════════════════════════════════════════════╗
  ║   Microsoft Graph – Task Synchronizer (To Do API)        ║
  ║   todoTask change notifications → GET with               ║
  ║   PidLidPercentComplete expanded → local replica         ║
  ╚══════════════════════════════════════════════════════════╝

""");
Console.ResetColor();

var cts = new CancellationTokenSource();

// Ctrl+C, closing the window and SIGTERM all stop the same way: the subscriptions are deleted
// before the process ends, so Graph isn't left sending to a tunnel with nothing behind it.
using var shutdown = new ShutdownSignals(cts, logger);

try
{
    var mailboxUserId = config["Subscription:MailboxUserId"]
        ?? throw new InvalidOperationException("Subscription:MailboxUserId missing");

    var replicaPath = config["Sync:StorePath"] ?? "task-replica.json";

    // ── dotnet run -- --list : print the saved replica and exit ─────────────
    // Reads only the local file (no Graph calls, no subscriptions), so it is safe to run
    // while the synchronizer itself is running in another window.
    if (args.Any(a => a.Equals("--list", StringComparison.OrdinalIgnoreCase)))
    {
        var saved = new TaskStore(replicaPath, mailboxUserId, loggerFactory);
        ConsoleTaskChangeReporter.PrintReplica(saved.GetAll(), lists: null, saved.FilePath);
        return 0;
    }

    // ── Graph access ─────────────────────────────────────────────────────────

    var tokens = ClientCredentialTokenProvider.FromConfiguration(config, loggerFactory);
    using var httpClient = new HttpClient { BaseAddress = GraphRestClient.DefaultBaseAddress };
    var graph = new GraphRestClient(httpClient, tokens, loggerFactory);

    // Which To Do lists to synchronize: all of them, unless Tasks:Lists names some. The
    // subscription service's list reconcile picks up lists created, deleted or renamed later.
    logger.LogInformation("[Startup] Resolving To Do lists...");
    var listResolver = new TodoListResolver(config, loggerFactory, graph);
    var lists = await listResolver.ResolveAsync(mailboxUserId, cts.Token) ?? [];

    if (lists.Count == 0)
    {
        logger.LogCritical("[Startup] No To Do list to synchronize – see the errors above.");
        return 1;
    }

    // ── Sync engine ──────────────────────────────────────────────────────────

    var options = TaskSyncOptions.FromConfiguration(config);
    var store = new TaskStore(replicaPath, mailboxUserId, loggerFactory);
    var gateway = new TodoGateway(graph, mailboxUserId, options.LogTaskJson, loggerFactory);

    // Add further ITaskChangeSink implementations here to push changes elsewhere.
    var sinks = new List<ITaskChangeSink> { new ConsoleTaskChangeReporter() };

    var engine = new TaskSyncEngine(options, loggerFactory, gateway, store, lists, sinks);

    // The webhook server must exist before the subscription service, which waits on it
    // (WaitForReadyAsync) before subscribing and consumes its lifecycle events.
    var webhookServer = new WebhookServer(config, loggerFactory, engine);

    // Before subscribing, check that the public URLs reach this app – Graph's own error when
    // they don't is just "validation request failed … NotFound" (see PublicUrlCheck).
    using var urlCheckClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var urlCheck = new PublicUrlCheck(urlCheckClient, webhookServer.InstanceId,
        int.TryParse(config["Webhook:Port"], out var webhookPort) ? webhookPort : 5000);
    var checkPublicUrl = !bool.TryParse(config["Webhook:CheckPublicUrl"], out var check) || check;

    var subscriptionService = new GraphSubscriptionService(config, loggerFactory, graph, lists, webhookServer, engine,
        refreshLists: ct => listResolver.ResolveAsync(mailboxUserId, ct, quiet: true),
        checkPublicUrl: checkPublicUrl ? urlCheck.CheckAsync : null,
        listChanges: (deltaLink, ct) => listResolver.GetListChangesAsync(mailboxUserId, deltaLink, ct));

    // Reconciles that a new list could explain (R / F, the periodic one, a task that left a list
    // but still exists) read the lists first, so a list created since the last check is in them.
    engine.ListCheck = subscriptionService.RequestListReconcile;

    // ── Run webhook server, sync engine and subscription manager concurrently ─

    var running = new[]
    {
        webhookServer.RunAsync(cts.Token),
        engine.RunAsync(cts.Token),
        subscriptionService.RunAsync(cts.Token)
    };

    var keyCommands = RunKeyCommandsAsync(store, engine, cts.Token);

    // Whichever stops first (Ctrl+C, or a failure such as the port being in use) stops
    // the others, so the subscriptions are always cleaned up on the way out.
    await Task.WhenAny(running);
    cts.Cancel();
    await Task.WhenAll(running);
    await keyCommands;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    // Ctrl+C during startup.
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Fatal error – exiting.");
    cts.Cancel();
    return 1;
}
finally
{
    // Subscriptions are deleted by now (GraphSubscriptionService.RunAsync's finally);
    // a console-close handler that is holding the process open can let it go.
    shutdown.CleanupFinished();
}

logger.LogInformation("Application exited.");
return 0;

// ── Console keys while running: L lists the synced tasks, R / F reconcile now ──────
static Task RunKeyCommandsAsync(TaskStore store, TaskSyncEngine engine, CancellationToken ct)
{
    if (Console.IsInputRedirected)
        return Task.CompletedTask;   // no interactive console (e.g. running as a service)

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  Keys: L = list the synced tasks · R = reconcile now (lists, then tasks by delta) · F = full reconcile · Ctrl+C = exit");
    Console.WriteLine();
    Console.ResetColor();

    return Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Poll rather than block in ReadKey, so shutdown isn't held up by a pending read.
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(100, ct);
                    continue;
                }

                switch (char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar))
                {
                    case 'L':
                        ConsoleTaskChangeReporter.PrintReplica(store.GetAll(), engine.Lists, store.FilePath);
                        break;
                    case 'R':
                        engine.RequestReconcileAfterListCheck("requested from the console");
                        break;
                    case 'F':
                        engine.RequestReconcileAfterListCheck("full, requested from the console", full: true);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;   // the console turned out not to be interactive
            }
        }
    });
}
