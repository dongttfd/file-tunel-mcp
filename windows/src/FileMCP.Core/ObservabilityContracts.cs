namespace FileMCP.Core;

public static class McpTokenEstimator
{
    public const string EstimatorId = "bytes_div_4_v1";

    public static long EstimateFromUtf8Bytes(long utf8Bytes)
    {
        if (utf8Bytes < 0) throw new ArgumentOutOfRangeException(nameof(utf8Bytes));
        return (utf8Bytes / 4) + (utf8Bytes % 4 == 0 ? 0 : 1);
    }
}

public enum ToolUsageCategory
{
    Read,
    Write,
    Command,
    Git,
    Skill,
    Other,
}

public readonly record struct ToolUsageClassification(
    ToolUsageCategory Category,
    bool IsExecutionTask,
    bool IsKnownTool);

public static class ToolUsageClassifier
{
    public static ToolUsageClassification Classify(string? toolName) => toolName switch
    {
        "list_files" or "read_file" or "read_file_range" or "search_content" or "search_filenames"
            => new(ToolUsageCategory.Read, false, true),

        "write_file" or "delete_file" or "delete_directory"
            => new(ToolUsageCategory.Write, true, true),

        "run_command"
            => new(ToolUsageCategory.Command, true, true),

        "git_status" or "git_log" or "git_diff"
            => new(ToolUsageCategory.Git, false, true),

        "git_init" or "git_add" or "git_commit" or "git_push"
            => new(ToolUsageCategory.Git, true, true),

        "list_codex_skills" or "load_codex_skill"
            => new(ToolUsageCategory.Skill, false, true),

        "filemcp_observability_connect"
            => new(ToolUsageCategory.Other, false, true),

        _ => new(ToolUsageCategory.Other, false, false),
    };
}

public readonly record struct UsageCounters(
    long McpRequests,
    long ToolCalls,
    long ExecutionTasks,
    long RequestBytes,
    long ResponseBytes,
    long TokensInEst,
    long TokensOutEst,
    long Errors,
    long ReadCalls,
    long WriteCalls,
    long CommandCalls,
    long GitCalls,
    long SkillCalls,
    long OtherCalls,
    long TotalLatencyTicks,
    long MaxLatencyTicks)
{
    public long TotalPayloadBytes => RequestBytes + ResponseBytes;
    public long TotalTokensEst => TokensInEst + TokensOutEst;
    public bool IsZero => this == default;

    public static UsageCounters operator +(UsageCounters left, UsageCounters right) => new(
        left.McpRequests + right.McpRequests,
        left.ToolCalls + right.ToolCalls,
        left.ExecutionTasks + right.ExecutionTasks,
        left.RequestBytes + right.RequestBytes,
        left.ResponseBytes + right.ResponseBytes,
        left.TokensInEst + right.TokensInEst,
        left.TokensOutEst + right.TokensOutEst,
        left.Errors + right.Errors,
        left.ReadCalls + right.ReadCalls,
        left.WriteCalls + right.WriteCalls,
        left.CommandCalls + right.CommandCalls,
        left.GitCalls + right.GitCalls,
        left.SkillCalls + right.SkillCalls,
        left.OtherCalls + right.OtherCalls,
        left.TotalLatencyTicks + right.TotalLatencyTicks,
        Math.Max(left.MaxLatencyTicks, right.MaxLatencyTicks));
}