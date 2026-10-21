using System.Text.Json;
using Sgs.Collector.Crawl;
using Sgs.Collector.Http;

namespace Sgs.Collector.Api;

public sealed record ErrorDto(string Error);

public sealed record StartedDto(bool Started);

public sealed record CancellingDto(bool Cancelling);

public sealed record PageDto<T>(long Total, IReadOnlyList<T> Items);

public sealed record ManualTaskDto(string Kind, long Done, long Total, string? Message, bool Finished);

public sealed record StatusDto(
    string State,
    string? NextScheduledAt,
    int IntervalMinutes,
    string WorkerBaseUrl,
    long PendingSync,
    long LocalVersions,
    RunLive? Current,
    RunLive? Last,
    ManualTaskDto? ManualTask);

public sealed record RunDto(
    long Id,
    string Trigger,
    string Status,
    string StartedAt,
    string? FinishedAt,
    JsonElement? Stats,
    string? Error,
    string? SyncStatus,
    string? SyncError);

public sealed record RunVersionDto(long ResourceId, string Key, long Version, long Size, string ContentType, string CollectedAt);

public sealed record RunFailureDto(long ResourceId, string Key, long? HttpStatus, string Error, bool HadHistory, string OccurredAt);

public sealed record ResourceListItemDto(
    long Id,
    string Key,
    long? ParentId,
    long LatestVersion,
    string? LastOutcome,
    long? LastHttpStatus,
    string? LastCheckedAt,
    string? LastSuccessAt,
    string? LastError);

public sealed record ResourceDto(
    long Id,
    string Key,
    long? ParentId,
    string? SourcePath,
    string? FetchUrl,
    long LatestVersion,
    string? LatestHash,
    string? Etag,
    string? LastModified,
    long? ProcessingRev,
    string FirstSeenAt,
    long? LastSeenRun,
    string? LastCheckedAt,
    string? LastOutcome,
    long? LastHttpStatus,
    string? LastError,
    string? LastSuccessAt);

public sealed record ResourceVersionDto(long Version, string Hash, long Size, string ContentType, string CollectedAt, long RunId, bool Synced, string Url);

public sealed record ResourceMemberDto(long Id, string Key, long LatestVersion, string? LastOutcome);

public sealed record ResourceFailureDto(long RunId, long? HttpStatus, string Error, bool HadHistory, string OccurredAt);

public sealed record ResourceDetailDto(
    ResourceDto Resource,
    string? ParentKey,
    string? LatestUrl,
    IReadOnlyList<ResourceVersionDto> Versions,
    IReadOnlyList<ResourceMemberDto> Members,
    IReadOnlyList<ResourceFailureDto> Failures);

public sealed record HeaderRuleDto(long Id, string Pattern, bool IsExact, IReadOnlyList<HeaderEntry> Headers, bool Enabled, string? Note, string UpdatedAt);

public sealed record HeaderRuleRequest(string? Pattern, List<HeaderEntry?>? Headers, bool? Enabled, string? Note);

public sealed record MatchedRuleDto(long Id, string Pattern);

public sealed record HeaderPreviewDto(string Key, bool InScope, IReadOnlyList<MatchedRuleDto> MatchedRules, IReadOnlyList<PreviewHeader> Headers);

internal static class ApiResults
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    public static IResult Error(int statusCode, string message) => Results.Json(new ErrorDto(message), statusCode: statusCode);

    public static IResult Accepted() => Results.Json(new StartedDto(true), statusCode: StatusCodes.Status202Accepted);

    public static (int Offset, int Limit) Page(int? offset, int? limit, int defaultLimit = DefaultLimit)
    {
        return (Math.Max(0, offset ?? 0), Math.Clamp(limit ?? defaultLimit, 1, MaxLimit));
    }
}
