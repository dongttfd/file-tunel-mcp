using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace FileMCP.Core;

internal interface ITelemetryRetentionStore
{
    Task CleanupRetentionAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

internal interface ITelemetryDeltaStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertDeltasAsync(DateTimeOffset capturedAtUtc, IReadOnlyDictionary<string, UsageCounters> deltas, CancellationToken cancellationToken = default);
}

internal sealed class TelemetrySqliteStore : ITelemetryDeltaStore, ITelemetryRetentionStore
{
    public const int SchemaVersion = 2;
    public static readonly TimeSpan DurableSessionRetention = TimeSpan.FromDays(35);
    private readonly string _databasePath;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private volatile bool _initialized;

    public TelemetrySqliteStore(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileMCP",
            "observability-v1.sqlite3");
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            var directory = Path.GetDirectoryName(Path.GetFullPath(_databasePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            await using var connection = await OpenConfiguredConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task UpsertDeltasAsync(
        DateTimeOffset capturedAtUtc,
        IReadOnlyDictionary<string, UsageCounters> deltas,
        CancellationToken cancellationToken = default)
    {
        if (deltas.Count == 0 || deltas.Values.All(value => value.IsZero)) return;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var utc = capturedAtUtc.ToUniversalTime();
            var unix = utc.ToUnixTimeSeconds();
            var buckets = new (string Table, long Epoch)[]
            {
                ("usage_minute", FloorBucket(unix, 60)),
                ("usage_hour", FloorBucket(unix, 3_600)),
                ("usage_day", FloorBucket(unix, 86_400)),
            };

            foreach (var pair in deltas)
            {
                if (pair.Value.IsZero) continue;
                foreach (var bucket in buckets)
                    await UpsertBucketAsync(connection, transaction, bucket.Table, pair.Key, bucket.Epoch, pair.Value, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task UpsertSessionDeltasAsync(LogicalSessionPersistenceBatch batch, CancellationToken cancellationToken = default)
    {
        if (batch.IsEmpty) return;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var delta in batch.Sessions)
            {
                await using (var session = connection.CreateCommand())
                {
                    session.Transaction = (SqliteTransaction)transaction;
                    session.CommandText = """
                        INSERT INTO logical_sessions(session_hash, created_epoch, last_seen_epoch, client_name, state)
                        VALUES($hash,$created,$last,NULL,$state)
                        ON CONFLICT(session_hash) DO UPDATE SET
                            created_epoch = MIN(created_epoch, excluded.created_epoch),
                            last_seen_epoch = MAX(last_seen_epoch, excluded.last_seen_epoch),
                            state = excluded.state;
                        """;
                    session.Parameters.AddWithValue("$hash", delta.SessionHash);
                    session.Parameters.AddWithValue("$created", delta.CreatedEpoch);
                    session.Parameters.AddWithValue("$last", delta.LastSeenEpoch);
                    session.Parameters.AddWithValue("$state", delta.State);
                    await session.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using var workspace = connection.CreateCommand();
                workspace.Transaction = (SqliteTransaction)transaction;
                workspace.CommandText = """
                    INSERT INTO logical_session_workspace(
                        session_hash,workspace_key,first_seen_epoch,last_seen_epoch,
                        mcp_requests,tool_calls,execution_tasks,request_bytes,response_bytes,tokens_in_est,tokens_out_est,
                        errors,read_calls,write_calls,command_calls,git_calls,skill_calls,other_calls)
                    VALUES($hash,$workspace,$first,$last,$mcp,$tools,$tasks,$req,$res,$tin,$tout,$errors,$read,$write,$command,$git,$skill,$other)
                    ON CONFLICT(session_hash,workspace_key) DO UPDATE SET
                        first_seen_epoch = MIN(first_seen_epoch, excluded.first_seen_epoch),
                        last_seen_epoch = MAX(last_seen_epoch, excluded.last_seen_epoch),
                        mcp_requests = mcp_requests + excluded.mcp_requests,
                        tool_calls = tool_calls + excluded.tool_calls,
                        execution_tasks = execution_tasks + excluded.execution_tasks,
                        request_bytes = request_bytes + excluded.request_bytes,
                        response_bytes = response_bytes + excluded.response_bytes,
                        tokens_in_est = tokens_in_est + excluded.tokens_in_est,
                        tokens_out_est = tokens_out_est + excluded.tokens_out_est,
                        errors = errors + excluded.errors,
                        read_calls = read_calls + excluded.read_calls,
                        write_calls = write_calls + excluded.write_calls,
                        command_calls = command_calls + excluded.command_calls,
                        git_calls = git_calls + excluded.git_calls,
                        skill_calls = skill_calls + excluded.skill_calls,
                        other_calls = other_calls + excluded.other_calls;
                    """;
                AddSessionUsageParameters(workspace, delta.SessionHash, delta.WorkspaceKey, delta.FirstSeenEpoch, delta.LastSeenEpoch, delta.Usage);
                await workspace.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var delta in batch.Unbound)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO unbound_session_workspace(
                        workspace_key,first_seen_epoch,last_seen_epoch,state,
                        mcp_requests,tool_calls,execution_tasks,request_bytes,response_bytes,tokens_in_est,tokens_out_est,
                        errors,read_calls,write_calls,command_calls,git_calls,skill_calls,other_calls)
                    VALUES($workspace,$first,$last,$state,$mcp,$tools,$tasks,$req,$res,$tin,$tout,$errors,$read,$write,$command,$git,$skill,$other)
                    ON CONFLICT(workspace_key) DO UPDATE SET
                        first_seen_epoch = MIN(first_seen_epoch, excluded.first_seen_epoch),
                        last_seen_epoch = MAX(last_seen_epoch, excluded.last_seen_epoch),
                        state = excluded.state,
                        mcp_requests = mcp_requests + excluded.mcp_requests,
                        tool_calls = tool_calls + excluded.tool_calls,
                        execution_tasks = execution_tasks + excluded.execution_tasks,
                        request_bytes = request_bytes + excluded.request_bytes,
                        response_bytes = response_bytes + excluded.response_bytes,
                        tokens_in_est = tokens_in_est + excluded.tokens_in_est,
                        tokens_out_est = tokens_out_est + excluded.tokens_out_est,
                        errors = errors + excluded.errors,
                        read_calls = read_calls + excluded.read_calls,
                        write_calls = write_calls + excluded.write_calls,
                        command_calls = command_calls + excluded.command_calls,
                        git_calls = git_calls + excluded.git_calls,
                        skill_calls = skill_calls + excluded.skill_calls,
                        other_calls = other_calls + excluded.other_calls;
                    """;
                command.Parameters.AddWithValue("$workspace", delta.WorkspaceKey);
                command.Parameters.AddWithValue("$first", delta.FirstSeenEpoch);
                command.Parameters.AddWithValue("$last", delta.LastSeenEpoch);
                command.Parameters.AddWithValue("$state", delta.State);
                AddUsageParameters(command, delta.Usage);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }
    public async Task<UsageCounters> QueryExactPeriodAsync(
        UsagePeriodRange range,
        string? workspaceKey = null,
        CancellationToken cancellationToken = default)
    {
        if (range.ToUtc <= range.FromUtc) return default;
        if (range.FromUtc.UtcTicks % TimeSpan.TicksPerMinute != 0)
            throw new ArgumentException("Exact period queries require a minute-aligned start boundary.", nameof(range));

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var fromEpoch = FloorBucket(range.FromUtc.ToUnixTimeSeconds(), 60);
        var toEpoch = CeilingMinuteExclusive(range.ToUtc);
        command.CommandText = """
            SELECT
                COALESCE(SUM(mcp_requests),0),
                COALESCE(SUM(tool_calls),0),
                COALESCE(SUM(execution_tasks),0),
                COALESCE(SUM(request_bytes),0),
                COALESCE(SUM(response_bytes),0),
                COALESCE(SUM(tokens_in_est),0),
                COALESCE(SUM(tokens_out_est),0),
                COALESCE(SUM(errors),0),
                COALESCE(SUM(read_calls),0),
                COALESCE(SUM(write_calls),0),
                COALESCE(SUM(command_calls),0),
                COALESCE(SUM(git_calls),0),
                COALESCE(SUM(skill_calls),0),
                COALESCE(SUM(other_calls),0),
                COALESCE(SUM(total_latency_us),0),
                COALESCE(MAX(max_latency_us),0)
            FROM usage_minute
            WHERE bucket_epoch >= $from_epoch AND bucket_epoch < $to_epoch
              AND ($workspace IS NULL OR workspace_key = $workspace);
            """;
        command.Parameters.AddWithValue("$from_epoch", fromEpoch);
        command.Parameters.AddWithValue("$to_epoch", toEpoch);
        command.Parameters.AddWithValue("$workspace", workspaceKey is null ? DBNull.Value : workspaceKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return default;
        return new UsageCounters(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            MicrosecondsToTicks(reader.GetInt64(14)),
            MicrosecondsToTicks(reader.GetInt64(15)));
    }

    public async Task CleanupRetentionAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DeleteOlderThanAsync(connection, transaction, "usage_minute", FloorBucket(nowUtc.ToUniversalTime().AddDays(-35).ToUnixTimeSeconds(), 60), cancellationToken).ConfigureAwait(false);
            await DeleteOlderThanAsync(connection, transaction, "usage_hour", FloorBucket(nowUtc.ToUniversalTime().AddDays(-90).ToUnixTimeSeconds(), 3_600), cancellationToken).ConfigureAwait(false);
            var sessionCutoff = nowUtc.ToUniversalTime().Subtract(DurableSessionRetention).ToUnixTimeSeconds();
            await DeleteStaleSessionsOlderThanAsync(connection, transaction, sessionCutoff, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static async Task DeleteStaleSessionsOlderThanAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        long cutoffEpoch,
        CancellationToken cancellationToken)
    {
        await using (var sessions = connection.CreateCommand())
        {
            sessions.Transaction = (SqliteTransaction)transaction;
            sessions.CommandText = "DELETE FROM logical_sessions WHERE state='stale' AND last_seen_epoch < $cutoff;";
            sessions.Parameters.AddWithValue("$cutoff", cutoffEpoch);
            await sessions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var unbound = connection.CreateCommand())
        {
            unbound.Transaction = (SqliteTransaction)transaction;
            unbound.CommandText = "DELETE FROM unbound_session_workspace WHERE state='stale' AND last_seen_epoch < $cutoff;";
            unbound.Parameters.AddWithValue("$cutoff", cutoffEpoch);
            await unbound.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    private static async Task DeleteOlderThanAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string table,
        long cutoffEpoch,
        CancellationToken cancellationToken)
    {
        if (table is not ("usage_minute" or "usage_hour")) throw new ArgumentOutOfRangeException(nameof(table));
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"DELETE FROM {table} WHERE bucket_epoch < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoffEpoch);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long CeilingMinuteExclusive(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var unix = utc.ToUnixTimeSeconds();
        var floor = FloorBucket(unix, 60);
        return utc.UtcTicks % TimeSpan.TicksPerMinute == 0 ? floor : floor + 60;
    }
    internal static long FloorBucket(long unixSeconds, long bucketSeconds) =>
        unixSeconds - Mod(unixSeconds, bucketSeconds);

    private async Task<SqliteConnection> OpenConfiguredConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS telemetry_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS usage_minute (
                workspace_key TEXT NOT NULL,
                bucket_epoch INTEGER NOT NULL,
                mcp_requests INTEGER NOT NULL DEFAULT 0,
                tool_calls INTEGER NOT NULL DEFAULT 0,
                execution_tasks INTEGER NOT NULL DEFAULT 0,
                request_bytes INTEGER NOT NULL DEFAULT 0,
                response_bytes INTEGER NOT NULL DEFAULT 0,
                tokens_in_est INTEGER NOT NULL DEFAULT 0,
                tokens_out_est INTEGER NOT NULL DEFAULT 0,
                errors INTEGER NOT NULL DEFAULT 0,
                read_calls INTEGER NOT NULL DEFAULT 0,
                write_calls INTEGER NOT NULL DEFAULT 0,
                command_calls INTEGER NOT NULL DEFAULT 0,
                git_calls INTEGER NOT NULL DEFAULT 0,
                skill_calls INTEGER NOT NULL DEFAULT 0,
                other_calls INTEGER NOT NULL DEFAULT 0,
                total_latency_us INTEGER NOT NULL DEFAULT 0,
                max_latency_us INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (workspace_key, bucket_epoch)
            );

            CREATE TABLE IF NOT EXISTS usage_hour AS SELECT * FROM usage_minute WHERE 0;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_usage_hour_workspace_bucket ON usage_hour(workspace_key, bucket_epoch);
            CREATE TABLE IF NOT EXISTS usage_day AS SELECT * FROM usage_minute WHERE 0;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_usage_day_workspace_bucket ON usage_day(workspace_key, bucket_epoch);

            CREATE TABLE IF NOT EXISTS logical_sessions (
                session_hash TEXT PRIMARY KEY,
                created_epoch INTEGER NOT NULL,
                last_seen_epoch INTEGER NOT NULL,
                client_name TEXT NULL,
                state TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS logical_session_workspace (
                session_hash TEXT NOT NULL,
                workspace_key TEXT NOT NULL,
                first_seen_epoch INTEGER NOT NULL,
                last_seen_epoch INTEGER NOT NULL,
                mcp_requests INTEGER NOT NULL DEFAULT 0,
                tool_calls INTEGER NOT NULL DEFAULT 0,
                execution_tasks INTEGER NOT NULL DEFAULT 0,
                request_bytes INTEGER NOT NULL DEFAULT 0,
                response_bytes INTEGER NOT NULL DEFAULT 0,
                tokens_in_est INTEGER NOT NULL DEFAULT 0,
                tokens_out_est INTEGER NOT NULL DEFAULT 0,
                errors INTEGER NOT NULL DEFAULT 0,
                read_calls INTEGER NOT NULL DEFAULT 0,
                write_calls INTEGER NOT NULL DEFAULT 0,
                command_calls INTEGER NOT NULL DEFAULT 0,
                git_calls INTEGER NOT NULL DEFAULT 0,
                skill_calls INTEGER NOT NULL DEFAULT 0,
                other_calls INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (session_hash, workspace_key),
                FOREIGN KEY (session_hash) REFERENCES logical_sessions(session_hash) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS unbound_session_workspace (
                workspace_key TEXT PRIMARY KEY,
                first_seen_epoch INTEGER NOT NULL,
                last_seen_epoch INTEGER NOT NULL,
                state TEXT NOT NULL,
                mcp_requests INTEGER NOT NULL DEFAULT 0,
                tool_calls INTEGER NOT NULL DEFAULT 0,
                execution_tasks INTEGER NOT NULL DEFAULT 0,
                request_bytes INTEGER NOT NULL DEFAULT 0,
                response_bytes INTEGER NOT NULL DEFAULT 0,
                tokens_in_est INTEGER NOT NULL DEFAULT 0,
                tokens_out_est INTEGER NOT NULL DEFAULT 0,
                errors INTEGER NOT NULL DEFAULT 0,
                read_calls INTEGER NOT NULL DEFAULT 0,
                write_calls INTEGER NOT NULL DEFAULT 0,
                command_calls INTEGER NOT NULL DEFAULT 0,
                git_calls INTEGER NOT NULL DEFAULT 0,
                skill_calls INTEGER NOT NULL DEFAULT 0,
                other_calls INTEGER NOT NULL DEFAULT 0
            );
            """;
        await ExecuteAsync(connection, schema, cancellationToken).ConfigureAwait(false);

        var existingVersion = await ReadMetaAsync(connection, "schema_version", cancellationToken).ConfigureAwait(false);
        if (existingVersion is not null && (!int.TryParse(existingVersion, out var parsed) || parsed > SchemaVersion))
            throw new FileMcpException($"Observability database schema '{existingVersion}' is newer than supported version {SchemaVersion}.");

        if (existingVersion is null)
            await WriteMetaAsync(connection, "schema_version", SchemaVersion.ToString(), cancellationToken).ConfigureAwait(false);
        else if (existingVersion != SchemaVersion.ToString())
            await MigrateAsync(connection, int.Parse(existingVersion), cancellationToken).ConfigureAwait(false);

        await WriteMetaAsync(connection, "token_estimator", McpTokenEstimator.EstimatorId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateAsync(SqliteConnection connection, int fromVersion, CancellationToken cancellationToken)
    {
        if (fromVersion == SchemaVersion) return;
        if (fromVersion == 1 && SchemaVersion == 2)
        {
            await WriteMetaAsync(connection, "schema_version", SchemaVersion.ToString(), cancellationToken).ConfigureAwait(false);
            return;
        }
        throw new FileMcpException($"Unsupported observability database migration from schema {fromVersion} to {SchemaVersion}.");
    }

    private static async Task UpsertBucketAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string table,
        string workspaceKey,
        long bucketEpoch,
        UsageCounters delta,
        CancellationToken cancellationToken)
    {
        var allowed = table is "usage_minute" or "usage_hour" or "usage_day";
        if (!allowed) throw new ArgumentOutOfRangeException(nameof(table));

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"""
            INSERT INTO {table} (
                workspace_key, bucket_epoch, mcp_requests, tool_calls, execution_tasks,
                request_bytes, response_bytes, tokens_in_est, tokens_out_est, errors,
                read_calls, write_calls, command_calls, git_calls, skill_calls, other_calls,
                total_latency_us, max_latency_us
            ) VALUES (
                $workspace, $bucket, $mcp_requests, $tool_calls, $execution_tasks,
                $request_bytes, $response_bytes, $tokens_in_est, $tokens_out_est, $errors,
                $read_calls, $write_calls, $command_calls, $git_calls, $skill_calls, $other_calls,
                $total_latency_us, $max_latency_us
            )
            ON CONFLICT(workspace_key, bucket_epoch) DO UPDATE SET
                mcp_requests = mcp_requests + excluded.mcp_requests,
                tool_calls = tool_calls + excluded.tool_calls,
                execution_tasks = execution_tasks + excluded.execution_tasks,
                request_bytes = request_bytes + excluded.request_bytes,
                response_bytes = response_bytes + excluded.response_bytes,
                tokens_in_est = tokens_in_est + excluded.tokens_in_est,
                tokens_out_est = tokens_out_est + excluded.tokens_out_est,
                errors = errors + excluded.errors,
                read_calls = read_calls + excluded.read_calls,
                write_calls = write_calls + excluded.write_calls,
                command_calls = command_calls + excluded.command_calls,
                git_calls = git_calls + excluded.git_calls,
                skill_calls = skill_calls + excluded.skill_calls,
                other_calls = other_calls + excluded.other_calls,
                total_latency_us = total_latency_us + excluded.total_latency_us,
                max_latency_us = MAX(max_latency_us, excluded.max_latency_us);
            """;
        command.Parameters.AddWithValue("$workspace", workspaceKey);
        command.Parameters.AddWithValue("$bucket", bucketEpoch);
        command.Parameters.AddWithValue("$mcp_requests", delta.McpRequests);
        command.Parameters.AddWithValue("$tool_calls", delta.ToolCalls);
        command.Parameters.AddWithValue("$execution_tasks", delta.ExecutionTasks);
        command.Parameters.AddWithValue("$request_bytes", delta.RequestBytes);
        command.Parameters.AddWithValue("$response_bytes", delta.ResponseBytes);
        command.Parameters.AddWithValue("$tokens_in_est", delta.TokensInEst);
        command.Parameters.AddWithValue("$tokens_out_est", delta.TokensOutEst);
        command.Parameters.AddWithValue("$errors", delta.Errors);
        command.Parameters.AddWithValue("$read_calls", delta.ReadCalls);
        command.Parameters.AddWithValue("$write_calls", delta.WriteCalls);
        command.Parameters.AddWithValue("$command_calls", delta.CommandCalls);
        command.Parameters.AddWithValue("$git_calls", delta.GitCalls);
        command.Parameters.AddWithValue("$skill_calls", delta.SkillCalls);
        command.Parameters.AddWithValue("$other_calls", delta.OtherCalls);
        command.Parameters.AddWithValue("$total_latency_us", TicksToMicroseconds(delta.TotalLatencyTicks));
        command.Parameters.AddWithValue("$max_latency_us", TicksToMicroseconds(delta.MaxLatencyTicks));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddSessionUsageParameters(SqliteCommand command, string sessionHash, string workspaceKey, long firstSeenEpoch, long lastSeenEpoch, UsageCounters usage)
    {
        command.Parameters.AddWithValue("$hash", sessionHash);
        command.Parameters.AddWithValue("$workspace", workspaceKey);
        command.Parameters.AddWithValue("$first", firstSeenEpoch);
        command.Parameters.AddWithValue("$last", lastSeenEpoch);
        AddUsageParameters(command, usage);
    }

    private static void AddUsageParameters(SqliteCommand command, UsageCounters usage)
    {
        command.Parameters.AddWithValue("$mcp", usage.McpRequests);
        command.Parameters.AddWithValue("$tools", usage.ToolCalls);
        command.Parameters.AddWithValue("$tasks", usage.ExecutionTasks);
        command.Parameters.AddWithValue("$req", usage.RequestBytes);
        command.Parameters.AddWithValue("$res", usage.ResponseBytes);
        command.Parameters.AddWithValue("$tin", usage.TokensInEst);
        command.Parameters.AddWithValue("$tout", usage.TokensOutEst);
        command.Parameters.AddWithValue("$errors", usage.Errors);
        command.Parameters.AddWithValue("$read", usage.ReadCalls);
        command.Parameters.AddWithValue("$write", usage.WriteCalls);
        command.Parameters.AddWithValue("$command", usage.CommandCalls);
        command.Parameters.AddWithValue("$git", usage.GitCalls);
        command.Parameters.AddWithValue("$skill", usage.SkillCalls);
        command.Parameters.AddWithValue("$other", usage.OtherCalls);
    }
    private static async Task<string?> ReadMetaAsync(SqliteConnection connection, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM telemetry_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteMetaAsync(SqliteConnection connection, string key, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO telemetry_meta(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long MicrosecondsToTicks(long microseconds)
    {
        if (microseconds <= 0) return 0;
        return (long)((decimal)microseconds * Stopwatch.Frequency / 1_000_000m);
    }
    private static long TicksToMicroseconds(long ticks)
    {
        if (ticks <= 0) return 0;
        return (long)((decimal)ticks * 1_000_000m / Stopwatch.Frequency);
    }

    private static long Mod(long value, long divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }
}

internal sealed class TelemetryWriter : IAsyncDisposable
{
    private readonly IReadOnlyList<WorkspaceUsageMeter> _meters;
    private readonly ITelemetryDeltaStore _store;
    private readonly TimeSpan _interval;
    private readonly Action<string>? _log;
    private readonly TelemetryPersistenceHealthTracker? _health;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Dictionary<string, UsageCounters> _pending = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TelemetryWriter(
        IEnumerable<WorkspaceUsageMeter> meters,
        ITelemetryDeltaStore store,
        TimeSpan? interval = null,
        Action<string>? log = null,
        TelemetryPersistenceHealthTracker? health = null)
    {
        _meters = meters.ToArray();
        _store = store;
        _interval = interval ?? TimeSpan.FromSeconds(1);
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _log = log;
        _health = health;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) return;
        try
        {
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _health?.RecordSuccess();
        }
        catch
        {
            _health?.RecordFailure();
            throw;
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_cts.Token);
    }

    public async Task FlushOnceAsync(DateTimeOffset capturedAtUtc, CancellationToken cancellationToken = default)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CollectPending();
            if (_pending.Count == 0) return;
            try
            {
                await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
                await _store.UpsertDeltasAsync(capturedAtUtc, _pending, cancellationToken).ConfigureAwait(false);
                _pending.Clear();
                _health?.RecordSuccess(capturedAtUtc);
            }
            catch
            {
                _health?.RecordFailure(capturedAtUtc);
                throw;
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    internal IReadOnlyDictionary<string, UsageCounters> PendingSnapshotForTests() =>
        _pending.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private void CollectPending()
    {
        foreach (var meter in _meters)
        {
            var delta = meter.DrainDelta();
            if (delta.IsZero) continue;
            _pending[meter.WorkspaceKey] = _pending.TryGetValue(meter.WorkspaceKey, out var existing)
                ? existing + delta
                : delta;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try { await FlushOnceAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex) { _log?.Invoke($"[Telemetry] flush failed: {ex.Message}\n"); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            if (_loop is not null)
            {
                try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            try { await FlushOnceAsync(DateTimeOffset.UtcNow).ConfigureAwait(false); }
            catch (Exception ex) { _log?.Invoke($"[Telemetry] final flush failed: {ex.Message}\n"); }
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }
        _flushGate.Dispose();
    }
}
