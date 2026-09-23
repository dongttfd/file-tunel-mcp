namespace FileMCP.Core;

internal sealed record TunnelSupervisorOptions(
    TimeSpan InitialBackoff,
    TimeSpan MaxBackoff,
    TimeSpan RestartWindow,
    int MaxRestartsInWindow,
    TimeSpan StableRunReset,
    double JitterRatio)
{
    public static TunnelSupervisorOptions Default { get; } = new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(5),
        5,
        TimeSpan.FromMinutes(2),
        0.20);

    public void Validate()
    {
        if (InitialBackoff <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(InitialBackoff));
        if (MaxBackoff < InitialBackoff) throw new ArgumentOutOfRangeException(nameof(MaxBackoff));
        if (RestartWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RestartWindow));
        if (MaxRestartsInWindow <= 0) throw new ArgumentOutOfRangeException(nameof(MaxRestartsInWindow));
        if (StableRunReset <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(StableRunReset));
        if (JitterRatio is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(JitterRatio));
    }
}

internal readonly record struct TunnelRestartDecision(
    bool IsCooldown,
    TimeSpan Delay,
    int AttemptNumber,
    DateTimeOffset? ResumeAtUtc);

internal sealed class TunnelRestartPolicy
{
    private readonly TunnelSupervisorOptions _options;
    private readonly Queue<DateTimeOffset> _attempts = new();
    private int _consecutiveRestarts;

    public TunnelRestartPolicy(TunnelSupervisorOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public int ConsecutiveRestarts => _consecutiveRestarts;
    public int AttemptsInWindow => _attempts.Count;

    public TunnelRestartDecision Next(
        DateTimeOffset processStartedUtc,
        DateTimeOffset nowUtc,
        Func<double>? random = null)
    {
        var started = processStartedUtc.ToUniversalTime();
        var now = nowUtc.ToUniversalTime();
        if (now - started >= _options.StableRunReset)
            Reset();

        while (_attempts.Count > 0 && now - _attempts.Peek() >= _options.RestartWindow)
            _attempts.Dequeue();

        if (_attempts.Count >= _options.MaxRestartsInWindow)
        {
            var resumeAt = _attempts.Peek() + _options.RestartWindow;
            var delay = resumeAt > now ? resumeAt - now : TimeSpan.Zero;
            return new TunnelRestartDecision(true, delay, _consecutiveRestarts, resumeAt);
        }

        var baseDelayTicks = _options.InitialBackoff.Ticks * Math.Pow(2, Math.Min(_consecutiveRestarts, 30));
        var clampedTicks = Math.Min(baseDelayTicks, _options.MaxBackoff.Ticks);
        var sample = Math.Clamp((random ?? Random.Shared.NextDouble)(), 0, 1);
        var jitterFactor = 1 - _options.JitterRatio + (2 * _options.JitterRatio * sample);
        var delayTicks = (long)Math.Clamp(clampedTicks * jitterFactor, 0, _options.MaxBackoff.Ticks);

        _attempts.Enqueue(now);
        _consecutiveRestarts++;
        return new TunnelRestartDecision(false, TimeSpan.FromTicks(delayTicks), _consecutiveRestarts, null);
    }

    public void Reset()
    {
        _attempts.Clear();
        _consecutiveRestarts = 0;
    }
}

public sealed record TunnelSupervisorSnapshot(
    long TotalRestarts,
    long CooldownEntries,
    int ConsecutiveRestarts,
    int AttemptsInWindow,
    bool RestartPending,
    DateTimeOffset? NextRestartUtc,
    DateTimeOffset? LastTunnelStartedUtc,
    DateTimeOffset? LastTunnelExitUtc,
    int? LastExitCode);
