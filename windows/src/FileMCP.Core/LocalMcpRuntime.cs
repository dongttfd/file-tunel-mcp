using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace FileMCP.Core;

internal sealed class ProfileLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _held;

    public ProfileLock(string profile)
    {
        var hash = Fnv1A64(Encoding.UTF8.GetBytes(profile));
        _mutex = new Mutex(false, $@"Local\FileMCP-profile-{hash:x16}");
    }

    public void Acquire()
    {
        if (_held) return;
        try { _held = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _held = true; }
        if (!_held) throw new FileMcpException("This tunnel profile is already in use by another FileMCP instance.");
    }

    public void Dispose()
    {
        if (_held) { try { _mutex.ReleaseMutex(); } catch { } _held = false; }
        _mutex.Dispose();
    }

    private static ulong Fnv1A64(IEnumerable<byte> bytes)
    {
        ulong hash = 1469598103934665603UL;
        foreach (var value in bytes) hash = (hash ^ value) * 1099511628211UL;
        return hash;
    }
}

public sealed class LocalMcpRuntime : IAsyncDisposable
{
    private sealed record TunnelLaunchContext(
        string Executable,
        string[] Arguments,
        IReadOnlyDictionary<string, string> Environment,
        string[] Sensitive,
        string HealthUrlFilePath);

    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _stateGate = new();
    private readonly string? _profileDirectoryOverride;
    private readonly string? _workspaceKey;
    private readonly ObservabilityHub? _observability;
    private readonly WorkspaceUsageMeter? _usageMeter;
    private readonly TunnelRestartPolicy _restartPolicy;
    private readonly Func<TimeSpan, CancellationToken, Task> _restartDelay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<double> _jitter;
    private readonly SemaphoreSlim _healthProbeGate = new(1, 1);
    private string? _configuredHealthAddress;
    private string? _resolvedHealthAddress;
    private bool _localServerReady;
    private bool _tunnelProcessRunning;
    private TunnelHealthProbeState _tunnelHealth = TunnelHealthProbeState.NotConfigured;
    private DateTimeOffset? _lastTunnelHealthCheckUtc;
    private DateTimeOffset? _lastTunnelHealthSuccessUtc;
    private LocalMcpRuntimeState _state = LocalMcpRuntimeState.Stopped;
    private CancellationTokenSource? _startupCts;
    private CancellationTokenSource? _restartCts;
    private Task? _restartTask;
    private LocalMcpServer? _server;
    private ManagedProcess? _tunnelProcess;
    private ProfileLock? _profileLock;
    private TunnelLaunchContext? _tunnelLaunch;
    private DateTimeOffset? _tunnelStartedUtc;
    private int _tunnelGeneration;
    private bool _requestedStop;

    private long _totalRestarts;
    private long _cooldownEntries;
    private int _supervisorConsecutiveRestarts;
    private int _supervisorAttemptsInWindow;
    private bool _restartPending;
    private DateTimeOffset? _nextRestartUtc;
    private DateTimeOffset? _lastTunnelStartedUtc;
    private DateTimeOffset? _lastTunnelExitUtc;
    private int? _lastExitCode;

    public LocalMcpRuntime(string? profileDirectory = null)
        : this(null, null, profileDirectory, TunnelSupervisorOptions.Default, null, null, null, true)
    {
    }

    public LocalMcpRuntime(string workspaceKey, ObservabilityHub observability, string? profileDirectory = null)
        : this(workspaceKey, observability, profileDirectory, TunnelSupervisorOptions.Default, null, null, null, true)
    {
    }

    internal LocalMcpRuntime(
        string workspaceKey,
        ObservabilityHub observability,
        string? profileDirectory,
        TunnelSupervisorOptions supervisorOptions,
        Func<TimeSpan, CancellationToken, Task>? restartDelay = null,
        Func<DateTimeOffset>? clock = null,
        Func<double>? jitter = null)
        : this(workspaceKey, observability, profileDirectory, supervisorOptions, restartDelay, clock, jitter, true)
    {
    }

