using System.Globalization;
using Microsoft.Extensions.Options;
using Sgs.Collector.Common;
using Sgs.Collector.Data;
using Sgs.Collector.Options;

namespace Sgs.Collector.Api;

internal static class ResourcesApi
{
    private const int DefaultResourceLimit = 100;
    private const int RecentFailureLimit = 50;
    private static readonly string[] Statuses = ["all", "ok", "failed", "never", "multi"];

    public static IEndpointRouteBuilder MapResourcesApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/resources", SearchAsync);
        app.MapGet("/api/resources/{id:long}", GetAsync);
        return app;
    }

    private static async Task<IResult> SearchAsync(ResourceRepository resources, string? q, string? status, int? offset, int? limit)
    {
        var normalized = string.IsNullOrEmpty(status) ? Statuses[0] : status;
        if (!Statuses.Contains(normalized, StringComparer.Ordinal))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "status 只能是 all、ok、failed、never 或 multi");
        }

        var page = ApiResults.Page(offset, limit, DefaultResourceLimit);
        var result = await resources.SearchAsync(string.IsNullOrWhiteSpace(q) ? null : q.Trim(), normalized, page.Offset, page.Limit);
        var items = result.Items
            .Select(r => new ResourceListItemDto(r.Id, r.ResourceKey, r.ParentId, r.LatestVersion, r.LastOutcome, r.LastHttpStatus,
                r.LastCheckedAt, r.LastSuccessAt, r.LastError))
            .ToList();
        return Results.Json(new PageDto<ResourceListItemDto>(result.Total, items));
    }

    private static async Task<IResult> GetAsync(ResourceRepository resources, IOptions<WorkerOptions> worker, long id)
    {
        var row = await resources.GetAsync(id);
        if (row is null)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "资源不存在");
        }

        string? parentKey = null;
        if (row.ParentId is { } parentId)
        {
            parentKey = (await resources.GetAsync(parentId))?.ResourceKey;
        }

        var baseUrl = StatusApi.WorkerBaseUrl(worker.Value);
        var urlPath = ResourceKeys.ToUrlPath(row.ResourceKey);
        var versions = (await resources.GetVersionsAsync(id))
            .Select(v => new ResourceVersionDto(v.Version, v.Hash, v.Size, v.ContentType, v.CollectedAt, v.RunId, v.Synced != 0,
                $"{baseUrl}/resource/{v.Version.ToString(CultureInfo.InvariantCulture)}/{urlPath}"))
            .ToList();
        var members = (await resources.GetMembersAsync(id))
            .Select(m => new ResourceMemberDto(m.Id, m.ResourceKey, m.LatestVersion, m.LastOutcome))
            .ToList();
        var failures = (await resources.GetRecentFailuresAsync(id, RecentFailureLimit))
            .Select(f => new ResourceFailureDto(f.RunId, f.HttpStatus, f.Error, f.HadHistory != 0, f.OccurredAt))
            .ToList();

        var resource = new ResourceDto(row.Id, row.ResourceKey, row.ParentId, row.SourcePath, row.FetchUrl, row.LatestVersion,
            row.LatestHash, row.Etag, row.LastModified, row.ProcessingRev, row.FirstSeenAt, row.LastSeenRun, row.LastCheckedAt,
            row.LastOutcome, row.LastHttpStatus, row.LastError, row.LastSuccessAt);
        var latestUrl = row.LatestVersion > 0 ? $"{baseUrl}/resource/{urlPath}" : null;

        return Results.Json(new ResourceDetailDto(resource, parentKey, latestUrl, versions, members, failures));
    }
}
