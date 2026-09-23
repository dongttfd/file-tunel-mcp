namespace FileMCP.Core;

internal sealed record ObservabilityMaintenanceSnapshot(
    long Runs,
    long FailureRuns,
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? LastSuccessfulRunUtc);

internal sealed class ObservabilityMaintenanceWorker : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    private readonly ITelemetryRetentionStore _store;
    private readonly LogicalSessionRegistry _sessions;
    private readonly LogicalChatCorrelationService _correlation;
    private readonly TimeSpan _interval;
    private readonly Action<string>? _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TelemetryPersistenceHealthTracker? _health;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _runs;
    private long _failureRuns;
    private long _lastRunUtcTicks;
    private long _lastSuccessfulRunUtcTicks;

    public ObservabilityMaintenanceWorker(
        ITelemetryRetentionStore store,
        LogicalSessionRegistry sessions,
        LogicalChatCorrelationService correlation,
        TimeSpan? interval = null,
        Action<string>? log = null,
        Func<DateTimeOffset>? clock = null,
        TelemetryPersistenceHealthTracker? health = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _interval = interval ?? DefaultInterval;
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _health = health;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task RunOnceAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var now = nowUtc.ToUniversalTime();
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var failed = false;
            try
            {
                await _store.CleanupRetentionAsync(now, cancellationToken).ConfigureAwait(false);
                _health?.RecordSuccess(now);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed = true;
                _health?.RecordFailure(now);
                _log?.Invoke($"[Telemetry] retention maintenance failed: {ex.Message}\n");
            }

            try { _sessions.Cleanup(now); }
            catch (Exception ex)
            {
                failed = true;
                _log?.Invoke($"[Telemetry] logical-session cleanup failed: {ex.Message}\n");
            }

            try { _correlation.Cleanup(now); }
            catch (Exception ex)
            {
                failed = true;
                _log?.Invoke($"[Telemetry] correlation cleanup failed: {ex.Message}\n");
            }

            Interlocked.Increment(ref _runs);
            Volatile.Write(ref _lastRunUtcTicks, now.UtcDateTime.Ticks);
            if (failed) Interlocked.Increment(ref _failureRuns);
            else Volatile.Write(ref _lastSuccessfulRunUtcTicks, now.UtcDateTime.Ticks);
        }
        finally
        {
            _runGate.Release();
        }
    }

    public ObservabilityMaintenanceSnapshot Snapshot() => new(
        Interlocked.Read(ref _runs),
        Interlocked.Read(ref _failureRuns),
        FromTicks(Volatile.Read(ref _lastRunUtcTicks)),
        FromTicks(Volatile.Read(ref _lastSuccessfulRunUtcTicks)));

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunOnceAsync(_clock(), cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await RunOnceAsync(_clock(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static DateTimeOffset? FromTicks(long ticks) =>
        ticks == 0 ? null : new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            if (_loop is not null)
            {
                try { await _loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }
        _runGate.Dispose();
    }
}
