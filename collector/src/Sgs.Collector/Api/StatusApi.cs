using Microsoft.Extensions.Options;
using Sgs.Collector.Common;
using Sgs.Collector.Data;
using Sgs.Collector.Hosting;
using Sgs.Collector.Options;

namespace Sgs.Collector.Api;

internal static class StatusApi
{
    public static IEndpointRouteBuilder MapStatusApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/status", GetStatusAsync);
        return app;
    }

    private static async Task<IResult> GetStatusAsync(
        RunCoordinator coordinator,
        SyncRepository sync,
        IOptions<CollectorOptions> collector,
        IOptions<WorkerOptions> worker)
    {
        var status = coordinator.GetStatus();
        var pending = await sync.CountUnsyncedAsync();
        var local = await sync.CountVersionsAsync();
        var task = status.ManualTask;

        return Results.Json(new StatusDto(
            status.State,
            status.NextScheduledAt is { } next ? Clock.FormatIso(next) : null,
            collector.Value.IntervalMinutes,
            WorkerBaseUrl(worker.Value),
            pending,
            local,
            status.Current,
            status.Last,
            task is null ? null : new ManualTaskDto(task.Kind, task.Done, task.Total, task.Message, task.Finished)));
    }

    public static string WorkerBaseUrl(WorkerOptions options) => options.BaseUrl.TrimEnd('/');
}
