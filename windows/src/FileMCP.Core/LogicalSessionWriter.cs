namespace FileMCP.Core;

internal sealed class LogicalSessionWriter : IAsyncDisposable
{
    private readonly LogicalSessionRegistry _registry;
    private readonly TelemetrySqliteStore _store;
    private readonly TimeSpan _interval;
    private readonly Action<string>? _log;
    private readonly TelemetryPersistenceHealthTracker? _health;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private LogicalSessionPersistenceBatch _pending = new([], []);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public LogicalSessionWriter(
        LogicalSessionRegistry registry,
        TelemetrySqliteStore store,
        TimeSpan? interval = null,
        Action<string>? log = null,
        TelemetryPersistenceHealthTracker? health = null)
    {
        _registry = registry;
        _store = store;
        _interval = interval ?? TimeSpan.FromSeconds(1);
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _log = log;
        _health = health;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) return;
        try
        {
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _health?.RecordSuccess();
        }
        catch
        {
            _health?.RecordFailure();
            throw;
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cts.Token);
    }

    public async Task FlushOnceAsync(DateTimeOffset capturedAtUtc, CancellationToken cancellationToken = default)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var drained = _registry.DrainPersistence(capturedAtUtc);
            _pending = Merge(_pending, drained);
            if (_pending.IsEmpty) return;
            try
            {
                await _store.UpsertSessionDeltasAsync(_pending, cancellationToken).ConfigureAwait(false);
                _registry.MarkPersisted(_pending);
                _pending = new([], []);
                _health?.RecordSuccess(capturedAtUtc);
            }
            catch
            {
                _health?.RecordFailure(capturedAtUtc);
                throw;
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    internal LogicalSessionPersistenceBatch PendingSnapshotForTests() => _pending;

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try { await FlushOnceAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex) { _log?.Invoke($"[Telemetry] session flush failed: {ex.Message}\n"); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static LogicalSessionPersistenceBatch Merge(LogicalSessionPersistenceBatch left, LogicalSessionPersistenceBatch right)
    {
        var sessions = new Dictionary<(string Hash, string Workspace), LogicalSessionPersistenceDelta>();
        foreach (var item in left.Sessions.Concat(right.Sessions))
        {
            var key = (item.SessionHash, item.WorkspaceKey.ToUpperInvariant());
            if (!sessions.TryGetValue(key, out var existing))
            {
                sessions[key] = item;
                continue;
            }
            sessions[key] = new LogicalSessionPersistenceDelta(
                item.SessionHash,
                item.WorkspaceKey,
                Math.Min(existing.CreatedEpoch, item.CreatedEpoch),
                Math.Min(existing.FirstSeenEpoch, item.FirstSeenEpoch),
                Math.Max(existing.LastSeenEpoch, item.LastSeenEpoch),
                item.LastSeenEpoch >= existing.LastSeenEpoch ? item.State : existing.State,
                existing.Usage + item.Usage);
        }

        var unbound = new Dictionary<string, UnboundSessionPersistenceDelta>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in left.Unbound.Concat(right.Unbound))
        {
            if (!unbound.TryGetValue(item.WorkspaceKey, out var existing))
            {
                unbound[item.WorkspaceKey] = item;
                continue;
            }
            unbound[item.WorkspaceKey] = new UnboundSessionPersistenceDelta(
                item.WorkspaceKey,
                Math.Min(existing.FirstSeenEpoch, item.FirstSeenEpoch),
                Math.Max(existing.LastSeenEpoch, item.LastSeenEpoch),
                item.LastSeenEpoch >= existing.LastSeenEpoch ? item.State : existing.State,
                existing.Usage + item.Usage);
        }
        return new LogicalSessionPersistenceBatch(sessions.Values.ToArray(), unbound.Values.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            if (_loop is not null)
            {
                try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            try { await FlushOnceAsync(DateTimeOffset.UtcNow).ConfigureAwait(false); }
            catch (Exception ex) { _log?.Invoke($"[Telemetry] final session flush failed: {ex.Message}\n"); }
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }
        _flushGate.Dispose();
    }
}
