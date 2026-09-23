using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using FileMCP.Core;
using Microsoft.Data.Sqlite;

namespace FileMCP.Core.Tests;

internal static class Program
{
    private static int _assertions;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "init" or "doctor" or "run")
            return await RunFakeTunnelClientAsync(args);

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("windows-tests: skipped (Windows required)");
            return 0;
        }

        var root = Path.Combine(Path.GetTempPath(), "filemcp-windows-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestObservabilityContracts();
            await TestMcpStandardTelemetryAsync(root);
            await TestMcpTraceContextAsync(root);
            await TestOtlpExporterAsync(root);
            TestLogicalChatCorrelation();
            await TestLogicalSessionRegistryAsync(root);
            await TestWorkspaceUsageMeterAsync();
            await TestTelemetryPersistenceAsync(root);
            await TestUsagePeriodsAndRetentionAsync(root);
            await TestObservabilityHubAsync(root);
            await TestObservabilityHardeningAsync(root);
            await TestV11HardeningAsync(root);
            await TestSettingsAndCredentialsAsync(root);
            await TestDesktopSingleInstanceCoordinatorAsync();
            await TestProcessRunnerAsync(root);
            TestTunnelRestartPolicy();
            await TestFilesystemAndToolsAsync(root);
            await TestGitSafetyAsync(root);
            await TestHttpAndMcpAsync(root);
            await TestHttpConnectionBoundsAsync(root);
            await TestRuntimeAsync(root);
            Console.WriteLine($"windows-core-tests: ok ({_assertions} assertions)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            var annotation = ex.ToString()
                .Replace("%", "%25", StringComparison.Ordinal)
                .Replace("\r", "%0D", StringComparison.Ordinal)
                .Replace("\n", "%0A", StringComparison.Ordinal);
            Console.Error.WriteLine($"::error file=windows/tests/FileMCP.Core.Tests/Program.cs,title=Windows integration failure::{annotation}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void TestObservabilityContracts()
    {
        Assert(McpTokenEstimator.EstimatorId == "bytes_div_4_v1", "token estimator id");
        Assert(McpTokenEstimator.EstimateFromUtf8Bytes(0) == 0, "token estimate zero");
        Assert(McpTokenEstimator.EstimateFromUtf8Bytes(1) == 1, "token estimate one byte");
        Assert(McpTokenEstimator.EstimateFromUtf8Bytes(4) == 1, "token estimate four bytes");
        Assert(McpTokenEstimator.EstimateFromUtf8Bytes(5) == 2, "token estimate rounds up");
        Assert(McpTokenEstimator.EstimateFromUtf8Bytes(long.MaxValue) > 0, "token estimate avoids overflow");
        try
        {
            _ = McpTokenEstimator.EstimateFromUtf8Bytes(-1);
            throw new Exception("Assertion failed: negative token bytes rejected");
        }
        catch (ArgumentOutOfRangeException)
        {
            Assert(true, "negative token bytes rejected");
        }

        var cases = new (string Name, ToolUsageCategory Category, bool Task)[]
        {
            ("list_files", ToolUsageCategory.Read, false),
            ("read_file", ToolUsageCategory.Read, false),
            ("read_file_range", ToolUsageCategory.Read, false),
            ("search_content", ToolUsageCategory.Read, false),
            ("search_filenames", ToolUsageCategory.Read, false),
            ("write_file", ToolUsageCategory.Write, true),
            ("delete_file", ToolUsageCategory.Write, true),
            ("delete_directory", ToolUsageCategory.Write, true),
            ("run_command", ToolUsageCategory.Command, true),
            ("git_init", ToolUsageCategory.Git, true),
            ("git_status", ToolUsageCategory.Git, false),
            ("git_log", ToolUsageCategory.Git, false),
            ("git_diff", ToolUsageCategory.Git, false),
            ("git_add", ToolUsageCategory.Git, true),
            ("git_commit", ToolUsageCategory.Git, true),
            ("git_push", ToolUsageCategory.Git, true),
            ("list_codex_skills", ToolUsageCategory.Skill, false),
            ("load_codex_skill", ToolUsageCategory.Skill, false),
            ("filemcp_observability_connect", ToolUsageCategory.Other, false),
        };
        foreach (var item in cases)
        {
            var classification = ToolUsageClassifier.Classify(item.Name);
            Assert(classification.IsKnownTool, $"classifier knows {item.Name}");
            Assert(classification.Category == item.Category, $"classifier category {item.Name}");
            Assert(classification.IsExecutionTask == item.Task, $"classifier task flag {item.Name}");
        }
        var unknown = ToolUsageClassifier.Classify("future_tool");
        Assert(!unknown.IsKnownTool && unknown.Category == ToolUsageCategory.Other && !unknown.IsExecutionTask, "classifier unknown tool");
        var nullTool = ToolUsageClassifier.Classify(null);
        Assert(!nullTool.IsKnownTool && nullTool.Category == ToolUsageCategory.Other, "classifier null tool");

        var counters = new UsageCounters(1, 2, 3, 10, 20, 3, 5, 0, 1, 1, 0, 0, 0, 0, 7, 4);
        Assert(counters.TotalPayloadBytes == 30, "usage counters total bytes");
        Assert(counters.TotalTokensEst == 8, "usage counters total token estimate");
        Console.WriteLine("windows-observability-contracts: ok");
    }
    private sealed record CapturedMetric(string Name, double Value, IReadOnlyDictionary<string, object?> Tags);

    private static async Task TestMcpStandardTelemetryAsync(string root)
    {
        Assert(McpTelemetryAttributeAdapter.SemanticProfileId == "filemcp.mcp.compat/2026-07-28/v1", "standard telemetry semantic profile is versioned for MCP 2026 compatibility");
        Assert(McpTelemetryAttributeAdapter.NormalizeProtocolVersion(FileMcpConstants.ModernProtocolVersion) == FileMcpConstants.ModernProtocolVersion, "standard telemetry preserves supported modern protocol version");
        Assert(McpTelemetryAttributeAdapter.NormalizeProtocolVersion("attacker-version") == "other", "standard telemetry bounds protocol-version cardinality");
        Assert(McpTelemetryAttributeAdapter.NormalizeMethod("tools/call") == "tools/call" && McpTelemetryAttributeAdapter.NormalizeMethod("private-method") == "other", "standard telemetry method dimensions are allowlisted");
        Assert(McpTelemetryAttributeAdapter.NormalizeToolName("read_file") == "read_file" && McpTelemetryAttributeAdapter.NormalizeToolName("private-tool-name") == "unknown", "standard telemetry tool dimensions are allowlisted");
        Assert(McpTelemetryAttributeAdapter.NormalizeWorkspace("d") == "D" && McpTelemetryAttributeAdapter.NormalizeWorkspace("private-workspace") == "other", "standard telemetry workspace dimensions are bounded");
        var boundedUtf8 = McpTelemetryAttributeAdapter.BoundUtf8(string.Concat(Enumerable.Repeat("ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Â ÃƒÂ¢Ã¢â€šÂ¬Ã¢â€žÂ¢ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¡ÃƒÆ’Ã†â€™Ãƒâ€ Ã¢â‚¬â„¢ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬Ãƒâ€¦Ã‚Â¡ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â¬", 100)), McpTelemetryAttributeAdapter.MaxAttributeUtf8Bytes);
        Assert(Encoding.UTF8.GetByteCount(boundedUtf8) <= McpTelemetryAttributeAdapter.MaxAttributeUtf8Bytes, "standard telemetry UTF-8 attribute bound never splits beyond byte cap");

        const string privateMarker = "PRIVATE_OTEL_MARKER_8A2DF991";
        var hostileTags = McpTelemetryAttributeAdapter.BuildTags(privateMarker, "tools/call", privateMarker, privateMarker);
        var hostileTagText = string.Join('|', hostileTags.Select(tag => $"{tag.Key}={tag.Value}"));
        Assert(!hostileTagText.Contains(privateMarker, StringComparison.Ordinal), "standard telemetry strips arbitrary client-controlled values from exported dimensions");
        Assert(hostileTags.Any(tag => tag.Key == "mcp.protocol.version" && Equals(tag.Value, "other")) && hostileTags.Any(tag => tag.Key == "mcp.tool.name" && Equals(tag.Value, "unknown")), "standard telemetry maps unbounded dimensions to fixed buckets");

        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == McpStandardTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity),
        };
        ActivitySource.AddActivityListener(activityListener);

        var measurements = new List<CapturedMetric>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == McpStandardTelemetry.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            lock (measurements)
                measurements.Add(new CapturedMetric(instrument.Name, measurement, tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value)));
        });
        meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            lock (measurements)
                measurements.Add(new CapturedMetric(instrument.Name, measurement, tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value)));
        });
        meterListener.Start();

        using var telemetry = new McpStandardTelemetry();
        using (var operation = telemetry.BeginOperation(FileMcpConstants.ModernProtocolVersion, "tools/call", "read_file", "D", 11))
            operation.Complete(13, isError: true);

        var directActivity = activities.Single(activity => activity.DisplayName == "mcp tools/call read_file");
        var directActivityTags = directActivity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value);
        Assert(directActivity.Kind == ActivityKind.Server && directActivity.Status == ActivityStatusCode.Error, "standard telemetry emits server Activity span with error status");
        Assert(Equals(directActivityTags["rpc.system.name"], "jsonrpc") && Equals(directActivityTags["jsonrpc.protocol.version"], "2.0") && Equals(directActivityTags["mcp.tool.name"], "read_file") && Equals(directActivityTags["gen_ai.operation.name"], "execute_tool") && Equals(directActivityTags["gen_ai.tool.name"], "read_file") && Equals(directActivityTags["filemcp.workspace"], "D"), "standard telemetry Activity uses bounded MCP/JSON-RPC/gen_ai semantic tags");
        Assert(!directActivityTags.Keys.Any(key => key.Contains("argument", StringComparison.OrdinalIgnoreCase) || key.Contains("content", StringComparison.OrdinalIgnoreCase) || key.Contains("chat", StringComparison.OrdinalIgnoreCase) || key.Contains("path", StringComparison.OrdinalIgnoreCase)), "standard telemetry Activity schema contains no argument/content/chat/path fields");

        var workspace = Path.Combine(root, "standard-telemetry-http");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "hello.txt"), "hello");
        var port = FreePort();
        var token = new string('t', 64);
        await using var server = new LocalMcpServer((ushort)port, workspace, "", "", false, token, _ => { }, workspaceKey: "D", standardTelemetry: telemetry);
        await server.StartAsync();

        var beforeHttpMeasurements = measurements.Count;
        _ = await SendHttpAsync(port, "POST", "/mcp", new Dictionary<string, string> { ["Content-Type"] = "application/json" }, "");
        _ = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), "{");
        Assert(measurements.Count == beforeHttpMeasurements, "standard telemetry ignores unauthenticated and malformed traffic");

        const string listBody = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}";
        const string readBody = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"read_file\",\"arguments\":{\"relative_path\":\"hello.txt\"}}}";
        const string unknownBody = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"private-tool-name\",\"arguments\":{\"secret\":\"PRIVATE_OTEL_MARKER_8A2DF991\"}}}";
        _ = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), listBody);
        _ = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), readBody);
        _ = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), unknownBody);

        CapturedMetric[] metricSnapshot;
        lock (measurements) metricSnapshot = measurements.ToArray();
        Assert(metricSnapshot.Where(metric => metric.Name == "mcp.server.requests").Sum(metric => metric.Value) == 4, "standard telemetry request counter includes direct operation plus three accepted MCP requests");
        Assert(metricSnapshot.Where(metric => metric.Name == "mcp.server.tool.calls").Sum(metric => metric.Value) == 3, "standard telemetry tool-call counter tracks direct/read/error calls");
        Assert(metricSnapshot.Where(metric => metric.Name == "mcp.server.errors").Sum(metric => metric.Value) == 2, "standard telemetry error counter tracks direct error and MCP tool error");
        Assert(metricSnapshot.Any(metric => metric.Name == "mcp.server.operation.duration") && metricSnapshot.Any(metric => metric.Name == "mcp.server.request.size") && metricSnapshot.Any(metric => metric.Name == "mcp.server.response.size"), "standard telemetry emits duration and payload-size histograms");
        var exportedText = string.Join('\n', metricSnapshot.SelectMany(metric => metric.Tags).Select(tag => $"{tag.Key}={tag.Value}"));
        Assert(!exportedText.Contains(privateMarker, StringComparison.Ordinal), "standard telemetry metric tags never export private tool arguments or marker values");
        Assert(metricSnapshot.Where(metric => metric.Name == "mcp.server.tool.calls").Any(metric => metric.Tags.TryGetValue("mcp.tool.name", out var value) && Equals(value, "unknown")), "standard telemetry unknown tool metric uses bounded unknown bucket");
        Console.WriteLine("windows-standard-mcp-telemetry: ok");
    }
    private static async Task TestMcpTraceContextAsync(string root)
    {
        const string metaTraceId = "11111111111111111111111111111111";
        const string metaSpanId = "2222222222222222";
        const string httpTraceId = "33333333333333333333333333333333";
        const string httpSpanId = "4444444444444444";
        const string metaTraceParent = "00-11111111111111111111111111111111-2222222222222222-01";
        const string httpTraceParent = "00-33333333333333333333333333333333-4444444444444444-01";
        const string baggageMarker = "PRIVATE_BAGGAGE_MUST_NOT_PROPAGATE_67A2";

        var meta = new JsonObject
        {
            ["traceparent"] = metaTraceParent,
            ["tracestate"] = "vendor=meta",
            ["baggage"] = $"private={baggageMarker}",
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["traceparent"] = httpTraceParent,
            ["tracestate"] = "vendor=http",
            ["baggage"] = $"private={baggageMarker}",
        };
        Assert(McpTraceContextAdapter.TryExtract(meta, headers, out var metaExtracted), "trace context extracts valid MCP _meta parent");
        Assert(metaExtracted.Source == McpTraceContextSource.McpMeta && metaExtracted.Parent.IsRemote, "MCP _meta trace context wins and is marked remote");
        Assert(metaExtracted.Parent.TraceId.ToString() == metaTraceId && metaExtracted.Parent.SpanId.ToString() == metaSpanId && metaExtracted.TraceState == "vendor=meta", "MCP _meta traceparent/tracestate parsed exactly");

        var invalidMeta = new JsonObject { ["traceparent"] = "not-a-traceparent", ["baggage"] = baggageMarker };
        Assert(McpTraceContextAdapter.TryExtract(invalidMeta, headers, out var fallbackExtracted), "invalid MCP _meta traceparent falls back to HTTP W3C context");
        Assert(fallbackExtracted.Source == McpTraceContextSource.Http && fallbackExtracted.Parent.TraceId.ToString() == httpTraceId && fallbackExtracted.Parent.SpanId.ToString() == httpSpanId, "HTTP traceparent is fallback parent");

        var baggageOnly = new JsonObject { ["baggage"] = baggageMarker };
        Assert(!McpTraceContextAdapter.TryExtract(baggageOnly, new Dictionary<string, string>(), out _), "baggage without traceparent is ignored");
        var overlongState = new string('a', McpTraceContextAdapter.MaxTraceStateUtf8Bytes + 1);
        var oversizedStateMeta = new JsonObject { ["traceparent"] = metaTraceParent, ["tracestate"] = overlongState };
        Assert(McpTraceContextAdapter.TryExtract(oversizedStateMeta, new Dictionary<string, string>(), out var boundedState) && boundedState.TraceState is null, "overlong tracestate is dropped while valid traceparent remains usable");

        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == McpStandardTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);
        using var telemetry = new McpStandardTelemetry();
        var workspace = Path.Combine(root, "trace-context-http");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "hello.txt"), "hello");
        var port = FreePort();
        var token = new string('r', 64);
        var correlation = new LogicalChatCorrelationService();
        var sessions = new LogicalSessionRegistry();
        await using var server = new LocalMcpServer((ushort)port, workspace, "", "", false, token, _ => { }, chatCorrelation: correlation, sessions: sessions, workspaceKey: "D", standardTelemetry: telemetry);
        await server.StartAsync();

        var httpHeaders = AuthHeaders(token);
        httpHeaders["traceparent"] = httpTraceParent;
        httpHeaders["tracestate"] = "vendor=http";
        httpHeaders["baggage"] = $"private={baggageMarker}";
        const string legacyListBody = "{\"jsonrpc\":\"2.0\",\"id\":101,\"method\":\"tools/list\",\"params\":{}}";
        _ = await SendHttpAsync(port, "POST", "/mcp", httpHeaders, legacyListBody);
        var httpActivity = activities.Last(activity => activity.DisplayName == "mcp tools/list");
        Assert(httpActivity.TraceId.ToString() == httpTraceId && httpActivity.ParentSpanId.ToString() == httpSpanId, "server Activity uses HTTP traceparent when MCP _meta has no trace context");
        Assert(httpActivity.TagObjects.Any(tag => tag.Key == "filemcp.trace.parent_source" && Equals(tag.Value, "http")), "server Activity records bounded HTTP parent-source tag");
        Assert(!httpActivity.Baggage.Any() && !httpActivity.TagObjects.Any(tag => tag.Value?.ToString()?.Contains(baggageMarker, StringComparison.Ordinal) == true), "HTTP baggage is not imported or exported");

        var metaListBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 102,
            ["method"] = "tools/list",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject
                {
                    ["traceparent"] = metaTraceParent,
                    ["tracestate"] = "vendor=meta",
                    ["baggage"] = $"private={baggageMarker}",
                },
            },
        }.ToJsonString();
        _ = await SendHttpAsync(port, "POST", "/mcp", httpHeaders, metaListBody);
        var metaActivity = activities.Last(activity => activity.DisplayName == "mcp tools/list");
        Assert(metaActivity.TraceId.ToString() == metaTraceId && metaActivity.ParentSpanId.ToString() == metaSpanId, "MCP _meta traceparent takes precedence over conflicting HTTP traceparent");
        Assert(metaActivity.TraceStateString == "vendor=meta" && metaActivity.TagObjects.Any(tag => tag.Key == "filemcp.trace.parent_source" && Equals(tag.Value, "mcp_meta")), "server Activity preserves bounded MCP tracestate and source semantics");
        Assert(!metaActivity.Baggage.Any() && !metaActivity.TagObjects.Any(tag => tag.Value?.ToString()?.Contains(baggageMarker, StringComparison.Ordinal) == true), "MCP baggage is intentionally ignored for privacy");

        var invalidMetaListBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 103,
            ["method"] = "tools/list",
            ["params"] = new JsonObject { ["_meta"] = new JsonObject { ["traceparent"] = "invalid" } },
        }.ToJsonString();
        _ = await SendHttpAsync(port, "POST", "/mcp", httpHeaders, invalidMetaListBody);
        var fallbackActivity = activities.Last(activity => activity.DisplayName == "mcp tools/list");
        Assert(fallbackActivity.TraceId.ToString() == httpTraceId && fallbackActivity.ParentSpanId.ToString() == httpSpanId, "invalid MCP trace context safely falls back to HTTP parent");

        var unboundReadBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 104,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "read_file",
                ["arguments"] = new JsonObject { ["relative_path"] = "hello.txt", ["_filemcp_chat"] = metaTraceParent },
                ["_meta"] = new JsonObject { ["traceparent"] = metaTraceParent, ["baggage"] = baggageMarker },
            },
        }.ToJsonString();
        var unboundRead = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), unboundReadBody);
        var unboundReadJson = JsonNode.Parse(HttpBody(unboundRead))!.AsObject();
        Assert(!unboundReadJson["result"]!["isError"]!.GetValue<bool>(), "trace identity does not act as authority and does not break valid tool execution");
        Assert(sessions.Snapshot(includeStale: true).Count == 0, "traceparent cannot be treated as logical chat identity");
        var unbound = sessions.UnboundSnapshot(includeStale: true).Single();
        Assert(unbound.Usage.ToolCalls == 1 && unbound.Usage.ReadCalls == 1, "invalid chat correlation remains unbound even when traceparent is valid");

        var traversalBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 105,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "read_file",
                ["arguments"] = new JsonObject { ["relative_path"] = "../escape.txt", ["_filemcp_chat"] = metaTraceParent },
                ["_meta"] = new JsonObject { ["traceparent"] = metaTraceParent },
            },
        }.ToJsonString();
        var traversal = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), traversalBody);
        var traversalJson = JsonNode.Parse(HttpBody(traversal))!.AsObject();
        Assert(traversalJson["result"]!["isError"]!.GetValue<bool>(), "trace context cannot bypass filesystem containment");
        var afterTraversal = sessions.UnboundSnapshot(includeStale: true).Single();
        Assert(afterTraversal.Usage.ToolCalls == 2 && afterTraversal.Usage.Errors == 1, "trace-context tool error remains ordinary unbound telemetry");
        Console.WriteLine("windows-mcp-trace-context: ok");
    }
    private sealed record OtlpCapturedRequest(string Path, string ContentType, byte[] Body);

    private sealed class OtlpTestCollector : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentQueue<OtlpCapturedRequest> _requests = new();
        private Task? _loop;

        public OtlpTestCollector()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = $"http://127.0.0.1:{port}";
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        public string Endpoint { get; }
        public OtlpCapturedRequest[] Requests => _requests.ToArray();

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                    _ = Task.Run(() => HandleAsync(client, cancellationToken), CancellationToken.None);
                }
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                var headerBytes = new List<byte>(1024);
                var tail = new Queue<byte>(4);
                while (headerBytes.Count < 64 * 1024)
                {
                    var one = new byte[1];
                    var read = await stream.ReadAsync(one, cancellationToken);
                    if (read == 0) return;
                    headerBytes.Add(one[0]);
                    tail.Enqueue(one[0]);
                    while (tail.Count > 4) tail.Dequeue();
                    if (tail.Count == 4 && tail.SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
                var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;
                var requestParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var path = requestParts.Length >= 2 ? requestParts[1] : "";
                var contentLength = 0;
                var contentType = "";
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var name = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) _ = int.TryParse(value, out contentLength);
                    if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
                }
                var body = new byte[Math.Max(0, contentLength)];
                var offset = 0;
                while (offset < body.Length)
                {
                    var read = await stream.ReadAsync(body.AsMemory(offset, body.Length - offset), cancellationToken);
                    if (read == 0) break;
                    offset += read;
                }
                if (offset != body.Length) Array.Resize(ref body, offset);
                _requests.Enqueue(new OtlpCapturedRequest(path, contentType, body));
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            if (_loop is not null)
            {
                try { await _loop; } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
            }
            _cts.Dispose();
        }
    }

    private static async Task TestOtlpExporterAsync(string root)
    {
        Assert(!new FileMcpSettings().OtlpEnabled && new FileMcpSettings().OtlpEndpoint == OtlpTelemetrySettings.DefaultEndpoint, "OTLP settings default to disabled with local collector endpoint");
        Assert(OtlpTelemetrySettings.NormalizeBaseEndpoint("http://127.0.0.1:4318").ToString() == "http://127.0.0.1:4318/", "OTLP base endpoint normalizes collector URI");
        foreach (var invalid in new[] { "ftp://127.0.0.1:4318", "http://user:pass@127.0.0.1:4318", "http://127.0.0.1:4318/custom", "http://127.0.0.1:4318?secret=yes" })
        {
            try
            {
                _ = OtlpTelemetrySettings.NormalizeBaseEndpoint(invalid);
                throw new Exception("Assertion failed: OTLP endpoint validation rejects unsafe/non-base URI");
            }
            catch (FileMcpException)
            {
                Assert(true, "OTLP endpoint validation rejects unsafe/non-base URI");
            }
        }

        await using var disabledCollector = new OtlpTestCollector();
        using (var disabledBridge = new OtlpTelemetryBridge())
        {
            var disabled = disabledBridge.Configure(new OtlpTelemetrySettings(false, disabledCollector.Endpoint));
            Assert(disabled.Status == OtlpExporterRuntimeStatus.Disabled && disabledBridge.ForceFlush(250), "OTLP disabled mode configures no exporter and flush is a no-op");
            using var disabledTelemetry = new McpStandardTelemetry();
            using (var operation = disabledTelemetry.BeginOperation(FileMcpConstants.ModernProtocolVersion, "tools/call", "read_file", "D", 3)) operation.Complete(4, false);
            await Task.Delay(150);
            Assert(disabledCollector.Requests.Length == 0, "OTLP disabled mode performs zero collector network requests");
        }

        await using var collector = new OtlpTestCollector();
        using (var bridge = new OtlpTelemetryBridge())
        {
            var configured = bridge.Configure(new OtlpTelemetrySettings(true, collector.Endpoint));
            Assert(configured.Status == OtlpExporterRuntimeStatus.Configured && configured.Endpoint == collector.Endpoint, "OTLP exporter configures HTTP/Protobuf trace and metric providers");
            using var telemetry = new McpStandardTelemetry();
            using (var operation = telemetry.BeginOperation(FileMcpConstants.ModernProtocolVersion, "tools/call", "read_file", "D", 5)) operation.Complete(7, false);
            _ = bridge.ForceFlush(2_000);
            Assert(await WaitUntilAsync(() => collector.Requests.Any(request => request.Path == "/v1/traces") && collector.Requests.Any(request => request.Path == "/v1/metrics"), TimeSpan.FromSeconds(3)), "OTLP exporter sends traces and metrics to standard HTTP/Protobuf endpoints");
            var exported = collector.Requests;
            Assert(exported.Where(request => request.Path is "/v1/traces" or "/v1/metrics").All(request => request.ContentType.Contains("application/x-protobuf", StringComparison.OrdinalIgnoreCase) && request.Body.Length > 0), "OTLP exporter uses non-empty protobuf payloads");
        }

        using (var invalidBridge = new OtlpTelemetryBridge())
        {
            var invalid = invalidBridge.Configure(new OtlpTelemetrySettings(true, "http://127.0.0.1:4318/not-a-base"));
            Assert(invalid.Status == OtlpExporterRuntimeStatus.ConfigurationError && !string.IsNullOrWhiteSpace(invalid.Error), "OTLP configuration errors degrade exporter without throwing into app startup");
        }

        var closedPort = FreePort();
        var downEndpoint = $"http://127.0.0.1:{closedPort}";
        await using (var hub = new ObservabilityHub(["D"], Path.Combine(root, "otlp-down.sqlite3"), TimeSpan.FromHours(1)))
        {
            var configured = hub.ConfigureOtlp(new OtlpTelemetrySettings(true, downEndpoint));
            Assert(configured.Status == OtlpExporterRuntimeStatus.Configured, "OTLP unreachable collector remains an optional configured exporter");
            var workspace = Path.Combine(root, "otlp-down-workspace");
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(workspace, "hello.txt"), "hello");
            var port = FreePort();
            var token = new string('o', 64);
            await using var server = new LocalMcpServer((ushort)port, workspace, "", "", false, token, _ => { }, workspaceKey: "D", standardTelemetry: hub.StandardTelemetry);
            await server.StartAsync();
            const string readBody = "{\"jsonrpc\":\"2.0\",\"id\":501,\"method\":\"tools/call\",\"params\":{\"name\":\"read_file\",\"arguments\":{\"relative_path\":\"hello.txt\"}}}";
            var response = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), readBody);
            Assert(response.StartsWith("HTTP/1.1 200 OK", StringComparison.Ordinal) && HttpBody(response).Contains("hello", StringComparison.Ordinal), "MCP request succeeds while optional OTLP collector is unreachable");
            var flushTimer = Stopwatch.StartNew();
            _ = hub.ForceFlushOtlpForTests(500);
            flushTimer.Stop();
            Assert(flushTimer.Elapsed < TimeSpan.FromSeconds(2), "OTLP collector outage is bounded by exporter timeout and never blocks MCP indefinitely");
        }

        Console.WriteLine("windows-otlp-exporter: ok");
    }
    private static void TestLogicalChatCorrelation()
    {
        var service = new LogicalChatCorrelationService();
        var first = service.Connect();
        Assert(!first.Resumed && LogicalChatCorrelationService.IsValidHandle(first.ChatInstanceId), "logical chat creates valid opaque handle");
        Assert(first.ChatInstanceId.StartsWith(LogicalChatCorrelationService.HandlePrefix, StringComparison.Ordinal), "logical chat handle prefix");
        Assert(service.TryResolve(first.ChatInstanceId, out var firstHash), "logical chat resolves known handle");
        Assert(firstHash.Length == 64 && !firstHash.Contains(first.ChatInstanceId, StringComparison.Ordinal), "logical chat persistence hash is opaque");
        Assert(LogicalChatCorrelationService.HashForPersistence(first.ChatInstanceId) == firstHash, "logical chat persistence hash deterministic");

        var resumed = service.Connect(first.ChatInstanceId);
        Assert(resumed.Resumed && resumed.ChatInstanceId == first.ChatInstanceId, "logical chat resumes known handle");
        var second = service.Connect();
        Assert(second.ChatInstanceId != first.ChatInstanceId, "logical chat creates unique handles");
        Assert(!service.TryResolve("chat_invalid", out _), "logical chat invalid handle remains unbound");

        var otherProcess = new LogicalChatCorrelationService();
        try
        {
            _ = otherProcess.Connect(first.ChatInstanceId);
            throw new Exception("Assertion failed: logical chat unknown resume rejected");
        }
        catch (FileMcpException ex) when (ex.Message.Contains("Unknown FileMCP chat correlation handle", StringComparison.Ordinal))
        {
            Assert(true, "logical chat unknown resume rejected");
        }
        var t0 = new DateTimeOffset(2026, 9, 22, 1, 0, 0, TimeSpan.Zero);
        var bounded = new LogicalChatCorrelationService(maxKnownHandles: 2, retention: TimeSpan.FromHours(1));
        var boundedFirst = bounded.Connect(nowUtc: t0).ChatInstanceId;
        var boundedSecond = bounded.Connect(nowUtc: t0.AddMinutes(1)).ChatInstanceId;
        Assert(bounded.TryResolve(boundedFirst, out _, t0.AddMinutes(30)), "logical chat touch refreshes retention age");
        var boundedThird = bounded.Connect(nowUtc: t0.AddMinutes(31)).ChatInstanceId;
        Assert(bounded.TryResolve(boundedFirst, out _, t0.AddMinutes(31)), "logical chat recent handle survives capacity pressure");
        Assert(!bounded.TryResolve(boundedSecond, out _, t0.AddMinutes(31)), "logical chat oldest handle evicted under capacity pressure");
        Assert(bounded.TryResolve(boundedThird, out _, t0.AddMinutes(31)), "logical chat newest handle retained under capacity pressure");
        var boundedStats = bounded.RetentionSnapshot();
        Assert(boundedStats.KnownHandles == 2 && boundedStats.PressureEvictions == 1 && boundedStats.CapacityPressureEvents == 1, "logical chat capacity is bounded with pressure metrics");

        var expiring = new LogicalChatCorrelationService(maxKnownHandles: 2, retention: TimeSpan.FromHours(1));
        var expiredHandle = expiring.Connect(nowUtc: t0).ChatInstanceId;
        var expiredStats = expiring.Cleanup(t0.AddHours(2));
        Assert(expiredStats.KnownHandles == 0 && expiredStats.ExpiredEvictions == 1, "logical chat TTL cleanup evicts expired handle");
        Assert(!expiring.TryResolve(expiredHandle, out _, t0.AddHours(2)), "logical chat expired handle no longer resolves");

        Console.WriteLine("windows-logical-chat-correlation: ok");
    }
    private static async Task TestLogicalSessionRegistryAsync(string root)
    {
        var t0 = new DateTimeOffset(2026, 9, 22, 1, 0, 0, TimeSpan.Zero);
        var correlation = new LogicalChatCorrelationService();
        var rawHandle = correlation.Connect().ChatInstanceId;
        var sessionHash = LogicalChatCorrelationService.HashForPersistence(rawHandle);
        var registry = new LogicalSessionRegistry();
        registry.RegisterSession(sessionHash, t0);

        var inFlight = registry.BeginToolCall("D", sessionHash, 5, "write_file", t0.AddSeconds(1));
        var activeInFlight = registry.Snapshot(t0.AddHours(1), includeStale: true).Single();
        Assert(activeInFlight.State == LogicalSessionActivityState.Active && activeInFlight.InFlightCalls == 1, "logical session in-flight call forces active state");
        registry.CompleteToolCall(inFlight, 9, false, 7, t0.AddSeconds(2));
        var active = registry.Snapshot(t0.AddSeconds(40), includeStale: true).Single();
        Assert(active.State == LogicalSessionActivityState.Active && active.InFlightCalls == 0, "logical session stays active through 45 second idle threshold");
        var idle = registry.Snapshot(t0.AddSeconds(48), includeStale: true).Single();
        Assert(idle.State == LogicalSessionActivityState.Idle, "logical session becomes idle after 45 seconds");
        var stale = registry.Snapshot(t0.AddMinutes(31), includeStale: true).Single();
        Assert(stale.State == LogicalSessionActivityState.Stale, "logical session becomes stale after 30 minutes");
        Assert(registry.Snapshot(t0.AddMinutes(31)).Count == 0, "logical session stale rows hidden by default");
        Assert(stale.Workspaces["D"].Usage.ToolCalls == 1 && stale.Workspaces["D"].Usage.WriteCalls == 1 && stale.Workspaces["D"].Usage.ExecutionTasks == 1, "logical session bound usage categorized");
        Assert(stale.Workspaces["D"].Usage.RequestBytes == 5 && stale.Workspaces["D"].Usage.ResponseBytes == 9, "logical session bound bytes captured");

        var unboundCall = registry.BeginToolCall("E", null, 4, "read_file", t0.AddSeconds(3));
        registry.CompleteToolCall(unboundCall, 8, true, 5, t0.AddSeconds(4));
        var unbound = registry.UnboundSnapshot(t0.AddSeconds(20), includeStale: true).Single();
        Assert(unbound.WorkspaceKey == "E" && unbound.Usage.ToolCalls == 1 && unbound.Usage.ReadCalls == 1 && unbound.Usage.Errors == 1, "logical session unbound traffic retained separately");

        var retentionHandle = correlation.Connect().ChatInstanceId;
        var retentionHash = LogicalChatCorrelationService.HashForPersistence(retentionHandle);
        var retentionRegistry = new LogicalSessionRegistry(maxSessions: 1, retention: TimeSpan.FromHours(1));
        retentionRegistry.RegisterSession(retentionHash, t0);
        var retentionCall = retentionRegistry.BeginToolCall("D", retentionHash, 3, "read_file", t0.AddSeconds(1));
        retentionRegistry.CompleteToolCall(retentionCall, 4, false, 2, t0.AddSeconds(2));
        var unpersistedBatch = retentionRegistry.DrainPersistence(t0.AddMinutes(31));
        var beforePersistCleanup = retentionRegistry.Cleanup(t0.AddHours(2));
        Assert(beforePersistCleanup.SessionCount == 1 && beforePersistCleanup.ExpiredSessionEvictions == 0, "logical session cleanup does not evict before final stale state is persisted");
        retentionRegistry.MarkPersisted(unpersistedBatch);
        var afterPersistCleanup = retentionRegistry.Cleanup(t0.AddHours(2));
        Assert(afterPersistCleanup.SessionCount == 0 && afterPersistCleanup.ExpiredSessionEvictions == 1, "logical session TTL cleanup evicts only persisted stale session");

        var pressureFirstHandle = correlation.Connect().ChatInstanceId;
        var pressureSecondHandle = correlation.Connect().ChatInstanceId;
        var pressureFirstHash = LogicalChatCorrelationService.HashForPersistence(pressureFirstHandle);
        var pressureSecondHash = LogicalChatCorrelationService.HashForPersistence(pressureSecondHandle);
        var pressureRegistry = new LogicalSessionRegistry(maxSessions: 1, retention: TimeSpan.FromHours(2));
        pressureRegistry.RegisterSession(pressureFirstHash, t0);
        var pressureCall = pressureRegistry.BeginToolCall("D", pressureFirstHash, 1, "read_file", t0.AddSeconds(1));
        pressureRegistry.CompleteToolCall(pressureCall, 1, false, 1, t0.AddSeconds(2));
        var pressureBatch = pressureRegistry.DrainPersistence(t0.AddMinutes(31));
        pressureRegistry.MarkPersisted(pressureBatch);
        pressureRegistry.RegisterSession(pressureSecondHash, t0.AddMinutes(40));
        var pressureSessions = pressureRegistry.Snapshot(t0.AddMinutes(40), includeStale: true);
        var pressureStats = pressureRegistry.RetentionSnapshot();
        Assert(pressureSessions.Count == 1 && pressureSessions[0].SessionHash == pressureSecondHash, "logical session cap evicts oldest persisted stale session for new admission");
        Assert(pressureStats.PressureSessionEvictions == 1 && pressureStats.CapacityPressureEvents == 1, "logical session cap exposes pressure metrics");

        var inFlightHash = LogicalChatCorrelationService.HashForPersistence(correlation.Connect().ChatInstanceId);
        var rejectedHash = LogicalChatCorrelationService.HashForPersistence(correlation.Connect().ChatInstanceId);
        var inFlightRegistry = new LogicalSessionRegistry(maxSessions: 1, retention: TimeSpan.FromHours(1));
        inFlightRegistry.RegisterSession(inFlightHash, t0);
        var tokens = new LogicalSessionCallToken[32];
        Parallel.For(0, tokens.Length, i => tokens[i] = inFlightRegistry.BeginToolCall("D", inFlightHash, 1, "read_file", t0.AddSeconds(i)));
        inFlightRegistry.RegisterSession(rejectedHash, t0.AddHours(2));
        var duringPressure = inFlightRegistry.Cleanup(t0.AddHours(2));
        Assert(duringPressure.SessionCount == 1 && inFlightRegistry.Snapshot(t0.AddHours(2), includeStale: true).Single().SessionHash == inFlightHash, "logical session cleanup never evicts in-flight session under pressure");
        Parallel.For(0, tokens.Length, i => inFlightRegistry.CompleteToolCall(tokens[i], 1, false, 1, t0.AddHours(2)));

        var databaseDirectory = Path.Combine(root, "logical-session-store");
        Directory.CreateDirectory(databaseDirectory);
        var database = Path.Combine(databaseDirectory, "sessions.sqlite3");
        var store = new TelemetrySqliteStore(database);
        var persistedRegistry = new LogicalSessionRegistry();
        await using (var writer = new LogicalSessionWriter(persistedRegistry, store, TimeSpan.FromHours(1)))
        {
            await writer.StartAsync();
            persistedRegistry.RegisterSession(sessionHash, t0);
            var bound = persistedRegistry.BeginToolCall("D", sessionHash, 5, "write_file", t0.AddSeconds(1));
            persistedRegistry.CompleteToolCall(bound, 9, false, 7, t0.AddSeconds(2));
            var unboundToken = persistedRegistry.BeginToolCall("E", null, 4, "read_file", t0.AddSeconds(1));
            persistedRegistry.CompleteToolCall(unboundToken, 8, true, 5, t0.AddSeconds(2));
            await writer.FlushOnceAsync(t0.AddSeconds(3));
            Assert(writer.PendingSnapshotForTests().IsEmpty, "logical session writer clears pending after successful flush");

            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                await using var schema = connection.CreateCommand();
                schema.CommandText = "SELECT value FROM telemetry_meta WHERE key='schema_version';";
                Assert((string?)await schema.ExecuteScalarAsync() == TelemetrySqliteStore.SchemaVersion.ToString(), "logical session store schema version current");

                await using var sessionQuery = connection.CreateCommand();
                sessionQuery.CommandText = "SELECT session_hash,state,created_epoch,last_seen_epoch FROM logical_sessions;";
                await using var sessionReader = await sessionQuery.ExecuteReaderAsync();
                Assert(await sessionReader.ReadAsync(), "logical session durable row exists");
                Assert(sessionReader.GetString(0) == sessionHash && sessionReader.GetString(0) != rawHandle, "logical session durable identity is hash only");
                Assert(sessionReader.GetString(1) == "active", "logical session durable active state");

                await using var workspaceQuery = connection.CreateCommand();
                workspaceQuery.CommandText = "SELECT tool_calls,execution_tasks,request_bytes,response_bytes,write_calls FROM logical_session_workspace WHERE session_hash=$hash AND workspace_key='D';";
                workspaceQuery.Parameters.AddWithValue("$hash", sessionHash);
                await using var workspaceReader = await workspaceQuery.ExecuteReaderAsync();
                Assert(await workspaceReader.ReadAsync(), "logical session durable workspace row exists");
                Assert(workspaceReader.GetInt64(0) == 1 && workspaceReader.GetInt64(1) == 1 && workspaceReader.GetInt64(2) == 5 && workspaceReader.GetInt64(3) == 9 && workspaceReader.GetInt64(4) == 1, "logical session durable bound counters");

                await using var unboundQuery = connection.CreateCommand();
                unboundQuery.CommandText = "SELECT tool_calls,read_calls,errors,request_bytes,response_bytes FROM unbound_session_workspace WHERE workspace_key='E';";
                await using var unboundReader = await unboundQuery.ExecuteReaderAsync();
                Assert(await unboundReader.ReadAsync(), "logical session durable unbound row exists");
                Assert(unboundReader.GetInt64(0) == 1 && unboundReader.GetInt64(1) == 1 && unboundReader.GetInt64(2) == 1 && unboundReader.GetInt64(3) == 4 && unboundReader.GetInt64(4) == 8, "logical session durable unbound counters");
            }

            await writer.FlushOnceAsync(t0.AddMinutes(31));
            await using var staleConnection = new SqliteConnection($"Data Source={database}");
            await staleConnection.OpenAsync();
            await using var staleQuery = staleConnection.CreateCommand();
            staleQuery.CommandText = "SELECT state FROM logical_sessions WHERE session_hash=$hash;";
            staleQuery.Parameters.AddWithValue("$hash", sessionHash);
            Assert((string?)await staleQuery.ExecuteScalarAsync() == "stale", "logical session state-only transition persisted");
        }

        foreach (var file in Directory.GetFiles(databaseDirectory, "sessions.sqlite3*"))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            var text = Encoding.UTF8.GetString(memory.ToArray());
            Assert(!text.Contains(rawHandle, StringComparison.Ordinal), $"logical session persistence never stores raw handle ({Path.GetFileName(file)})");
        }

        var migrationDatabase = Path.Combine(root, "logical-session-store", "migration-v1.sqlite3");
        await using (var connection = new SqliteConnection($"Data Source={migrationDatabase}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE telemetry_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL); INSERT INTO telemetry_meta(key,value) VALUES('schema_version','1');";
            await command.ExecuteNonQueryAsync();
        }
        var migrationStore = new TelemetrySqliteStore(migrationDatabase);
        await migrationStore.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={migrationDatabase}"))
        {
            await connection.OpenAsync();
            await using var version = connection.CreateCommand();
            version.CommandText = "SELECT value FROM telemetry_meta WHERE key='schema_version';";
            Assert((string?)await version.ExecuteScalarAsync() == "2", "logical session schema migrates v1 to v2");
            await using var table = connection.CreateCommand();
            table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='unbound_session_workspace';";
            Assert((long)(await table.ExecuteScalarAsync() ?? 0L) == 1, "logical session v2 migration creates unbound table");
        }
        Console.WriteLine("windows-logical-session-registry: ok");
    }
    private static async Task TestWorkspaceUsageMeterAsync()
    {
        var meter = new WorkspaceUsageMeter("d");
        Assert(meter.WorkspaceKey == "D", "usage meter normalizes workspace key");
        meter.RecordRequest(5);
        meter.RecordResponse(9);
        meter.RecordToolCall("write_file", isError: true, latencyTicks: 7);
        var first = meter.Snapshot();
        Assert(first.McpRequests == 1 && first.ToolCalls == 1 && first.ExecutionTasks == 1, "usage meter basic counts");
        Assert(first.RequestBytes == 5 && first.ResponseBytes == 9, "usage meter basic bytes");
        Assert(first.TokensInEst == 2 && first.TokensOutEst == 3, "usage meter basic token estimates");
        Assert(first.Errors == 1 && first.WriteCalls == 1, "usage meter basic category and error");
        Assert(first.TotalLatencyTicks == 7 && first.MaxLatencyTicks == 7, "usage meter basic latency");
        var firstDelta = meter.DrainDelta();
        Assert(firstDelta == first, "usage meter first delta equals first lifetime snapshot");
        Assert(meter.DrainDelta().IsZero, "usage meter drain resets pending only");
        Assert(meter.Snapshot() == first, "usage meter drain preserves lifetime snapshot");

        var concurrent = new WorkspaceUsageMeter("E");
        const int workers = 8;
        const int perWorker = 2_000;
        var done = 0;
        UsageCounters drained = default;
        var drainTask = Task.Run(async () =>
        {
            var total = default(UsageCounters);
            while (Volatile.Read(ref done) == 0)
            {
                total += concurrent.DrainDelta();
                await Task.Yield();
            }
            total += concurrent.DrainDelta();
            drained = total;
        });

        var producers = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                concurrent.RecordRequest(5);
                concurrent.RecordResponse(9);
                concurrent.RecordToolCall("write_file", i % 10 == 0, (i % 17) + 1);
            }
        })).ToArray();
        await Task.WhenAll(producers);
        Volatile.Write(ref done, 1);
        await drainTask;

        var expectedCalls = workers * perWorker;
        var errorsPerWorker = ((perWorker - 1) / 10) + 1;
        var latencyPerWorker = Enumerable.Range(0, perWorker).Sum(i => (long)((i % 17) + 1));
        var lifetime = concurrent.Snapshot();
        Assert(lifetime.McpRequests == expectedCalls, "usage meter concurrent request count");
        Assert(lifetime.ToolCalls == expectedCalls && lifetime.ExecutionTasks == expectedCalls, "usage meter concurrent tool/task count");
        Assert(lifetime.RequestBytes == expectedCalls * 5L && lifetime.ResponseBytes == expectedCalls * 9L, "usage meter concurrent bytes");
        Assert(lifetime.TokensInEst == expectedCalls * 2L && lifetime.TokensOutEst == expectedCalls * 3L, "usage meter concurrent token estimates");
        Assert(lifetime.Errors == workers * errorsPerWorker, "usage meter concurrent errors");
        Assert(lifetime.WriteCalls == expectedCalls && lifetime.ReadCalls == 0 && lifetime.OtherCalls == 0, "usage meter concurrent category");
        Assert(lifetime.TotalLatencyTicks == workers * latencyPerWorker && lifetime.MaxLatencyTicks == 17, "usage meter concurrent latency");
        Assert(drained == lifetime, "usage meter concurrent drains lose no increments");
        Assert(concurrent.DrainDelta().IsZero, "usage meter pending empty after concurrent drain");

        var defensive = new WorkspaceUsageMeter("F");
        defensive.RecordRequest(-1);
        defensive.RecordResponse(-2);
        defensive.RecordToolCall(null, false, -3);
        var defensiveSnapshot = defensive.Snapshot();
        Assert(defensiveSnapshot.McpRequests == 1 && defensiveSnapshot.RequestBytes == 0 && defensiveSnapshot.ResponseBytes == 0, "usage meter clamps invalid byte metrics without breaking request path");
        Assert(defensiveSnapshot.ToolCalls == 1 && defensiveSnapshot.OtherCalls == 1 && defensiveSnapshot.TotalLatencyTicks == 0, "usage meter defensive unknown tool/latency");
        Console.WriteLine("windows-observability-meter: ok");
    }
    private sealed class FailOnceTelemetryStore : ITelemetryDeltaStore
    {
        private bool _failed;
        public int InitializeCalls { get; private set; }
        public Dictionary<string, UsageCounters> Persisted { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            return Task.CompletedTask;
        }

        public Task UpsertDeltasAsync(DateTimeOffset capturedAtUtc, IReadOnlyDictionary<string, UsageCounters> deltas, CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                throw new IOException("intentional telemetry store failure");
            }
            foreach (var pair in deltas)
                Persisted[pair.Key] = Persisted.TryGetValue(pair.Key, out var existing) ? existing + pair.Value : pair.Value;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingRetentionStore : ITelemetryRetentionStore
    {
        public int Calls { get; private set; }

        public Task CleanupRetentionAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new IOException("intentional retention store failure");
        }
    }
    private static async Task TestTelemetryPersistenceAsync(string root)
    {
        var directory = Path.Combine(root, "telemetry");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "observability.sqlite3");
        var store = new TelemetrySqliteStore(database);
        await store.InitializeAsync();
        await store.InitializeAsync();
        Assert(File.Exists(database), "telemetry sqlite database created");

        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var journal = connection.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode;";
            Assert(string.Equals((string?)await journal.ExecuteScalarAsync(), "wal", StringComparison.OrdinalIgnoreCase), "telemetry sqlite WAL enabled");

            await using var meta = connection.CreateCommand();
            meta.CommandText = "SELECT value FROM telemetry_meta WHERE key='schema_version';";
            Assert((string?)await meta.ExecuteScalarAsync() == TelemetrySqliteStore.SchemaVersion.ToString(), "telemetry schema version");
            meta.CommandText = "SELECT value FROM telemetry_meta WHERE key='token_estimator';";
            Assert((string?)await meta.ExecuteScalarAsync() == McpTokenEstimator.EstimatorId, "telemetry estimator metadata");

            foreach (var table in new[] { "usage_minute", "usage_hour", "usage_day" })
            {
                await using var schema = connection.CreateCommand();
                schema.CommandText = $"PRAGMA table_info({table});";
                await using var reader = await schema.ExecuteReaderAsync();
                var columns = new List<string>();
                while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
                Assert(columns.Contains("workspace_key") && columns.Contains("request_bytes") && columns.Contains("tokens_out_est"), $"telemetry {table} expected columns");
                Assert(!columns.Any(name => name.Contains("content", StringComparison.OrdinalIgnoreCase) || name.Contains("argument", StringComparison.OrdinalIgnoreCase) || name.Contains("body", StringComparison.OrdinalIgnoreCase)), $"telemetry {table} stores no MCP content/body/arguments");
            }
        }

        var meter = new WorkspaceUsageMeter("D");
        await using var writer = new TelemetryWriter([meter], store, TimeSpan.FromHours(1));
        var captured = DateTimeOffset.Parse("2026-09-22T00:00:30Z");
        meter.RecordRequest(5);
        meter.RecordResponse(9);
        meter.RecordToolCall("write_file", false, Stopwatch.Frequency / 1_000);
        await writer.FlushOnceAsync(captured);
        Assert(writer.PendingSnapshotForTests().Count == 0, "telemetry writer clears pending after commit");

        meter.RecordRequest(4);
        meter.RecordResponse(4);
        meter.RecordToolCall("git_status", true, Stopwatch.Frequency / 2_000);
        await writer.FlushOnceAsync(captured.AddSeconds(10));

        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            foreach (var (table, size) in new[] { ("usage_minute", 60L), ("usage_hour", 3_600L), ("usage_day", 86_400L) })
            {
                var epoch = TelemetrySqliteStore.FloorBucket(captured.ToUnixTimeSeconds(), size);
                await using var query = connection.CreateCommand();
                query.CommandText = $"SELECT mcp_requests,tool_calls,execution_tasks,request_bytes,response_bytes,tokens_in_est,tokens_out_est,errors,read_calls,write_calls,git_calls,total_latency_us,max_latency_us FROM {table} WHERE workspace_key='D' AND bucket_epoch=$bucket;";
                query.Parameters.AddWithValue("$bucket", epoch);
                await using var reader = await query.ExecuteReaderAsync();
                Assert(await reader.ReadAsync(), $"telemetry {table} row exists");
                Assert(reader.GetInt64(0) == 2 && reader.GetInt64(1) == 2 && reader.GetInt64(2) == 1, $"telemetry {table} call/task aggregation");
                Assert(reader.GetInt64(3) == 9 && reader.GetInt64(4) == 13, $"telemetry {table} byte aggregation");
                Assert(reader.GetInt64(5) == 3 && reader.GetInt64(6) == 4, $"telemetry {table} token aggregation");
                Assert(reader.GetInt64(7) == 1 && reader.GetInt64(8) == 0 && reader.GetInt64(9) == 1 && reader.GetInt64(10) == 1, $"telemetry {table} category/error aggregation");
                Assert(reader.GetInt64(11) > 0 && reader.GetInt64(12) > 0 && reader.GetInt64(12) <= reader.GetInt64(11), $"telemetry {table} latency aggregation");
            }
        }

        var retryMeter = new WorkspaceUsageMeter("E");
        var failOnce = new FailOnceTelemetryStore();
        await using var retryWriter = new TelemetryWriter([retryMeter], failOnce, TimeSpan.FromHours(1));
        retryMeter.RecordRequest(8);
        retryMeter.RecordResponse(12);
        retryMeter.RecordToolCall("run_command", false, 5);
        await AssertThrowsAsync(() => retryWriter.FlushOnceAsync(captured), "intentional telemetry store failure", "telemetry writer surfaces explicit test flush failure");
        Assert(retryWriter.PendingSnapshotForTests().TryGetValue("E", out var pending) && pending.McpRequests == 1 && pending.CommandCalls == 1, "telemetry writer retains pending delta after failed commit");
        await retryWriter.FlushOnceAsync(captured.AddSeconds(1));
        Assert(retryWriter.PendingSnapshotForTests().Count == 0, "telemetry writer clears pending after retry");
        Assert(failOnce.Persisted.TryGetValue("E", out var retried) && retried.McpRequests == 1 && retried.RequestBytes == 8 && retried.ResponseBytes == 12, "telemetry writer retry persists original delta once");

        var newer = Path.Combine(directory, "newer.sqlite3");
        await using (var connection = new SqliteConnection($"Data Source={newer}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE telemetry_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL); INSERT INTO telemetry_meta(key,value) VALUES('schema_version','999');";
            await command.ExecuteNonQueryAsync();
        }
        var newerStore = new TelemetrySqliteStore(newer);
        await AssertThrowsAsync(() => newerStore.InitializeAsync(), "newer than supported", "telemetry rejects newer schema");
        Console.WriteLine("windows-observability-sqlite: ok");
    }
    private static async Task TestUsagePeriodsAndRetentionAsync(string root)
    {
        var quarterHour = TimeZoneInfo.CreateCustomTimeZone(
            "FileMCP-Test-UTC+05:45",
            TimeSpan.FromMinutes(345),
            "FileMCP Test UTC+05:45",
            "FileMCP Test UTC+05:45");
        var now = new DateTimeOffset(2026, 9, 22, 0, 7, 30, TimeSpan.Zero);
        var today = UsagePeriodResolver.Resolve(UsagePeriodPreset.Today, now, quarterHour);
        var yesterday = UsagePeriodResolver.Resolve(UsagePeriodPreset.Yesterday, now, quarterHour);
        var seven = UsagePeriodResolver.Resolve(UsagePeriodPreset.SevenDays, now, quarterHour);
        var thirty = UsagePeriodResolver.Resolve(UsagePeriodPreset.ThirtyDays, now, quarterHour);
        Assert(today.FromUtc == new DateTimeOffset(2026, 9, 21, 18, 15, 0, TimeSpan.Zero) && today.ToUtc == now, "period today quarter-hour timezone boundary");
        Assert(yesterday.FromUtc == new DateTimeOffset(2026, 9, 20, 18, 15, 0, TimeSpan.Zero) && yesterday.ToUtc == today.FromUtc, "period yesterday quarter-hour timezone boundary");
        Assert(seven.FromUtc == new DateTimeOffset(2026, 9, 15, 18, 15, 0, TimeSpan.Zero), "period seven-day local boundary");
        Assert(thirty.FromUtc == new DateTimeOffset(2026, 8, 23, 18, 15, 0, TimeSpan.Zero), "period thirty-day local boundary");

        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var afterFallback = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var fallbackYesterday = UsagePeriodResolver.Resolve(UsagePeriodPreset.Yesterday, afterFallback, eastern);
        Assert(fallbackYesterday.FromUtc == new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), "period DST yesterday start");
        Assert(fallbackYesterday.ToUtc == new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), "period DST yesterday end");
        Assert(fallbackYesterday.Duration == TimeSpan.FromHours(25), "period DST 25-hour local day");

        var database = Path.Combine(root, "telemetry-periods", "periods.sqlite3");
        var store = new TelemetrySqliteStore(database);
        await store.InitializeAsync();
        var one = new UsageCounters(1, 1, 0, 4, 8, 1, 2, 0, 1, 0, 0, 0, 0, 0, 1, 1);
        await store.UpsertDeltasAsync(today.FromUtc.AddMinutes(-1), new Dictionary<string, UsageCounters> { ["D"] = one });
        await store.UpsertDeltasAsync(today.FromUtc, new Dictionary<string, UsageCounters> { ["D"] = one, ["E"] = one });
        await store.UpsertDeltasAsync(now.AddMinutes(-1), new Dictionary<string, UsageCounters> { ["D"] = one });

        var dToday = await store.QueryExactPeriodAsync(today, "D");
        var globalToday = await store.QueryExactPeriodAsync(today);
        Assert(dToday.McpRequests == 2 && dToday.RequestBytes == 8 && dToday.ResponseBytes == 16, "period exact workspace query excludes previous local day");
        Assert(globalToday.McpRequests == 3 && globalToday.ReadCalls == 3, "period exact global query aggregates workspaces");
        await AssertThrowsAsync(
            () => store.QueryExactPeriodAsync(new UsagePeriodRange(today.FromUtc.AddSeconds(1), today.ToUtc), "D"),
            "minute-aligned",
            "period query rejects unrepresentable sub-minute start");

        var retentionNow = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        await store.UpsertDeltasAsync(retentionNow.AddDays(-10), new Dictionary<string, UsageCounters> { ["D"] = one });
        await store.UpsertDeltasAsync(retentionNow.AddDays(-40), new Dictionary<string, UsageCounters> { ["D"] = one });
        await store.UpsertDeltasAsync(retentionNow.AddDays(-100), new Dictionary<string, UsageCounters> { ["D"] = one });
        var oldDayEpoch = TelemetrySqliteStore.FloorBucket(retentionNow.AddDays(-100).ToUnixTimeSeconds(), 86_400);
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            var oldStaleHash = new string('a', 64);
            var recentStaleHash = new string('b', 64);
            var oldActiveHash = new string('c', 64);
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO logical_sessions(session_hash,created_epoch,last_seen_epoch,client_name,state) VALUES
                  ($oldStale,$oldCreated,$oldSeen,NULL,'stale'),
                  ($recentStale,$recentCreated,$recentSeen,NULL,'stale'),
                  ($oldActive,$oldCreated,$oldSeen,NULL,'active');
                INSERT INTO logical_session_workspace(session_hash,workspace_key,first_seen_epoch,last_seen_epoch)
                  VALUES($oldStale,'D',$oldCreated,$oldSeen);
                INSERT INTO unbound_session_workspace(workspace_key,first_seen_epoch,last_seen_epoch,state)
                  VALUES('Z',$oldCreated,$oldSeen,'stale');
                """;
            seed.Parameters.AddWithValue("$oldStale", oldStaleHash);
            seed.Parameters.AddWithValue("$recentStale", recentStaleHash);
            seed.Parameters.AddWithValue("$oldActive", oldActiveHash);
            seed.Parameters.AddWithValue("$oldCreated", retentionNow.AddDays(-50).ToUnixTimeSeconds());
            seed.Parameters.AddWithValue("$oldSeen", retentionNow.AddDays(-40).ToUnixTimeSeconds());
            seed.Parameters.AddWithValue("$recentCreated", retentionNow.AddDays(-20).ToUnixTimeSeconds());
            seed.Parameters.AddWithValue("$recentSeen", retentionNow.AddDays(-10).ToUnixTimeSeconds());
            await seed.ExecuteNonQueryAsync();
        }

        await store.CleanupRetentionAsync(retentionNow);

        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            var minuteCutoff = TelemetrySqliteStore.FloorBucket(retentionNow.AddDays(-35).ToUnixTimeSeconds(), 60);
            var hourCutoff = TelemetrySqliteStore.FloorBucket(retentionNow.AddDays(-90).ToUnixTimeSeconds(), 3_600);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM usage_minute WHERE bucket_epoch < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", minuteCutoff);
            Assert((long)(await command.ExecuteScalarAsync() ?? -1L) == 0, "retention removes minute rows older than 35 days");
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM usage_hour WHERE bucket_epoch < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", hourCutoff);
            Assert((long)(await command.ExecuteScalarAsync() ?? -1L) == 0, "retention removes hour rows older than 90 days");
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM usage_day WHERE workspace_key='D' AND bucket_epoch=$bucket;";
            command.Parameters.AddWithValue("$bucket", oldDayEpoch);
            Assert((long)(await command.ExecuteScalarAsync() ?? 0L) == 1, "retention preserves long-term day row");
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM logical_sessions WHERE session_hash=$hash;";
            command.Parameters.AddWithValue("$hash", new string('a', 64));
            Assert((long)(await command.ExecuteScalarAsync() ?? -1L) == 0, "retention removes durable stale logical session older than 35 days");
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM logical_session_workspace WHERE session_hash=$hash;";
            command.Parameters.AddWithValue("$hash", new string('a', 64));
            Assert((long)(await command.ExecuteScalarAsync() ?? -1L) == 0, "retention cascade removes durable stale session workspace rows");
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM logical_sessions WHERE session_hash IN ($recent,$active);";
            command.Parameters.AddWithValue("$recent", new string('b', 64));
            command.Parameters.AddWithValue("$active", new string('c', 64));
            Assert((long)(await command.ExecuteScalarAsync() ?? -1L) == 2, "retention preserves recent stale and old non-stale durable sessions");
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM unbound_session_workspace WHERE workspace_key='Z';";
            Assert((long)(await command.ExecuteScalarAsync() ?? -1L) == 0, "retention removes old durable stale unbound row");
        }
        Console.WriteLine("windows-observability-periods: ok");
    }
    private static async Task TestObservabilityHubAsync(string root)
    {
        var database = Path.Combine(root, "observability-hub", "hub.sqlite3");
        await using var hub = new ObservabilityHub(["C", "D", "E", "F"], database, TimeSpan.FromHours(1));
        await hub.StartAsync();
        await hub.StartAsync();

        hub.MeterFor("d").RecordRequest(5);
        hub.MeterFor("D").RecordResponse(9);
        hub.MeterFor("D").RecordToolCall("write_file", false, 7);
        hub.MeterFor("E").RecordRequest(4);
        hub.MeterFor("E").RecordResponse(8);
        hub.MeterFor("E").RecordToolCall("read_file", true, 5);

        var initial = hub.Snapshot();
        Assert(initial.AppUptime >= TimeSpan.Zero, "observability hub app uptime available");
        Assert(initial.Workspaces.Count == 4, "observability hub exposes all workspaces");
        Assert(initial.Workspaces["D"].LifetimeUsage.McpRequests == 1 && initial.Workspaces["E"].LifetimeUsage.McpRequests == 1, "observability hub per-workspace usage");
        Assert(initial.GlobalUsage == initial.Workspaces.Values.Select(item => item.LifetimeUsage).Aggregate(default(UsageCounters), (sum, value) => sum + value), "observability hub global usage equals workspace sum");
        Assert(initial.GlobalUsage.McpRequests == 2 && initial.GlobalUsage.ToolCalls == 2 && initial.GlobalUsage.Errors == 1, "observability hub global counters");
        Assert(initial.Persistence.Status == TelemetryPersistenceStatus.Ready && initial.Persistence.LastSuccessUtc.HasValue, "observability hub reports persistence ready after successful initialization");

        hub.MarkRuntimeRunning("D");
        await Task.Delay(20);
        var running1 = hub.Snapshot();
        Assert(running1.Workspaces["D"].RuntimeRunning && running1.Workspaces["D"].RuntimeUptime > TimeSpan.Zero, "observability hub runtime uptime starts");
        var uptime1 = running1.Workspaces["D"].RuntimeUptime;
        hub.MarkRuntimeRunning("D");
        await Task.Delay(15);
        var running2 = hub.Snapshot();
        Assert(running2.Workspaces["D"].RuntimeUptime > uptime1, "observability hub repeated running mark does not reset uptime");
        hub.MarkRuntimeStopped("D");
        var stopped = hub.Snapshot();
        Assert(!stopped.Workspaces["D"].RuntimeRunning && stopped.Workspaces["D"].RuntimeUptime == TimeSpan.Zero, "observability hub runtime stop clears current uptime");

        var captured = new DateTimeOffset(2026, 9, 22, 0, 12, 0, TimeSpan.Zero);
        await hub.FlushOnceForTestsAsync(captured);
        var range = new UsagePeriodRange(captured, captured.AddMinutes(1));
        var dPersisted = await hub.QueryExactPeriodAsync(range, "D");
        var globalPersisted = await hub.QueryExactPeriodAsync(range);
        Assert(dPersisted.McpRequests == 1 && dPersisted.WriteCalls == 1, "observability hub persists/query D usage");
        Assert(globalPersisted.McpRequests == 2 && globalPersisted.ReadCalls == 1 && globalPersisted.WriteCalls == 1, "observability hub global persisted query");

        await hub.RunMaintenanceOnceForTestsAsync(captured.AddDays(1));
        Assert(hub.MaintenanceSnapshotForTests().Runs >= 1, "observability hub wires process-wide maintenance worker");

        var maintenanceNow = new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);
        var maintenanceBase = maintenanceNow.AddHours(-3);
        var maintenanceCorrelation = new LogicalChatCorrelationService(maxKnownHandles: 2, retention: TimeSpan.FromHours(1));
        var maintenanceHandle = maintenanceCorrelation.Connect(nowUtc: maintenanceBase).ChatInstanceId;
        var maintenanceHash = LogicalChatCorrelationService.HashForPersistence(maintenanceHandle);
        var maintenanceSessions = new LogicalSessionRegistry(maxSessions: 2, retention: TimeSpan.FromHours(1));
        maintenanceSessions.RegisterSession(maintenanceHash, maintenanceBase);
        var maintenanceCall = maintenanceSessions.BeginToolCall("D", maintenanceHash, 1, "read_file", maintenanceBase.AddSeconds(1));
        maintenanceSessions.CompleteToolCall(maintenanceCall, 1, false, 1, maintenanceBase.AddSeconds(2));
        var maintenanceBatch = maintenanceSessions.DrainPersistence(maintenanceBase.AddMinutes(31));
        maintenanceSessions.MarkPersisted(maintenanceBatch);
        var failingRetentionStore = new FailingRetentionStore();
        var maintenanceLogs = new StringBuilder();
        await using (var maintenance = new ObservabilityMaintenanceWorker(
            failingRetentionStore,
            maintenanceSessions,
            maintenanceCorrelation,
            TimeSpan.FromMilliseconds(10),
            text => maintenanceLogs.Append(text),
            () => maintenanceNow))
        {
            await maintenance.RunOnceAsync(maintenanceNow);
            var maintenanceSnapshot = maintenance.Snapshot();
            Assert(failingRetentionStore.Calls == 1 && maintenanceSnapshot.Runs == 1 && maintenanceSnapshot.FailureRuns == 1, "maintenance records retention-store failure without throwing");
            Assert(maintenanceSessions.RetentionSnapshot().SessionCount == 0, "maintenance still cleans in-memory sessions after SQLite retention failure");
            Assert(!maintenanceCorrelation.TryResolve(maintenanceHandle, out _, maintenanceNow), "maintenance still cleans correlation handles after SQLite retention failure");
            Assert(maintenanceLogs.ToString().Contains("retention maintenance failed", StringComparison.Ordinal), "maintenance failure is surfaced through telemetry log");
        }

        var periodicStore = new FailingRetentionStore();
        var periodicWorker = new ObservabilityMaintenanceWorker(
            periodicStore,
            new LogicalSessionRegistry(),
            new LogicalChatCorrelationService(),
            TimeSpan.FromMilliseconds(5),
            clock: () => maintenanceNow);
        await periodicWorker.StartAsync();
        await Task.Delay(30);
        var periodicSnapshot = periodicWorker.Snapshot();
        var disposeTimer = Stopwatch.StartNew();
        await periodicWorker.DisposeAsync();
        disposeTimer.Stop();
        Assert(periodicSnapshot.Runs >= 1 && periodicStore.Calls >= 1, "maintenance PeriodicTimer runs automatically despite isolated failures");
        Assert(disposeTimer.Elapsed < TimeSpan.FromSeconds(1), "maintenance cancellation stops PeriodicTimer promptly");

        try
        {
            _ = hub.MeterFor("Z");
            throw new Exception("Assertion failed: observability hub rejects unknown workspace");
        }
        catch (KeyNotFoundException)
        {
            Assert(true, "observability hub rejects unknown workspace");
        }
        await using var realtimeHub = new ObservabilityHub(["D"], Path.Combine(root, "observability-realtime.sqlite3"), TimeSpan.FromHours(1));
        var rt0 = new DateTimeOffset(2026, 9, 22, 2, 0, 0, TimeSpan.Zero);
        var baselineSample = realtimeHub.CaptureRealtimeSample(rt0);
        Assert(baselineSample.Delta.IsZero, "observability realtime first sample establishes zero baseline");
        realtimeHub.MeterFor("D").RecordRequest(5);
        realtimeHub.MeterFor("D").RecordResponse(9);
        realtimeHub.MeterFor("D").RecordToolCall("read_file", false, 11);
        var activitySample = realtimeHub.CaptureRealtimeSample(rt0.AddSeconds(1));
        Assert(activitySample.Delta.McpRequests == 1 && activitySample.Delta.ToolCalls == 1 && activitySample.Delta.ReadCalls == 1, "observability realtime captures call delta");
        Assert(activitySample.Delta.TokensInEst == 2 && activitySample.Delta.TokensOutEst == 3, "observability realtime captures token-estimate delta");
        for (var index = 2; index < 910; index++) realtimeHub.CaptureRealtimeSample(rt0.AddSeconds(index));
        var realtimeSamples = realtimeHub.RealtimeSamples();
        Assert(realtimeSamples.Count == 900, "observability realtime ring bounded to 15 minutes");
        Assert(realtimeSamples[^1].CapturedUtc == rt0.AddSeconds(909), "observability realtime ring keeps newest sample");
        Console.WriteLine("windows-observability-hub: ok");
    }
    private static async Task TestObservabilityHardeningAsync(string root)
    {
        var workspace = Path.Combine(root, "observability-hardening-workspace");
        var databaseDirectory = Path.Combine(root, "observability-hardening-db");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(databaseDirectory);
        File.WriteAllText(Path.Combine(workspace, "parallel.txt"), "parallel-safe-content", new UTF8Encoding(false));

        var database = Path.Combine(databaseDirectory, "observability.sqlite3");
        await using (var hub = new ObservabilityHub(["D"], database, TimeSpan.FromHours(1)))
        {
            await hub.StartAsync();
            var port = FreePort();
            var token = new string('h', 64);
            await using var server = new LocalMcpServer(
                (ushort)port,
                workspace,
                "",
                "",
                false,
                token,
                _ => { },
                hub.MeterFor("D"),
                hub.ChatCorrelation,
                hub.Sessions,
                "D");
            await server.StartAsync();

            const string privatePayload = "FILEMCP_PRIVATE_PAYLOAD_6D572D44_DO_NOT_PERSIST";
            const string privatePath = "private-marker-8b59e4.txt";
            const string privateSearch = "PRIVATE_SEARCH_TERM_AE4218";

            const string connectBody = "{\"jsonrpc\":\"2.0\",\"id\":700,\"method\":\"tools/call\",\"params\":{\"name\":\"filemcp_observability_connect\",\"arguments\":{}}}";
            var connectResponse = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), connectBody);
            var connectJson = JsonNode.Parse(HttpBody(connectResponse))!.AsObject();
            var rawHandle = connectJson["result"]!["structuredContent"]!["chat_instance_id"]!.GetValue<string>();

            var writeBody = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 701,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "write_file",
                    ["arguments"] = new JsonObject
                    {
                        ["relative_path"] = privatePath,
                        ["content"] = privatePayload + " " + privateSearch,
                        ["_filemcp_chat"] = rawHandle,
                    },
                },
            }.ToJsonString();
            var writeResponse = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), writeBody);
            Assert(!JsonNode.Parse(HttpBody(writeResponse))!.AsObject()["result"]!["isError"]!.GetValue<bool>(), "hardening private payload write succeeds");

            var readBody = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 702,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "read_file",
                    ["arguments"] = new JsonObject { ["relative_path"] = privatePath, ["_filemcp_chat"] = rawHandle },
                },
            }.ToJsonString();
            var readResponse = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), readBody);
            Assert(HttpBody(readResponse).Contains(privatePayload, StringComparison.Ordinal), "hardening private response really contains sensitive fixture");

            var searchBody = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 703,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "search_content",
                    ["arguments"] = new JsonObject { ["query"] = privateSearch, ["_filemcp_chat"] = rawHandle },
                },
            }.ToJsonString();
            var searchResponse = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), searchBody);
            Assert(HttpBody(searchResponse).Contains(privateSearch, StringComparison.Ordinal), "hardening search response really contains sensitive query fixture");

            var beforeParallel = hub.Snapshot().GlobalUsage;
            const int parallelCalls = 64;
            var parallelRequests = Enumerable.Range(0, parallelCalls).Select(index =>
            {
                var body = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 800 + index,
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject
                    {
                        ["name"] = "read_file",
                        ["arguments"] = new JsonObject { ["relative_path"] = "parallel.txt", ["_filemcp_chat"] = rawHandle },
                    },
                }.ToJsonString();
                return SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), body);
            }).ToArray();
            var parallelResponses = await Task.WhenAll(parallelRequests);
            Assert(parallelResponses.All(response => HttpBody(response).Contains("parallel-safe-content", StringComparison.Ordinal)), "hardening parallel server calls all succeed");
            var afterParallel = hub.Snapshot().GlobalUsage;
            Assert(afterParallel.ToolCalls - beforeParallel.ToolCalls == parallelCalls, "hardening parallel server telemetry loses no tool calls");
            Assert(afterParallel.ReadCalls - beforeParallel.ReadCalls == parallelCalls, "hardening parallel server telemetry preserves categories");
            var boundSession = hub.Sessions.Snapshot(includeStale: true).Single();
            Assert(boundSession.Workspaces["D"].Usage.ReadCalls >= parallelCalls + 1, "hardening parallel bound-session attribution loses no read calls");

            await hub.FlushAsync(DateTimeOffset.UtcNow);
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(databaseDirectory, "observability.sqlite3*"))
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                var persisted = Encoding.UTF8.GetString(memory.ToArray());
                Assert(!persisted.Contains(privatePayload, StringComparison.Ordinal), $"hardening telemetry DB excludes response/request content ({Path.GetFileName(file)})");
                Assert(!persisted.Contains(privatePath, StringComparison.Ordinal), $"hardening telemetry DB excludes file arguments ({Path.GetFileName(file)})");
                Assert(!persisted.Contains(privateSearch, StringComparison.Ordinal), $"hardening telemetry DB excludes search arguments ({Path.GetFileName(file)})");
                Assert(!persisted.Contains(rawHandle, StringComparison.Ordinal), $"hardening telemetry DB excludes raw correlation handle ({Path.GetFileName(file)})");
            }
        }

        var corruptDirectory = Path.Combine(root, "observability-corrupt");
        Directory.CreateDirectory(corruptDirectory);
        var corruptDatabase = Path.Combine(corruptDirectory, "observability.sqlite3");
        await File.WriteAllBytesAsync(corruptDatabase, Encoding.ASCII.GetBytes("NOT_A_SQLITE_DATABASE_FILEMCP_CORRUPT_FIXTURE"));
        await using (var corruptHub = new ObservabilityHub(["D"], corruptDatabase, TimeSpan.FromHours(1)))
        {
            var initializationFailed = false;
            try { await corruptHub.StartAsync(); }
            catch (Exception) { initializationFailed = true; }
            Assert(initializationFailed, "hardening corrupt telemetry database is detected");
            var degradedPersistence = corruptHub.Snapshot().Persistence;
            Assert(degradedPersistence.Status == TelemetryPersistenceStatus.Degraded && degradedPersistence.FailureCount >= 1, "persistence health degrades after corrupt database failure");

            var port = FreePort();
            var token = new string('r', 64);
            await using var server = new LocalMcpServer(
                (ushort)port,
                workspace,
                "",
                "",
                false,
                token,
                _ => { },
                corruptHub.MeterFor("D"),
                corruptHub.ChatCorrelation,
                corruptHub.Sessions,
                "D");
            await server.StartAsync();
            const string body = "{\"jsonrpc\":\"2.0\",\"id\":950,\"method\":\"tools/call\",\"params\":{\"name\":\"read_file\",\"arguments\":{\"relative_path\":\"parallel.txt\"}}}";
            var response = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), body);
            Assert(HttpBody(response).Contains("parallel-safe-content", StringComparison.Ordinal), "hardening corrupt telemetry cannot fail valid MCP operation");
            Assert(corruptHub.Snapshot().GlobalUsage.ToolCalls == 1, "hardening in-memory telemetry remains available while DB is corrupt");

            SqliteConnection.ClearAllPools();
            File.Delete(corruptDatabase);
            await corruptHub.StartAsync();
            await corruptHub.FlushAsync(DateTimeOffset.UtcNow);
            Assert(File.Exists(corruptDatabase), "hardening telemetry can recover after corrupt DB is removed");
            Assert(corruptHub.Snapshot().Persistence.Status == TelemetryPersistenceStatus.Ready, "persistence health recovers after successful database reinitialization");
            await using var recovered = new SqliteConnection($"Data Source={corruptDatabase}");
            await recovered.OpenAsync();
            await using var version = recovered.CreateCommand();
            version.CommandText = "SELECT value FROM telemetry_meta WHERE key='schema_version';";
            Assert((string?)await version.ExecuteScalarAsync() == TelemetrySqliteStore.SchemaVersion.ToString(), "hardening recovered telemetry DB has current schema");
        }

        Console.WriteLine("windows-observability-hardening: ok");
    }
    private static async Task TestV11HardeningAsync(string root)
    {
        var start = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);

        // Simulate one new logical chat per minute for a full day. The service must remain
        // bounded even when the process stays alive indefinitely.
        var correlation = new LogicalChatCorrelationService(maxKnownHandles: 128, retention: TimeSpan.FromHours(1));
        for (var minute = 0; minute < 24 * 60; minute++)
        {
            var now = start.AddMinutes(minute);
            _ = correlation.Connect(nowUtc: now);
            if (minute % 15 == 0) _ = correlation.Cleanup(now);
        }
        var correlationChurn = correlation.Cleanup(start.AddHours(24));
        Assert(correlationChurn.KnownHandles <= 60, "24h correlation churn remains bounded by TTL instead of process lifetime");
        Assert(correlationChurn.ExpiredEvictions >= 1_300, "24h correlation churn evicts expired handles continuously");

        // Connect-only logical sessions have no workspace row to persist. They still need
        // deterministic eviction after their state has been drained, otherwise a connect flood
        // can permanently consume the session cap.
        var connectOnly = new LogicalSessionRegistry(maxSessions: 128, retention: TimeSpan.FromHours(1));
        for (var minute = 0; minute < 24 * 60; minute++)
        {
            var now = start.AddMinutes(minute);
            connectOnly.RegisterSession(((ulong)minute + 1UL).ToString("x64"), now);
            _ = connectOnly.DrainPersistence(now);
            if (minute % 15 == 0) _ = connectOnly.Cleanup(now);
        }
        var connectOnlyChurn = connectOnly.Cleanup(start.AddHours(24));
        Assert(connectOnlyChurn.SessionCount <= 60, "24h connect-only session churn remains bounded by TTL");
        Assert(connectOnlyChurn.ExpiredSessionEvictions >= 1_300, "24h connect-only session churn evicts old state instead of leaking registry entries");

        var pressureRegistry = new LogicalSessionRegistry(maxSessions: 8, retention: TimeSpan.FromHours(1));
        for (var index = 0; index < 8; index++)
        {
            pressureRegistry.RegisterSession(((ulong)index + 1UL).ToString("x64"), start);
            _ = pressureRegistry.DrainPersistence(start);
        }
        var admittedHash = 99UL.ToString("x64");
        pressureRegistry.RegisterSession(admittedHash, start.AddMinutes(31));
        var pressureSnapshot = pressureRegistry.RetentionSnapshot();
        Assert(pressureSnapshot.SessionCount == 8 && pressureSnapshot.PressureSessionEvictions == 1, "connect-only stale session can be pressure-evicted at the configured cap");
        Assert(pressureRegistry.Snapshot(start.AddMinutes(31), includeStale: true).Any(session => session.SessionHash == admittedHash), "capacity pressure admits the new connect-only session after stale eviction");

        // Seed a large synthetic history directly into SQLite to prove retention is a row-count
        // invariant rather than a happy-path example with one or two rows.
        var database = Path.Combine(root, "v11-hardening-retention.sqlite3");
        var store = new TelemetrySqliteStore(database);
        await store.InitializeAsync();
        var retentionNow = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            async Task InsertRowsAsync(string table, int count, long firstEpoch, long stepSeconds)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = $"INSERT INTO {table} VALUES($workspace,$bucket,{string.Join(',', Enumerable.Repeat("0", 16))});";
                command.Parameters.AddWithValue("$workspace", "D");
                var bucket = command.Parameters.Add("$bucket", SqliteType.Integer);
                for (var index = 0; index < count; index++)
                {
                    bucket.Value = firstEpoch + index * stepSeconds;
                    await command.ExecuteNonQueryAsync();
                }
            }

            await InsertRowsAsync("usage_minute", 12_000, retentionNow.AddDays(-60).ToUnixTimeSeconds(), 60);
            await InsertRowsAsync("usage_minute", 120, retentionNow.AddMinutes(-119).ToUnixTimeSeconds(), 60);
            await InsertRowsAsync("usage_hour", 4_000, retentionNow.AddDays(-300).ToUnixTimeSeconds(), 3_600);
            await InsertRowsAsync("usage_hour", 72, retentionNow.AddHours(-71).ToUnixTimeSeconds(), 3_600);
            await InsertRowsAsync("usage_day", 500, retentionNow.AddDays(-500).ToUnixTimeSeconds(), 86_400);
            await transaction.CommitAsync();
        }
        await store.CleanupRetentionAsync(retentionNow);
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            async Task<long> CountAsync(string table)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM {table};";
                return (long)(await command.ExecuteScalarAsync() ?? -1L);
            }
            Assert(await CountAsync("usage_minute") == 120, "retention removes 12k expired minute rows while preserving recent minute history");
            Assert(await CountAsync("usage_hour") == 72, "retention removes 4k expired hour rows while preserving recent hour history");
            Assert(await CountAsync("usage_day") == 500, "retention preserves long-term day rows during high-volume cleanup");
        }

        // A sustained crash storm must not grow restart state or spin forever. After the budget
        // is consumed, all further decisions stay in cooldown until the window expires.
        var stormOptions = new TunnelSupervisorOptions(
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(5),
            5,
            TimeSpan.FromMinutes(2),
            0);
        var stormPolicy = new TunnelRestartPolicy(stormOptions);
        var stormStarted = start;
        var cooldownDecisions = 0;
        for (var index = 0; index < 10_000; index++)
        {
            var decision = stormPolicy.Next(stormStarted, start.AddSeconds(1), () => 0.5);
            if (decision.IsCooldown) cooldownDecisions++;
        }
        Assert(stormPolicy.AttemptsInWindow == stormOptions.MaxRestartsInWindow && stormPolicy.ConsecutiveRestarts == stormOptions.MaxRestartsInWindow, "10k crash decisions remain bounded by restart budget state");
        Assert(cooldownDecisions == 10_000 - stormOptions.MaxRestartsInWindow, "restart storm enters cooldown instead of creating an unbounded retry loop");

        // End-to-end privacy check against actual OTLP protobuf: private tool arguments, file
        // content and raw chat handles must never be present in exported bytes.
        const string privatePathMarker = "PRIVATE_OTLP_PATH_7F1BC9";
        const string privateContentMarker = "PRIVATE_OTLP_CONTENT_0C42A1";
        await using var collector = new OtlpTestCollector();
        await using var hub = new ObservabilityHub(["D"], Path.Combine(root, "v11-hardening-otlp.sqlite3"), TimeSpan.FromHours(1));
        Assert(hub.ConfigureOtlp(new OtlpTelemetrySettings(true, collector.Endpoint)).Status == OtlpExporterRuntimeStatus.Configured, "hardening OTLP collector configures");
        var workspace = Path.Combine(root, "v11-hardening-otlp-workspace");
        Directory.CreateDirectory(workspace);
        var privateFile = privatePathMarker + ".txt";
        File.WriteAllText(Path.Combine(workspace, privateFile), privateContentMarker);
        var handle = hub.ChatCorrelation.Connect(nowUtc: start).ChatInstanceId;
        var hash = LogicalChatCorrelationService.HashForPersistence(handle);
        hub.Sessions.RegisterSession(hash, start);
        var port = FreePort();
        var auth = new string('v', 64);
        await using var server = new LocalMcpServer(
            (ushort)port,
            workspace,
            "",
            "",
            false,
            auth,
            _ => { },
            chatCorrelation: hub.ChatCorrelation,
            sessions: hub.Sessions,
            workspaceKey: "D",
            standardTelemetry: hub.StandardTelemetry);
        await server.StartAsync();
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 808,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "read_file",
                ["arguments"] = new JsonObject
                {
                    ["relative_path"] = privateFile,
                    ["_filemcp_chat"] = handle,
                },
            },
        }.ToJsonString();
        var response = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(auth), body);
        Assert(response.StartsWith("HTTP/1.1 200 OK", StringComparison.Ordinal) && HttpBody(response).Contains(privateContentMarker, StringComparison.Ordinal), "hardening privacy fixture exercises real private path/content through MCP response");
        _ = hub.ForceFlushOtlpForTests(2_000);
        Assert(await WaitUntilAsync(() => collector.Requests.Any(request => request.Path == "/v1/traces") && collector.Requests.Any(request => request.Path == "/v1/metrics"), TimeSpan.FromSeconds(3)), "hardening privacy fixture exports OTLP trace and metrics");
        var exportedBytes = collector.Requests.SelectMany(request => request.Body).ToArray();
        var exportedText = Encoding.UTF8.GetString(exportedBytes);
        Assert(!exportedText.Contains(privatePathMarker, StringComparison.Ordinal), "OTLP protobuf contains no private file path marker");
        Assert(!exportedText.Contains(privateContentMarker, StringComparison.Ordinal), "OTLP protobuf contains no private file content marker");
        Assert(!exportedText.Contains(handle, StringComparison.Ordinal), "OTLP protobuf contains no raw logical chat handle");

        Console.WriteLine("windows-v11-hardening: ok");
    }

    private static Task TestSettingsAndCredentialsAsync(string root)
    {
        var settingsDir = Path.Combine(root, "settings");
        var store = new SettingsStore(settingsDir);
        var settings = new FileMcpSettings
        {
            TunnelId = "tunnel_" + new string('a', 32), Profile = "windows-test", Port = 18080,
            AllowedDirectory = Path.Combine(root, "workspace"), HealthAddress = "127.0.0.1:0",
            GitUserName = "FileMCP Test", GitUserEmail = "filemcp@example.invalid", EnableCommands = true,
            OtlpEnabled = true, OtlpEndpoint = "http://127.0.0.1:4319",
        };
        store.Save(settings); var loaded = store.Load();
        Assert(loaded.TunnelId == settings.TunnelId && loaded.Profile == settings.Profile && loaded.EnableCommands, "settings roundtrip");
        Assert(loaded.OtlpEnabled && loaded.OtlpEndpoint == "http://127.0.0.1:4319", "settings roundtrip preserves optional OTLP configuration");
        Assert(loaded.Workspaces.Count == 4 && loaded.Workspaces.Select(item => item.Key).SequenceEqual(new[] { "C", "D", "E", "F" }), "multi-workspace defaults");
        Assert(loaded.Workspaces.Count(item => item.Enabled) == 1 && loaded.Workspaces.Any(item => item.Enabled && item.AllowedDirectory == settings.AllowedDirectory), "legacy workspace mapped to matching drive");
        Assert(loaded.Workspaces.Select(item => item.Port).Distinct().Count() == 4, "workspace ports unique");
        Assert(loaded.Workspaces.Select(item => item.Profile).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4, "workspace profiles unique");
        Assert(!File.ReadAllText(Path.Combine(settingsDir, "settings.json")).Contains("apiKey", StringComparison.OrdinalIgnoreCase), "settings contain no API key");

        var target = "FileMCP/tests/" + Guid.NewGuid().ToString("N");
        var credentials = new WindowsCredentialStore(target);
        try
        {
            credentials.SaveApiKey("sk-test-filemcp");
            Assert(credentials.HasSavedApiKey, "credential exists");
            Assert(credentials.ReadApiKey() == "sk-test-filemcp", "credential roundtrip");
            credentials.DeleteApiKey();
            Assert(!credentials.HasSavedApiKey, "credential delete");
        }
        finally { try { credentials.DeleteApiKey(); } catch { } }
        Console.WriteLine("windows-settings-credentials: ok");
        return Task.CompletedTask;
    }

    private static async Task TestDesktopSingleInstanceCoordinatorAsync()
    {
        var instanceKey = "filemcp-single-instance-test-" + Guid.NewGuid().ToString("N");
        using var primary = new DesktopSingleInstanceCoordinator(instanceKey);
        Assert(primary.IsPrimary, "desktop first instance becomes primary");

        var activationCount = 0;
        var firstActivation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartActivationListener(() =>
        {
            if (Interlocked.Increment(ref activationCount) == 1)
                firstActivation.TrySetResult(true);
        });

        using var secondary = new DesktopSingleInstanceCoordinator(instanceKey);
        Assert(!secondary.IsPrimary, "desktop second instance is secondary");
        Assert(await secondary.SignalPrimaryAsync(TimeSpan.FromSeconds(2)), "desktop secondary signals primary");
        await firstActivation.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(Volatile.Read(ref activationCount) == 1, "desktop primary receives activation signal");

        _ = await secondary.SendCommandForTestAsync("unknown\n", TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert(Volatile.Read(ref activationCount) == 1, "desktop unknown command does not activate");

        _ = await secondary.SendCommandForTestAsync(new string('x', 80) + "\n", TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert(Volatile.Read(ref activationCount) == 1, "desktop oversized command does not activate");

        Assert(await secondary.SignalPrimaryAsync(TimeSpan.FromSeconds(2)), "desktop repeated activation signal sent");
        Assert(await WaitUntilAsync(() => Volatile.Read(ref activationCount) >= 2, TimeSpan.FromSeconds(2)), "desktop repeated activation received");

        primary.Dispose();
        await Task.Delay(50);
        using var replacement = new DesktopSingleInstanceCoordinator(instanceKey);
        Assert(replacement.IsPrimary, "desktop primary ownership recovers after dispose");

        Console.WriteLine("windows-desktop-single-instance: ok");
    }

    private static async Task TestProcessRunnerAsync(string root)
    {
        var timeout = await ProcessRunner.RunAsync("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 10"], timeoutSeconds: 1);
        Assert(timeout.TimedOut, "process timeout");

        var bounded = await ProcessRunner.RunAsync("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('x' * 20000)"], timeoutSeconds: 10, outputLimitBytes: 10_000);
        Assert(bounded.Stdout.Contains("[...truncated ", StringComparison.Ordinal), "bounded process output");

        var pidFile = Path.Combine(root, "child.pid");
        var escaped = pidFile.Replace("'", "''", StringComparison.Ordinal);
        var script = "$p=Start-Process powershell.exe -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 20' -PassThru; Set-Content -LiteralPath '" + escaped + "' -Value $p.Id";
        var parent = await ProcessRunner.RunAsync("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script], timeoutSeconds: 5);
        Assert(parent.ExitCode == 0 && File.Exists(pidFile), "descendant fixture");
        await Task.Delay(500);
        var childPid = int.Parse(File.ReadAllText(pidFile).Trim());
        var childAlive = false;
        try { using var child = Process.GetProcessById(childPid); childAlive = !child.HasExited; } catch (ArgumentException) { }
        Assert(!childAlive, "job object descendant cleanup");
        Console.WriteLine("windows-process-runner: ok");
    }

    private static async Task TestFilesystemAndToolsAsync(string root)
    {
        var workspace = Path.Combine(root, "files"); Directory.CreateDirectory(workspace);
        var safe = new LocalTools(workspace, "FileMCP Test", "filemcp@example.invalid", false);
        var full = new LocalTools(workspace, "FileMCP Test", "filemcp@example.invalid", true);
        Assert(safe.ToolDefinitions.Count == 15 && !safe.HasTool("run_command"), "safe tool count");
        Assert(full.ToolDefinitions.Count == 16 && full.HasTool("run_command"), "full tool count");

        var volumeRoot = Path.GetPathRoot(workspace) ?? throw new Exception("Workspace volume root unavailable");
        var volumeSafe = new LocalTools(volumeRoot, "FileMCP Test", "filemcp@example.invalid", false);
        var volumeFull = new LocalTools(volumeRoot, "FileMCP Test", "filemcp@example.invalid", true);
        var volumeWorkspace = Path.GetRelativePath(volumeRoot, workspace).Replace('\\', '/');
        var volumeFile = volumeWorkspace + "/volume-root.txt";
        await volumeSafe.CallAsync("write_file", Obj(("relative_path", volumeFile), ("content", "volume-root-ok\n")));
        var volumeRead = await volumeSafe.CallAsync("read_file", Obj(("relative_path", volumeFile)));
        Assert(volumeRead.StructuredContent["result"]!.GetValue<string>().Contains("volume-root-ok"), "volume-root read/write descendant");
        var volumeList = await volumeSafe.CallAsync("list_files", Obj(("subpath", volumeWorkspace)));
        Assert(volumeList.StructuredContent["result"]!.AsArray().Any(n => n!.GetValue<string>() == "volume-root.txt"), "volume-root list descendant");
        var volumeSearch = await volumeSafe.CallAsync("search_content", Obj(("query", "volume-root-ok"), ("path", volumeWorkspace)));
        Assert(volumeSearch.StructuredContent["matches"]!.AsArray().Count == 1, "volume-root search descendant");
        var volumeCommand = await volumeFull.CallAsync("run_command", Obj(("command", "Write-Output volume-root-command-ok"), ("cwd", volumeWorkspace), ("timeout_seconds", 5)));
        Assert(volumeCommand.StructuredContent["result"]!.GetValue<string>().Contains("volume-root-command-ok"), "volume-root command cwd descendant");
        await AssertThrowsAsync(() => volumeSafe.CallAsync("delete_directory", Obj(("relative_path", ""))), "shared root directory", "volume-root deletion refused");
        await volumeSafe.CallAsync("delete_file", Obj(("relative_path", volumeFile)));

        await safe.CallAsync("write_file", Obj(("relative_path", "docs/note.txt"), ("content", "alpha\nbeta\ngamma\n")));
        var read = await safe.CallAsync("read_file", Obj(("relative_path", "docs/note.txt")));
        Assert(read.StructuredContent["result"]!.GetValue<string>().Contains("beta"), "read file");
        var range = await safe.CallAsync("read_file_range", Obj(("relative_path", "docs/note.txt"), ("start_line", 2), ("end_line", 3)));
        Assert(range.StructuredContent["content"]!.GetValue<string>() == "beta\ngamma", "read range");
        var search = await safe.CallAsync("search_content", Obj(("query", "beta"), ("path", "docs")));
        Assert(search.StructuredContent["matches"]!.AsArray().Count == 1, "search content");
        var names = await safe.CallAsync("search_filenames", Obj(("query", "note")));
        Assert(names.StructuredContent["result"]!.AsArray().Count == 1, "search filename");
        var list = await safe.CallAsync("list_files", Obj(("subpath", "docs")));
        Assert(list.StructuredContent["result"]!.AsArray().Any(n => n!.GetValue<string>() == "note.txt"), "list files");

        await full.CallAsync("run_command", Obj(("command", "Write-Output windows-command-ok"), ("cwd", ""), ("timeout_seconds", 5)));
        var command = await full.CallAsync("run_command", Obj(("command", "Write-Output windows-command-ok"), ("timeout_seconds", 5)));
        Assert(command.StructuredContent["result"]!.GetValue<string>().Contains("windows-command-ok"), "run command");

        await AssertThrowsAsync(() => safe.CallAsync("read_file", Obj(("relative_path", "..\\outside.txt"))), "outside the shared directory", "lexical traversal refused");

        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside-secret");
        var junction = Path.Combine(workspace, "escape");
        var mklink = await ProcessRunner.RunAsync("cmd.exe", ["/d", "/c", "mklink", "/J", junction, outside], timeoutSeconds: 5);
        Assert(mklink.ExitCode == 0, $"junction fixture: exit={mklink.ExitCode} stdout={mklink.Stdout} stderr={mklink.Stderr}");
        await AssertThrowsAsync(() => safe.CallAsync("read_file", Obj(("relative_path", "escape/secret.txt"))), "outside the shared directory", "junction read refused");
        await AssertThrowsAsync(() => safe.CallAsync("delete_directory", Obj(("relative_path", "escape"))), "Use delete_file", "junction directory delete refused");
        await safe.CallAsync("delete_file", Obj(("relative_path", "escape")));
        Assert(!Directory.Exists(junction) && File.Exists(Path.Combine(outside, "secret.txt")), "junction delete removes link only");
        await safe.CallAsync("delete_file", Obj(("relative_path", "docs/note.txt")));
        await safe.CallAsync("delete_directory", Obj(("relative_path", "docs")));
        Assert(!Directory.Exists(Path.Combine(workspace, "docs")), "delete directory");
        Console.WriteLine("windows-filesystem-tools: ok");
    }

    private static async Task TestGitSafetyAsync(string root)
    {
        var gitVersion = await ProcessRunner.RunAsync("git.exe", ["--version"], timeoutSeconds: 10);
        Assert(gitVersion.ExitCode == 0, "git available");
        var workspace = Path.Combine(root, "git"); Directory.CreateDirectory(workspace);
        var tools = new LocalTools(workspace, "FileMCP Test", "filemcp@example.invalid", false);
        await tools.CallAsync("git_init", Obj(("repo_path", "repo")));
        await tools.CallAsync("write_file", Obj(("relative_path", "repo/a.txt"), ("content", "one\n")));
        await tools.CallAsync("git_add", Obj(("repo_path", "repo"), ("paths", "a.txt")));
        await tools.CallAsync("git_commit", Obj(("repo_path", "repo"), ("message", "initial")));
        var log = await tools.CallAsync("git_log", Obj(("repo_path", "repo"), ("count", 5)));
        Assert(log.StructuredContent["result"]!.GetValue<string>().Contains("initial"), "git log");

        var repo = Path.Combine(workspace, "repo");
        var hooksDirectory = Path.Combine(repo, ".git", "hooks");
        Directory.CreateDirectory(hooksDirectory);
        var hook = Path.Combine(hooksDirectory, "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\ntouch hook-ran\n", new UTF8Encoding(false));
        File.AppendAllText(Path.Combine(repo, "a.txt"), "two\n");
        await tools.CallAsync("git_add", Obj(("repo_path", "repo"), ("paths", "a.txt")));
        await tools.CallAsync("git_commit", Obj(("repo_path", "repo"), ("message", "safe commit")));
        Assert(!File.Exists(Path.Combine(repo, "hook-ran")), "git hooks suppressed");

        await GitCli(repo, ["config", "filter.audit.clean", "cat"]);
        await GitCli(repo, ["config", "filter.audit.smudge", "cat"]);
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), "*.filter filter=audit\n");
        File.WriteAllText(Path.Combine(repo, "blocked.filter"), "filtered\n");
        await AssertThrowsAsync(() => tools.CallAsync("git_add", Obj(("repo_path", "repo"), ("paths", "blocked.filter"))), "content filter", "git filter refused");

        var include = Path.Combine(root, "outside-gitconfig"); File.WriteAllText(include, "[user]\nname = outside\n");
        await GitCli(repo, ["config", "include.path", include]);
        await AssertThrowsAsync(() => tools.CallAsync("git_status", Obj(("repo_path", "repo"))), "config includes", "git config include refused");
        await GitCli(repo, ["config", "--unset-all", "include.path"]);

        var linkedMain = Path.Combine(workspace, "linked-main");
        var linkedWorktree = Path.Combine(workspace, "linked-worktree");
        Directory.CreateDirectory(linkedMain);
        await GitCli(linkedMain, ["init", "-b", "main"]);
        File.WriteAllText(Path.Combine(linkedMain, "linked.txt"), "linked\n");
        await GitCli(linkedMain, ["add", "linked.txt"]);
        await GitCli(linkedMain, ["-c", "user.name=FileMCP Test", "-c", "user.email=filemcp@example.invalid", "commit", "-m", "linked initial"]);
        await GitCli(linkedMain, ["worktree", "add", "-b", "linked-branch", linkedWorktree]);
        var linkedStatus = await tools.CallAsync("git_status", Obj(("repo_path", "linked-worktree")));
        Assert(linkedStatus.StructuredContent["result"]!.GetValue<string>() == "(working tree clean)", "linked worktree inside shared root");

        var escapeRepo = Path.Combine(workspace, "worktree-escape");
        var outsideWorktree = Path.Combine(root, "outside-worktree");
        Directory.CreateDirectory(escapeRepo); Directory.CreateDirectory(outsideWorktree);
        await GitCli(escapeRepo, ["init", "-b", "main"]);
        await GitCli(escapeRepo, ["config", "core.worktree", outsideWorktree]);
        await AssertThrowsAsync(() => tools.CallAsync("git_status", Obj(("repo_path", "worktree-escape"))), "worktree is outside or different", "core.worktree escape refused");

        var bare = Path.Combine(root, "outside.git");
        var initBare = await ProcessRunner.RunAsync("git.exe", ["init", "--bare", bare], timeoutSeconds: 20); Assert(initBare.ExitCode == 0, "bare remote fixture");
        await GitCli(repo, ["remote", "add", "origin", bare]);
        await GitCli(repo, ["config", "branch.main.remote", "origin"]); await GitCli(repo, ["config", "branch.main.merge", "refs/heads/main"]);
        await AssertThrowsAsync(() => tools.CallAsync("git_push", Obj(("repo_path", "repo"))), "transport 'file' not allowed", "local file push refused");
        Console.WriteLine("windows-git-safe-mode: ok");
    }

    private static async Task TestHttpAndMcpAsync(string root)
    {
        var workspace = Path.Combine(root, "http"); Directory.CreateDirectory(workspace); File.WriteAllText(Path.Combine(workspace, "hello.txt"), "hello");
        var port = FreePort(); var token = new string('a', 64);
        await using var server = new LocalMcpServer((ushort)port, workspace, "", "", false, token, _ => { });
        await server.StartAsync();

        var fuzzIterations = int.TryParse(Environment.GetEnvironmentVariable("MCP_HTTP_FUZZ_ITERATIONS"), out var configuredFuzz) ? Math.Max(1, configuredFuzz) : 160;
        var random = new Random(0xF11E);
        for (var index = 0; index < fuzzIterations; index++)
        {
            var bytes = new byte[random.Next(0, 2048)];
            random.NextBytes(bytes);
            _ = server.ParseHttpRequest(bytes);
        }
        Assert(true, $"HTTP malformed fuzz ({fuzzIterations} iterations)");
        var authBeforeBodyRequest = Encoding.ASCII.GetBytes(
            $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/json\r\nContent-Length: 1000000\r\n\r\n");
        var authBeforeBody = server.ParseHttpRequest(authBeforeBodyRequest);
        Assert(authBeforeBody.Status == HttpParseStatus.Failure && authBeforeBody.FailureStatus == 401, "local auth rejected before request body");

        var unauthorized = await SendHttpAsync(port, "POST", "/mcp", new Dictionary<string, string> { ["Content-Type"] = "application/json" }, "");
        Assert(unauthorized.StartsWith("HTTP/1.1 401 Unauthorized", StringComparison.Ordinal), "local auth required");

        var discoveryPath = await SendHttpAsync(port, "GET", "/.well-known/oauth-protected-resource/mcp", new Dictionary<string, string>(), "");
        Assert(discoveryPath.StartsWith("HTTP/1.1 404 Not Found", StringComparison.Ordinal) && HttpBody(discoveryPath) == "Not found", "OAuth discovery path is public and not advertised");

        var discoveryRoot = await SendHttpAsync(port, "GET", "/.well-known/oauth-protected-resource", new Dictionary<string, string>(), "");
        Assert(discoveryRoot.StartsWith("HTTP/1.1 404 Not Found", StringComparison.Ordinal), "OAuth discovery root is public and not advertised");

        var unauthorizedUnknownPath = await SendHttpAsync(port, "GET", "/not-found", new Dictionary<string, string>(), "");
        Assert(unauthorizedUnknownPath.StartsWith("HTTP/1.1 401 Unauthorized", StringComparison.Ordinal), "unknown paths still require local auth");

        var unauthorizedDiscoveryPost = await SendHttpAsync(port, "POST", "/.well-known/oauth-protected-resource/mcp", new Dictionary<string, string>(), "");
        Assert(unauthorizedDiscoveryPost.StartsWith("HTTP/1.1 401 Unauthorized", StringComparison.Ordinal), "only GET OAuth discovery bypasses local auth");

        var legacy = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");
        var legacyBody = JsonNode.Parse(HttpBody(legacy))!.AsObject();
        var legacyTools = legacyBody["result"]!["tools"]!.AsArray();
        Assert(legacyTools.Count == 17, "legacy tools list");
        Assert(legacyTools.Any(t => t!["name"]!.GetValue<string>() == "list_codex_skills"), "legacy list_codex_skills exposed");
        Assert(legacyTools.Any(t => t!["name"]!.GetValue<string>() == "load_codex_skill"), "legacy load_codex_skill exposed");

        var skillDir = Path.Combine(workspace, ".agents", "skills", "speckit-analyze");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: speckit-analyze\ndescription: Analyze the current spec.\n---\n\n# Analyze\nFollow this skill exactly.\n", new UTF8Encoding(false));
        var loadSkill = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), "{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"tools/call\",\"params\":{\"name\":\"load_codex_skill\",\"arguments\":{\"name\":\"speckit-analyze\"}}}");
        var loadedSkillBody = JsonNode.Parse(HttpBody(loadSkill))!.AsObject();
        var loadedSkill = loadedSkillBody["result"]!["structuredContent"]!.AsObject();
        Assert(loadedSkill["name"]!.GetValue<string>() == "speckit-analyze", "load skill name");
        Assert(loadedSkill["instructions"]!.GetValue<string>().Contains("Follow this skill exactly.", StringComparison.Ordinal), "load skill instructions");

        var invalidSkill = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), "{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"tools/call\",\"params\":{\"name\":\"load_codex_skill\",\"arguments\":{\"name\":\"../escape\"}}}");
        var invalidSkillBody = JsonNode.Parse(HttpBody(invalidSkill))!.AsObject();
        Assert(invalidSkillBody["result"]!["isError"]!.GetValue<bool>(), "unsafe skill name refused");

        var modernHeaders = AuthHeaders(token); modernHeaders["MCP-Protocol-Version"] = FileMcpConstants.ModernProtocolVersion; modernHeaders["Mcp-Method"] = "tools/list";
        var modernBody = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "tools/list",
            ["params"] = new JsonObject { ["_meta"] = new JsonObject { ["io.modelcontextprotocol/protocolVersion"] = FileMcpConstants.ModernProtocolVersion, ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(), ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" } } },
        };
        var modern = await SendHttpAsync(port, "POST", "/mcp", modernHeaders, modernBody.ToJsonString());
        var modernJson = JsonNode.Parse(HttpBody(modern))!.AsObject();
        Assert(modernJson["result"]!["resultType"]!.GetValue<string>() == "complete", "modern result type");
        Assert(modernJson["result"]!["tools"]!.AsArray().Count == 17, "modern tools list");

        var meteredPort = FreePort();
        var meter = new WorkspaceUsageMeter("D");
        await using var meteredServer = new LocalMcpServer((ushort)meteredPort, workspace, "", "", false, token, _ => { }, meter);
        await meteredServer.StartAsync();
        var meteredBefore = meter.Snapshot();
        _ = await SendHttpAsync(meteredPort, "POST", "/mcp", new Dictionary<string, string> { ["Content-Type"] = "application/json" }, "");
        _ = await SendHttpAsync(meteredPort, "POST", "/mcp", AuthHeaders(token), "{");
        Assert(meter.Snapshot() == meteredBefore, "telemetry ignores unauthenticated and malformed MCP traffic");

        const string meteredListBody = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}";
        const string meteredReadBody = "{\"jsonrpc\":\"2.0\",\"id\":31,\"method\":\"tools/call\",\"params\":{\"name\":\"read_file\",\"arguments\":{\"relative_path\":\"hello.txt\"}}}";
        const string meteredUnknownBody = "{\"jsonrpc\":\"2.0\",\"id\":32,\"method\":\"tools/call\",\"params\":{\"name\":\"future_tool\",\"arguments\":{}}}";
        var meteredList = await SendHttpAsync(meteredPort, "POST", "/mcp", AuthHeaders(token), meteredListBody);
        var unmeteredRead = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), meteredReadBody);
        var meteredRead = await SendHttpAsync(meteredPort, "POST", "/mcp", AuthHeaders(token), meteredReadBody);
        var unmeteredUnknown = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), meteredUnknownBody);
        var meteredUnknown = await SendHttpAsync(meteredPort, "POST", "/mcp", AuthHeaders(token), meteredUnknownBody);
        Assert(HttpBody(meteredList) == HttpBody(legacy), "telemetry preserves tools/list JSON-RPC body");
        Assert(HttpBody(meteredRead) == HttpBody(unmeteredRead), "telemetry preserves successful tool response body");
        Assert(HttpBody(meteredUnknown) == HttpBody(unmeteredUnknown), "telemetry preserves tool error response body");

        var meteredSnapshot = meter.Snapshot();
        var requestBodies = new[] { meteredListBody, meteredReadBody, meteredUnknownBody };
        var responseBodies = new[] { HttpBody(meteredList), HttpBody(meteredRead), HttpBody(meteredUnknown) };
        var expectedRequestBytes = requestBodies.Sum(body => (long)Encoding.UTF8.GetByteCount(body));
        var expectedResponseBytes = responseBodies.Sum(body => (long)Encoding.UTF8.GetByteCount(body));
        var expectedTokensIn = requestBodies.Sum(body => McpTokenEstimator.EstimateFromUtf8Bytes(Encoding.UTF8.GetByteCount(body)));
        var expectedTokensOut = responseBodies.Sum(body => McpTokenEstimator.EstimateFromUtf8Bytes(Encoding.UTF8.GetByteCount(body)));
        Assert(meteredSnapshot.McpRequests == 3 && meteredSnapshot.ToolCalls == 2, "telemetry counts accepted MCP/tool calls");
        Assert(meteredSnapshot.ReadCalls == 1 && meteredSnapshot.OtherCalls == 1 && meteredSnapshot.Errors == 1, "telemetry classifies successful and unknown tool calls");
        Assert(meteredSnapshot.ExecutionTasks == 0, "telemetry does not mark read/unknown calls as execution tasks");
        Assert(meteredSnapshot.RequestBytes == expectedRequestBytes && meteredSnapshot.ResponseBytes == expectedResponseBytes, "telemetry counts MCP JSON payload bytes only");
        Assert(meteredSnapshot.TokensInEst == expectedTokensIn && meteredSnapshot.TokensOutEst == expectedTokensOut, "telemetry estimates each MCP payload independently");
        Assert(meteredSnapshot.TotalLatencyTicks > 0 && meteredSnapshot.MaxLatencyTicks > 0, "telemetry captures tool latency");
        var correlatedPort = FreePort();
        var correlatedMeter = new WorkspaceUsageMeter("D");
        var chatCorrelation = new LogicalChatCorrelationService();
        var serverSessions = new LogicalSessionRegistry();
        await using var correlatedServer = new LocalMcpServer((ushort)correlatedPort, workspace, "", "", false, token, _ => { }, correlatedMeter, chatCorrelation, serverSessions, "D");
        await correlatedServer.StartAsync();

        var correlatedList = await SendHttpAsync(correlatedPort, "POST", "/mcp", AuthHeaders(token), meteredListBody);
        var correlatedListJson = JsonNode.Parse(HttpBody(correlatedList))!.AsObject();
        var correlatedTools = correlatedListJson["result"]!["tools"]!.AsArray();
        Assert(correlatedTools.Count == 18, "logical correlation facade adds one connect tool");
        var connectDefinition = correlatedTools.Single(tool => tool!["name"]!.GetValue<string>() == "filemcp_observability_connect")!.AsObject();
        Assert(connectDefinition["annotations"]!["readOnlyHint"]!.GetValue<bool>(), "logical correlation connect tool is read-only metadata");
        var readDefinition = correlatedTools.Single(tool => tool!["name"]!.GetValue<string>() == "read_file")!.AsObject();
        Assert(readDefinition["inputSchema"]!["properties"]!["_filemcp_chat"]!["type"]!.GetValue<string>() == "string", "logical correlation metadata is exposed on normal tool facade schema");

        const string connectBody = "{\"jsonrpc\":\"2.0\",\"id\":40,\"method\":\"tools/call\",\"params\":{\"name\":\"filemcp_observability_connect\",\"arguments\":{}}}";
        var connectResponse = await SendHttpAsync(correlatedPort, "POST", "/mcp", AuthHeaders(token), connectBody);
        var connectJson = JsonNode.Parse(HttpBody(connectResponse))!.AsObject();
        Assert(!connectJson["result"]!["isError"]!.GetValue<bool>(), "logical correlation connect succeeds");
        var chatId = connectJson["result"]!["structuredContent"]!["chat_instance_id"]!.GetValue<string>();
        Assert(LogicalChatCorrelationService.IsValidHandle(chatId), "logical correlation connect returns valid handle");
        Assert(!connectJson["result"]!["structuredContent"]!["resumed"]!.GetValue<bool>(), "logical correlation first connect is new");

        var resumeBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 41,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "filemcp_observability_connect",
                ["arguments"] = new JsonObject { ["chat_instance_id"] = chatId },
            },
        }.ToJsonString();
        var resumeResponse = await SendHttpAsync(correlatedPort, "POST", "/mcp", AuthHeaders(token), resumeBody);
        var resumeJson = JsonNode.Parse(HttpBody(resumeResponse))!.AsObject();
        Assert(resumeJson["result"]!["structuredContent"]!["resumed"]!.GetValue<bool>(), "logical correlation resumes known handle");
        Assert(resumeJson["result"]!["structuredContent"]!["chat_instance_id"]!.GetValue<string>() == chatId, "logical correlation resume preserves handle");

        var boundReadBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 31,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "read_file",
                ["arguments"] = new JsonObject { ["relative_path"] = "hello.txt", ["_filemcp_chat"] = chatId },
            },
        }.ToJsonString();
        var boundRead = await SendHttpAsync(correlatedPort, "POST", "/mcp", AuthHeaders(token), boundReadBody);
        Assert(HttpBody(boundRead) == HttpBody(unmeteredRead), "logical correlation metadata is stripped before strict tool validation and preserves tool result");

        var foreignHandle = new LogicalChatCorrelationService().Connect().ChatInstanceId;
        var foreignReadBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 31,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "read_file",
                ["arguments"] = new JsonObject { ["relative_path"] = "hello.txt", ["_filemcp_chat"] = foreignHandle },
            },
        }.ToJsonString();
        var foreignRead = await SendHttpAsync(correlatedPort, "POST", "/mcp", AuthHeaders(token), foreignReadBody);
        Assert(HttpBody(foreignRead) == HttpBody(unmeteredRead), "unknown correlation handle remains unbound without changing tool behavior");

        var traversalBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 42,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "read_file",
                ["arguments"] = new JsonObject { ["relative_path"] = "../outside.txt", ["_filemcp_chat"] = chatId },
            },
        }.ToJsonString();
        var traversalResponse = await SendHttpAsync(correlatedPort, "POST", "/mcp", AuthHeaders(token), traversalBody);
        var traversalJson = JsonNode.Parse(HttpBody(traversalResponse))!.AsObject();
        Assert(traversalJson["result"]!["isError"]!.GetValue<bool>(), "logical correlation handle cannot bypass path containment");

        var commandBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 43,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "run_command",
                ["arguments"] = new JsonObject { ["command"] = "Write-Output should-not-run", ["_filemcp_chat"] = chatId },
            },
        }.ToJsonString();
        var commandResponse = await SendHttpAsync(correlatedPort, "POST", "/mcp", AuthHeaders(token), commandBody);
        var commandJson = JsonNode.Parse(HttpBody(commandResponse))!.AsObject();
        Assert(commandJson["error"]!["message"]!.GetValue<string>().Contains("Unknown tool: run_command", StringComparison.Ordinal), "logical correlation handle cannot enable disabled commands");

        var correlatedModernHeaders = AuthHeaders(token);
        correlatedModernHeaders["MCP-Protocol-Version"] = FileMcpConstants.ModernProtocolVersion;
        correlatedModernHeaders["Mcp-Method"] = "server/discover";
        var discoverBody = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 44,
            ["method"] = "server/discover",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject
                {
                    ["io.modelcontextprotocol/protocolVersion"] = FileMcpConstants.ModernProtocolVersion,
                    ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
                    ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
                },
            },
        }.ToJsonString();
        var discoverResponse = await SendHttpAsync(correlatedPort, "POST", "/mcp", correlatedModernHeaders, discoverBody);
        var discoverJson = JsonNode.Parse(HttpBody(discoverResponse))!.AsObject();
        Assert(discoverJson["result"]!["instructions"]!.GetValue<string>().Contains("filemcp_observability_connect", StringComparison.Ordinal), "modern discovery instructs logical correlation handshake");
        var liveBound = serverSessions.Snapshot(includeStale: true).Single();
        Assert(liveBound.Workspaces["D"].Usage.ToolCalls == 4, "server session attribution counts connect/resume/bound read/traversal");
        Assert(liveBound.Workspaces["D"].Usage.OtherCalls == 2 && liveBound.Workspaces["D"].Usage.ReadCalls == 2, "server session attribution classifies bound calls");
        Assert(liveBound.Workspaces["D"].Usage.Errors == 1, "server session attribution records bound tool error");
        var liveUnbound = serverSessions.UnboundSnapshot(includeStale: true).Single();
        Assert(liveUnbound.WorkspaceKey == "D" && liveUnbound.Usage.ToolCalls == 1 && liveUnbound.Usage.ReadCalls == 1, "server session attribution keeps foreign handle traffic unbound");
        var badHost = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), "", host: "evil.example");
        Assert(badHost.StartsWith("HTTP/1.1 403 Forbidden", StringComparison.Ordinal), "host validation");
        Console.WriteLine("windows-http-mcp: ok");
    }

    private static async Task TestHttpConnectionBoundsAsync(string root)
    {
        var workspace = Path.Combine(root, "http-bounds");
        Directory.CreateDirectory(workspace);
        var token = new string('b', 64);

        var saturationPort = FreePort();
        var saturationLimits = new LocalMcpServerLimits(2, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        await using (var saturationServer = new LocalMcpServer(
            (ushort)saturationPort, workspace, "", "", false, token, _ => { },
            null, null, null, null, null, saturationLimits))
        {
            await saturationServer.StartAsync();
            using var first = new TcpClient();
            using var second = new TcpClient();
            await first.ConnectAsync(IPAddress.Loopback, saturationPort);
            await second.ConnectAsync(IPAddress.Loopback, saturationPort);
            await first.GetStream().WriteAsync("G"u8.ToArray());
            await second.GetStream().WriteAsync("G"u8.ToArray());
            await Task.Delay(150);

            using var excess = new TcpClient();
            await excess.ConnectAsync(IPAddress.Loopback, saturationPort);
            Assert(await WaitForSocketCloseAsync(excess, TimeSpan.FromSeconds(1)), "HTTP connection cap rejects excess client");

            first.Dispose();
            await Task.Delay(150);
            var response = await SendHttpAsync(
                saturationPort,
                "POST",
                "/mcp",
                AuthHeaders(token),
                "{\"jsonrpc\":\"2.0\",\"id\":90,\"method\":\"tools/list\",\"params\":{}}");
            Assert(response.StartsWith("HTTP/1.1 200 OK", StringComparison.Ordinal), "HTTP connection slot is reusable after release");
        }

        var idlePort = FreePort();
        var idleLimits = new LocalMcpServerLimits(4, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
        await using (var idleServer = new LocalMcpServer(
            (ushort)idlePort, workspace, "", "", false, token, _ => { },
            null, null, null, null, null, idleLimits))
        {
            await idleServer.StartAsync();
            using var idle = new TcpClient();
            await idle.ConnectAsync(IPAddress.Loopback, idlePort);
            Assert(await WaitForSocketCloseAsync(idle, TimeSpan.FromSeconds(2)), "HTTP idle connection times out");
        }

        var tricklePort = FreePort();
        var trickleLimits = new LocalMcpServerLimits(4, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(650));
        await using (var trickleServer = new LocalMcpServer(
            (ushort)tricklePort, workspace, "", "", false, token, _ => { },
            null, null, null, null, null, trickleLimits))
        {
            await trickleServer.StartAsync();
            using var trickle = new TcpClient();
            await trickle.ConnectAsync(IPAddress.Loopback, tricklePort);
            var stream = trickle.GetStream();
            var writeFailed = false;
            for (var index = 0; index < 12; index++)
            {
                try { await stream.WriteAsync(new byte[] { (byte)'G' }); }
                catch (IOException) { writeFailed = true; break; }
                catch (SocketException) { writeFailed = true; break; }
                await Task.Delay(100);
            }
            var closed = writeFailed || await WaitForSocketCloseAsync(trickle, TimeSpan.FromSeconds(1));
            Assert(closed, "HTTP absolute header deadline stops trickle client");
        }

        var stopPort = FreePort();
        var stopLimits = new LocalMcpServerLimits(4, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        await using (var stopServer = new LocalMcpServer(
            (ushort)stopPort, workspace, "", "", false, token, _ => { },
            null, null, null, null, null, stopLimits))
        {
            await stopServer.StartAsync();
            using var blocked = new TcpClient();
            await blocked.ConnectAsync(IPAddress.Loopback, stopPort);
            await blocked.GetStream().WriteAsync("G"u8.ToArray());
            await Task.Delay(100);
            stopServer.Stop();
            Assert(await WaitForSocketCloseAsync(blocked, TimeSpan.FromSeconds(1)), "HTTP server stop cancels blocked read");
        }

        Console.WriteLine("windows-http-connection-bounds: ok");
    }

    private static void TestTunnelRestartPolicy()
    {
        var options = new TunnelSupervisorOptions(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(800),
            TimeSpan.FromSeconds(10),
            2,
            TimeSpan.FromSeconds(5),
            0);
        var policy = new TunnelRestartPolicy(options);
        var t0 = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);

        var first = policy.Next(t0, t0, () => 0.5);
        Assert(!first.IsCooldown && first.AttemptNumber == 1 && first.Delay == TimeSpan.FromMilliseconds(100), "tunnel supervisor first restart uses initial backoff");
        var second = policy.Next(t0.AddMilliseconds(100), t0.AddMilliseconds(200), () => 0.5);
        Assert(!second.IsCooldown && second.AttemptNumber == 2 && second.Delay == TimeSpan.FromMilliseconds(200), "tunnel supervisor backoff doubles for consecutive crash");
        var cooldown = policy.Next(t0.AddMilliseconds(200), t0.AddMilliseconds(300), () => 0.5);
        Assert(cooldown.IsCooldown && cooldown.Delay > TimeSpan.Zero && cooldown.ResumeAtUtc.HasValue, "tunnel supervisor enforces restart budget with cooldown");

        policy.Reset();
        _ = policy.Next(t0, t0, () => 0.5);
        var afterStable = policy.Next(t0, t0.AddSeconds(6), () => 0.5);
        Assert(!afterStable.IsCooldown && afterStable.AttemptNumber == 1 && afterStable.Delay == TimeSpan.FromMilliseconds(100), "tunnel supervisor stable run resets consecutive backoff and budget");

        var jitterPolicy = new TunnelRestartPolicy(options with { JitterRatio = 0.20 });
        var lowJitter = jitterPolicy.Next(t0, t0, () => 0);
        jitterPolicy.Reset();
        var highJitter = jitterPolicy.Next(t0, t0, () => 1);
        Assert(lowJitter.Delay == TimeSpan.FromMilliseconds(80) && highJitter.Delay == TimeSpan.FromMilliseconds(120), "tunnel supervisor jitter is bounded around base delay");
        Assert(!LocalMcpRuntime.TryParseProbeEndpoint("127.0.0.1:0", out _, out _), "dynamic configured health port requires resolved URL-file discovery");
        Assert(LocalMcpRuntime.TryParseProbeEndpoint("[::1]:12345", out var probeHost, out var probePort) && probeHost == "::1" && probePort == 12345, "fixed loopback tunnel health endpoint parses for probing");
        Console.WriteLine("windows-tunnel-restart-policy: ok");
    }
    private static async Task TestRuntimeAsync(string root)
    {
        var workspace = Path.Combine(root, "runtime-workspace"); Directory.CreateDirectory(workspace);
        var profiles = Path.Combine(root, "runtime-profiles"); var capture = Path.Combine(root, "tunnel-env.txt");
        var healthPort = FreePort();
        using var healthListener = new TcpListener(IPAddress.Loopback, healthPort);
        healthListener.Start();
        Environment.SetEnvironmentVariable("MCP_TUNNEL_CLIENT", Environment.ProcessPath);
        Environment.SetEnvironmentVariable("MCP_TEST_ENV_CAPTURE", capture);
        Environment.SetEnvironmentVariable("LOG_HTTP_RAW_UNSAFE", "true");
        Environment.SetEnvironmentVariable("MCP_SERVER_URL", "http://evil.invalid/mcp");
        await using var observability = new ObservabilityHub(["D"], Path.Combine(root, "runtime-observability.sqlite3"), TimeSpan.FromHours(1));
        await observability.StartAsync();
        var runtime = new LocalMcpRuntime("D", observability, profiles); var logs = new StringBuilder(); runtime.Log += text => logs.Append(text);
        try
        {
            Directory.CreateDirectory(profiles);
            var healthFixture = Path.Combine(root, "resolved-health-url.txt");
            File.WriteAllText(healthFixture, "http://127.0.0.1:32123\n");
            Assert(
                LocalMcpRuntime.TryReadResolvedHealthEndpoint(healthFixture, out var resolvedAddress, out var resolvedHost, out var resolvedPort) &&
                resolvedAddress == "127.0.0.1:32123" && resolvedHost == "127.0.0.1" && resolvedPort == 32123,
                "dynamic health URL parser accepts loopback HTTP endpoint");
            File.WriteAllText(healthFixture, "http://[::1]:32124/");
            Assert(
                LocalMcpRuntime.TryReadResolvedHealthEndpoint(healthFixture, out resolvedAddress, out resolvedHost, out resolvedPort) &&
                resolvedAddress == "[::1]:32124" && resolvedHost == "::1" && resolvedPort == 32124,
                "dynamic health URL parser accepts loopback IPv6 endpoint");
            foreach (var invalid in new[]
            {
                "https://127.0.0.1:32123",
                "http://10.10.10.10:32123",
                "http://user:pass@127.0.0.1:32123",
                "http://127.0.0.1:32123/healthz",
                "http://127.0.0.1:32123/?q=1",
                "http://127.0.0.1:32123/#fragment",
                "http://127.0.0.1",
            })
            {
                File.WriteAllText(healthFixture, invalid);
                Assert(!LocalMcpRuntime.TryReadResolvedHealthEndpoint(healthFixture, out _, out _, out _), "dynamic health URL parser rejects unsafe or ambiguous endpoint: " + invalid);
            }
            File.WriteAllBytes(healthFixture, new byte[513]);
            Assert(!LocalMcpRuntime.TryReadResolvedHealthEndpoint(healthFixture, out _, out _, out _), "dynamic health URL parser rejects oversized file");

            var config = new LocalMcpConfiguration("tunnel_" + new string('b', 32), "sk-runtime-test-secret", "runtime-test", (ushort)FreePort(), workspace, $"127.0.0.1:{healthPort}", "", "", false);
            await runtime.StartAsync(config);
            Assert(runtime.State.Status == LocalMcpRuntimeStatus.Running, "runtime running");
            await Task.Delay(10);
            var runningObservation = observability.Snapshot().Workspaces["D"];
            Assert(runningObservation.RuntimeRunning && runningObservation.RuntimeUptime > TimeSpan.Zero, "runtime reports connected uptime to observability hub");
            Assert(File.Exists(Path.Combine(profiles, "runtime-test.yaml")), "isolated profile created");
            var captureText = File.ReadAllText(capture);
            Assert(captureText.Contains("X-FileMCP-Local-Token: env:FILEMCP_LOCAL_AUTH_TOKEN"), "local auth header env indirection");
            Assert(!captureText.Contains("LOG_HTTP_RAW_UNSAFE=true", StringComparison.Ordinal) && !captureText.Contains("MCP_SERVER_URL=http://evil", StringComparison.Ordinal), "dangerous tunnel env not inherited");
            Assert(!logs.ToString().Contains("sk-runtime-test-secret", StringComparison.Ordinal), "api key redacted");
            Assert(!System.Text.RegularExpressions.Regex.IsMatch(logs.ToString(), "[0-9a-f]{64}"), "local token redacted");
            Assert(logs.ToString().Contains("[Skills]", StringComparison.Ordinal), "skill scan logged on connect");
            var runtimeHealth = await runtime.RefreshHealthAsync();
            Assert(runtimeHealth.LocalServerReady && runtimeHealth.TunnelProcessRunning, "runtime component health reports local MCP server and tunnel process ready");
            Assert(runtimeHealth.TunnelHealth == TunnelHealthProbeState.Reachable && runtimeHealth.LastTunnelHealthSuccessUtc.HasValue, "runtime fixed loopback tunnel health endpoint is reachable");
            healthListener.Stop();
            runtimeHealth = await runtime.RefreshHealthAsync();
            Assert(runtimeHealth.TunnelHealth == TunnelHealthProbeState.Unreachable && runtimeHealth.LastTunnelHealthSuccessUtc.HasValue, "runtime health records tunnel-health failure while preserving last success");
            await runtime.StopAsync();
            Assert(runtime.State.Status == LocalMcpRuntimeStatus.Stopped, "runtime stopped");
            Assert(!runtime.HealthSnapshot.LocalServerReady && !runtime.HealthSnapshot.TunnelProcessRunning, "runtime component health clears server/process readiness after stop");
            var stoppedObservation = observability.Snapshot().Workspaces["D"];
            Assert(!stoppedObservation.RuntimeRunning && stoppedObservation.RuntimeUptime == TimeSpan.Zero, "runtime clears connected uptime when stopped");

            var dynamicHealthPort = FreePort();
            using var dynamicHealthListener = new TcpListener(IPAddress.Loopback, dynamicHealthPort);
            dynamicHealthListener.Start();
            var dynamicProfile = "runtime-dynamic-health";
            var dynamicHealthUrlFile = Path.Combine(profiles, dynamicProfile + ".health-url");
            File.WriteAllText(dynamicHealthUrlFile, "STALE-HEALTH-URL");
            Environment.SetEnvironmentVariable("MCP_TEST_HEALTH_URL", $"http://127.0.0.1:{dynamicHealthPort}");
            Environment.SetEnvironmentVariable("MCP_TEST_HEALTH_REQUIRE_FRESH", "1");
            await using (var dynamicRuntime = new LocalMcpRuntime("D", observability, profiles))
            {
                var dynamicConfig = new LocalMcpConfiguration(
                    "tunnel_" + new string('h', 32),
                    "test-key",
                    dynamicProfile,
                    (ushort)FreePort(),
                    workspace,
                    "127.0.0.1:0",
                    "",
                    "",
                    false);
                await dynamicRuntime.StartAsync(dynamicConfig);
                Assert(dynamicRuntime.State.Status == LocalMcpRuntimeStatus.Running, "dynamic-health runtime running");
                Assert(await WaitUntilAsync(() => File.Exists(dynamicHealthUrlFile), TimeSpan.FromSeconds(2)), "tunnel run writes resolved health URL file");
                Assert(File.ReadAllText(dynamicHealthUrlFile).Trim() == $"http://127.0.0.1:{dynamicHealthPort}", "stale health URL file is replaced by current tunnel launch");
                var dynamicHealth = await dynamicRuntime.RefreshHealthAsync();
                Assert(dynamicHealth.TunnelHealth == TunnelHealthProbeState.Reachable && dynamicHealth.LastTunnelHealthSuccessUtc.HasValue, "dynamic :0 tunnel health resolves and probes reachable endpoint");
                dynamicHealthListener.Stop();
                dynamicHealth = await dynamicRuntime.RefreshHealthAsync();
                Assert(dynamicHealth.TunnelHealth == TunnelHealthProbeState.Unreachable && dynamicHealth.LastTunnelHealthSuccessUtc.HasValue, "dynamic health preserves last success after endpoint becomes unreachable");
                await dynamicRuntime.StopAsync();
                Assert(!File.Exists(dynamicHealthUrlFile), "dynamic health URL file is removed on runtime stop");
            }

            Environment.SetEnvironmentVariable("MCP_TEST_HEALTH_URL", "http://127.0.0.1:65534");
            Environment.SetEnvironmentVariable("MCP_TEST_HEALTH_REQUIRE_FRESH", "1");
            var restartCounter = Path.Combine(root, "runtime-restart-counter.txt");
            Environment.SetEnvironmentVariable("MCP_TEST_TUNNEL_RUN_COUNTER", restartCounter);
            Environment.SetEnvironmentVariable("MCP_TEST_TUNNEL_FAIL_RUNS", "2");
            var fastSupervisor = new TunnelSupervisorOptions(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(500),
                5,
                TimeSpan.FromMilliseconds(200),
                0);
            var restartLogs = new StringBuilder();
            await using (var restartingRuntime = new LocalMcpRuntime("D", observability, profiles, fastSupervisor, jitter: () => 0.5))
            {
                restartingRuntime.Log += text => restartLogs.Append(text);
                var restartConfig = new LocalMcpConfiguration("tunnel_" + new string('c', 32), "sk-runtime-restart-secret", "runtime-restart-test", (ushort)FreePort(), workspace, "127.0.0.1:0", "", "", false);
                await restartingRuntime.StartAsync(restartConfig);
                // Process startup can exceed three seconds on a loaded Windows host even when the bounded restart policy is healthy.
                Assert(await WaitUntilAsync(() => ReadCounter(restartCounter) >= 3 && restartingRuntime.State.Status == LocalMcpRuntimeStatus.Running, TimeSpan.FromSeconds(8)), "runtime auto-restarts crashed tunnel until it stays running");
                var supervisor = restartingRuntime.SupervisorSnapshot;
                Assert(supervisor.TotalRestarts >= 2 && supervisor.LastExitCode == 23 && !supervisor.RestartPending, "runtime supervisor records real restart evidence");
                Assert(restartLogs.ToString().Contains("Tunnel restart succeeded", StringComparison.Ordinal), "runtime logs successful supervised restart");
                await restartingRuntime.StopAsync();
                Assert(restartingRuntime.State.Status == LocalMcpRuntimeStatus.Stopped, "runtime supervised tunnel stops explicitly");
            }

            var cancelCounter = Path.Combine(root, "runtime-restart-cancel-counter.txt");
            Environment.SetEnvironmentVariable("MCP_TEST_TUNNEL_RUN_COUNTER", cancelCounter);
            Environment.SetEnvironmentVariable("MCP_TEST_TUNNEL_FAIL_RUNS", "100");
            var slowSupervisor = new TunnelSupervisorOptions(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30),
                5,
                TimeSpan.FromMinutes(1),
                0);
                        await using (var pendingRuntime = new LocalMcpRuntime("D", observability, profiles, slowSupervisor, jitter: () => 0.5))
            {
                var pendingConfig = new LocalMcpConfiguration("tunnel_" + new string('d', 32), "sk-runtime-cancel-secret", "runtime-cancel-test", (ushort)FreePort(), workspace, "127.0.0.1:0", "", "", false);
                await pendingRuntime.StartAsync(pendingConfig);
                Assert(await WaitUntilAsync(() => pendingRuntime.State.Status == LocalMcpRuntimeStatus.Restarting, TimeSpan.FromSeconds(2)), "runtime enters restarting state after unexpected tunnel exit");
                var restartingHealth = pendingRuntime.HealthSnapshot;
                Assert(restartingHealth.LocalServerReady && !restartingHealth.TunnelProcessRunning && restartingHealth.RestartPending, "runtime health keeps local MCP ready while tunnel restart is pending");
                var stopTimer = Stopwatch.StartNew();
                await pendingRuntime.StopAsync();
                stopTimer.Stop();
                Assert(stopTimer.Elapsed < TimeSpan.FromSeconds(1), "user stop cancels pending tunnel backoff immediately");
                Assert(pendingRuntime.State.Status == LocalMcpRuntimeStatus.Stopped && !pendingRuntime.SupervisorSnapshot.RestartPending, "user stop leaves no pending tunnel restart");
            }
            var cooldownCounter = Path.Combine(root, "runtime-restart-cooldown-counter.txt");
            Environment.SetEnvironmentVariable("MCP_TEST_TUNNEL_RUN_COUNTER", cooldownCounter);
            Environment.SetEnvironmentVariable("MCP_TEST_TUNNEL_FAIL_RUNS", "100");
            var cooldownSupervisor = new TunnelSupervisorOptions(
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(30),
                1,
                TimeSpan.FromMinutes(1),
                0);
            static async Task ControlledRestartDelay(TimeSpan delay, CancellationToken token)
            {
                if (delay >= TimeSpan.FromSeconds(1))
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            await using (var cooldownRuntime = new LocalMcpRuntime("D", observability, profiles, cooldownSupervisor, ControlledRestartDelay, jitter: () => 0.5))
            {
                var cooldownConfig = new LocalMcpConfiguration("tunnel_" + new string('e', 32), "sk-runtime-cooldown-secret", "runtime-cooldown-test", (ushort)FreePort(), workspace, "127.0.0.1:0", "", "", false);
                await cooldownRuntime.StartAsync(cooldownConfig);
                Assert(await WaitUntilAsync(() => cooldownRuntime.State.Status == LocalMcpRuntimeStatus.Cooldown, TimeSpan.FromSeconds(2)), "runtime enters cooldown after restart budget is exhausted");
                var cooldownHealth = cooldownRuntime.HealthSnapshot;
                Assert(cooldownHealth.LocalServerReady && !cooldownHealth.TunnelProcessRunning && cooldownHealth.RestartPending, "runtime health exposes local-server availability during tunnel cooldown");
                var cooldownSnapshot = cooldownRuntime.SupervisorSnapshot;
                Assert(cooldownSnapshot.CooldownEntries >= 1 && cooldownSnapshot.RestartPending && cooldownSnapshot.NextRestartUtc.HasValue, "runtime cooldown exposes pending resume evidence");
                var cooldownStopTimer = Stopwatch.StartNew();
                await cooldownRuntime.StopAsync();
                cooldownStopTimer.Stop();
                Assert(cooldownStopTimer.Elapsed < TimeSpan.FromSeconds(1), "user stop cancels cooldown wait immediately");
                Assert(cooldownRuntime.State.Status == LocalMcpRuntimeStatus.Stopped && !cooldownRuntime.SupervisorSnapshot.RestartPending, "runtime cooldown cancellation leaves no restart pending");
            }
        }
        finally
        {
            await runtime.DisposeAsync();
            Environment.SetEnvironmentVariable("MCP_TUNNEL_CLIENT", null); Environment.SetEnvironmentVariable("MCP_TEST_ENV_CAPTURE", null);
            Environment.SetEnvironmentVariable("LOG_HTTP_RAW_UNSAFE", null); Environment.SetEnvironmentVariable("MCP_SERVER_URL", null);
            Environment.SetEnvironmentVariable("MCP_TEST_TUNNEL_RUN_COUNTER", null); Environment.SetEnvironmentVariable("MCP_TEST_TUNNEL_FAIL_RUNS", null);
            Environment.SetEnvironmentVariable("MCP_TEST_HEALTH_URL", null); Environment.SetEnvironmentVariable("MCP_TEST_HEALTH_REQUIRE_FRESH", null);
        }
        Console.WriteLine("windows-runtime-lifecycle: ok");
    }

    private static async Task<int> RunFakeTunnelClientAsync(string[] args)
    {
        var capture = Environment.GetEnvironmentVariable("MCP_TEST_ENV_CAPTURE");
        if (!string.IsNullOrEmpty(capture))
        {
            var text = string.Join('\n', new[]
            {
                "MCP_EXTRA_HEADERS=" + Environment.GetEnvironmentVariable("MCP_EXTRA_HEADERS"),
                "MCP_DISCOVERY_EXTRA_HEADERS=" + Environment.GetEnvironmentVariable("MCP_DISCOVERY_EXTRA_HEADERS"),
                "FILEMCP_LOCAL_AUTH_TOKEN=" + Environment.GetEnvironmentVariable("FILEMCP_LOCAL_AUTH_TOKEN"),
                "LOG_HTTP_RAW_UNSAFE=" + Environment.GetEnvironmentVariable("LOG_HTTP_RAW_UNSAFE"),
                "MCP_SERVER_URL=" + Environment.GetEnvironmentVariable("MCP_SERVER_URL"),
            });
            File.WriteAllText(capture, text);
        }
        var token = Environment.GetEnvironmentVariable("FILEMCP_LOCAL_AUTH_TOKEN") ?? ""; var apiKey = Environment.GetEnvironmentVariable("CONTROL_PLANE_API_KEY") ?? "";
        if (args[0] == "init")
        {
            var profile = ValueAfter(args, "--profile"); var profileDir = ValueAfter(args, "--profile-dir");
            Directory.CreateDirectory(profileDir); File.WriteAllText(Path.Combine(profileDir, profile + ".yaml"), "control_plane:\n  api_key: env:CONTROL_PLANE_API_KEY\n");
            Console.WriteLine("init-ok " + apiKey + " " + token); return 0;
        }
        if (args[0] == "doctor") { Console.WriteLine("doctor-ok " + apiKey + " " + token); return 0; }

        var healthUrlIndex = Array.IndexOf(args, "--health.url-file");
        if (healthUrlIndex >= 0)
        {
            if (healthUrlIndex + 1 >= args.Length) return 92;
            var healthUrlFile = args[healthUrlIndex + 1];
            if (Environment.GetEnvironmentVariable("MCP_TEST_HEALTH_REQUIRE_FRESH") == "1" && File.Exists(healthUrlFile))
            {
                Console.Error.WriteLine("stale-health-url-file");
                return 91;
            }
            var healthUrl = Environment.GetEnvironmentVariable("MCP_TEST_HEALTH_URL");
            if (!string.IsNullOrWhiteSpace(healthUrl))
            {
                var parent = Path.GetDirectoryName(healthUrlFile);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                File.WriteAllText(healthUrlFile, healthUrl);
            }
        }

        var counterPath = Environment.GetEnvironmentVariable("MCP_TEST_TUNNEL_RUN_COUNTER");
        if (!string.IsNullOrWhiteSpace(counterPath))
        {
            var count = 0;
            if (File.Exists(counterPath)) int.TryParse(File.ReadAllText(counterPath), out count);
            count++;
            File.WriteAllText(counterPath, count.ToString());
            _ = int.TryParse(Environment.GetEnvironmentVariable("MCP_TEST_TUNNEL_FAIL_RUNS"), out var failRuns);
            if (count <= failRuns)
            {
                Console.Error.WriteLine($"run-crash-{count}");
                return 23;
            }
        }
        Console.WriteLine("run-ok " + apiKey + " " + token);
        await Task.Delay(Timeout.InfiniteTimeSpan); return 0;
    }

    private static int ReadCounter(string path)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            return int.TryParse(File.ReadAllText(path), out var value) ? value : 0;
        }
        catch (IOException)
        {
            // The fake tunnel process may hold the counter file exclusively for a few
            // milliseconds while rewriting it. Treat that transient state as
            // "not updated yet" so WaitUntilAsync retries instead of flaking.
            return 0;
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            if (predicate()) return true;
            await Task.Delay(10);
        }
        return predicate();
    }
    private static string ValueAfter(string[] args, string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new InvalidOperationException("missing " + key); }
    private static async Task GitCli(string repo, string[] args) { var all = new List<string> { "-C", repo }; all.AddRange(args); var result = await ProcessRunner.RunAsync("git.exe", all, timeoutSeconds: 20); if (result.ExitCode != 0) throw new Exception("git fixture failed: " + result.Stderr); }
    private static JsonObject Obj(params (string Key, object Value)[] values) { var obj = new JsonObject(); foreach (var (key, value) in values) obj[key] = JsonValue.Create(value); return obj; }
    private static void Assert(bool condition, string message) { _assertions++; if (!condition) throw new Exception("Assertion failed: " + message); }
    private static async Task AssertThrowsAsync(Func<Task> action, string contains, string message) { try { await action(); } catch (Exception ex) when (ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) { Assert(true, message); return; } throw new Exception("Assertion failed: " + message); }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static Dictionary<string, string> AuthHeaders(string token) => new(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "application/json", [FileMcpConstants.LocalAuthHeaderName] = token };
    private static async Task<string> SendHttpAsync(int port, string method, string path, Dictionary<string, string> headers, string body, string? host = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body); using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port); var stream = client.GetStream();
        var builder = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: {host ?? $"127.0.0.1:{port}"}\r\n"); foreach (var pair in headers) builder.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n"); builder.Append("Content-Length: ").Append(bytes.Length).Append("\r\n\r\n");
        var head = Encoding.UTF8.GetBytes(builder.ToString()); await stream.WriteAsync(head); await stream.WriteAsync(bytes); client.Client.Shutdown(SocketShutdown.Send);
        using var memory = new MemoryStream(); var buffer = new byte[8192]; int read; while ((read = await stream.ReadAsync(buffer)) > 0) memory.Write(buffer, 0, read); return Encoding.UTF8.GetString(memory.ToArray());
    }
    private static async Task<bool> WaitForSocketCloseAsync(TcpClient client, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[1];
        try
        {
            var read = await client.GetStream().ReadAsync(buffer, cts.Token);
            return read == 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (SocketException)
        {
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
    private static string HttpBody(string response) { var index = response.IndexOf("\r\n\r\n", StringComparison.Ordinal); return index >= 0 ? response[(index + 4)..] : response; }
}
