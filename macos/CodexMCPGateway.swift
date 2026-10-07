import Foundation

private enum CodexGatewayError: LocalizedError {
    case message(String)
    var errorDescription: String? { if case let .message(value) = self { return value }; return nil }
}

private struct CodexServerConfig {
    let name: String
    let command: String
    let arguments: [String]
    let cwd: String?
    let environment: [String: String]
    let startupTimeout: Int
    let toolTimeout: Int
}

private final class CodexStdioSession {
    private let config: CodexServerConfig
    private let log: (String) -> Void
    private let lock = NSLock()
    private let startLock = NSLock()
    private var process: Process?
    private var input: FileHandle?
    private var nextID = 1
    private var replies: [Int: (DispatchSemaphore, Result<[String: Any], Error>?)] = [:]

    init(config: CodexServerConfig, log: @escaping (String) -> Void) {
        self.config = config
        self.log = log
    }

    func start() throws {
        startLock.lock()
        defer { startLock.unlock() }
        lock.lock(); let running = process?.isRunning == true; lock.unlock()
        if running { return }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: resolveCommand(config.command))
        process.arguments = config.arguments
        if let cwd = config.cwd { process.currentDirectoryURL = URL(fileURLWithPath: cwd) }
        var environment = ProcessInfo.processInfo.environment
        config.environment.forEach { environment[$0.key] = $0.value }
        process.environment = environment
        let stdin = Pipe(), stdout = Pipe(), stderr = Pipe()
        process.standardInput = stdin; process.standardOutput = stdout; process.standardError = stderr
        process.terminationHandler = { [weak self] process in self?.failed("server exited with status \(process.terminationStatus)") }
        try process.run()
        lock.lock(); self.process = process; input = stdin.fileHandleForWriting; lock.unlock()
        readLines(stdout.fileHandleForReading, protocolStream: true)
        readLines(stderr.fileHandleForReading, protocolStream: false)
        _ = try request(method: "initialize", params: [
            "protocolVersion": "2025-06-18", "capabilities": [:],
            "clientInfo": ["name": "filemcp", "version": "0.4.0"],
        ], timeout: config.startupTimeout)
        try send(["jsonrpc": "2.0", "method": "notifications/initialized", "params": [:]])
        log("[Codex MCP] Connected: \(config.name)\n")
    }

    func request(method: String, params: [String: Any], timeout: Int? = nil) throws -> [String: Any] {
        try startIfNeeded(method: method)
        lock.lock()
        let id = nextID; nextID += 1
        let signal = DispatchSemaphore(value: 0)
        replies[id] = (signal, nil)
        lock.unlock()
        do { try send(["jsonrpc": "2.0", "id": id, "method": method, "params": params]) }
        catch { lock.lock(); replies.removeValue(forKey: id); lock.unlock(); throw error }
        guard signal.wait(timeout: .now() + .seconds(timeout ?? config.toolTimeout)) == .success else {
            lock.lock(); replies.removeValue(forKey: id); lock.unlock()
            throw CodexGatewayError.message("Codex MCP \(config.name) timed out during \(method)")
        }
        lock.lock(); let result = replies.removeValue(forKey: id)?.1; lock.unlock()
        return try result?.get() ?? { throw CodexGatewayError.message("Codex MCP \(config.name) returned no result") }()
    }

    func stop() {
        lock.lock(); let process = self.process; self.process = nil; input = nil; lock.unlock()
        if process?.isRunning == true { process?.terminate(); process?.waitUntilExit() }
        failed("session stopped")
    }

    private func startIfNeeded(method: String) throws {
        lock.lock(); let running = process?.isRunning == true; lock.unlock()
        if !running && method != "initialize" { try start() }
    }

    private func send(_ object: [String: Any]) throws {
        let data = try JSONSerialization.data(withJSONObject: object) + Data([0x0a])
        lock.lock()
        defer { lock.unlock() }
        guard let input else { throw CodexGatewayError.message("Codex MCP \(config.name) is not running") }
        try input.write(contentsOf: data)
    }

    private func readLines(_ handle: FileHandle, protocolStream: Bool) {
        DispatchQueue.global(qos: .utility).async { [weak self] in
            var buffer = Data()
            while true {
                guard let chunk = try? handle.read(upToCount: 65_536), !chunk.isEmpty else { return }
                buffer.append(chunk)
                while let newline = buffer.firstIndex(of: 0x0a) {
                    let line = buffer.prefix(upTo: newline); buffer.removeSubrange(...newline)
                    guard let self else { return }
                    if protocolStream { self.receive(Data(line)) }
                }
                if buffer.count > 8_000_000 { self?.failed("protocol message exceeded 8 MB"); return }
            }
        }
    }

    private func receive(_ data: Data) {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let id = object["id"] as? Int else { return }
        let response: Result<[String: Any], Error>
        if let error = object["error"] as? [String: Any] {
            response = .failure(CodexGatewayError.message(error["message"] as? String ?? "Downstream MCP error"))
        } else if let result = object["result"] as? [String: Any] { response = .success(result) }
        else { response = .failure(CodexGatewayError.message("Malformed downstream MCP response")) }
        lock.lock(); if let pending = replies[id] { replies[id] = (pending.0, response); pending.0.signal() }; lock.unlock()
    }

    private func failed(_ message: String) {
        lock.lock()
        for (id, value) in replies {
            replies[id] = (value.0, .failure(CodexGatewayError.message("Codex MCP \(config.name): \(message)")))
            value.0.signal()
        }
        lock.unlock()
        if message != "session stopped" { log("[Codex MCP] \(config.name): \(message)\n") }
    }

    private func resolveCommand(_ command: String) -> String {
        if command.hasPrefix("/") { return command }
        let path = ProcessInfo.processInfo.environment["PATH"] ?? "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin"
        for directory in path.split(separator: ":") {
            let candidate = String(directory) + "/" + command
            if FileManager.default.isExecutableFile(atPath: candidate) { return candidate }
        }
        return command
    }
}

