namespace FileMCP.Core;

public enum LogicalSessionActivityState
{
    Active,
    Idle,
    Stale,
}

public sealed record LogicalSessionWorkspaceSnapshot(
    string WorkspaceKey,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    UsageCounters Usage);

public sealed record LogicalSessionSnapshot(
    string SessionHash,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastSeenUtc,
    int InFlightCalls,
    LogicalSessionActivityState State,
    IReadOnlyDictionary<string, LogicalSessionWorkspaceSnapshot> Workspaces);

public sealed record UnboundSessionSnapshot(
    string WorkspaceKey,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    int InFlightCalls,
    LogicalSessionActivityState State,
    UsageCounters Usage);

public sealed record LogicalSessionRetentionSnapshot(
    int SessionCount,
    int UnboundCount,
    long ExpiredSessionEvictions,
    long ExpiredUnboundEvictions,
    long PressureSessionEvictions,
    long CapacityPressureEvents);

internal readonly record struct LogicalSessionCallToken(
    string WorkspaceKey,
    string? SessionHash,
    DateTimeOffset StartedUtc,
    long RequestBytes,
    string? ToolName);

internal sealed record LogicalSessionPersistenceDelta(
    string SessionHash,
    string WorkspaceKey,
    long CreatedEpoch,
    long FirstSeenEpoch,
    long LastSeenEpoch,
    string State,
    UsageCounters Usage);

internal sealed record UnboundSessionPersistenceDelta(
    string WorkspaceKey,
    long FirstSeenEpoch,
    long LastSeenEpoch,
    string State,
    UsageCounters Usage);

internal sealed record LogicalSessionPersistenceBatch(
    IReadOnlyList<LogicalSessionPersistenceDelta> Sessions,
    IReadOnlyList<UnboundSessionPersistenceDelta> Unbound)
{
    public bool IsEmpty => Sessions.Count == 0 && Unbound.Count == 0;
}

public sealed class LogicalSessionRegistry
{
    public static readonly TimeSpan ActiveIdleThreshold = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(2);
    public const int DefaultMaxSessions = 4096;

    private sealed class WorkspaceEntry
    {
        public required DateTimeOffset FirstSeenUtc;
        public required DateTimeOffset LastSeenUtc;
        public UsageCounters Usage;
        public UsageCounters PendingUsage;
    }

    private sealed class SessionEntry
    {
        public required string Hash;
        public required DateTimeOffset CreatedUtc;
        public required DateTimeOffset LastSeenUtc;
        public int InFlight;
        public readonly Dictionary<string, WorkspaceEntry> Workspaces = new(StringComparer.OrdinalIgnoreCase);
        public bool PendingState;
        public string? LastDrainedState;
        public string? LastPersistedState;
    }

    private sealed class UnboundEntry
    {
        public required DateTimeOffset FirstSeenUtc;
        public required DateTimeOffset LastSeenUtc;
        public int InFlight;
        public UsageCounters Usage;
        public UsageCounters PendingUsage;
        public bool PendingState;
        public string? LastDrainedState;
        public string? LastPersistedState;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UnboundEntry> _unbound = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxSessions;
    private readonly TimeSpan _retention;
    private long _expiredSessionEvictions;
    private long _expiredUnboundEvictions;
    private long _pressureSessionEvictions;
    private long _capacityPressureEvents;

    public LogicalSessionRegistry(int maxSessions = DefaultMaxSessions, TimeSpan? retention = null)
    {
        if (maxSessions <= 0) throw new ArgumentOutOfRangeException(nameof(maxSessions));
        _maxSessions = maxSessions;
        _retention = retention ?? DefaultRetention;
        if (_retention <= StaleThreshold) throw new ArgumentOutOfRangeException(nameof(retention), "Logical session retention must exceed the stale threshold.");
    }

