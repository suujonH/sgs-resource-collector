using Sgs.Collector.Common;
using Sgs.Collector.Data;
using Sgs.Collector.Storage;

namespace Sgs.Collector.Sync;

public sealed class CatalogRestore
{
    private const string TriggerRestore = "restore";
    private const string StatusCompleted = "completed";
    private const string StatusCancelled = "cancelled";
    private const string StatusFailed = "failed";
    private const string SyncSkipped = "skipped";
    private const int PageSize = 1000;

    private readonly SyncRepository _sync;
    private readonly RunRepository _runs;
    private readonly ICatalogClient _client;

    public CatalogRestore(SyncRepository sync, RunRepository runs, ICatalogClient client)
    {
        _sync = sync;
        _runs = runs;
        _client = client;
    }

    public async Task<int> RestoreAsync(Action<long, long>? onProgress, CancellationToken ct)
    {
        if (await _sync.CountCrawlVersionsAsync() > 0)
        {
            throw new InvalidOperationException(
                "本地数据库已有收集产生的版本，不能从 Worker 恢复。若本地数据库已损坏，请停止程序、删除 App_Data/collector.db* 后重启再恢复。");
        }

        var now = Clock.NowIso();
        var imported = 0;
        long runId = 0;
        try
        {
            var stats = await _client.GetStatsAsync(ct);
            runId = await _runs.CreateAsync(TriggerRestore, now);

            var afterKey = "";
            long afterVersion = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await _client.ExportAsync(afterKey, afterVersion, PageSize, ct);
                if (page.Rows.Count > 0)
                {
                    await _sync.ImportPageAsync(page.Rows, runId, now);
                    imported += page.Rows.Count;
                    onProgress?.Invoke(imported, stats.Versions);
                }

                if (page.Next is null)
                {
                    break;
                }

                afterKey = page.Next.Key;
                afterVersion = page.Next.Version;
            }

            await _runs.FinishAsync(runId, StatusCompleted, $"{{\"restored\":{imported}}}", null, Clock.NowIso());
            await _runs.SetSyncResultAsync(runId, SyncSkipped, null);
            return imported;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (runId != 0)
            {
                await _runs.FinishAsync(runId, StatusCancelled, $"{{\"restored\":{imported}}}", null, Clock.NowIso());
            }

            throw;
        }
        catch (Exception ex)
        {
            if (runId != 0)
            {
                await _runs.FinishAsync(runId, StatusFailed, $"{{\"restored\":{imported}}}", ex.Message, Clock.NowIso());
            }

            throw;
        }
    }
}
