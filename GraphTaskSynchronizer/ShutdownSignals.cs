using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GraphTaskSynchronizer;

/// <summary>
/// Turns each way of stopping the console app into the same orderly shutdown: cancel, let the
/// subscription service delete its subscriptions, then exit.
///
///   Ctrl+C                           Console.CancelKeyPress
///   closing the console window       SIGHUP  (Windows: CTRL_CLOSE_EVENT)
///   kill, logoff, system shutdown    SIGTERM (Windows: CTRL_SHUTDOWN_EVENT)
///
/// Without the last two the process simply ends and its subscriptions live on: Graph keeps
/// sending change notifications and reauthorizationRequired challenges to the tunnel with
/// nothing listening (ngrok answers 502 Bad Gateway) until they expire. Windows ends the
/// process as soon as a close handler returns, and allows about five seconds, so the handler
/// waits up to <see cref="CloseGrace"/> for <see cref="CleanupFinished"/>.
///
/// Stopping the debugger kills the process outright and nothing runs; the next start reuses
/// or deletes whatever was left behind.
/// </summary>
public sealed class ShutdownSignals : IDisposable
{
    internal static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(4.5);

    private readonly CancellationTokenSource _cts;
    private readonly ILogger _logger;
    private readonly ManualResetEventSlim _cleanupFinished = new(false);
    private readonly List<PosixSignalRegistration> _registrations = new();

    public ShutdownSignals(CancellationTokenSource cts, ILogger logger)
    {
        _cts = cts;
        _logger = logger;

        Console.CancelKeyPress += OnCancelKeyPress;
        Register(PosixSignal.SIGHUP);
        Register(PosixSignal.SIGTERM);
    }

    /// <summary>Call once the subscriptions are deleted; releases a waiting close handler.</summary>
    public void CleanupFinished() => _cleanupFinished.Set();

    private void Register(PosixSignal signal)
    {
        try
        {
            _registrations.Add(PosixSignalRegistration.Create(signal, OnSignal));
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
        {
            _logger.LogDebug("{Signal} can't be handled on this platform: {Message}", signal, ex.Message);
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        RequestShutdown("Ctrl+C");
    }

    private void OnSignal(PosixSignalContext context)
    {
        // Unix: don't let the runtime end the process – Main exits once cleanup has run.
        // Windows ends the process when this handler returns, whatever Cancel says.
        context.Cancel = true;
        RequestShutdown(context.Signal == PosixSignal.SIGHUP ? "console window closed" : "termination signal");
        _cleanupFinished.Wait(CloseGrace);
    }

    private void RequestShutdown(string why)
    {
        if (_cts.IsCancellationRequested)
            return;

        _logger.LogWarning("Shutdown requested ({Why}) – deleting subscriptions...", why);
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already shut down.
        }
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        foreach (var registration in _registrations)
            registration.Dispose();
        _cleanupFinished.Set();
    }
}