final class CodexMCPGateway {
    private let enabled: Bool
    private let executable: String
    private let allowlist: Set<String>
    private let log: (String) -> Void
    private let lock = NSLock()
    private var configs: [String: CodexServerConfig] = [:]
    private var sessions: [String: CodexStdioSession] = [:]

    init(enabled: Bool, executable: String, allowlist: [String], log: @escaping (String) -> Void) {
        self.enabled = enabled; self.executable = executable; self.allowlist = Set(allowlist); self.log = log
    }

    var toolDefinitions: [[String: Any]] {
        guard enabled else { return [] }
        let annotations: [String: Any] = ["readOnlyHint": true, "destructiveHint": false, "openWorldHint": true]
        return [
            definition("list_codex_mcp_servers", "List allowlisted MCP servers from the effective Codex configuration.", [:], [], annotations),
            definition("list_codex_mcp_tools", "List tools exposed by one allowlisted Codex MCP server.", ["server": string("Exact server name.")], ["server"], annotations),
            definition("call_codex_mcp_tool", "Call one tool on an allowlisted Codex MCP server. The downstream operation may be destructive.", ["server": string("Exact server name."), "tool": string("Exact downstream tool name."), "arguments": ["type": "object", "additionalProperties": true]], ["server", "tool"], ["readOnlyHint": false, "destructiveHint": true, "openWorldHint": true]),
        ]
    }

    func hasTool(_ name: String) -> Bool { enabled && ["list_codex_mcp_servers", "list_codex_mcp_tools", "call_codex_mcp_tool"].contains(name) }

    func refresh() {
        guard enabled else { return }
        do {
            let codex = try resolveCodex()
            let result = try ProcessRunner.run(executable: codex, arguments: ["mcp", "list", "--json"], timeoutSeconds: 15, outputLimitBytes: 2_000_000)
            guard result.exitCode == 0, !result.timedOut else { throw CodexGatewayError.message("Could not read Codex MCP configuration") }
            guard let rows = try JSONSerialization.jsonObject(with: Data(result.stdout.utf8)) as? [[String: Any]] else { throw CodexGatewayError.message("Unsupported Codex MCP configuration output") }
            var found: [String: CodexServerConfig] = [:]
            for row in rows where row["enabled"] as? Bool == true {
                guard let name = row["name"] as? String, allowlist.contains(name), let transport = row["transport"] as? [String: Any], transport["type"] as? String == "stdio", let command = transport["command"] as? String else { continue }
                let environment = transport["env"] as? [String: String] ?? [:]
                found[name] = CodexServerConfig(name: name, command: command, arguments: transport["args"] as? [String] ?? [], cwd: transport["cwd"] as? String, environment: environment, startupTimeout: Int(row["startup_timeout_sec"] as? Double ?? 15), toolTimeout: Int(row["tool_timeout_sec"] as? Double ?? 60))
            }
            lock.lock(); configs = found; lock.unlock()
            log("[Codex MCP] Loaded \(found.count) allowlisted server(s).\n")
        } catch { log("[Codex MCP] WARNING: \(error.localizedDescription)\n") }
    }

