using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace FileMCP.Core;

public enum McpTraceContextSource
{
    None,
    McpMeta,
    Http,
}

public readonly record struct McpExtractedTraceContext(
    ActivityContext Parent,
    McpTraceContextSource Source,
    string? TraceState);

public static class McpTraceContextAdapter
{
    public const int MaxTraceParentUtf8Bytes = 128;
    public const int MaxTraceStateUtf8Bytes = 512;

    public static bool TryExtract(
        JsonObject? meta,
        IReadOnlyDictionary<string, string> headers,
        out McpExtractedTraceContext extracted)
    {
        if (TryReadMeta(meta, out var metaTraceParent, out var metaTraceState) &&
            TryParse(metaTraceParent, metaTraceState, McpTraceContextSource.McpMeta, out extracted))
            return true;

        var headerTraceParent = headers.GetValueOrDefault("traceparent");
        var headerTraceState = headers.GetValueOrDefault("tracestate");
        if (TryParse(headerTraceParent, headerTraceState, McpTraceContextSource.Http, out extracted))
            return true;

        extracted = default;
        return false;
    }

    private static bool TryReadMeta(JsonObject? meta, out string? traceParent, out string? traceState)
    {
        traceParent = TryReadString(meta, "traceparent");
        traceState = TryReadString(meta, "tracestate");
        return !string.IsNullOrWhiteSpace(traceParent);
    }

    private static string? TryReadString(JsonObject? obj, string key)
    {
        if (obj?[key] is not JsonValue value || !value.TryGetValue<string>(out var text)) return null;
        return text;
    }

    private static bool TryParse(
        string? traceParent,
        string? traceState,
        McpTraceContextSource source,
        out McpExtractedTraceContext extracted)
    {
        extracted = default;
        if (string.IsNullOrWhiteSpace(traceParent)) return false;
        var parent = traceParent.Trim();
        if (Encoding.UTF8.GetByteCount(parent) > MaxTraceParentUtf8Bytes) return false;

        string? state = null;
        if (!string.IsNullOrWhiteSpace(traceState))
        {
            var trimmedState = traceState.Trim();
            if (Encoding.UTF8.GetByteCount(trimmedState) <= MaxTraceStateUtf8Bytes)
                state = trimmedState;
        }

        if (!ActivityContext.TryParse(parent, state, isRemote: true, out var context)) return false;
        extracted = new McpExtractedTraceContext(context, source, state);
        return true;
    }
}
