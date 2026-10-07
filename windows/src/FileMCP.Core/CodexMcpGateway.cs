using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FileMCP.Core;

internal sealed class CodexMcpGateway : IDisposable
{
    private readonly bool _enabled;
    private readonly string _executable;
    private readonly HashSet<string> _allowlist;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private Dictionary<string, ServerConfig> _servers = [];
    private readonly Dictionary<string, Session> _sessions = [];

    public CodexMcpGateway(bool enabled, string executable, IEnumerable<string> allowlist, Action<string> log)
    { _enabled = enabled; _executable = executable; _allowlist = allowlist.ToHashSet(StringComparer.Ordinal); _log = log; }

    public JsonArray ToolDefinitions => !_enabled ? [] : new JsonArray(
        Def("list_codex_mcp_servers", "List allowlisted MCP servers from the effective Codex configuration.", new(), [] , true),
        Def("list_codex_mcp_tools", "List tools exposed by one allowlisted Codex MCP server.", new() { ["server"] = Str("Exact server name.") }, ["server"], true),
        Def("call_codex_mcp_tool", "Call one tool on an allowlisted Codex MCP server. The downstream operation may be destructive.", new() { ["server"] = Str("Exact server name."), ["tool"] = Str("Exact downstream tool name."), ["arguments"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true } }, ["server", "tool"], false));