    func call(_ name: String, arguments: [String: Any]) throws -> CodexSkillToolOutput {
        switch name {
        case "list_codex_mcp_servers": return output(["servers": snapshot().keys.sorted().map { ["name": $0, "transport": "stdio", "available": true] }, "count": snapshot().count])
        case "list_codex_mcp_tools":
            let server = try required(arguments, "server"), result = try session(server).request(method: "tools/list", params: [:])
            return output(["server": server, "tools": result["tools"] as? [[String: Any]] ?? []])
        case "call_codex_mcp_tool":
            let server = try required(arguments, "server"), tool = try required(arguments, "tool")
            let listed = try session(server).request(method: "tools/list", params: [:])
            guard (listed["tools"] as? [[String: Any]])?.contains(where: { $0["name"] as? String == tool }) == true else { throw CodexGatewayError.message("Unknown tool \(tool) on Codex MCP \(server)") }
            let result = try session(server).request(method: "tools/call", params: ["name": tool, "arguments": arguments["arguments"] as? [String: Any] ?? [:]])
            let content = result["content"] as? [[String: Any]] ?? [["type": "text", "text": "Downstream MCP returned no content"]]
            return CodexSkillToolOutput(content: content, structuredContent: result["structuredContent"] as? [String: Any] ?? ["server": server, "tool": tool, "isError": result["isError"] as? Bool ?? false])
        default: throw CodexGatewayError.message("Unknown Codex MCP gateway tool")
        }
    }

    func stop() { lock.lock(); let current = sessions; sessions.removeAll(); configs.removeAll(); lock.unlock(); current.values.forEach { $0.stop() } }
    private func snapshot() -> [String: CodexServerConfig] { lock.lock(); defer { lock.unlock() }; return configs }
    private func session(_ name: String) throws -> CodexStdioSession {
        lock.lock(); defer { lock.unlock() }
        guard let config = configs[name] else { throw CodexGatewayError.message("Codex MCP server is not allowlisted or available: \(name)") }
        if let value = sessions[name] { return value }
        let value = CodexStdioSession(config: config, log: log); sessions[name] = value; return value
    }
    private func resolveCodex() throws -> String {
        let candidates = executable.isEmpty ? ["/opt/homebrew/bin/codex", "/usr/local/bin/codex", "/usr/bin/codex"] : [executable]
        guard let value = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else { throw CodexGatewayError.message("Codex executable was not found. Select it in Settings or disable Codex MCP federation.") }
        return value
    }
    private func required(_ args: [String: Any], _ key: String) throws -> String { guard let value = args[key] as? String, !value.isEmpty else { throw CodexGatewayError.message("Missing or invalid argument: \(key)") }; return value }
    private func output(_ value: [String: Any]) -> CodexSkillToolOutput { let data = try? JSONSerialization.data(withJSONObject: value, options: [.prettyPrinted]); return CodexSkillToolOutput(content: [["type": "text", "text": String(data: data ?? Data(), encoding: .utf8) ?? "{}"]], structuredContent: value) }
    private func string(_ description: String) -> [String: Any] { ["type": "string", "description": description] }
    private func definition(_ name: String, _ description: String, _ properties: [String: Any], _ required: [String], _ annotations: [String: Any]) -> [String: Any] { ["name": name, "description": description, "inputSchema": ["type": "object", "properties": properties, "required": required, "additionalProperties": false], "outputSchema": ["type": "object", "additionalProperties": true], "annotations": annotations] }
}
