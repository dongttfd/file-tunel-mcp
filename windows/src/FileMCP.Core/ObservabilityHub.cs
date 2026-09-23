using System.Diagnostics;

namespace FileMCP.Core;

public sealed record WorkspaceObservabilitySnapshot(
    string WorkspaceKey,
    UsageCounters LifetimeUsage,
    bool RuntimeRunning,
    TimeSpan RuntimeUptime);

public sealed record ObservabilitySnapshot(
    TimeSpan AppUptime,
    UsageCounters GlobalUsage,
    IReadOnlyDictionary<string, WorkspaceObservabilitySnapshot> Workspaces,
    TelemetryPersistenceHealthSnapshot Persistence,
    OtlpExporterSnapshot Otlp);

public sealed record RealtimeUsageSample(
    DateTimeOffset CapturedUtc,
    UsageCounters Delta);

public sealed class ObservabilityHub : IAsyncDisposable
{
    private const int MaxRealtimeSamples = 15 * 60;

    private readonly Dictionary<string, WorkspaceUsageMeter> _meters;
    private readonly Dictionary<string, long> _runtimeStartedTicks;
    private readonly object _runtimeGate = new();
    private readonly object _realtimeGate = new();
    private readonly Queue<RealtimeUsageSample> _realtimeSamples = new();
    private readonly long _appStartedTicks = Stopwatch.GetTimestamp();
    private readonly TelemetrySqliteStore _store;
    private readonly OtlpTelemetryBridge _otlpExport;
    private readonly TelemetryPersistenceHealthTracker _persistenceHealth = new();
    private readonly TelemetryWriter _writer;
    private readonly LogicalSessionWriter _sessionWriter;
    private readonly ObservabilityMaintenanceWorker _maintenance;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private UsageCounters _lastRealtimeUsage;
    private bool _hasRealtimeBaseline;
    private bool _started;
    private bool _disposed;

    public LogicalChatCorrelationService ChatCorrelation { get; } = new();
    public LogicalSessionRegistry Sessions { get; } = new();
    public McpStandardTelemetry StandardTelemetry { get; } = new();

    public ObservabilityHub(
        IEnumerable<string> workspaceKeys,
        string? databasePath = null,
        TimeSpan? writerInterval = null,
        Action<string>? log = null,
        TimeSpan? maintenanceInterval = null)
    {
        var keys = workspaceKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keys.Length == 0) throw new ArgumentException("At least one workspace key is required.", nameof(workspaceKeys));

