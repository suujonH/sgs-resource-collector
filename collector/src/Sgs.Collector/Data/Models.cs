namespace Sgs.Collector.Data;

// 表模型：属性名与列名按 PascalCase 一一对应（Db 的静态构造函数开启了下划线匹配）

public sealed class ResourceRow
{
    public long Id { get; set; }
    public string ResourceKey { get; set; } = "";
    public long? ParentId { get; set; }
    public string? SourcePath { get; set; }
    public string? FetchUrl { get; set; }
    public long LatestVersion { get; set; }
    public string? LatestHash { get; set; }
    public string? Etag { get; set; }
    public string? LastModified { get; set; }
    public long? ProcessingRev { get; set; }
    public string FirstSeenAt { get; set; } = "";
    public long? LastSeenRun { get; set; }
    public string? LastCheckedAt { get; set; }
    public string? LastOutcome { get; set; }
    public long? LastHttpStatus { get; set; }
    public string? LastError { get; set; }
    public string? LastSuccessAt { get; set; }
}

public sealed class VersionRow
{
    public long ResourceId { get; set; }
    public long Version { get; set; }
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public string ContentType { get; set; } = "";
    public string CollectedAt { get; set; } = "";
    public long RunId { get; set; }
    public long Synced { get; set; }
}

public sealed class RunRow
{
    public long Id { get; set; }
    public string TriggerKind { get; set; } = "";
    public string Status { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string? FinishedAt { get; set; }
    public string? StatsJson { get; set; }
    public string? Error { get; set; }
    public string? SyncStatus { get; set; }
    public string? SyncError { get; set; }
}

public sealed class FailureRow
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public long ResourceId { get; set; }
    public long? HttpStatus { get; set; }
    public string Error { get; set; } = "";
    public long HadHistory { get; set; }
    public string OccurredAt { get; set; } = "";
}

public sealed class HeaderRuleRow
{
    public long Id { get; set; }
    public string Pattern { get; set; } = "";
    public string HeadersJson { get; set; } = "";
    public long Enabled { get; set; }
    public string? Note { get; set; }
    public string UpdatedAt { get; set; } = "";
}

public sealed class LinkRow
{
    public string Url { get; set; } = "";
    public string Source { get; set; } = "";
}

// 查询结果模型：字段与各查询的 SELECT 列一致

public sealed class UnsyncedVersionRow
{
    public long ResourceId { get; set; }
    public long Version { get; set; }
    public string ResourceKey { get; set; } = "";
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public string ContentType { get; set; } = "";
    public string CollectedAt { get; set; } = "";
}

public sealed class RunVersionItemRow
{
    public long ResourceId { get; set; }
    public string ResourceKey { get; set; } = "";
    public long Version { get; set; }
    public long Size { get; set; }
    public string ContentType { get; set; } = "";
    public string CollectedAt { get; set; } = "";
}

public sealed class RunFailureItemRow
{
    public long ResourceId { get; set; }
    public string ResourceKey { get; set; } = "";
    public long? HttpStatus { get; set; }
    public string Error { get; set; } = "";
    public long HadHistory { get; set; }
    public string OccurredAt { get; set; } = "";
}

public sealed class ResourceListItemRow
{
    public long Id { get; set; }
    public string ResourceKey { get; set; } = "";
    public long? ParentId { get; set; }
    public long LatestVersion { get; set; }
    public string? LastOutcome { get; set; }
    public long? LastHttpStatus { get; set; }
    public string? LastCheckedAt { get; set; }
    public string? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class MemberRow
{
    public long Id { get; set; }
    public string ResourceKey { get; set; } = "";
    public long LatestVersion { get; set; }
    public string? LastOutcome { get; set; }
}

public sealed record PagedResult<T>(long Total, IReadOnlyList<T> Items);
