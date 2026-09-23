using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

namespace FileMCP.Core;

public static class McpTelemetryAttributeAdapter
{
    public const string SemanticProfileId = "filemcp.mcp.compat/2026-07-28/v1";
    public const int MaxAttributeUtf8Bytes = 128;

    private static readonly HashSet<string> KnownMethods = new(StringComparer.Ordinal)
    {
        "initialize",
        "ping",
        "tools/list",
        "tools/call",
        "server/discover",
    };

    public static string NormalizeProtocolVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "legacy";
        var trimmed = BoundUtf8(value.Trim(), MaxAttributeUtf8Bytes);
        if (trimmed == FileMcpConstants.ModernProtocolVersion) return trimmed;
        if (FileMcpConstants.LegacySupportedVersions.Contains(trimmed)) return trimmed;
        return "other";
    }

    public static string NormalizeMethod(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "other";
        var trimmed = BoundUtf8(value.Trim(), MaxAttributeUtf8Bytes);
        return KnownMethods.Contains(trimmed) ? trimmed : "other";
    }

    public static string NormalizeToolName(string? value)
    {
        var classification = ToolUsageClassifier.Classify(value);
        if (!classification.IsKnownTool || string.IsNullOrWhiteSpace(value)) return "unknown";
        return BoundUtf8(value.Trim(), MaxAttributeUtf8Bytes);
    }

    public static string NormalizeWorkspace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var key = value.Trim().ToUpperInvariant();
        return key is "C" or "D" or "E" or "F" ? key : "other";
    }

    internal static KeyValuePair<string, object?>[] BuildTags(
        string? protocolVersion,
        string? method,
        string? toolName,
        string? workspaceKey)
    {
        var normalizedMethod = NormalizeMethod(method);
        var tags = new List<KeyValuePair<string, object?>>(9)
        {
            new("rpc.system.name", "jsonrpc"),
            new("jsonrpc.protocol.version", "2.0"),
            new("mcp.protocol.version", NormalizeProtocolVersion(protocolVersion)),
            new("mcp.method.name", normalizedMethod),
            new("filemcp.workspace", NormalizeWorkspace(workspaceKey)),
            new("filemcp.mcp.semantic_profile", SemanticProfileId),
        };
        if (normalizedMethod == "tools/call")
        {
            var normalizedTool = NormalizeToolName(toolName);
            tags.Add(new("gen_ai.operation.name", "execute_tool"));
            tags.Add(new("gen_ai.tool.name", normalizedTool));
            tags.Add(new("mcp.tool.name", normalizedTool));
        }
        return tags.ToArray();
    }

    internal static string BoundUtf8(string value, int maxBytes)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
        var builder = new StringBuilder(value.Length);
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes) break;
            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return builder.ToString();
    }
}

public sealed class McpStandardTelemetry : IDisposable
{
    public const string ActivitySourceName = "FileMCP.MCP";
    public const string MeterName = "FileMCP.MCP";
    public const string InstrumentationVersion = "1.0.0";

    private readonly ActivitySource _activitySource = new(ActivitySourceName, InstrumentationVersion);
    private readonly Meter _meter = new(MeterName, InstrumentationVersion);
    private readonly Counter<long> _requests;
    private readonly Counter<long> _toolCalls;
    private readonly Counter<long> _errors;
    private readonly Histogram<double> _durationMs;
    private readonly Histogram<long> _requestBytes;
    private readonly Histogram<long> _responseBytes;
    private int _disposed;

    public McpStandardTelemetry()
    {
        _requests = _meter.CreateCounter<long>("mcp.server.requests", unit: "{request}", description: "Accepted MCP JSON-RPC requests.");
        _toolCalls = _meter.CreateCounter<long>("mcp.server.tool.calls", unit: "{call}", description: "Accepted MCP tool calls.");
        _errors = _meter.CreateCounter<long>("mcp.server.errors", unit: "{error}", description: "MCP operations completed with an error response.");
        _durationMs = _meter.CreateHistogram<double>("mcp.server.operation.duration", unit: "ms", description: "MCP operation duration in milliseconds.");
        _requestBytes = _meter.CreateHistogram<long>("mcp.server.request.size", unit: "By", description: "MCP JSON request payload bytes.");
        _responseBytes = _meter.CreateHistogram<long>("mcp.server.response.size", unit: "By", description: "MCP JSON response payload bytes.");
    }

