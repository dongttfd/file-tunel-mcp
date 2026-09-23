using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace FileMCP.Core;

public readonly record struct LogicalChatConnection(string ChatInstanceId, bool Resumed);

public sealed record LogicalChatCorrelationRetentionSnapshot(
    int KnownHandles,
    long ExpiredEvictions,
    long PressureEvictions,
    long CapacityPressureEvents);

public sealed class LogicalChatCorrelationService
{
    public const string HandlePrefix = "chat_";
    public const int DefaultMaxKnownHandles = 4096;
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(2);
    private const int RandomBytes = 32;

    private sealed class CorrelationEntry
    {
        public long LastSeenUtcTicks;
        public CorrelationEntry(DateTimeOffset nowUtc) => LastSeenUtcTicks = nowUtc.UtcDateTime.Ticks;
    }

    private readonly ConcurrentDictionary<string, CorrelationEntry> _knownHashes = new(StringComparer.Ordinal);
    private readonly object _capacityGate = new();
    private readonly int _maxKnownHandles;
    private readonly TimeSpan _retention;
    private long _expiredEvictions;
    private long _pressureEvictions;
    private long _capacityPressureEvents;

    public LogicalChatCorrelationService(int maxKnownHandles = DefaultMaxKnownHandles, TimeSpan? retention = null)
    {
        if (maxKnownHandles <= 0) throw new ArgumentOutOfRangeException(nameof(maxKnownHandles));
        _maxKnownHandles = maxKnownHandles;
        _retention = retention ?? DefaultRetention;
        if (_retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
    }

    public LogicalChatConnection Connect(string? chatInstanceId = null, DateTimeOffset? nowUtc = null)
    {
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        if (string.IsNullOrWhiteSpace(chatInstanceId))
        {
            lock (_capacityGate)
            {
                RemoveExpired(now);
                EnsureCapacityForOne(now);
                while (true)
                {
                    var handle = HandlePrefix + Base64Url(RandomNumberGenerator.GetBytes(RandomBytes));
                    if (_knownHashes.TryAdd(Hash(handle), new CorrelationEntry(now)))
                        return new LogicalChatConnection(handle, false);
                }
            }
        }

        var candidate = chatInstanceId.Trim();
        if (!IsValidHandle(candidate))
            throw new FileMcpException("Invalid FileMCP chat correlation handle.");
        var hash = Hash(candidate);
        if (!_knownHashes.TryGetValue(hash, out var entry))
            throw new FileMcpException("Unknown FileMCP chat correlation handle. Call filemcp_observability_connect without chat_instance_id to establish a new correlation handle.");
        Touch(entry, now);
        return new LogicalChatConnection(candidate, true);
    }

    public bool TryResolve(string? chatInstanceId, out string sessionHash, DateTimeOffset? nowUtc = null)
    {
        sessionHash = "";
        if (string.IsNullOrWhiteSpace(chatInstanceId)) return false;
        var candidate = chatInstanceId.Trim();
        if (!IsValidHandle(candidate)) return false;
        var hash = Hash(candidate);
        if (!_knownHashes.TryGetValue(hash, out var entry)) return false;
        Touch(entry, (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());
        sessionHash = hash;
        return true;
    }

    public LogicalChatCorrelationRetentionSnapshot Cleanup(DateTimeOffset? nowUtc = null)
    {
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        lock (_capacityGate)
        {
            RemoveExpired(now);
            TrimToCount(_maxKnownHandles, pressure: true);
            return RetentionSnapshot();
        }
    }

    public LogicalChatCorrelationRetentionSnapshot RetentionSnapshot() => new(
        _knownHashes.Count,
        Interlocked.Read(ref _expiredEvictions),
        Interlocked.Read(ref _pressureEvictions),
        Interlocked.Read(ref _capacityPressureEvents));

    public static bool IsValidHandle(string value)
    {
        if (!value.StartsWith(HandlePrefix, StringComparison.Ordinal) || value.Length != HandlePrefix.Length + 43) return false;
        return value[HandlePrefix.Length..].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
    }

    public static string HashForPersistence(string chatInstanceId)
    {
        if (!IsValidHandle(chatInstanceId)) throw new ArgumentException("Invalid FileMCP chat correlation handle.", nameof(chatInstanceId));
        return Hash(chatInstanceId);
    }

    private void EnsureCapacityForOne(DateTimeOffset now)
    {
        if (_knownHashes.Count < _maxKnownHandles) return;
        Interlocked.Increment(ref _capacityPressureEvents);
        RemoveExpired(now);
        if (_knownHashes.Count >= _maxKnownHandles)
            TrimToCount(_maxKnownHandles - 1, pressure: true);
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        var cutoffTicks = now.Subtract(_retention).UtcDateTime.Ticks;
        foreach (var pair in _knownHashes)
        {
            if (Volatile.Read(ref pair.Value.LastSeenUtcTicks) > cutoffTicks) continue;
            if (_knownHashes.TryRemove(new KeyValuePair<string, CorrelationEntry>(pair.Key, pair.Value)))
                Interlocked.Increment(ref _expiredEvictions);
        }
    }

    private void TrimToCount(int targetCount, bool pressure)
    {
        targetCount = Math.Max(0, targetCount);
        if (_knownHashes.Count <= targetCount) return;
        foreach (var pair in _knownHashes.OrderBy(pair => Volatile.Read(ref pair.Value.LastSeenUtcTicks)).ToArray())
        {
            if (_knownHashes.Count <= targetCount) break;
            if (_knownHashes.TryRemove(new KeyValuePair<string, CorrelationEntry>(pair.Key, pair.Value)) && pressure)
                Interlocked.Increment(ref _pressureEvictions);
        }
    }

    private static void Touch(CorrelationEntry entry, DateTimeOffset now)
    {
        var candidate = now.UtcDateTime.Ticks;
        while (true)
        {
            var current = Volatile.Read(ref entry.LastSeenUtcTicks);
            if (candidate <= current) return;
            if (Interlocked.CompareExchange(ref entry.LastSeenUtcTicks, candidate, current) == current) return;
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
