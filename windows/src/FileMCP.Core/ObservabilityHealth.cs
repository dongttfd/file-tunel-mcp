namespace FileMCP.Core;

public enum TelemetryPersistenceStatus
{
    Starting,
    Ready,
    Degraded,
}

public sealed record TelemetryPersistenceHealthSnapshot(
    TelemetryPersistenceStatus Status,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    long FailureCount);

internal sealed class TelemetryPersistenceHealthTracker
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset? _lastFailureUtc;
    private long _failureCount;

    public void RecordSuccess(DateTimeOffset? nowUtc = null)
    {
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (_gate) _lastSuccessUtc = Max(_lastSuccessUtc, now);
    }

    public void RecordFailure(DateTimeOffset? nowUtc = null)
    {
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (_gate)
        {
            _lastFailureUtc = Max(_lastFailureUtc, now);
            _failureCount++;
        }
    }

    public TelemetryPersistenceHealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            var status = _lastFailureUtc.HasValue && (!_lastSuccessUtc.HasValue || _lastFailureUtc > _lastSuccessUtc)
                ? TelemetryPersistenceStatus.Degraded
                : _lastSuccessUtc.HasValue
                    ? TelemetryPersistenceStatus.Ready
                    : TelemetryPersistenceStatus.Starting;
            return new TelemetryPersistenceHealthSnapshot(status, _lastSuccessUtc, _lastFailureUtc, _failureCount);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset? current, DateTimeOffset candidate) =>
        !current.HasValue || candidate > current.Value ? candidate : current.Value;
}

public enum TunnelHealthProbeState
{
    NotConfigured,
    Unknown,
    Reachable,
    Unreachable,
}

public sealed record LocalMcpRuntimeHealthSnapshot(
    string? WorkspaceKey,
    LocalMcpRuntimeStatus RuntimeStatus,
    bool LocalServerReady,
    bool TunnelProcessRunning,
    TunnelHealthProbeState TunnelHealth,
    DateTimeOffset? LastTunnelHealthCheckUtc,
    DateTimeOffset? LastTunnelHealthSuccessUtc,
    bool RestartPending,
    DateTimeOffset? NextRestartUtc,
    int ConsecutiveRestarts,
    int AttemptsInWindow,
    long TotalRestarts,
    long CooldownEntries);
