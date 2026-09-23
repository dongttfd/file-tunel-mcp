namespace FileMCP.Core;

public sealed class WorkspaceUsageMeter
{
    private sealed class AtomicCounterSet
    {
        public long McpRequests;
        public long ToolCalls;
        public long ExecutionTasks;
        public long RequestBytes;
        public long ResponseBytes;
        public long TokensInEst;
        public long TokensOutEst;
        public long Errors;
        public long ReadCalls;
        public long WriteCalls;
        public long CommandCalls;
        public long GitCalls;
        public long SkillCalls;
        public long OtherCalls;
        public long TotalLatencyTicks;
        public long MaxLatencyTicks;

        public UsageCounters Snapshot() => new(
            Volatile.Read(ref McpRequests),
            Volatile.Read(ref ToolCalls),
            Volatile.Read(ref ExecutionTasks),
            Volatile.Read(ref RequestBytes),
            Volatile.Read(ref ResponseBytes),
            Volatile.Read(ref TokensInEst),
            Volatile.Read(ref TokensOutEst),
            Volatile.Read(ref Errors),
            Volatile.Read(ref ReadCalls),
            Volatile.Read(ref WriteCalls),
            Volatile.Read(ref CommandCalls),
            Volatile.Read(ref GitCalls),
            Volatile.Read(ref SkillCalls),
            Volatile.Read(ref OtherCalls),
            Volatile.Read(ref TotalLatencyTicks),
            Volatile.Read(ref MaxLatencyTicks));

        public UsageCounters Drain() => new(
            Interlocked.Exchange(ref McpRequests, 0),
            Interlocked.Exchange(ref ToolCalls, 0),
            Interlocked.Exchange(ref ExecutionTasks, 0),
            Interlocked.Exchange(ref RequestBytes, 0),
            Interlocked.Exchange(ref ResponseBytes, 0),
            Interlocked.Exchange(ref TokensInEst, 0),
            Interlocked.Exchange(ref TokensOutEst, 0),
            Interlocked.Exchange(ref Errors, 0),
            Interlocked.Exchange(ref ReadCalls, 0),
            Interlocked.Exchange(ref WriteCalls, 0),
            Interlocked.Exchange(ref CommandCalls, 0),
            Interlocked.Exchange(ref GitCalls, 0),
            Interlocked.Exchange(ref SkillCalls, 0),
            Interlocked.Exchange(ref OtherCalls, 0),
            Interlocked.Exchange(ref TotalLatencyTicks, 0),
            Interlocked.Exchange(ref MaxLatencyTicks, 0));

        public void AddRequest(long bytes)
        {
            Interlocked.Increment(ref McpRequests);
            if (bytes <= 0) return;
            Interlocked.Add(ref RequestBytes, bytes);
            Interlocked.Add(ref TokensInEst, McpTokenEstimator.EstimateFromUtf8Bytes(bytes));
        }

        public void AddResponse(long bytes)
        {
            if (bytes <= 0) return;
            Interlocked.Add(ref ResponseBytes, bytes);
            Interlocked.Add(ref TokensOutEst, McpTokenEstimator.EstimateFromUtf8Bytes(bytes));
        }

        public void AddToolCall(ToolUsageClassification classification, bool isError, long latencyTicks)
        {
            Interlocked.Increment(ref ToolCalls);
            if (classification.IsExecutionTask) Interlocked.Increment(ref ExecutionTasks);
            if (isError) Interlocked.Increment(ref Errors);

            switch (classification.Category)
            {
                case ToolUsageCategory.Read:
                    Interlocked.Increment(ref ReadCalls);
                    break;
                case ToolUsageCategory.Write:
                    Interlocked.Increment(ref WriteCalls);
                    break;
                case ToolUsageCategory.Command:
                    Interlocked.Increment(ref CommandCalls);
                    break;
                case ToolUsageCategory.Git:
                    Interlocked.Increment(ref GitCalls);
                    break;
                case ToolUsageCategory.Skill:
                    Interlocked.Increment(ref SkillCalls);
                    break;
                default:
                    Interlocked.Increment(ref OtherCalls);
                    break;
            }

            if (latencyTicks <= 0) return;
            Interlocked.Add(ref TotalLatencyTicks, latencyTicks);
            UpdateMax(ref MaxLatencyTicks, latencyTicks);
        }

        private static void UpdateMax(ref long target, long candidate)
        {
            var current = Volatile.Read(ref target);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref target, candidate, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }

    private readonly AtomicCounterSet _lifetime = new();
    private readonly AtomicCounterSet _pending = new();

    public WorkspaceUsageMeter(string workspaceKey)
    {
        if (string.IsNullOrWhiteSpace(workspaceKey)) throw new ArgumentException("Workspace key cannot be empty.", nameof(workspaceKey));
        WorkspaceKey = workspaceKey.Trim().ToUpperInvariant();
    }

    public string WorkspaceKey { get; }

    public UsageCounters Snapshot() => _lifetime.Snapshot();

    public UsageCounters DrainDelta() => _pending.Drain();

    public void RecordRequest(long requestBytes)
    {
        var bytes = Math.Max(0, requestBytes);
        _lifetime.AddRequest(bytes);
        _pending.AddRequest(bytes);
    }

    public void RecordResponse(long responseBytes)
    {
        var bytes = Math.Max(0, responseBytes);
        _lifetime.AddResponse(bytes);
        _pending.AddResponse(bytes);
    }

    public void RecordToolCall(string? toolName, bool isError, long latencyTicks)
    {
        var classification = ToolUsageClassifier.Classify(toolName);
        var latency = Math.Max(0, latencyTicks);
        _lifetime.AddToolCall(classification, isError, latency);
        _pending.AddToolCall(classification, isError, latency);
    }
}