    public void RegisterSession(string sessionHash, DateTimeOffset? nowUtc = null)
    {
        ValidateHash(sessionHash);
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionHash, out var existing))
            {
                if (now > existing.LastSeenUtc) existing.LastSeenUtc = now;
                existing.PendingState = true;
                return;
            }
            if (!TryReserveSessionSlot(now)) return;
            _sessions[sessionHash] = NewSessionEntry(sessionHash, now);
        }
    }

    internal LogicalSessionCallToken BeginToolCall(
        string workspaceKey,
        string? sessionHash,
        long requestBytes,
        string? toolName,
        DateTimeOffset? nowUtc = null)
    {
        var key = NormalizeWorkspaceKey(workspaceKey);
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var bytes = Math.Max(0, requestBytes);
        string? effectiveSessionHash = sessionHash;
        lock (_gate)
        {
            if (effectiveSessionHash is not null)
            {
                ValidateHash(effectiveSessionHash);
                if (!_sessions.TryGetValue(effectiveSessionHash, out var session))
                {
                    if (TryReserveSessionSlot(now))
                    {
                        session = NewSessionEntry(effectiveSessionHash, now);
                        _sessions[effectiveSessionHash] = session;
                    }
                    else
                    {
                        effectiveSessionHash = null;
                    }
                }

                if (effectiveSessionHash is not null && _sessions.TryGetValue(effectiveSessionHash, out session))
                {
                    session.InFlight++;
                    session.LastSeenUtc = Max(session.LastSeenUtc, now);
                    session.PendingState = true;
                    var workspace = GetOrCreateWorkspace(session.Workspaces, key, now);
                    workspace.LastSeenUtc = Max(workspace.LastSeenUtc, now);
                }
                else
                {
                    BeginUnbound(key, now);
                }
            }
            else
            {
                BeginUnbound(key, now);
            }
        }
        return new LogicalSessionCallToken(key, effectiveSessionHash, now, bytes, toolName);
    }

    internal void CompleteToolCall(
        LogicalSessionCallToken token,
        long responseBytes,
        bool isError,
        long latencyTicks,
        DateTimeOffset? nowUtc = null)
    {
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var delta = CountersForCall(token.RequestBytes, Math.Max(0, responseBytes), token.ToolName, isError, Math.Max(0, latencyTicks));
        lock (_gate)
        {
            if (token.SessionHash is not null && _sessions.TryGetValue(token.SessionHash, out var session))
            {
                session.InFlight = Math.Max(0, session.InFlight - 1);
                session.LastSeenUtc = Max(session.LastSeenUtc, now);
                session.PendingState = true;
                var workspace = GetOrCreateWorkspace(session.Workspaces, token.WorkspaceKey, token.StartedUtc);
                workspace.LastSeenUtc = Max(workspace.LastSeenUtc, now);
                workspace.Usage += delta;
                workspace.PendingUsage += delta;
            }
            else
            {
                var unbound = GetOrCreateUnbound(token.WorkspaceKey, token.StartedUtc);
                unbound.InFlight = Math.Max(0, unbound.InFlight - 1);
                unbound.LastSeenUtc = Max(unbound.LastSeenUtc, now);
                unbound.Usage += delta;
                unbound.PendingUsage += delta;
                unbound.PendingState = true;
            }
        }
    }

    public IReadOnlyList<LogicalSessionSnapshot> Snapshot(DateTimeOffset? nowUtc = null, bool includeStale = false)
    {
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (_gate)
        {
            return _sessions.Values
                .Select(entry => ToSnapshot(entry, now))
                .Where(entry => includeStale || entry.State != LogicalSessionActivityState.Stale)
                .OrderByDescending(entry => entry.LastSeenUtc)
                .ToArray();
        }
    }

    public IReadOnlyList<UnboundSessionSnapshot> UnboundSnapshot(DateTimeOffset? nowUtc = null, bool includeStale = false)
    {
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (_gate)
        {
            return _unbound.Select(pair =>
                {
                    var state = StateFor(pair.Value.InFlight, pair.Value.LastSeenUtc, now);
                    return new UnboundSessionSnapshot(pair.Key, pair.Value.FirstSeenUtc, pair.Value.LastSeenUtc, pair.Value.InFlight, state, pair.Value.Usage);
                })
                .Where(entry => includeStale || entry.State != LogicalSessionActivityState.Stale)
                .OrderByDescending(entry => entry.LastSeenUtc)
                .ToArray();
        }
    }

    public LogicalSessionRetentionSnapshot Cleanup(DateTimeOffset? nowUtc = null)
    {
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (_gate)
        {
            RemoveExpiredSessions(now);
            RemoveExpiredUnbound(now);
            while (_sessions.Count > _maxSessions && TryEvictOnePersistedStale(now, pressure: true)) { }
            return RetentionSnapshotUnsafe();
        }
    }

    public LogicalSessionRetentionSnapshot RetentionSnapshot()
    {
        lock (_gate) return RetentionSnapshotUnsafe();
    }

    internal LogicalSessionPersistenceBatch DrainPersistence(DateTimeOffset? nowUtc = null)
    {
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (_gate)
        {
            var sessions = new List<LogicalSessionPersistenceDelta>();
            foreach (var session in _sessions.Values)
            {
                var state = StateFor(session.InFlight, session.LastSeenUtc, now).ToString().ToLowerInvariant();
                foreach (var workspace in session.Workspaces)
                {
                    if (workspace.Value.PendingUsage.IsZero && !session.PendingState && session.LastDrainedState == state) continue;
                    sessions.Add(new LogicalSessionPersistenceDelta(
                        session.Hash,
                        workspace.Key,
                        session.CreatedUtc.ToUnixTimeSeconds(),
                        workspace.Value.FirstSeenUtc.ToUnixTimeSeconds(),
                        workspace.Value.LastSeenUtc.ToUnixTimeSeconds(),
                        state,
                        workspace.Value.PendingUsage));
                    workspace.Value.PendingUsage = default;
                }
                session.PendingState = false;
                session.LastDrainedState = state;
            }

            var unbound = new List<UnboundSessionPersistenceDelta>();
            foreach (var pair in _unbound)
            {
                var entry = pair.Value;
                var currentState = StateFor(entry.InFlight, entry.LastSeenUtc, now).ToString().ToLowerInvariant();
                if (entry.PendingUsage.IsZero && !entry.PendingState && entry.LastDrainedState == currentState) continue;
                unbound.Add(new UnboundSessionPersistenceDelta(
                    pair.Key,
                    entry.FirstSeenUtc.ToUnixTimeSeconds(),
                    entry.LastSeenUtc.ToUnixTimeSeconds(),
                    currentState,
                    entry.PendingUsage));
                entry.PendingUsage = default;
                entry.PendingState = false;
                entry.LastDrainedState = currentState;
            }
            return new LogicalSessionPersistenceBatch(sessions, unbound);
        }
    }

    internal void MarkPersisted(LogicalSessionPersistenceBatch batch)
    {
        lock (_gate)
        {
            foreach (var delta in batch.Sessions)
                if (_sessions.TryGetValue(delta.SessionHash, out var session))
                    session.LastPersistedState = delta.State;
            foreach (var delta in batch.Unbound)
                if (_unbound.TryGetValue(delta.WorkspaceKey, out var unbound))
                    unbound.LastPersistedState = delta.State;
        }
    }

    internal void RestorePersistence(LogicalSessionPersistenceBatch batch)
    {
        lock (_gate)
        {
            foreach (var delta in batch.Sessions)
            {
                if (!_sessions.TryGetValue(delta.SessionHash, out var session))
                {
                    session = new SessionEntry
                    {
                        Hash = delta.SessionHash,
                        CreatedUtc = DateTimeOffset.FromUnixTimeSeconds(delta.CreatedEpoch),
                        LastSeenUtc = DateTimeOffset.FromUnixTimeSeconds(delta.LastSeenEpoch),
                    };
                    _sessions[delta.SessionHash] = session;
                }
                session.PendingState = true;
                var workspace = GetOrCreateWorkspace(session.Workspaces, delta.WorkspaceKey, DateTimeOffset.FromUnixTimeSeconds(delta.FirstSeenEpoch));
                workspace.LastSeenUtc = Max(workspace.LastSeenUtc, DateTimeOffset.FromUnixTimeSeconds(delta.LastSeenEpoch));
                workspace.PendingUsage += delta.Usage;
            }
            foreach (var delta in batch.Unbound)
            {
                var entry = GetOrCreateUnbound(delta.WorkspaceKey, DateTimeOffset.FromUnixTimeSeconds(delta.FirstSeenEpoch));
                entry.LastSeenUtc = Max(entry.LastSeenUtc, DateTimeOffset.FromUnixTimeSeconds(delta.LastSeenEpoch));
                entry.PendingUsage += delta.Usage;
                entry.PendingState = true;
            }
        }
    }

    public static LogicalSessionActivityState StateFor(int inFlightCalls, DateTimeOffset lastSeenUtc, DateTimeOffset nowUtc)
    {
        if (inFlightCalls > 0) return LogicalSessionActivityState.Active;
        var idle = nowUtc.ToUniversalTime() - lastSeenUtc.ToUniversalTime();
        if (idle <= ActiveIdleThreshold) return LogicalSessionActivityState.Active;
        if (idle <= StaleThreshold) return LogicalSessionActivityState.Idle;
        return LogicalSessionActivityState.Stale;
    }

    private bool TryReserveSessionSlot(DateTimeOffset now)
    {
        if (_sessions.Count < _maxSessions) return true;
        _capacityPressureEvents++;
        RemoveExpiredSessions(now);
        if (_sessions.Count < _maxSessions) return true;
        return TryEvictOnePersistedStale(now, pressure: true);
    }

    private bool TryEvictOnePersistedStale(DateTimeOffset now, bool pressure)
    {
        var candidate = _sessions.Values
            .Where(entry => CanEvictSession(entry, now, requireRetentionAge: false))
            .OrderBy(entry => entry.LastSeenUtc)
            .FirstOrDefault();
        if (candidate is null) return false;
        if (!_sessions.Remove(candidate.Hash)) return false;
        if (pressure) _pressureSessionEvictions++;
        return true;
    }

    private void RemoveExpiredSessions(DateTimeOffset now)
    {
        foreach (var hash in _sessions.Values
                     .Where(entry => CanEvictSession(entry, now, requireRetentionAge: true))
                     .Select(entry => entry.Hash)
                     .ToArray())
        {
            if (_sessions.Remove(hash)) _expiredSessionEvictions++;
        }
    }

    private void RemoveExpiredUnbound(DateTimeOffset now)
    {
        foreach (var key in _unbound
                     .Where(pair => CanEvictUnbound(pair.Value, now))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            if (_unbound.Remove(key)) _expiredUnboundEvictions++;
        }
    }

    private bool CanEvictSession(SessionEntry entry, DateTimeOffset now, bool requireRetentionAge)
    {
        if (entry.InFlight != 0 || entry.PendingState) return false;
        if (StateFor(entry.InFlight, entry.LastSeenUtc, now) != LogicalSessionActivityState.Stale) return false;
        if (entry.Workspaces.Count > 0)
        {
            if (entry.LastPersistedState != "stale") return false;
            if (entry.Workspaces.Values.Any(workspace => !workspace.PendingUsage.IsZero)) return false;
        }
        return !requireRetentionAge || now - entry.LastSeenUtc >= _retention;
    }

    private bool CanEvictUnbound(UnboundEntry entry, DateTimeOffset now)
    {
        if (entry.InFlight != 0 || entry.PendingState || entry.LastPersistedState != "stale" || !entry.PendingUsage.IsZero) return false;
        if (StateFor(entry.InFlight, entry.LastSeenUtc, now) != LogicalSessionActivityState.Stale) return false;
        return now - entry.LastSeenUtc >= _retention;
    }

    private void BeginUnbound(string key, DateTimeOffset now)
    {
        var unbound = GetOrCreateUnbound(key, now);
        unbound.InFlight++;
        unbound.LastSeenUtc = Max(unbound.LastSeenUtc, now);
        unbound.PendingState = true;
    }

    private LogicalSessionRetentionSnapshot RetentionSnapshotUnsafe() => new(
        _sessions.Count,
        _unbound.Count,
        _expiredSessionEvictions,
        _expiredUnboundEvictions,
        _pressureSessionEvictions,
        _capacityPressureEvents);

    private static SessionEntry NewSessionEntry(string hash, DateTimeOffset now) => new()
    {
        Hash = hash,
        CreatedUtc = now,
        LastSeenUtc = now,
        PendingState = true,
    };

    private static LogicalSessionSnapshot ToSnapshot(SessionEntry entry, DateTimeOffset now)
    {
        var workspaces = entry.Workspaces.ToDictionary(
            pair => pair.Key,
            pair => new LogicalSessionWorkspaceSnapshot(pair.Key, pair.Value.FirstSeenUtc, pair.Value.LastSeenUtc, pair.Value.Usage),
            StringComparer.OrdinalIgnoreCase);
        return new LogicalSessionSnapshot(entry.Hash, entry.CreatedUtc, entry.LastSeenUtc, entry.InFlight, StateFor(entry.InFlight, entry.LastSeenUtc, now), workspaces);
    }

    private static WorkspaceEntry GetOrCreateWorkspace(Dictionary<string, WorkspaceEntry> workspaces, string key, DateTimeOffset now)
    {
        if (workspaces.TryGetValue(key, out var existing)) return existing;
        var created = new WorkspaceEntry { FirstSeenUtc = now, LastSeenUtc = now };
        workspaces[key] = created;
        return created;
    }

    private UnboundEntry GetOrCreateUnbound(string key, DateTimeOffset now)
    {
        if (_unbound.TryGetValue(key, out var existing)) return existing;
        var created = new UnboundEntry { FirstSeenUtc = now, LastSeenUtc = now, PendingState = true };
        _unbound[key] = created;
        return created;
    }

    private static UsageCounters CountersForCall(long requestBytes, long responseBytes, string? toolName, bool isError, long latencyTicks)
    {
        var classification = ToolUsageClassifier.Classify(toolName);
        return new UsageCounters(
            1,
            1,
            classification.IsExecutionTask ? 1 : 0,
            requestBytes,
            responseBytes,
            McpTokenEstimator.EstimateFromUtf8Bytes(requestBytes),
            McpTokenEstimator.EstimateFromUtf8Bytes(responseBytes),
            isError ? 1 : 0,
            classification.Category == ToolUsageCategory.Read ? 1 : 0,
            classification.Category == ToolUsageCategory.Write ? 1 : 0,
            classification.Category == ToolUsageCategory.Command ? 1 : 0,
            classification.Category == ToolUsageCategory.Git ? 1 : 0,
            classification.Category == ToolUsageCategory.Skill ? 1 : 0,
            classification.Category == ToolUsageCategory.Other ? 1 : 0,
            latencyTicks,
            latencyTicks);
    }

    private static string NormalizeWorkspaceKey(string workspaceKey)
    {
        if (string.IsNullOrWhiteSpace(workspaceKey)) throw new ArgumentException("Workspace key cannot be empty.", nameof(workspaceKey));
        return workspaceKey.Trim().ToUpperInvariant();
    }

    private static void ValidateHash(string sessionHash)
    {
        if (sessionHash.Length != 64 || !sessionHash.All(ch => char.IsAsciiHexDigit(ch)))
            throw new ArgumentException("Logical session hash must be a lowercase or uppercase SHA-256 hex digest.", nameof(sessionHash));
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
}
