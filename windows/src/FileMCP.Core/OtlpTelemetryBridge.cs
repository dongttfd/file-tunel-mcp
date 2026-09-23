using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FileMCP.Core;

public sealed record OtlpTelemetrySettings(bool Enabled, string Endpoint)
{
    public const string DefaultEndpoint = "http://127.0.0.1:4318";
    public static OtlpTelemetrySettings Disabled { get; } = new(false, DefaultEndpoint);
    public static Uri NormalizeBaseEndpoint(string endpoint) => OtlpTelemetryBridge.NormalizeBaseEndpoint(endpoint);
}

public enum OtlpExporterRuntimeStatus
{
    Disabled,
    Configured,
    ConfigurationError,
}

public sealed record OtlpExporterSnapshot(
    OtlpExporterRuntimeStatus Status,
    string Endpoint,
    DateTimeOffset? ConfiguredAtUtc,
    string? Error);

internal sealed class OtlpTelemetryBridge : IDisposable
{
    private const int ExportTimeoutMilliseconds = 1_000;
    private readonly object _gate = new();
    private readonly Action<string>? _log;
    private TracerProvider? _tracerProvider;
    private MeterProvider? _meterProvider;
    private OtlpExporterSnapshot _snapshot = new(OtlpExporterRuntimeStatus.Disabled, OtlpTelemetrySettings.DefaultEndpoint, null, null);
    private bool _disposed;

    public OtlpTelemetryBridge(Action<string>? log = null) => _log = log;

    public OtlpExporterSnapshot Snapshot()
    {
        lock (_gate) return _snapshot;
    }

    public OtlpExporterSnapshot Configure(OtlpTelemetrySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DisposeProvidersUnsafe();

            var endpointText = string.IsNullOrWhiteSpace(settings.Endpoint)
                ? OtlpTelemetrySettings.DefaultEndpoint
                : settings.Endpoint.Trim();
            if (!settings.Enabled)
            {
                _snapshot = new OtlpExporterSnapshot(OtlpExporterRuntimeStatus.Disabled, endpointText, null, null);
                return _snapshot;
            }

            try
            {
                var baseEndpoint = NormalizeBaseEndpoint(endpointText);
                var tracesEndpoint = new Uri(baseEndpoint, "v1/traces");
                var metricsEndpoint = new Uri(baseEndpoint, "v1/metrics");
                var version = typeof(OtlpTelemetryBridge).Assembly.GetName().Version?.ToString() ?? "unknown";

                _tracerProvider = Sdk.CreateTracerProviderBuilder()
                    .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("FileMCP", serviceVersion: version))
                    .AddSource(McpStandardTelemetry.ActivitySourceName)
                    .AddOtlpExporter(options => ConfigureExporter(options, tracesEndpoint))
                    .Build();

                _meterProvider = Sdk.CreateMeterProviderBuilder()
                    .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("FileMCP", serviceVersion: version))
                    .AddMeter(McpStandardTelemetry.MeterName)
                    .AddOtlpExporter((options, readerOptions) =>
                    {
                        ConfigureExporter(options, metricsEndpoint);
                        readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5_000;
                        readerOptions.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds = ExportTimeoutMilliseconds;
                    })
                    .Build();

                _snapshot = new OtlpExporterSnapshot(
                    OtlpExporterRuntimeStatus.Configured,
                    baseEndpoint.GetLeftPart(UriPartial.Authority),
                    DateTimeOffset.UtcNow,
                    null);
                _log?.Invoke("[Telemetry] Optional OTLP export configured (HTTP/Protobuf).\n");
            }
            catch (Exception ex)
            {
                DisposeProvidersUnsafe();
                _snapshot = new OtlpExporterSnapshot(
                    OtlpExporterRuntimeStatus.ConfigurationError,
                    endpointText,
                    null,
                    ex.Message);
                _log?.Invoke($"[Telemetry] Optional OTLP export disabled after configuration error: {ex.Message}\n");
            }
            return _snapshot;
        }
    }

    public bool ForceFlush(int timeoutMilliseconds = ExportTimeoutMilliseconds)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_snapshot.Status == OtlpExporterRuntimeStatus.Disabled) return true;
            if (_snapshot.Status != OtlpExporterRuntimeStatus.Configured) return false;
            try
            {
                var traces = _tracerProvider?.ForceFlush(timeoutMilliseconds) ?? true;
                var metrics = _meterProvider?.ForceFlush(timeoutMilliseconds) ?? true;
                return traces && metrics;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Telemetry] OTLP flush failure ignored: {ex.Message}\n");
                return false;
            }
        }
    }

    public static Uri NormalizeBaseEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FileMcpException("OTLP endpoint must be an absolute http:// or https:// URI.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new FileMcpException("OTLP endpoint must not contain embedded credentials.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new FileMcpException("OTLP endpoint must not contain a query string or fragment.");
        if (uri.AbsolutePath is not ("" or "/"))
            throw new FileMcpException("OTLP endpoint must be a base collector URI without a path; FileMCP adds /v1/traces and /v1/metrics.");
        return new Uri(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static void ConfigureExporter(OtlpExporterOptions options, Uri endpoint)
    {
        options.Endpoint = endpoint;
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.TimeoutMilliseconds = ExportTimeoutMilliseconds;
        options.Headers = string.Empty;
    }

    private void DisposeProvidersUnsafe()
    {
        try { _tracerProvider?.Dispose(); } catch { }
        try { _meterProvider?.Dispose(); } catch { }
        _tracerProvider = null;
        _meterProvider = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeProvidersUnsafe();
        }
    }
}