        _meters = keys.ToDictionary(key => key, key => new WorkspaceUsageMeter(key), StringComparer.OrdinalIgnoreCase);
        _runtimeStartedTicks = keys.ToDictionary(key => key, _ => 0L, StringComparer.OrdinalIgnoreCase);
        _store = new TelemetrySqliteStore(databasePath);
        _otlpExport = new OtlpTelemetryBridge(log);
        _writer = new TelemetryWriter(_meters.Values, _store, writerInterval, log, _persistenceHealth);
        _sessionWriter = new LogicalSessionWriter(Sessions, _store, writerInterval, log, _persistenceHealth);
        _maintenance = new ObservabilityMaintenanceWorker(_store, Sessions, ChatCorrelation, maintenanceInterval, log, health: _persistenceHealth);
    }

    public WorkspaceUsageMeter MeterFor(string workspaceKey)
    {
        var key = NormalizeWorkspaceKey(workspaceKey);
        return _meters.TryGetValue(key, out var meter)
            ? meter
            : throw new KeyNotFoundException($"Unknown FileMCP workspace key '{workspaceKey}'.");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_started) return;
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started) return;
            await _writer.StartAsync(cancellationToken).ConfigureAwait(false);
            await _sessionWriter.StartAsync(cancellationToken).ConfigureAwait(false);
            await _maintenance.StartAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public void MarkRuntimeRunning(string workspaceKey)
    {
        var key = NormalizeWorkspaceKey(workspaceKey);
        lock (_runtimeGate)
        {
            if (!_runtimeStartedTicks.ContainsKey(key)) throw new KeyNotFoundException($"Unknown FileMCP workspace key '{workspaceKey}'.");
            if (_runtimeStartedTicks[key] == 0) _runtimeStartedTicks[key] = Stopwatch.GetTimestamp();
        }
    }

    public void MarkRuntimeStopped(string workspaceKey)
    {
        var key = NormalizeWorkspaceKey(workspaceKey);
        lock (_runtimeGate)
        {
            if (!_runtimeStartedTicks.ContainsKey(key)) throw new KeyNotFoundException($"Unknown FileMCP workspace key '{workspaceKey}'.");
            _runtimeStartedTicks[key] = 0;
        }
    }

    public RealtimeUsageSample CaptureRealtimeSample(DateTimeOffset? capturedUtc = null)
    {
        ThrowIfDisposed();
        var now = (capturedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var current = Snapshot().GlobalUsage;
        lock (_realtimeGate)
        {
            var delta = _hasRealtimeBaseline ? SubtractNonNegative(current, _lastRealtimeUsage) : default;
            _lastRealtimeUsage = current;
            _hasRealtimeBaseline = true;
            var sample = new RealtimeUsageSample(now, delta);
            _realtimeSamples.Enqueue(sample);
            while (_realtimeSamples.Count > MaxRealtimeSamples) _realtimeSamples.Dequeue();
            return sample;
        }
    }

    public IReadOnlyList<RealtimeUsageSample> RealtimeSamples()
    {
        ThrowIfDisposed();
        lock (_realtimeGate) return _realtimeSamples.ToArray();
    }

    public ObservabilitySnapshot Snapshot()
    {
        ThrowIfDisposed();
        Dictionary<string, long> starts;
        lock (_runtimeGate) starts = new Dictionary<string, long>(_runtimeStartedTicks, StringComparer.OrdinalIgnoreCase);

        var workspaces = new Dictionary<string, WorkspaceObservabilitySnapshot>(StringComparer.OrdinalIgnoreCase);
        var global = default(UsageCounters);
        foreach (var pair in _meters)
        {
            var usage = pair.Value.Snapshot();
            global += usage;
            var started = starts[pair.Key];
            workspaces[pair.Key] = new WorkspaceObservabilitySnapshot(
                pair.Key,
                usage,
                started != 0,
                started == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(started));
        }
        return new ObservabilitySnapshot(Stopwatch.GetElapsedTime(_appStartedTicks), global, workspaces, _persistenceHealth.Snapshot(), _otlpExport.Snapshot());
    }

    public OtlpExporterSnapshot ConfigureOtlp(OtlpTelemetrySettings settings)
    {
        ThrowIfDisposed();
        return _otlpExport.Configure(settings);
    }

    public OtlpExporterSnapshot OtlpSnapshot()
    {
        ThrowIfDisposed();
        return _otlpExport.Snapshot();
    }

    internal bool ForceFlushOtlpForTests(int timeoutMilliseconds = 1_000)
    {
        ThrowIfDisposed();
        return _otlpExport.ForceFlush(timeoutMilliseconds);
    }
    public async Task<UsageCounters> QueryExactPeriodAsync(UsagePeriodRange range, string? workspaceKey = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var key = workspaceKey is null ? null : NormalizeWorkspaceKey(workspaceKey);
        if (key is not null && !_meters.ContainsKey(key)) throw new KeyNotFoundException($"Unknown FileMCP workspace key '{workspaceKey}'.");
        try
        {
            var usage = await _store.QueryExactPeriodAsync(range, key, cancellationToken).ConfigureAwait(false);
            _persistenceHealth.RecordSuccess();
            return usage;
        }
        catch
        {
            _persistenceHealth.RecordFailure();
            throw;
        }
    }

    public async Task FlushAsync(DateTimeOffset capturedAtUtc, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _writer.FlushOnceAsync(capturedAtUtc, cancellationToken).ConfigureAwait(false);
        await _sessionWriter.FlushOnceAsync(capturedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    public async Task CleanupRetentionAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            await _store.CleanupRetentionAsync(nowUtc, cancellationToken).ConfigureAwait(false);
            _persistenceHealth.RecordSuccess(nowUtc);
        }
        catch
        {
            _persistenceHealth.RecordFailure(nowUtc);
            throw;
        }
    }

    internal Task RunMaintenanceOnceForTestsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
        _maintenance.RunOnceAsync(nowUtc, cancellationToken);

    internal ObservabilityMaintenanceSnapshot MaintenanceSnapshotForTests() => _maintenance.Snapshot();
    internal async Task FlushOnceForTestsAsync(DateTimeOffset capturedAtUtc, CancellationToken cancellationToken = default)
    {
        await _writer.FlushOnceAsync(capturedAtUtc, cancellationToken).ConfigureAwait(false);
        await _sessionWriter.FlushOnceAsync(capturedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    private static UsageCounters SubtractNonNegative(UsageCounters current, UsageCounters previous) => new(
        Math.Max(0, current.McpRequests - previous.McpRequests),
        Math.Max(0, current.ToolCalls - previous.ToolCalls),
        Math.Max(0, current.ExecutionTasks - previous.ExecutionTasks),
        Math.Max(0, current.RequestBytes - previous.RequestBytes),
        Math.Max(0, current.ResponseBytes - previous.ResponseBytes),
        Math.Max(0, current.TokensInEst - previous.TokensInEst),
        Math.Max(0, current.TokensOutEst - previous.TokensOutEst),
        Math.Max(0, current.Errors - previous.Errors),
        Math.Max(0, current.ReadCalls - previous.ReadCalls),
        Math.Max(0, current.WriteCalls - previous.WriteCalls),
        Math.Max(0, current.CommandCalls - previous.CommandCalls),
        Math.Max(0, current.GitCalls - previous.GitCalls),
        Math.Max(0, current.SkillCalls - previous.SkillCalls),
        Math.Max(0, current.OtherCalls - previous.OtherCalls),
        Math.Max(0, current.TotalLatencyTicks - previous.TotalLatencyTicks),
        current.MaxLatencyTicks);

    private static string NormalizeWorkspaceKey(string workspaceKey)
    {
        if (string.IsNullOrWhiteSpace(workspaceKey)) throw new ArgumentException("Workspace key cannot be empty.", nameof(workspaceKey));
        return workspaceKey.Trim().ToUpperInvariant();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ObservabilityHub));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _maintenance.DisposeAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        await _sessionWriter.DisposeAsync().ConfigureAwait(false);
        _otlpExport.Dispose();
        StandardTelemetry.Dispose();
        _startGate.Dispose();
    }
}
