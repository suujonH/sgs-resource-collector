using System.Text.Json;
using Sgs.Collector.Data;
using Sgs.Collector.Hosting;

namespace Sgs.Collector.Api;

internal static class RunsApi
{
    private const string FilterAll = "all";
    private const string FilterHistory = "history";
    private const string FilterNever = "never";

    public static IEndpointRouteBuilder MapRunsApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/runs", StartRun);
        app.MapPost("/api/runs/current/cancel", Cancel);
        app.MapGet("/api/runs", ListAsync);
        app.MapGet("/api/runs/{id:long}", GetAsync);
        app.MapGet("/api/runs/{id:long}/versions", GetVersionsAsync);
        app.MapGet("/api/runs/{id:long}/failures", GetFailuresAsync);
        return app;
    }

    private static IResult StartRun(RunCoordinator coordinator)
    {
        return coordinator.TryStartRun(RunCoordinator.TriggerManual)
            ? ApiResults.Accepted()
            : ApiResults.Error(StatusCodes.Status409Conflict, "当前已有任务在运行");
    }

    private static IResult Cancel(RunCoordinator coordinator)
    {
        return coordinator.TryCancel()
            ? Results.Json(new CancellingDto(true), statusCode: StatusCodes.Status202Accepted)
            : ApiResults.Error(StatusCodes.Status409Conflict, "当前没有可终止的任务");
    }

    private static async Task<IResult> ListAsync(RunRepository runs, int? offset, int? limit)
    {
        var page = ApiResults.Page(offset, limit);
        var result = await runs.ListAsync(page.Offset, page.Limit);
        return Results.Json(new PageDto<RunDto>(result.Total, result.Items.Select(ToDto).ToList()));
    }

    private static async Task<IResult> GetAsync(RunRepository runs, long id)
    {
        var row = await runs.GetAsync(id);
        return row is null ? NotFound() : Results.Json(ToDto(row));
    }

    private static async Task<IResult> GetVersionsAsync(RunRepository runs, long id, int? offset, int? limit)
    {
        if (await runs.GetAsync(id) is null)
        {
            return NotFound();
        }

        var page = ApiResults.Page(offset, limit);
        var result = await runs.GetRunVersionsAsync(id, page.Offset, page.Limit);
        var items = result.Items
            .Select(r => new RunVersionDto(r.ResourceId, r.ResourceKey, r.Version, r.Size, r.ContentType, r.CollectedAt))
            .ToList();
        return Results.Json(new PageDto<RunVersionDto>(result.Total, items));
    }

    private static async Task<IResult> GetFailuresAsync(RunRepository runs, long id, string? filter, int? offset, int? limit)
    {
        var normalized = string.IsNullOrEmpty(filter) ? FilterAll : filter;
        if (!string.Equals(normalized, FilterAll, StringComparison.Ordinal)
            && !string.Equals(normalized, FilterHistory, StringComparison.Ordinal)
            && !string.Equals(normalized, FilterNever, StringComparison.Ordinal))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "filter 只能是 all、history 或 never");
        }

        if (await runs.GetAsync(id) is null)
        {
            return NotFound();
        }

        var page = ApiResults.Page(offset, limit);
        var result = await runs.GetRunFailuresAsync(id, normalized, page.Offset, page.Limit);
        var items = result.Items
            .Select(r => new RunFailureDto(r.ResourceId, r.ResourceKey, r.HttpStatus, r.Error, r.HadHistory != 0, r.OccurredAt))
            .ToList();
        return Results.Json(new PageDto<RunFailureDto>(result.Total, items));
    }

    private static IResult NotFound() => ApiResults.Error(StatusCodes.Status404NotFound, "收集轮次不存在");

    private static RunDto ToDto(RunRow row)
    {
        return new RunDto(row.Id, row.TriggerKind, row.Status, row.StartedAt, row.FinishedAt, ParseStats(row.StatsJson),
            row.Error, row.SyncStatus, row.SyncError);
    }

    private static JsonElement? ParseStats(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