    private LocalMcpRuntime(
        string? workspaceKey,
        ObservabilityHub? observability,
        string? profileDirectory,
        TunnelSupervisorOptions supervisorOptions,
        Func<TimeSpan, CancellationToken, Task>? restartDelay,
        Func<DateTimeOffset>? clock,
        Func<double>? jitter,
        bool initialize)
    {
        _ = initialize;
        supervisorOptions.Validate();
        _restartPolicy = new TunnelRestartPolicy(supervisorOptions);
        _restartDelay = restartDelay ?? ((delay, token) => Task.Delay(delay, token));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _jitter = jitter ?? Random.Shared.NextDouble;
        _profileDirectoryOverride = profileDirectory;

        if (observability is not null)
        {
            _workspaceKey = string.IsNullOrWhiteSpace(workspaceKey)
                ? throw new ArgumentException("Workspace key cannot be empty.", nameof(workspaceKey))
                : workspaceKey.Trim().ToUpperInvariant();
            _observability = observability;
            _usageMeter = observability.MeterFor(_workspaceKey);
        }
    }

    public event Action<LocalMcpRuntimeState>? StateChanged;
    public event Action<string>? Log;

    public LocalMcpRuntimeState State { get { lock (_stateGate) return _state; } }

    public TunnelSupervisorSnapshot SupervisorSnapshot
    {
        get
        {
            lock (_stateGate)
            {
                return new TunnelSupervisorSnapshot(
                    _totalRestarts,
                    _cooldownEntries,
                    _supervisorConsecutiveRestarts,
                    _supervisorAttemptsInWindow,
                    _restartPending,
                    _nextRestartUtc,
                    _lastTunnelStartedUtc,
                    _lastTunnelExitUtc,
                    _lastExitCode);
            }
        }
    }

    public LocalMcpRuntimeHealthSnapshot HealthSnapshot
    {
        get
        {
            lock (_stateGate)
            {
                return new LocalMcpRuntimeHealthSnapshot(
                    _workspaceKey,
                    _state.Status,
                    _localServerReady,
                    _tunnelProcessRunning,
                    _tunnelHealth,
                    _lastTunnelHealthCheckUtc,
                    _lastTunnelHealthSuccessUtc,
                    _restartPending,
                    _nextRestartUtc,
                    _supervisorConsecutiveRestarts,
                    _supervisorAttemptsInWindow,
                    _totalRestarts,
                    _cooldownEntries);
            }
        }
    }

    public async Task<LocalMcpRuntimeHealthSnapshot> RefreshHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!await _healthProbeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return HealthSnapshot;
        try
        {
            string? address;
            string? resolvedAddress;
            string? healthUrlFilePath;
            bool processRunning;
            lock (_stateGate)
            {
                address = _configuredHealthAddress;
                resolvedAddress = _resolvedHealthAddress;
                healthUrlFilePath = _tunnelLaunch?.HealthUrlFilePath;
                processRunning = _tunnelProcessRunning;
            }

            var now = _clock().ToUniversalTime();
            if (!TryParseProbeEndpoint(address, out var host, out var port))
            {
                if (!TryParseProbeEndpoint(resolvedAddress, out host, out port))
                {
                    if (!TryReadResolvedHealthEndpoint(healthUrlFilePath, out var discoveredAddress, out host, out port))
                    {
                        lock (_stateGate)
                        {
                            _tunnelHealth = TunnelHealthProbeState.NotConfigured;
                            _lastTunnelHealthCheckUtc = now;
                        }
                        return HealthSnapshot;
                    }

                    lock (_stateGate) _resolvedHealthAddress = discoveredAddress;
                }
            }

            if (!processRunning)
            {
                lock (_stateGate)
                {
                    _tunnelHealth = TunnelHealthProbeState.Unreachable;
                    _lastTunnelHealthCheckUtc = now;
                }
                return HealthSnapshot;
            }

            var reachable = false;
            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(300));
                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                reachable = client.Connected;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (SocketException) { }

