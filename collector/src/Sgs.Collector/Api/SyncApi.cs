using Sgs.Collector.Hosting;

namespace Sgs.Collector.Api;

internal static class SyncApi
{
    public static IEndpointRouteBuilder MapSyncApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sync", (RunCoordinator coordinator) => StartSync(coordinator, false));
        app.MapPost("/api/sync/full", (RunCoordinator coordinator) => StartSync(coordinator, true));
        app.MapPost("/api/restore", StartRestore);
        return app;
    }

    private static IResult StartSync(RunCoordinator coordinator, bool full)
    {
        return coordinator.TryStartSync(full)
            ? ApiResults.Accepted()
            : ApiResults.Error(StatusCodes.Status409Conflict, "当前正忙，无法开始同步");
    }

    private static IResult StartRestore(RunCoordinator coordinator)
    {
        return coordinator.TryStartRestore()
            ? ApiResults.Accepted()
            : ApiResults.Error(StatusCodes.Status409Conflict, "当前正忙，无法开始恢复");
    }
}
