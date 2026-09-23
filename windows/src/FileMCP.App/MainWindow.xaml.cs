using System.Diagnostics;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FileMCP.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace FileMCP.App;

public partial class MainWindow : Window
{
    private static readonly string[] WorkspaceKeys = ["C", "D", "E", "F"];

    private readonly SettingsStore _settingsStore = new();
    private readonly WindowsCredentialStore _credentialStore = new();
    private readonly Dictionary<string, LocalMcpRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservabilityHub _observability;
    private readonly DispatcherTimer _overviewTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private FileMcpSettings _settings;
    private bool _quitting;
    private UsagePeriodPreset _selectedOverviewPeriod = UsagePeriodPreset.Today;
    private DateTimeOffset _lastOverviewPeriodRefreshUtc = DateTimeOffset.MinValue;
    private int _overviewPeriodQueryGeneration;
    private bool _overviewPeriodQueryRunning;
    private string _logBuffer = "";
    private const int MaxLogCharacters = 500_000;

    public MainWindow()
    {
        InitializeComponent();
        var observabilityDatabaseOverride = Environment.GetEnvironmentVariable("FILEMCP_OBSERVABILITY_DB");
        _observability = new ObservabilityHub(
            WorkspaceKeys,
            string.IsNullOrWhiteSpace(observabilityDatabaseOverride) ? null : observabilityDatabaseOverride,
            log: text => Dispatcher.BeginInvoke(new Action(() => AppendLog(text))));
        _overviewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _overviewTimer.Tick += OverviewTimer_Tick;
        OverviewPeriodCombo.SelectionChanged += OverviewPeriodCombo_SelectionChanged;
        _settings = LoadSettingsSafely();
        ApplySettings(_settings);
        ConfigureOtlpFromSettings();
        UpdateApiKeyStatus();

        foreach (var key in WorkspaceKeys)
        {
            var capturedKey = key;
            var runtime = new LocalMcpRuntime(capturedKey, _observability);
            runtime.Log += text => Dispatcher.BeginInvoke(new Action(() => AppendLog($"[{capturedKey}] {text}")));
            runtime.StateChanged += state => Dispatcher.BeginInvoke(new Action(() => UpdateRuntimeState(capturedKey, state)));
            _runtimes[capturedKey] = runtime;
        }

        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open FileMCP", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("About FileMCP", null, (_, _) => Dispatcher.Invoke(ShowAbout));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit FileMCP", null, (_, _) => Dispatcher.Invoke(async () => await QuitAsync()));
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "FileMCP",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = LoadApplicationIcon(),
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);

        RefreshRuntimeUi();
        _ = StartObservabilityAsync();
        RefreshOverviewLiveUi();
        _ = RefreshOverviewPeriodAsync(force: true);
        _overviewTimer.Start();
    }

    private async Task StartObservabilityAsync()
    {
        try
        {
            await _observability.StartAsync();
            await RunObservabilityPackageSmokeIfRequestedAsync();
            await RunOtlpPackageSmokeIfRequestedAsync();
            AppendLog("[Telemetry] Observability store ready.\n");
        }
        catch (Exception ex)
        {
            AppendLog($"[Telemetry] Persistent observability unavailable; MCP remains operational: {ex.Message}\n");
        }
    }
    private async Task RunObservabilityPackageSmokeIfRequestedAsync()
    {
        var markerPath = Environment.GetEnvironmentVariable("FILEMCP_OBSERVABILITY_SMOKE_MARKER");
        if (string.IsNullOrWhiteSpace(markerPath)) return;

        try
        {
            var now = DateTimeOffset.UtcNow;
            var meter = _observability.MeterFor("C");
            meter.RecordRequest(4);
            meter.RecordResponse(4);
            meter.RecordToolCall("read_file", isError: false, latencyTicks: 1);
            await _observability.FlushAsync(now);

            var unix = now.ToUnixTimeSeconds();
            var minuteStart = DateTimeOffset.FromUnixTimeSeconds(unix - unix % 60);
            var persisted = await _observability.QueryExactPeriodAsync(
                new UsagePeriodRange(minuteStart, now.AddMinutes(1)),
                "C");
            if (persisted.ToolCalls < 1 || persisted.ReadCalls < 1 || persisted.RequestBytes < 4 || persisted.ResponseBytes < 4)
                throw new InvalidOperationException("Packaged SQLite telemetry write/read verification returned incomplete counters.");

            var directory = Path.GetDirectoryName(Path.GetFullPath(markerPath));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                markerPath,
                $"PASS tool_calls={persisted.ToolCalls} read_calls={persisted.ReadCalls}");
        }
        catch (Exception ex)
        {
            try { await File.WriteAllTextAsync(markerPath, "FAIL " + ex.Message); } catch { }
            throw;
        }
    }
    private async Task RunOtlpPackageSmokeIfRequestedAsync()
    {
        var markerPath = Environment.GetEnvironmentVariable("FILEMCP_OTLP_SMOKE_MARKER");
        if (string.IsNullOrWhiteSpace(markerPath)) return;

        var endpoint = Environment.GetEnvironmentVariable("FILEMCP_OTLP_SMOKE_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint)) endpoint = "http://127.0.0.1:1";
        try
        {
            var configured = _observability.ConfigureOtlp(new OtlpTelemetrySettings(true, endpoint));
            if (configured.Status != OtlpExporterRuntimeStatus.Configured)
                throw new InvalidOperationException("Packaged OTLP provider configuration failed: " + configured.Error);

            using (var operation = _observability.StandardTelemetry.BeginOperation(
                       FileMcpConstants.ModernProtocolVersion,
                       "tools/call",
                       "read_file",
                       "C",
                       4))
                operation.Complete(4, isError: false);

            await Task.Delay(100);
            var directory = Path.GetDirectoryName(Path.GetFullPath(markerPath));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(markerPath, $"PASS status={configured.Status} endpoint={configured.Endpoint}");
        }
        catch (Exception ex)
        {
            try { await File.WriteAllTextAsync(markerPath, "FAIL " + ex.Message); } catch { }
            throw;
        }
        finally
        {
            ConfigureOtlpFromSettings();
        }
    }
    private async void OverviewTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshRuntimeHealthAsync();
        RefreshOverviewLiveUi();
        if (DateTimeOffset.UtcNow - _lastOverviewPeriodRefreshUtc >= TimeSpan.FromSeconds(5))
            await RefreshOverviewPeriodAsync(force: false);
    }

    private async Task RefreshRuntimeHealthAsync()
    {
        if (_quitting) return;
        try
        {
            await Task.WhenAll(_runtimes.Values.Select(runtime => runtime.RefreshHealthAsync()));
        }
        catch (ObjectDisposedException) when (_quitting) { }
    }

    private void OverviewPeriodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedOverviewPeriod = OverviewPeriodCombo.SelectedIndex switch
        {
            1 => UsagePeriodPreset.Yesterday,
            2 => UsagePeriodPreset.SevenDays,
            3 => UsagePeriodPreset.ThirtyDays,
            _ => UsagePeriodPreset.Today,
        };
        _ = RefreshOverviewPeriodAsync(force: true);
    }

    private void RefreshOverviewLiveUi()
    {
        if (_quitting) return;
        ObservabilitySnapshot snapshot;
        try { snapshot = _observability.Snapshot(); }
        catch (ObjectDisposedException) { return; }

        OverviewClockText.Text = DateTimeOffset.Now.ToString("ddd, dd/MM/yyyy  HH:mm:ss");
        OverviewAppUptimeText.Text = "App uptime: " + FormatDuration(snapshot.AppUptime);
        foreach (var key in WorkspaceKeys)
            if (snapshot.Workspaces.TryGetValue(key, out var workspace)) UpdateOverviewWorkspaceLive(key, workspace, _runtimes[key].HealthSnapshot);

        var realtime = _observability.CaptureRealtimeSample();
        UpdateOverviewHealth(snapshot);
        UpdateRealtimeGraph(realtime);
        RefreshObservedSessionsUi();
    }

    private void UpdateOverviewHealth(ObservabilitySnapshot snapshot)
    {
        var connected = snapshot.Workspaces.Values.Count(workspace => workspace.RuntimeRunning);
        var usage = snapshot.GlobalUsage;
        var averageMs = usage.ToolCalls == 0
            ? 0d
            : (double)usage.TotalLatencyTicks / Stopwatch.Frequency / usage.ToolCalls * 1_000d;
        var maxMs = usage.MaxLatencyTicks <= 0
            ? 0d
            : (double)usage.MaxLatencyTicks / Stopwatch.Frequency * 1_000d;

        OverviewConnectedRuntimesText.Text = $"{connected} / {WorkspaceKeys.Length}";
        OverviewAverageLatencyText.Text = $"{averageMs:N1} ms";
        OverviewMaxLatencyText.Text = $"{maxMs:N1} ms";
        var persistenceText = snapshot.Persistence.Status switch
        {
            TelemetryPersistenceStatus.Ready => "Ready",
            TelemetryPersistenceStatus.Degraded => $"Degraded ({snapshot.Persistence.FailureCount:N0})",
            _ => "Starting",
        };
        var otlpText = snapshot.Otlp.Status switch
        {
            OtlpExporterRuntimeStatus.Configured => "OTLP on",
            OtlpExporterRuntimeStatus.ConfigurationError => "OTLP error",
            _ => "OTLP off",
        };
        OverviewTelemetryHealthText.Text = $"{persistenceText} | {otlpText}";
        OverviewTelemetryHealthText.Foreground = snapshot.Persistence.Status switch
        {
            TelemetryPersistenceStatus.Ready => System.Windows.Media.Brushes.ForestGreen,
            TelemetryPersistenceStatus.Degraded => System.Windows.Media.Brushes.DarkOrange,
            _ => System.Windows.Media.Brushes.Gray,
        };
    }

    private void UpdateRealtimeGraph(RealtimeUsageSample current)
    {
        OverviewRealtimeCurrentText.Text = $"Calls/s: {FormatCount(current.Delta.ToolCalls)} | Tokens est./s: {FormatCount(current.Delta.TotalTokensEst)}";
        var samples = _observability.RealtimeSamples();
        var width = OverviewRealtimeCanvas.ActualWidth;
        var height = OverviewRealtimeCanvas.ActualHeight;
        if (samples.Count < 2 || width <= 1 || height <= 1)
        {
            OverviewCallsPolyline.Points = new PointCollection();
            OverviewTokensPolyline.Points = new PointCollection();
            return;
        }

        OverviewCallsPolyline.Points = BuildRealtimePoints(samples, sample => sample.Delta.ToolCalls, width, height);
        OverviewTokensPolyline.Points = BuildRealtimePoints(samples, sample => sample.Delta.TotalTokensEst, width, height);
    }

    private static PointCollection BuildRealtimePoints(
        IReadOnlyList<RealtimeUsageSample> samples,
        Func<RealtimeUsageSample, long> selector,
        double width,
        double height)
    {
        var max = Math.Max(1L, samples.Max(selector));
        var denominator = Math.Max(1, samples.Count - 1);
        var points = new PointCollection(samples.Count);
        for (var index = 0; index < samples.Count; index++)
        {
            var x = width * index / denominator;
            var y = height - height * selector(samples[index]) / max;
            points.Add(new System.Windows.Point(x, y));
        }
        return points;
    }
    private async Task RefreshOverviewPeriodAsync(bool force)
    {
        if (_quitting) return;
        var nowUtc = DateTimeOffset.UtcNow;
        if (!force && nowUtc - _lastOverviewPeriodRefreshUtc < TimeSpan.FromSeconds(5)) return;
        if (_overviewPeriodQueryRunning)
        {
            if (force) Interlocked.Increment(ref _overviewPeriodQueryGeneration);
            return;
        }

        var generation = Interlocked.Increment(ref _overviewPeriodQueryGeneration);
        _overviewPeriodQueryRunning = true;
        var range = UsagePeriodResolver.Resolve(_selectedOverviewPeriod, nowUtc, TimeZoneInfo.Local);
        OverviewPeriodRangeText.Text = $"{range.FromUtc.ToLocalTime():dd/MM/yyyy HH:mm} - {range.ToUtc.ToLocalTime():dd/MM/yyyy HH:mm}";
        OverviewPeriodUpdatedText.Text = "Loading...";
        try
        {
            await _observability.FlushAsync(nowUtc);
            var globalTask = _observability.QueryExactPeriodAsync(range);
            var workspaceTasks = WorkspaceKeys.ToDictionary(
                key => key,
                key => _observability.QueryExactPeriodAsync(range, key),
                StringComparer.OrdinalIgnoreCase);
            await Task.WhenAll(workspaceTasks.Values.Append(globalTask));
            if (_quitting || generation != Volatile.Read(ref _overviewPeriodQueryGeneration)) return;

            ApplyOverviewUsage(await globalTask);
            foreach (var pair in workspaceTasks)
                ApplyOverviewWorkspacePeriodUsage(pair.Key, await pair.Value);
            _lastOverviewPeriodRefreshUtc = nowUtc;
            OverviewPeriodUpdatedText.Text = "Updated " + DateTimeOffset.Now.ToString("HH:mm:ss");
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _overviewPeriodQueryGeneration))
                OverviewPeriodUpdatedText.Text = "Unavailable: " + ex.Message;
        }
        finally
        {
            _overviewPeriodQueryRunning = false;
        }
    }

    private void ApplyOverviewUsage(UsageCounters usage)
    {
        OverviewTokenInText.Text = FormatCount(usage.TokensInEst);
        OverviewTokenOutText.Text = FormatCount(usage.TokensOutEst);
        OverviewTokenTotalText.Text = FormatCount(usage.TotalTokensEst);
        OverviewInputBytesText.Text = FormatBytes(usage.RequestBytes);
        OverviewOutputBytesText.Text = FormatBytes(usage.ResponseBytes);
        OverviewTotalBytesText.Text = FormatBytes(usage.TotalPayloadBytes);
        OverviewToolCallsText.Text = FormatCount(usage.ToolCalls);
        OverviewExecutionTasksText.Text = FormatCount(usage.ExecutionTasks);
        OverviewReadCallsText.Text = FormatCount(usage.ReadCalls);
        OverviewWriteCallsText.Text = FormatCount(usage.WriteCalls);
        OverviewCommandCallsText.Text = FormatCount(usage.CommandCalls);
        OverviewGitCallsText.Text = FormatCount(usage.GitCalls);
        OverviewSkillCallsText.Text = FormatCount(usage.SkillCalls);
        OverviewErrorsText.Text = FormatCount(usage.Errors);
    }

    private void UpdateOverviewWorkspaceLive(string key, WorkspaceObservabilitySnapshot workspace, LocalMcpRuntimeHealthSnapshot health)
    {
        var status = OverviewStatusText(key);
        status.Text = health.RuntimeStatus switch
        {
            LocalMcpRuntimeStatus.Running => "Connected",
            LocalMcpRuntimeStatus.Restarting => "Reconnecting...",
            LocalMcpRuntimeStatus.Cooldown => "Reconnect cooldown",
            LocalMcpRuntimeStatus.Starting => "Connecting...",
            LocalMcpRuntimeStatus.Stopping => "Disconnecting...",
            LocalMcpRuntimeStatus.Failed => "Failed",
            _ => "Stopped",
        };
        status.Foreground = health.RuntimeStatus switch
        {
            LocalMcpRuntimeStatus.Running => System.Windows.Media.Brushes.ForestGreen,
            LocalMcpRuntimeStatus.Restarting or LocalMcpRuntimeStatus.Cooldown or LocalMcpRuntimeStatus.Starting or LocalMcpRuntimeStatus.Stopping => System.Windows.Media.Brushes.DarkOrange,
            LocalMcpRuntimeStatus.Failed => System.Windows.Media.Brushes.Firebrick,
            _ => System.Windows.Media.Brushes.Gray,
        };
        OverviewUptimeText(key).Text = workspace.RuntimeRunning ? "Uptime: " + FormatDuration(workspace.RuntimeUptime) : "Uptime: -";
        var nextRestart = health.RestartPending && health.NextRestartUtc.HasValue
            ? $" | next restart {Math.Max(0, (health.NextRestartUtc.Value - DateTimeOffset.UtcNow).TotalSeconds):N1}s"
            : "";
        var lastHealthOk = health.LastTunnelHealthSuccessUtc.HasValue
            ? $" | last OK {health.LastTunnelHealthSuccessUtc.Value.ToLocalTime():HH:mm:ss}"
            : "";
        var restartCount = health.TotalRestarts > 0 ? $" | restarts {health.TotalRestarts:N0}" : "";
        OverviewComponentHealthText(key).Text =
            $"MCP: {(health.LocalServerReady ? "ready" : "down")} | Tunnel: {(health.TunnelProcessRunning ? "running" : "down")} | Health: {FormatTunnelHealth(health.TunnelHealth)}{lastHealthOk}{restartCount}{nextRestart}";
    }

    private static string FormatTunnelHealth(TunnelHealthProbeState state) => state switch
    {
        TunnelHealthProbeState.Reachable => "reachable",
        TunnelHealthProbeState.Unreachable => "unreachable",
        TunnelHealthProbeState.NotConfigured => "dynamic/pending",
        _ => "unknown",
    };

    private void ApplyOverviewWorkspacePeriodUsage(string key, UsageCounters usage)
    {
        OverviewCallsText(key).Text = "Calls: " + FormatCount(usage.ToolCalls);
        OverviewTokensText(key).Text = "MCP tokens (est.): " + FormatCount(usage.TotalTokensEst);
    }

    private void RefreshObservedSessionsUi()
    {
        var selectedKey = (OverviewSessionsGrid.SelectedItem as ObservedSessionRow)?.Key;
        var rows = new List<ObservedSessionRow>();
        foreach (var session in _observability.Sessions.Snapshot())
        {
            var usage = session.Workspaces.Values.Select(value => value.Usage).Aggregate(default(UsageCounters), (sum, value) => sum + value);
            rows.Add(new ObservedSessionRow(
                "B:" + session.SessionHash,
                "Bound",
                session.State.ToString(),
                ShortHash(session.SessionHash),
                string.Join(",", session.Workspaces.Keys.OrderBy(key => key)),
                session.CreatedUtc,
                session.LastSeenUtc,
                usage.ToolCalls,
                usage.ExecutionTasks,
                usage.TotalTokensEst,
                usage.TotalPayloadBytes,
                usage.Errors));
        }
        foreach (var unbound in _observability.Sessions.UnboundSnapshot())
        {
            rows.Add(new ObservedSessionRow(
                "U:" + unbound.WorkspaceKey,
                "Unbound",
                unbound.State.ToString(),
                "unbound-" + unbound.WorkspaceKey,
                unbound.WorkspaceKey,
                unbound.FirstSeenUtc,
                unbound.LastSeenUtc,
                unbound.Usage.ToolCalls,
                unbound.Usage.ExecutionTasks,
                unbound.Usage.TotalTokensEst,
                unbound.Usage.TotalPayloadBytes,
                unbound.Usage.Errors));
        }
        rows = rows.OrderBy(row => row.State == nameof(LogicalSessionActivityState.Active) ? 0 : 1)
            .ThenByDescending(row => row.LastSeenUtc)
            .ToList();

        OverviewSessionSummaryText.Text = rows.Count == 0
            ? "No correlated AI chats yet. Unbound MCP traffic remains separate and is never counted as a chat."
            : $"{rows.Count(row => row.Kind == "Bound")} AI chat(s), {rows.Count(row => row.Kind == "Unbound")} unbound MCP activity bucket(s).";
        OverviewSessionsGrid.ItemsSource = rows;
        if (selectedKey is not null)
        {
            var selected = rows.FirstOrDefault(row => row.Key == selectedKey);
            if (selected is not null) OverviewSessionsGrid.SelectedItem = selected;
        }
    }

    private void OverviewSessionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OverviewSessionsGrid.SelectedItem is not ObservedSessionRow row)
        {
            OverviewSessionDetailText.Text = "Select an AI chat or unbound activity row.";
            return;
        }
        OverviewSessionDetailText.Text =
            $"Type: {row.Kind} | State: {row.State} | Observed ID: {row.DisplayId} | Workspaces: {row.Workspaces}\n" +
            $"First seen: {row.FirstSeenUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss} | Last seen: {row.LastSeenUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}\n" +
            $"Calls: {FormatCount(row.Calls)} | Tasks: {FormatCount(row.Tasks)} | MCP tokens est.: {FormatCount(row.Tokens)} | Payload: {FormatBytes(row.PayloadBytes)} | Errors: {FormatCount(row.Errors)}";
    }

    private static string ShortHash(string hash) => hash.Length <= 12 ? hash : hash[..12] + "...";

    private sealed record ObservedSessionRow(
        string Key,
        string Kind,
        string State,
        string DisplayId,
        string Workspaces,
        DateTimeOffset FirstSeenUtc,
        DateTimeOffset LastSeenUtc,
        long Calls,
        long Tasks,
        long Tokens,
        long PayloadBytes,
        long Errors)
    {
        public string LastSeen => LastSeenUtc.ToLocalTime().ToString("dd/MM HH:mm:ss");
    }

    private TextBlock OverviewStatusText(string key) => key switch
    {
        "C" => COverviewStatusText,
        "D" => DOverviewStatusText,
        "E" => EOverviewStatusText,
        "F" => FOverviewStatusText,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private TextBlock OverviewComponentHealthText(string key) => key switch
    {
        "C" => COverviewHealthText,
        "D" => DOverviewHealthText,
        "E" => EOverviewHealthText,
        "F" => FOverviewHealthText,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };
    private TextBlock OverviewUptimeText(string key) => key switch
    {
        "C" => COverviewUptimeText,
        "D" => DOverviewUptimeText,
        "E" => EOverviewUptimeText,
        "F" => FOverviewUptimeText,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private TextBlock OverviewCallsText(string key) => key switch
    {
        "C" => COverviewCallsText,
        "D" => DOverviewCallsText,
        "E" => EOverviewCallsText,
        "F" => FOverviewCallsText,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private TextBlock OverviewTokensText(string key) => key switch
    {
        "C" => COverviewTokensText,
        "D" => DOverviewTokensText,
        "E" => EOverviewTokensText,
        "F" => FOverviewTokensText,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private static string FormatCount(long value) => value.ToString("N0");

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1_024) return $"{bytes:N0} B";
        if (bytes < 1_048_576) return $"{bytes / 1_024d:N1} KB";
        if (bytes < 1_073_741_824) return $"{bytes / 1_048_576d:N1} MB";
        return $"{bytes / 1_073_741_824d:N2} GB";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}d {duration.Hours:D2}h {duration.Minutes:D2}m";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}h {duration.Minutes:D2}m {duration.Seconds:D2}s";
        return $"{duration.Minutes:D2}m {duration.Seconds:D2}s";
    }
    private FileMcpSettings LoadSettingsSafely()
    {
        try { return _settingsStore.Load(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "FileMCP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return new FileMcpSettings();
        }
    }

    private void ApplySettings(FileMcpSettings settings)
    {
        foreach (var key in WorkspaceKeys)
        {
            var workspace = settings.Workspaces.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (workspace is null) continue;

            EnabledBox(key).IsChecked = workspace.Enabled;
            TunnelBox(key).Text = workspace.TunnelId;
            PathBox(key).Text = workspace.AllowedDirectory;
            ProfileBoxFor(key).Text = workspace.Profile;
            PortBoxFor(key).Text = workspace.Port.ToString();
            HealthBox(key).Text = workspace.HealthAddress;
        }

        GitNameBox.Text = settings.GitUserName;
        GitEmailBox.Text = settings.GitUserEmail;
        EnableCommandsCheckBox.IsChecked = settings.EnableCommands;
        OtlpEnabledCheckBox.IsChecked = settings.OtlpEnabled;
        OtlpEndpointBox.Text = string.IsNullOrWhiteSpace(settings.OtlpEndpoint) ? OtlpTelemetrySettings.DefaultEndpoint : settings.OtlpEndpoint;
    }

    private void UpdateApiKeyStatus()
    {
        var saved = _credentialStore.HasSavedApiKey;
        ApiKeyStatusText.Text = saved ? "API key is saved in Windows Credential Manager" : "No API key is saved";
        DeleteApiKeyButton.IsEnabled = saved;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimes.Values.Any(runtime => runtime.State.Status is LocalMcpRuntimeStatus.Running or LocalMcpRuntimeStatus.Restarting or LocalMcpRuntimeStatus.Cooldown or LocalMcpRuntimeStatus.Starting or LocalMcpRuntimeStatus.Stopping))
        {
            await StopAllAsync();
            return;
        }

        if (!ValidateConnection(requireApiKey: true) || !ValidateSettings()) return;

        try
        {
            SaveTypedApiKeyIfPresent();
            SaveAllSettings();
            var apiKey = _credentialStore.ReadApiKey();

            foreach (var workspace in _settings.Workspaces.Where(item => item.Enabled))
            {
                var runtime = _runtimes[workspace.Key];
                await runtime.StartAsync(BuildConfiguration(workspace, apiKey));
            }

            RefreshRuntimeUi();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void SaveConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateConnection(requireApiKey: true)) return;

        try
        {
            SaveTypedApiKeyIfPresent();
            SaveConnectionFields();
            _settingsStore.Save(_settings);
            UpdateApiKeyStatus();
            AppendLog("Connection settings saved.\n");

            if (_runtimes.Values.Any(runtime => runtime.State.Status != LocalMcpRuntimeStatus.Stopped))
                AppendLog("Connection changes will take effect after the affected workspace reconnects.\n");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSettings()) return;

        try
        {
            SaveSettingsFields();
            _settingsStore.Save(_settings);
            AppendLog("Workspace settings saved.\n");

            if (_runtimes.Values.Any(runtime => runtime.State.Status != LocalMcpRuntimeStatus.Stopped))
                AppendLog("Workspace changes will take effect after the affected workspace reconnects.\n");

            RefreshRuntimeUi();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void DeleteApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _credentialStore.DeleteApiKey();
            ApiKeyBox.Password = "";
            UpdateApiKeyStatus();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string key || !WorkspaceKeys.Contains(key))
            return;

        var pathBox = PathBox(key);
        var initialDirectory = Directory.Exists(pathBox.Text)
            ? pathBox.Text
            : key == "C"
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : key + @":\";

        var dialog = new OpenFolderDialog
        {
            Title = $"Choose shared directory for drive {key}",
            Multiselect = false,
            InitialDirectory = initialDirectory,
        };

        if (dialog.ShowDialog(this) == true)
            pathBox.Text = dialog.FolderName;
    }

    private async void Quit_Click(object sender, RoutedEventArgs e) => await QuitAsync();

    private bool ValidateConnection(bool requireApiKey)
    {
        var enabled = WorkspaceKeys.Where(key => EnabledBox(key).IsChecked == true).ToArray();
        if (enabled.Length == 0)
        {
            MainTabs.SelectedItem = SettingsTab;
            ShowError("Enable at least one workspace in Settings.");
            return false;
        }

        foreach (var key in enabled)
        {
            var tunnelId = TunnelBox(key).Text.Trim();
            if (tunnelId.Length == 0)
            {
                MainTabs.SelectedItem = ConnectionTab;
                ShowError($"Enter a Tunnel ID for drive {key}.");
                return false;
            }

            if (!LocalMcpRuntime.IsValidTunnelId(tunnelId))
            {
                MainTabs.SelectedItem = ConnectionTab;
                ShowError($"Drive {key}: Tunnel ID must match tunnel_<32 lowercase letters or digits>.");
                return false;
            }
        }

        if (requireApiKey && ApiKeyBox.Password.Trim().Length == 0 && !_credentialStore.HasSavedApiKey)
        {
            MainTabs.SelectedItem = ConnectionTab;
            ShowError("Enter a Runtime API key in the Connection tab.");
            return false;
        }

        return true;
    }

    private bool ValidateSettings()
    {
        var enabledKeys = new List<string>();
        var usedPorts = new HashSet<int>();
        var usedProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in WorkspaceKeys)
        {
            var enabled = EnabledBox(key).IsChecked == true;
            if (enabled) enabledKeys.Add(key);

            var path = PathBox(key).Text.Trim();
            var profile = ProfileBoxFor(key).Text.Trim();
            var health = HealthBox(key).Text.Trim();

            if (!LocalMcpRuntime.IsValidProfileName(profile))
            {
                MainTabs.SelectedItem = SettingsTab;
                AdvancedExpander.IsExpanded = true;
                ShowError($"Drive {key}: profile must start with a letter or number and contain only letters, numbers, '.', '_' or '-' (maximum 128 characters).");
                return false;
            }

            if (!int.TryParse(PortBoxFor(key).Text.Trim(), out var port) || port is < 1 or > 65535)
            {
                MainTabs.SelectedItem = SettingsTab;
                AdvancedExpander.IsExpanded = true;
                ShowError($"Drive {key}: MCP port must be between 1 and 65535.");
                return false;
            }

            if (LocalMcpRuntime.NormalizeHealthAddress(health) is null)
            {
                MainTabs.SelectedItem = SettingsTab;
                AdvancedExpander.IsExpanded = true;
                ShowError($"Drive {key}: health listener must use localhost, 127.0.0.1, or [::1] with a port from 0 to 65535.");
                return false;
            }

            if (!enabled) continue;

            if (path.Length == 0 || !Directory.Exists(path))
            {
                MainTabs.SelectedItem = SettingsTab;
                ShowError($"Drive {key}: choose an existing shared directory.");
                return false;
            }

            if (!PathBelongsToDrive(path, key))
            {
                MainTabs.SelectedItem = SettingsTab;
                ShowError($"Drive {key}: the shared directory must be located on drive {key}:.");
                return false;
            }

            if (!usedPorts.Add(port))
            {
                MainTabs.SelectedItem = SettingsTab;
                AdvancedExpander.IsExpanded = true;
                ShowError($"Drive {key}: MCP port {port} is already used by another enabled workspace.");
                return false;
            }

            if (!usedProfiles.Add(profile))
            {
                MainTabs.SelectedItem = SettingsTab;
                AdvancedExpander.IsExpanded = true;
                ShowError($"Drive {key}: profile '{profile}' is already used by another enabled workspace.");
                return false;
            }
        }

        if (OtlpEnabledCheckBox.IsChecked == true)
        {
            try { _ = OtlpTelemetrySettings.NormalizeBaseEndpoint(OtlpEndpointBox.Text); }
            catch (Exception ex) when (ex is FileMcpException or UriFormatException)
            {
                MainTabs.SelectedItem = SettingsTab;
                AdvancedExpander.IsExpanded = true;
                ShowError(ex.Message);
                return false;
            }
        }

        if (enabledKeys.Count == 0)
        {
            MainTabs.SelectedItem = SettingsTab;
            ShowError("Enable at least one workspace.");
            return false;
        }

        return true;
    }

    private static bool PathBelongsToDrive(string path, string key)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrWhiteSpace(root) &&
                   root.Length >= 2 &&
                   char.ToUpperInvariant(root[0]) == key[0] &&
                   root[1] == ':';
        }
        catch
        {
            return false;
        }
    }

    private void SaveTypedApiKeyIfPresent()
    {
        var typedKey = ApiKeyBox.Password.Trim();
        if (typedKey.Length == 0) return;

        _credentialStore.SaveApiKey(typedKey);
        ApiKeyBox.Password = "";
        UpdateApiKeyStatus();
    }

    private void SaveAllSettings()
    {
        SaveConnectionFields();
        SaveSettingsFields();
        _settingsStore.Save(_settings);
    }

    private void SaveConnectionFields()
    {
        foreach (var key in WorkspaceKeys)
            Workspace(key).TunnelId = TunnelBox(key).Text.Trim();
    }

    private void SaveSettingsFields()
    {
        foreach (var key in WorkspaceKeys)
        {
            var workspace = Workspace(key);
            workspace.Enabled = EnabledBox(key).IsChecked == true;
            workspace.AllowedDirectory = PathBox(key).Text.Trim();
            workspace.Profile = ProfileBoxFor(key).Text.Trim();
            workspace.Port = int.Parse(PortBoxFor(key).Text.Trim());
            workspace.HealthAddress = HealthBox(key).Text.Trim();
        }

        _settings.GitUserName = GitNameBox.Text.Trim();
        _settings.GitUserEmail = GitEmailBox.Text.Trim();
        _settings.EnableCommands = EnableCommandsCheckBox.IsChecked == true;
        _settings.OtlpEnabled = OtlpEnabledCheckBox.IsChecked == true;
        var otlpEndpoint = OtlpEndpointBox.Text.Trim();
        _settings.OtlpEndpoint = otlpEndpoint.Length == 0 ? OtlpTelemetrySettings.DefaultEndpoint : otlpEndpoint;
        ConfigureOtlpFromSettings();
    }

    private void ConfigureOtlpFromSettings()
    {
        var endpoint = string.IsNullOrWhiteSpace(_settings.OtlpEndpoint)
            ? OtlpTelemetrySettings.DefaultEndpoint
            : _settings.OtlpEndpoint.Trim();
        _ = _observability.ConfigureOtlp(new OtlpTelemetrySettings(_settings.OtlpEnabled, endpoint));
    }

    private LocalMcpConfiguration BuildConfiguration(FileMcpWorkspaceSettings workspace, string apiKey) =>
        new(
            workspace.TunnelId,
            apiKey,
            workspace.Profile,
            checked((ushort)workspace.Port),
            workspace.AllowedDirectory,
            workspace.HealthAddress,
            _settings.GitUserName,
            _settings.GitUserEmail,
            _settings.EnableCommands);

    private async Task StopAllAsync()
    {
        var tasks = _runtimes.Values
            .Where(runtime => runtime.State.Status != LocalMcpRuntimeStatus.Stopped)
            .Select(runtime => runtime.StopAsync())
            .ToArray();

        if (tasks.Length > 0)
            await Task.WhenAll(tasks);

        RefreshRuntimeUi();
    }

    private void UpdateRuntimeState(string key, LocalMcpRuntimeState state)
    {
        UpdateWorkspaceStatus(key, state);
        UpdateConnectButton();

        if (state.Status == LocalMcpRuntimeStatus.Failed && !string.IsNullOrEmpty(state.Error))
            ShowError($"Drive {key}: {state.Error}");
    }

    private void RefreshRuntimeUi()
    {
        foreach (var key in WorkspaceKeys)
            UpdateWorkspaceStatus(key, _runtimes.TryGetValue(key, out var runtime) ? runtime.State : LocalMcpRuntimeState.Stopped);

        UpdateConnectButton();
    }

    private void UpdateWorkspaceStatus(string key, LocalMcpRuntimeState state)
    {
        var status = StatusText(key);
        var enabled = EnabledBox(key).IsChecked == true;

        if (!enabled && state.Status == LocalMcpRuntimeStatus.Stopped)
        {
            status.Text = "Disabled";
            status.Foreground = System.Windows.Media.Brushes.Gray;
            return;
        }

        switch (state.Status)
        {
            case LocalMcpRuntimeStatus.Stopped:
                status.Text = "Stopped";
                status.Foreground = System.Windows.Media.Brushes.Gray;
                break;
            case LocalMcpRuntimeStatus.Starting:
                status.Text = "Connecting...";
                status.Foreground = System.Windows.Media.Brushes.DarkOrange;
                break;
            case LocalMcpRuntimeStatus.Running:
                status.Text = "Connected";
                status.Foreground = System.Windows.Media.Brushes.ForestGreen;
                break;
            case LocalMcpRuntimeStatus.Restarting:
                status.Text = "Reconnecting...";
                status.Foreground = System.Windows.Media.Brushes.DarkOrange;
                break;
            case LocalMcpRuntimeStatus.Cooldown:
                status.Text = "Reconnect cooldown";
                status.Foreground = System.Windows.Media.Brushes.DarkOrange;
                break;
            case LocalMcpRuntimeStatus.Stopping:
                status.Text = "Disconnecting...";
                status.Foreground = System.Windows.Media.Brushes.DarkOrange;
                break;
            case LocalMcpRuntimeStatus.Failed:
                status.Text = "Failed";
                status.Foreground = System.Windows.Media.Brushes.Firebrick;
                break;
        }
    }

    private void UpdateConnectButton()
    {
        var states = _runtimes.Values.Select(runtime => runtime.State.Status).ToArray();

        if (states.Any(status => status is LocalMcpRuntimeStatus.Starting or LocalMcpRuntimeStatus.Stopping))
        {
            ConnectButton.Content = states.Any(status => status == LocalMcpRuntimeStatus.Starting) ? "Connecting..." : "Disconnecting...";
            ConnectButton.IsEnabled = false;
            return;
        }

        if (states.Any(status => status is LocalMcpRuntimeStatus.Running or LocalMcpRuntimeStatus.Restarting or LocalMcpRuntimeStatus.Cooldown))
        {
            ConnectButton.Content = "Disconnect all";
            ConnectButton.IsEnabled = true;
            return;
        }

        ConnectButton.Content = "Connect all";
        ConnectButton.IsEnabled = true;
    }

    private FileMcpWorkspaceSettings Workspace(string key) =>
        _settings.Workspaces.First(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private System.Windows.Controls.CheckBox EnabledBox(string key) => key switch
    {
        "C" => CEnabledCheckBox,
        "D" => DEnabledCheckBox,
        "E" => EEnabledCheckBox,
        "F" => FEnabledCheckBox,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private System.Windows.Controls.TextBox TunnelBox(string key) => key switch
    {
        "C" => CTunnelIdBox,
        "D" => DTunnelIdBox,
        "E" => ETunnelIdBox,
        "F" => FTunnelIdBox,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private System.Windows.Controls.TextBox PathBox(string key) => key switch
    {
        "C" => CPathBox,
        "D" => DPathBox,
        "E" => EPathBox,
        "F" => FPathBox,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private System.Windows.Controls.TextBox ProfileBoxFor(string key) => key switch
    {
        "C" => CProfileBox,
        "D" => DProfileBox,
        "E" => EProfileBox,
        "F" => FProfileBox,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private System.Windows.Controls.TextBox PortBoxFor(string key) => key switch
    {
        "C" => CPortBox,
        "D" => DPortBox,
        "E" => EPortBox,
        "F" => FPortBox,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private System.Windows.Controls.TextBox HealthBox(string key) => key switch
    {
        "C" => CHealthBox,
        "D" => DHealthBox,
        "E" => EHealthBox,
        "F" => FHealthBox,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private System.Windows.Controls.TextBlock StatusText(string key) => key switch
    {
        "C" => CStatusText,
        "D" => DStatusText,
        "E" => EStatusText,
        "F" => FStatusText,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private void AppendLog(string text)
    {
        _logBuffer += text;
        if (_logBuffer.Length > MaxLogCharacters)
            _logBuffer = "[...older log truncated...]\n" + _logBuffer[^MaxLogCharacters..];

        LogBox.Text = _logBuffer;
        LogBox.ScrollToEnd();
    }

    private void ShowError(string message) =>
        System.Windows.MessageBox.Show(this, message, "FileMCP", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void ShowAbout()
    {
        System.Windows.MessageBox.Show(
            this,
            "FileMCP 0.4.0\n\nNative Windows MCP bridge with multi-workspace C/D/E/F support for controlled local file, Git, and optional command access.",
            "About FileMCP",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true;
        Hide();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    internal void ActivateFromSecondaryLaunch()
    {
        ShowFromTray();

        var markerPath = Environment.GetEnvironmentVariable("FILEMCP_SINGLE_INSTANCE_SMOKE_MARKER");
        if (string.IsNullOrWhiteSpace(markerPath)) return;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(markerPath));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(markerPath, $"PASS pid={Environment.ProcessId}");
        }
        catch (Exception ex)
        {
            AppendLog($"[Desktop] Could not write single-instance smoke marker: {ex.Message}\n");
        }
    }

    public void ShutdownForSystemSession()
    {
        if (_quitting) return;
        _quitting = true;

        foreach (var runtime in _runtimes.Values)
        {
            try { runtime.ShutdownAsync().GetAwaiter().GetResult(); } catch { }
        }

        try { _observability.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }

        _overviewTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;

        try
        {
            await Task.WhenAll(_runtimes.Values.Select(runtime => runtime.ShutdownAsync()));
        }
        finally
        {
            _overviewTimer.Stop();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();

            foreach (var runtime in _runtimes.Values)
                await runtime.DisposeAsync();

            await _observability.DisposeAsync();

            System.Windows.Application.Current.Shutdown();
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        if (e.Key == Key.W)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.OemComma)
        {
            MainTabs.SelectedItem = SettingsTab;
            ShowFromTray();
            e.Handled = true;
        }
        else if (e.Key == Key.Q)
        {
            _ = QuitAsync();
            e.Handled = true;
        }
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        var icon = Environment.ProcessPath is { Length: > 0 } path
            ? System.Drawing.Icon.ExtractAssociatedIcon(path)
            : null;

        return icon ?? SystemIcons.Application;
    }
}