            lock (_stateGate)
            {
                _lastTunnelHealthCheckUtc = now;
                _tunnelHealth = reachable ? TunnelHealthProbeState.Reachable : TunnelHealthProbeState.Unreachable;
                if (reachable) _lastTunnelHealthSuccessUtc = now;
            }
            return HealthSnapshot;
        }
        finally
        {
            _healthProbeGate.Release();
        }
    }
    public async Task StartAsync(LocalMcpConfiguration configuration)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State.Status is not (LocalMcpRuntimeStatus.Stopped or LocalMcpRuntimeStatus.Failed)) return;
            if (_requestedStop) { _requestedStop = false; SetState(LocalMcpRuntimeState.Stopped); return; }
            CancelRestartUnsafe();
            _restartPolicy.Reset();
            UpdatePolicyCountersUnsafe();
            ResetSupervisorLifecycleUnsafe();
            SetState(LocalMcpRuntimeState.Starting);
            EmitLog("[Runtime] Starting FileMCP...\n");
            _startupCts?.Dispose();
            _startupCts = new CancellationTokenSource();
            var cancellationToken = _startupCts.Token;
            try
            {
                var healthAddress = ValidateConfiguration(configuration);
                SetConfiguredHealthAddress(healthAddress);
                _profileLock = new ProfileLock(configuration.Profile);
                _profileLock.Acquire();
                var localAuthToken = MakeLocalAuthToken();
                _server = new LocalMcpServer(
                    configuration.Port,
                    configuration.AllowedDirectory,
                    configuration.GitUserName,
                    configuration.GitUserEmail,
                    configuration.EnableCommands,
                    localAuthToken,
                    EmitLog,
                    _usageMeter,
                    _observability?.ChatCorrelation,
                    _observability?.Sessions,
                    _workspaceKey,
                    _observability?.StandardTelemetry);
                await _server.StartAsync(cancellationToken).ConfigureAwait(false);
                lock (_stateGate) _localServerReady = true;

                var tunnelClient = TunnelClientPath();
                var profileDirectory = TunnelProfileDirectory();
                var environment = TunnelClientEnvironment(configuration.ApiKey, localAuthToken);
                var localAuthHeader = $"{FileMcpConstants.LocalAuthHeaderName}: env:FILEMCP_LOCAL_AUTH_TOKEN";
                environment["MCP_EXTRA_HEADERS"] = localAuthHeader;
                environment["MCP_DISCOVERY_EXTRA_HEADERS"] = localAuthHeader;
                string[] sensitive = [configuration.ApiKey, localAuthToken];

                EmitLog("[Tunnel] Configuring Secure MCP Tunnel...\n");
                var initResult = await ProcessRunner.RunAsync(
                    tunnelClient,
                    ["init", "--sample", "sample_mcp_remote_no_auth", "--profile", configuration.Profile, "--profile-dir", profileDirectory, "--force", "--tunnel-id", configuration.TunnelId, "--mcp-server-url", $"http://127.0.0.1:{configuration.Port}/mcp", "--health-listen-addr", healthAddress],
                    environment: environment,
                    timeoutSeconds: 30,
                    outputLimitBytes: 250_000,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                RequireSuccess(initResult, "tunnel-client init", sensitive);

                EmitLog("[Tunnel] Checking tunnel configuration...\n");
                var doctorResult = await ProcessRunner.RunAsync(
                    tunnelClient,
                    ["doctor", "--profile", configuration.Profile, "--profile-dir", profileDirectory, "--health-listen-addr", healthAddress, "--explain"],
                    environment: environment,
                    timeoutSeconds: 30,
                    outputLimitBytes: 250_000,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                RequireSuccess(doctorResult, "tunnel-client doctor", sensitive);

                var healthUrlFilePath = Path.Combine(profileDirectory, configuration.Profile + ".health-url");
                _tunnelLaunch = new TunnelLaunchContext(
                    tunnelClient,
                    ["run", "--profile", configuration.Profile, "--profile-dir", profileDirectory, "--health-listen-addr", healthAddress, "--health.url-file", healthUrlFilePath],
                    environment,
                    sensitive,
                    healthUrlFilePath);
                StartTunnelProcessUnsafe(_tunnelLaunch, countAsRestart: false);
                SetState(LocalMcpRuntimeState.Running);
                EmitLog($"[Runtime] OpenAI Secure MCP Tunnel started. Command execution: {(configuration.EnableCommands ? "enabled" : "disabled")}.\n");
            }
            catch (OperationCanceledException) when (_requestedStop || _startupCts?.IsCancellationRequested == true)
            {
                FinishStop();
            }
            catch (Exception ex)
            {
                CleanupRuntime();
                SetState(LocalMcpRuntimeState.Failed(ex.Message));
                EmitLog("[Runtime] ERROR: " + ex.Message + "\n");
            }
        }
        finally { _serial.Release(); }
    }

    public async Task StopAsync()
    {
        _requestedStop = true;
        _startupCts?.Cancel();
        CancelRestartSignal();
        Task? restartTask;
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State.Status == LocalMcpRuntimeStatus.Stopped)
            {
                _requestedStop = false;
                restartTask = _restartTask;
            }
            else
            {
                if (State.Status != LocalMcpRuntimeStatus.Stopping) SetState(LocalMcpRuntimeState.Stopping);
                EmitLog("[Runtime] Disconnecting...\n");
                if (_tunnelProcess is not null) await _tunnelProcess.StopAsync().ConfigureAwait(false);
                restartTask = _restartTask;
                FinishStop();
            }
        }
        finally { _serial.Release(); }
        await AwaitRestartTaskAsync(restartTask).ConfigureAwait(false);
    }

    public async Task ShutdownAsync()
    {
        _requestedStop = true;
        _startupCts?.Cancel();
        CancelRestartSignal();
        Task? restartTask;
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            _tunnelProcess?.StopSynchronously();
            restartTask = _restartTask;
            FinishStop();
        }
        finally { _serial.Release(); }
        await AwaitRestartTaskAsync(restartTask).ConfigureAwait(false);
    }

    private async Task TunnelDidExitAsync(int generation, int exitCode)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != _tunnelGeneration) return;
            if (_tunnelProcess is null && State.Status == LocalMcpRuntimeStatus.Stopped) return;

            _tunnelProcess?.Dispose();
            _tunnelProcess = null;
            var now = _clock().ToUniversalTime();
            lock (_stateGate)
            {
                _tunnelProcessRunning = false;
                var hadProbeEndpoint =
                    TryParseProbeEndpoint(_configuredHealthAddress, out _, out _) ||
                    TryParseProbeEndpoint(_resolvedHealthAddress, out _, out _);
                _resolvedHealthAddress = null;
                if (hadProbeEndpoint)
                {
                    _tunnelHealth = TunnelHealthProbeState.Unreachable;
                    _lastTunnelHealthCheckUtc = now;
                }
                else
                {
                    _tunnelHealth = TunnelHealthProbeState.NotConfigured;
                }
            }
            RecordTunnelExitUnsafe(now, exitCode);

            if (_requestedStop || State.Status == LocalMcpRuntimeStatus.Stopping)
            {
                FinishStop();
                return;
            }

            if (_tunnelLaunch is null)
            {
                var missing = "Tunnel stopped and restart context is unavailable.";
                SetState(LocalMcpRuntimeState.Failed(missing));
                EmitLog("[Runtime] " + missing + "\n");
                return;
            }

            var started = _tunnelStartedUtc ?? now;
            var decision = _restartPolicy.Next(started, now, _jitter);
            UpdatePolicyCountersUnsafe();
            ScheduleRestartUnsafe(decision, exitCode, now);
        }
        finally { _serial.Release(); }
    }

    private void ScheduleRestartUnsafe(TunnelRestartDecision decision, int exitCode, DateTimeOffset now)
    {
        CancelRestartUnsafe();
        _restartCts = new CancellationTokenSource();
        var token = _restartCts.Token;
        SetRestartPendingUnsafe(decision, now);
        if (decision.IsCooldown)
        {
            lock (_stateGate) _cooldownEntries++;
            SetState(LocalMcpRuntimeState.Cooldown($"Tunnel exited with status {exitCode}; restart budget exhausted."));
            EmitLog($"[Runtime] Tunnel exited unexpectedly (status {exitCode}). Restart budget exhausted; cooldown {decision.Delay.TotalMilliseconds:0} ms.\n");
        }
        else
        {
            SetState(LocalMcpRuntimeState.Restarting($"Tunnel exited with status {exitCode}; restart attempt {decision.AttemptNumber} pending."));
            EmitLog($"[Runtime] Tunnel exited unexpectedly (status {exitCode}). Restart attempt {decision.AttemptNumber} in {decision.Delay.TotalMilliseconds:0} ms.\n");
        }
        _restartTask = Task.Run(() => RestartLoopAsync(decision, token), CancellationToken.None);
    }

    private async Task RestartLoopAsync(TunnelRestartDecision initialDecision, CancellationToken cancellationToken)
    {
        var decision = initialDecision;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (decision.Delay > TimeSpan.Zero)
                    await _restartDelay(decision.Delay, cancellationToken).ConfigureAwait(false);

                if (decision.IsCooldown)
                {
                    await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (_requestedStop || _tunnelLaunch is null) return;
                        _restartPolicy.Reset();
                        UpdatePolicyCountersUnsafe();
                        var now = _clock().ToUniversalTime();
                        decision = _restartPolicy.Next(now, now, _jitter);
                        UpdatePolicyCountersUnsafe();
                        SetRestartPendingUnsafe(decision, now);
                        SetState(LocalMcpRuntimeState.Restarting($"Tunnel cooldown ended; restart attempt {decision.AttemptNumber} pending."));
                    }
                    finally { _serial.Release(); }
                    continue;
                }

                await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_requestedStop || _tunnelLaunch is null) return;
                    try
                    {
                        StartTunnelProcessUnsafe(_tunnelLaunch, countAsRestart: true);
                        SetState(LocalMcpRuntimeState.Running);
                        EmitLog("[Runtime] Tunnel restart succeeded.\n");
                        return;
                    }
                    catch (Exception ex)
                    {
                        var now = _clock().ToUniversalTime();
                        EmitLog($"[Runtime] Tunnel restart launch failed: {ex.Message}\n");
                        decision = _restartPolicy.Next(now, now, _jitter);
                        UpdatePolicyCountersUnsafe();
                        SetRestartPendingUnsafe(decision, now);
                        if (decision.IsCooldown)
                        {
                            lock (_stateGate) _cooldownEntries++;
                            SetState(LocalMcpRuntimeState.Cooldown("Tunnel restart budget exhausted after launch failures."));
                        }
                        else
                        {
                            SetState(LocalMcpRuntimeState.Restarting($"Tunnel restart attempt {decision.AttemptNumber} pending."));
                        }
                    }
                }
                finally { _serial.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void StartTunnelProcessUnsafe(TunnelLaunchContext launch, bool countAsRestart)
    {
        PrepareHealthUrlFileForLaunch(launch.HealthUrlFilePath);
        var generation = ++_tunnelGeneration;
        _tunnelProcess = ProcessRunner.StartManaged(
            launch.Executable,
            launch.Arguments,
            null,
            launch.Environment,
            text => EmitLog("[Tunnel] " + Redact(text, launch.Sensitive)),
            exitCode => _ = Task.Run(() => TunnelDidExitAsync(generation, exitCode)));
        var now = _clock().ToUniversalTime();
        _tunnelStartedUtc = now;
        lock (_stateGate)
        {
            _lastTunnelStartedUtc = now;
            _tunnelProcessRunning = true;
            _resolvedHealthAddress = null;
            _tunnelHealth = TryParseProbeEndpoint(_configuredHealthAddress, out _, out _)
                ? TunnelHealthProbeState.Unknown
                : TunnelHealthProbeState.NotConfigured;
            _restartPending = false;
            _nextRestartUtc = null;
            if (countAsRestart) _totalRestarts++;
        }
    }

    private void RecordTunnelExitUnsafe(DateTimeOffset now, int exitCode)
    {
        lock (_stateGate)
        {
            _lastTunnelExitUtc = now;
            _lastExitCode = exitCode;
        }
    }

    private void SetRestartPendingUnsafe(TunnelRestartDecision decision, DateTimeOffset now)
    {
        lock (_stateGate)
        {
            _restartPending = true;
            _nextRestartUtc = now + decision.Delay;
        }
    }

    private void UpdatePolicyCountersUnsafe()
    {
        lock (_stateGate)
        {
            _supervisorConsecutiveRestarts = _restartPolicy.ConsecutiveRestarts;
            _supervisorAttemptsInWindow = _restartPolicy.AttemptsInWindow;
        }
    }

    private void ResetSupervisorLifecycleUnsafe()
    {
        lock (_stateGate)
        {
            _restartPending = false;
            _nextRestartUtc = null;
            _lastTunnelStartedUtc = null;
            _lastTunnelExitUtc = null;
            _lastExitCode = null;
        }
    }

    private void CancelRestartSignal()
    {
        CancellationTokenSource? cts;
        lock (_stateGate) cts = _restartCts;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void CancelRestartUnsafe()
    {
        try { _restartCts?.Cancel(); } catch (ObjectDisposedException) { }
        _restartCts?.Dispose();
        _restartCts = null;
        lock (_stateGate)
        {
            _restartPending = false;
            _nextRestartUtc = null;
        }
    }

    private static async Task AwaitRestartTaskAsync(Task? restartTask)
    {
        if (restartTask is null) return;
        try { await restartTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private void FinishStop()
    {
        CancelRestartUnsafe();
        CleanupRuntime();
        _restartPolicy.Reset();
        UpdatePolicyCountersUnsafe();
        _requestedStop = false;
        SetState(LocalMcpRuntimeState.Stopped);
        EmitLog("[Runtime] Tunnel stopped.\n");
    }

    private void CleanupRuntime()
    {
        _tunnelGeneration++;
        var healthUrlFilePath = _tunnelLaunch?.HealthUrlFilePath;
        _server?.Stop();
        _server = null;
        _tunnelProcess?.Dispose();
        _tunnelProcess = null;
        _profileLock?.Dispose();
        _profileLock = null;
        _tunnelLaunch = null;
        _tunnelStartedUtc = null;
        BestEffortDeleteHealthUrlFile(healthUrlFilePath);
        lock (_stateGate)
        {
            _localServerReady = false;
            _tunnelProcessRunning = false;
            _resolvedHealthAddress = null;
            _tunnelHealth = TryParseProbeEndpoint(_configuredHealthAddress, out _, out _)
                ? TunnelHealthProbeState.Unknown
                : TunnelHealthProbeState.NotConfigured;
        }
    }

    private void SetConfiguredHealthAddress(string healthAddress)
    {
        lock (_stateGate)
        {
            _configuredHealthAddress = healthAddress;
            _resolvedHealthAddress = null;
            _tunnelHealth = TryParseProbeEndpoint(healthAddress, out _, out _)
                ? TunnelHealthProbeState.Unknown
                : TunnelHealthProbeState.NotConfigured;
            _lastTunnelHealthCheckUtc = null;
            _lastTunnelHealthSuccessUtc = null;
        }
    }

    private void PrepareHealthUrlFileForLaunch(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FileMcpException("Could not prepare the tunnel health discovery file.");
        }

        lock (_stateGate)
        {
            _resolvedHealthAddress = null;
            _tunnelHealth = TryParseProbeEndpoint(_configuredHealthAddress, out _, out _)
                ? TunnelHealthProbeState.Unknown
                : TunnelHealthProbeState.NotConfigured;
        }
    }

    private static void BestEffortDeleteHealthUrlFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    internal static bool TryReadResolvedHealthEndpoint(string? path, out string normalizedAddress, out string host, out int port)
    {
        normalizedAddress = "";
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > 512) return false;
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is <= 0 or > 512) return false;
            var text = new UTF8Encoding(false, true).GetString(bytes).Trim();
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
            if (uri.AbsolutePath is not ("" or "/")) return false;

            var rawHost = uri.Host.Trim('[', ']').ToLowerInvariant();
            string resolvedHost;
            if (uri.HostNameType == UriHostNameType.IPv6)
            {
                if (!IPAddress.TryParse(rawHost, out var ipv6) || !IPAddress.IsLoopback(ipv6)) return false;
                resolvedHost = "::1";
            }
            else
            {
                if (rawHost is not ("localhost" or "127.0.0.1")) return false;
                resolvedHost = rawHost;
            }

            var authority = uri.Authority;
            var hasExplicitPort = uri.HostNameType == UriHostNameType.IPv6
                ? authority.LastIndexOf("]:", StringComparison.Ordinal) >= 0
                : authority.LastIndexOf(':') > 0;
            if (!hasExplicitPort || uri.Port is <= 0 or > 65535) return false;

            host = resolvedHost;
            port = uri.Port;
            normalizedAddress = resolvedHost == "::1" ? $"[::1]:{port}" : $"{resolvedHost}:{port}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or UriFormatException)
        {
            return false;
        }
    }

    internal static bool TryParseProbeEndpoint(string? normalizedHealthAddress, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(normalizedHealthAddress)) return false;
        var value = normalizedHealthAddress.Trim();
        string portText;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close <= 1 || close + 2 > value.Length || value[close + 1] != ':') return false;
            host = value[1..close];
            portText = value[(close + 2)..];
        }
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon <= 0 || colon == value.Length - 1) return false;
            host = value[..colon];
            portText = value[(colon + 1)..];
        }
        return int.TryParse(portText, out port) && port is > 0 and <= 65535;
    }
    public static string ValidateConfiguration(LocalMcpConfiguration configuration)
    {
        foreach (var (label, value) in new[] { ("Tunnel ID", configuration.TunnelId), ("Runtime API key", configuration.ApiKey), ("Profile", configuration.Profile), ("Shared directory", configuration.AllowedDirectory) })
            if (string.IsNullOrWhiteSpace(value)) throw new FileMcpException($"{label} cannot be empty.");
        if (!IsValidTunnelId(configuration.TunnelId)) throw new FileMcpException("Tunnel ID must match tunnel_<32 lowercase letters or digits>.");
        if (!IsValidProfileName(configuration.Profile)) throw new FileMcpException("Profile must start with a letter or number and contain only letters, numbers, '.', '_' or '-' (maximum 128 characters).");
        if (configuration.Port == 0) throw new FileMcpException("MCP port must be between 1 and 65535.");
        return NormalizeHealthAddress(configuration.HealthAddress) ?? throw new FileMcpException("Health listener must use localhost, 127.0.0.1, or [::1] with a port from 0 to 65535.");
    }

    public static bool IsValidTunnelId(string value) => value.Length == 39 && value.StartsWith("tunnel_", StringComparison.Ordinal) && value[7..].All(ch => (ch >= 'a' && ch <= 'z') || char.IsAsciiDigit(ch));
    public static bool IsValidProfileName(string value) => value.Length is >= 1 and <= 128 && char.IsAsciiLetterOrDigit(value[0]) && value.Skip(1).All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');

    public static string? NormalizeHealthAddress(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return "127.0.0.1:0";
        if (trimmed.StartsWith('['))
        {
            var close = trimmed.IndexOf(']');
            if (close < 0 || trimmed[1..close].ToLowerInvariant() != "::1") return null;
            var rest = trimmed[(close + 1)..];
            if (!rest.StartsWith(':') || !ValidHealthPort(rest[1..])) return null;
            return "[::1]:" + rest[1..];
        }
        var parts = trimmed.Split(':', 2);
        if (parts.Length != 2 || (parts[0].ToLowerInvariant() is not ("127.0.0.1" or "localhost")) || !ValidHealthPort(parts[1])) return null;
        return parts[0].ToLowerInvariant() + ":" + parts[1];
    }

    private static bool ValidHealthPort(string value) => value.Length > 0 && value.All(char.IsAsciiDigit) && uint.TryParse(value, out var port) && port <= 65535;

    private Dictionary<string, string> TunnelClientEnvironment(string apiKey, string localAuthToken)
    {
        var inherited = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        string[] pass = ["PATH", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "SystemRoot", "WINDIR", "LANG", "LC_ALL", "LC_CTYPE", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy"];
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in pass) if (inherited.TryGetValue(key, out var value) && value.Length > 0) result[key] = value;
        foreach (var pair in inherited.Where(pair => pair.Key.StartsWith("MCP_TEST_", StringComparison.OrdinalIgnoreCase))) result[pair.Key] = pair.Value;
        var noProxy = (inherited.GetValueOrDefault("NO_PROXY") ?? inherited.GetValueOrDefault("no_proxy") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var loopback in new[] { "127.0.0.1", "localhost", "::1" }) if (!noProxy.Contains(loopback, StringComparer.OrdinalIgnoreCase)) noProxy.Add(loopback);
        result["NO_PROXY"] = result["no_proxy"] = string.Join(',', noProxy);
        result["CONTROL_PLANE_API_KEY"] = apiKey;
        result["FILEMCP_LOCAL_AUTH_TOKEN"] = localAuthToken;
        return result;
    }

    private string TunnelProfileDirectory()
    {
        var directory = _profileDirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileMCP", "tunnel-profiles");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string TunnelClientPath()
    {
        var sibling = Path.Combine(AppContext.BaseDirectory, "tunnel-client.exe");
        if (File.Exists(sibling)) return sibling;
        var overridePath = Environment.GetEnvironmentVariable("MCP_TUNNEL_CLIENT");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;
        throw new FileMcpException("tunnel-client.exe was not found beside FileMCP.exe.");
    }

    private static string MakeLocalAuthToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private void RequireSuccess(ProcessResult result, string operation, IReadOnlyList<string> sensitive)
    {
        if (result.TimedOut) throw new FileMcpException(operation + " timed out.");
        if (result.ExitCode != 0) throw new FileMcpException(operation + " failed: " + Redact(string.IsNullOrEmpty(result.Stderr) ? result.Stdout : result.Stderr, sensitive));
        if (result.Stdout.Length > 0) EmitLog("[Tunnel] " + Redact(result.Stdout, sensitive) + (result.Stdout.EndsWith('\n') ? "" : "\n"));
        if (result.Stderr.Length > 0) EmitLog("[Tunnel] " + Redact(result.Stderr, sensitive) + (result.Stderr.EndsWith('\n') ? "" : "\n"));
    }

    private static string Redact(string text, IReadOnlyList<string> sensitive)
    {
        foreach (var secret in sensitive.Where(s => s.Length > 0)) text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return text;
    }

    private void EmitLog(string text) => Log?.Invoke(text);

    private void SetState(LocalMcpRuntimeState state)
    {
        LocalMcpRuntimeState previous;
        lock (_stateGate)
        {
            previous = _state;
            _state = state;
        }

        if (_observability is not null && _workspaceKey is not null)
        {
            try
            {
                if (previous.Status != LocalMcpRuntimeStatus.Running && state.Status == LocalMcpRuntimeStatus.Running)
                    _observability.MarkRuntimeRunning(_workspaceKey);
                else if (previous.Status == LocalMcpRuntimeStatus.Running && state.Status != LocalMcpRuntimeStatus.Running)
                    _observability.MarkRuntimeStopped(_workspaceKey);
            }
            catch (Exception ex)
            {
                EmitLog($"[Telemetry] runtime uptime metric ignored: {ex.Message}\n");
            }
        }

        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _serial.Dispose();
        _startupCts?.Dispose();
        _restartCts?.Dispose();
        await _healthProbeGate.WaitAsync().ConfigureAwait(false);
        _healthProbeGate.Release();
        _healthProbeGate.Dispose();
    }
}