    public bool HasTool(string name) => _enabled && (name is "list_codex_mcp_servers" or "list_codex_mcp_tools" or "call_codex_mcp_tool");

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!_enabled) return;
        try
        {
            var codex = ResolveCodex();
            var result = await ProcessRunner.RunAsync(codex, ["mcp", "list", "--json"], timeoutSeconds: 15, outputLimitBytes: 2_000_000, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0) throw new FileMcpException("Could not read Codex MCP configuration.");
            var rows = JsonNode.Parse(result.Stdout)?.AsArray() ?? throw new FileMcpException("Unsupported Codex MCP configuration output.");
            var found = new Dictionary<string, ServerConfig>(StringComparer.Ordinal);
            foreach (var node in rows.OfType<JsonObject>())
            {
                var name = node["name"]?.GetValue<string>(); var transport = node["transport"] as JsonObject;
                if (node["enabled"]?.GetValue<bool>() != true || name is null || !_allowlist.Contains(name) || transport?["type"]?.GetValue<string>() != "stdio") continue;
                var command = transport["command"]?.GetValue<string>(); if (command is null) continue;
                var args = transport["args"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [];
                var env = transport["env"] is JsonObject envNode ? envNode.ToDictionary(x => x.Key, x => x.Value?.GetValue<string>() ?? "") : [];
                found[name] = new(name, command, args, transport["cwd"]?.GetValue<string>(), env,
                    (int)(node["startup_timeout_sec"]?.GetValue<double>() ?? 15), (int)(node["tool_timeout_sec"]?.GetValue<double>() ?? 60));
            }
            lock (_gate) _servers = found;
            _log($"[Codex MCP] Loaded {found.Count} allowlisted server(s).\n");
        }
        catch (Exception ex) { _log("[Codex MCP] WARNING: " + ex.Message + "\n"); }
    }

    public async Task<ToolCallOutput> CallAsync(string name, JsonObject args, CancellationToken cancellationToken)
    {
        if (name == "list_codex_mcp_servers")
        {
            JsonArray items; lock (_gate) items = new(_servers.Keys.Order().Select(x => (JsonNode?)new JsonObject { ["name"] = x, ["transport"] = "stdio", ["available"] = true }).ToArray());
            return Output(new JsonObject { ["servers"] = items, ["count"] = items.Count });
        }
        var server = Required(args, "server"); var session = GetSession(server);
        if (name == "list_codex_mcp_tools") { var listed = await session.RequestAsync("tools/list", new(), cancellationToken); return Output(new JsonObject { ["server"] = server, ["tools"] = listed["tools"]?.DeepClone() ?? new JsonArray() }); }
        if (name != "call_codex_mcp_tool") throw new FileMcpException("Unknown Codex MCP gateway tool.");
        var tool = Required(args, "tool"); var tools = (await session.RequestAsync("tools/list", new(), cancellationToken))["tools"]?.AsArray();
        if (tools?.Any(x => x?["name"]?.GetValue<string>() == tool) != true) throw new FileMcpException($"Unknown tool {tool} on Codex MCP {server}.");
        var result = await session.RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args["arguments"]?.DeepClone() ?? new JsonObject() }, cancellationToken);
        return new((result["content"] as JsonArray)?.DeepClone().AsArray() ?? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Downstream MCP returned no content" }),
            (result["structuredContent"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject { ["server"] = server, ["tool"] = tool, ["isError"] = result["isError"]?.GetValue<bool>() ?? false });
    }

    public void Dispose() { lock (_gate) { foreach (var value in _sessions.Values) value.Dispose(); _sessions.Clear(); _servers.Clear(); } }
    private Session GetSession(string name) { lock (_gate) { if (!_servers.TryGetValue(name, out var config)) throw new FileMcpException("Codex MCP server is not allowlisted or available: " + name); return _sessions.TryGetValue(name, out var value) ? value : _sessions[name] = new Session(config, _log); } }
    private string ResolveCodex() { var candidates = string.IsNullOrWhiteSpace(_executable) ? new[] { "codex.exe", "codex" } : [_executable]; return candidates.FirstOrDefault(x => Path.IsPathRooted(x) ? File.Exists(x) : true) ?? throw new FileMcpException("Codex executable was not found. Select it in Settings or disable Codex MCP federation."); }
    private static string Required(JsonObject args, string key) => args[key]?.GetValue<string>() is { Length: > 0 } value ? value : throw new FileMcpException("Missing or invalid argument: " + key);
    private static ToolCallOutput Output(JsonObject value) => new(new JsonArray(new JsonObject { ["type"] = "text", ["text"] = value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) }), value);
    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Def(string name, string description, JsonObject properties, string[] required, bool readOnly) => new() { ["name"] = name, ["description"] = description, ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(required.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["additionalProperties"] = false }, ["outputSchema"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true }, ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly, ["destructiveHint"] = !readOnly, ["openWorldHint"] = true } };
    private sealed record ServerConfig(string Name, string Command, string[] Args, string? Cwd, Dictionary<string,string> Env, int StartupTimeout, int ToolTimeout);

    private sealed class Session(ServerConfig config, Action<string> log) : IDisposable
    {
        private readonly SemaphoreSlim _serial = new(1, 1); private Process? _process; private StreamWriter? _input; private StreamReader? _output; private int _id;
        public async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
        {
            await _serial.WaitAsync(ct); try { await StartAsync(ct); var id = ++_id; await _input!.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString()); await _input.FlushAsync(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(config.ToolTimeout)); while (await _output!.ReadLineAsync(timeout.Token) is { } line) { if (line.Length > 8_000_000) throw new FileMcpException("Downstream MCP response exceeded 8 MB."); var message = JsonNode.Parse(line)?.AsObject(); if (message?["id"]?.GetValue<int>() != id) continue; if (message["error"] is JsonObject error) throw new FileMcpException(error["message"]?.GetValue<string>() ?? "Downstream MCP error"); return message["result"]?.AsObject() ?? throw new FileMcpException("Malformed downstream MCP response"); } throw new FileMcpException("Downstream MCP closed stdout"); } finally { _serial.Release(); }
        }
        private async Task StartAsync(CancellationToken ct) { if (_process?.HasExited == false) return; var info = new ProcessStartInfo(config.Command) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = config.Cwd ?? "" }; foreach (var arg in config.Args) info.ArgumentList.Add(arg); foreach (var item in config.Env) info.Environment[item.Key] = item.Value; _process = Process.Start(info) ?? throw new FileMcpException("Could not start Codex MCP " + config.Name); _process.ErrorDataReceived += (_, _) => { }; _process.BeginErrorReadLine(); _input = _process.StandardInput; _output = _process.StandardOutput; using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct); startup.CancelAfter(TimeSpan.FromSeconds(config.StartupTimeout)); var result = await RawRequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "filemcp", ["version"] = "0.4.0" } }, startup.Token); _ = result; await _input.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized", ["params"] = new JsonObject() }.ToJsonString()); await _input.FlushAsync(); log("[Codex MCP] Connected: " + config.Name + "\n"); }
        private async Task<JsonObject> RawRequestAsync(string method, JsonObject p, CancellationToken ct) { var id = ++_id; await _input!.WriteLineAsync(new JsonObject { ["jsonrpc"]="2.0", ["id"]=id, ["method"]=method, ["params"]=p }.ToJsonString()); await _input.FlushAsync(); while (await _output!.ReadLineAsync(ct) is { } line) { var m=JsonNode.Parse(line)?.AsObject(); if(m?["id"]?.GetValue<int>()==id) return m["result"]?.AsObject() ?? throw new FileMcpException("MCP initialize failed"); } throw new FileMcpException("MCP initialize failed"); }
        public void Dispose() { try { if (_process?.HasExited == false) _process.Kill(true); } catch { } _process?.Dispose(); _serial.Dispose(); }
    }
}