    public McpStandardTelemetryOperation BeginOperation(
        string? protocolVersion,
        string? method,
        string? toolName,
        string? workspaceKey,
        long requestBytes,
        McpExtractedTraceContext? traceContext = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var tags = McpTelemetryAttributeAdapter.BuildTags(protocolVersion, method, toolName, workspaceKey);
        var normalizedMethod = McpTelemetryAttributeAdapter.NormalizeMethod(method);
        var normalizedTool = normalizedMethod == "tools/call" ? McpTelemetryAttributeAdapter.NormalizeToolName(toolName) : null;
        var activityName = normalizedTool is null ? $"mcp {normalizedMethod}" : $"mcp {normalizedMethod} {normalizedTool}";
        var activity = traceContext.HasValue
            ? _activitySource.StartActivity(activityName, ActivityKind.Server, traceContext.Value.Parent, tags)
            : _activitySource.StartActivity(activityName, ActivityKind.Server, default(ActivityContext), tags);
        if (activity is not null && traceContext.HasValue)
            activity.SetTag("filemcp.trace.parent_source", traceContext.Value.Source == McpTraceContextSource.McpMeta ? "mcp_meta" : "http");
        return new McpStandardTelemetryOperation(this, activity, tags, normalizedMethod, Math.Max(0, requestBytes));
    }

    internal void Complete(
        Activity? activity,
        KeyValuePair<string, object?>[] tags,
        string normalizedMethod,
        long requestBytes,
        long responseBytes,
        long latencyTicks,
        bool isError)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var safeResponseBytes = Math.Max(0, responseBytes);
        var safeLatencyTicks = Math.Max(0, latencyTicks);
        _requests.Add(1, tags);
        if (normalizedMethod == "tools/call") _toolCalls.Add(1, tags);
        if (isError) _errors.Add(1, tags);
        _requestBytes.Record(requestBytes, tags);
        _responseBytes.Record(safeResponseBytes, tags);
        _durationMs.Record((double)safeLatencyTicks / Stopwatch.Frequency * 1_000d, tags);
        if (activity is not null)
        {
            activity.SetTag("filemcp.mcp.request_bytes", requestBytes);
            activity.SetTag("filemcp.mcp.response_bytes", safeResponseBytes);
            activity.SetStatus(isError ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
            if (isError) activity.SetTag("error.type", "mcp.error");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _activitySource.Dispose();
        _meter.Dispose();
    }
}

public sealed class McpStandardTelemetryOperation : IDisposable
{
    private readonly McpStandardTelemetry _owner;
    private readonly Activity? _activity;
    private readonly KeyValuePair<string, object?>[] _tags;
    private readonly string _normalizedMethod;
    private readonly long _requestBytes;
    private readonly long _startedTicks = Stopwatch.GetTimestamp();
    private int _completed;

    internal McpStandardTelemetryOperation(
        McpStandardTelemetry owner,
        Activity? activity,
        KeyValuePair<string, object?>[] tags,
        string normalizedMethod,
        long requestBytes)
    {
        _owner = owner;
        _activity = activity;
        _tags = tags;
        _normalizedMethod = normalizedMethod;
        _requestBytes = requestBytes;
    }

    public void Complete(long responseBytes, bool isError)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        var latency = Math.Max(0, Stopwatch.GetTimestamp() - _startedTicks);
        try { _owner.Complete(_activity, _tags, _normalizedMethod, _requestBytes, responseBytes, latency, isError); }
        finally { _activity?.Dispose(); }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _completed) == 0) Complete(0, isError: true);
    }
}
