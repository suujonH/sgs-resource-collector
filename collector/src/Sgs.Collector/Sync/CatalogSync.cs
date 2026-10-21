using Microsoft.Extensions.Options;
using Sgs.Collector.Data;
using Sgs.Collector.Options;
using Sgs.Collector.Storage;

namespace Sgs.Collector.Sync;

public sealed record SyncReport(int Pushed, int Accepted, IReadOnlyList<string> Problems);

public sealed class CatalogSync
{
    private const int MaxProblems = 50;

    private readonly SyncRepository _sync;
    private readonly ICatalogClient _client;
    private readonly WorkerOptions _options;

    public CatalogSync(SyncRepository sync, ICatalogClient client, IOptions<WorkerOptions> options)
    {
        _sync = sync;
        _client = client;
        _options = options.Value;
    }

    public async Task<SyncReport> PushPendingAsync(Action<long, long>? onProgress, CancellationToken ct)
    {
        var total = await _sync.CountUnsyncedAsync();
        var batchSize = Math.Max(1, _options.SyncBatchSize);
        long done = 0;
        var accepted = 0;
        var problems = new List<string>();
        long cursorResourceId = 0;
        long cursorVersion = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await _sync.GetUnsyncedAfterAsync(cursorResourceId, cursorVersion, batchSize);
            if (batch.Count == 0)
            {
                break;
            }

            // 游标先于结果前移：被拒绝的行保持未同步，但不会被同一轮再次取到
            cursorResourceId = batch[^1].ResourceId;
            cursorVersion = batch[^1].Version;

            var dtos = batch
                .Select(r => new CatalogVersionDto(r.ResourceKey, r.Version, r.Hash, r.Size, r.ContentType, r.CollectedAt))
                .ToList();
            var result = await _client.SyncAsync(dtos, ct);

            var bad = new HashSet<int>();
            foreach (var rejected in result.Rejected)
            {
                if (rejected.Index < 0 || rejected.Index >= batch.Count)
                {
                    continue;
                }

                bad.Add(rejected.Index);
                var row = batch[rejected.Index];
                AddProblem(problems, $"无效：{row.ResourceKey} v{row.Version}：{rejected.Reason}");
            }

            foreach (var conflict in result.Conflicts)
            {
                for (var i = 0; i < batch.Count; i++)
                {
                    if (batch[i].Version == conflict.Version
                        && string.Equals(batch[i].ResourceKey, conflict.Key, StringComparison.Ordinal))
                    {
                        bad.Add(i);
                    }
                }

                AddProblem(problems, $"冲突：{conflict.Key} v{conflict.Version}（Worker 已有不同内容 {conflict.ExistingHash}）");
            }

            var good = batch.Where((_, i) => !bad.Contains(i)).ToList();
            await _sync.MarkSyncedAsync(good);
            accepted += result.Accepted;
            done += batch.Count;
            onProgress?.Invoke(done, total);
        }

        return new SyncReport((int)done, accepted, problems);
    }

    public async Task<SyncReport> FullResyncAsync(Action<long, long>? onProgress, CancellationToken ct)
    {
        await _sync.ResetAllAsync();
        return await PushPendingAsync(onProgress, ct);
    }

    private static void AddProblem(List<string> problems, string message)
    {
        if (problems.Count < MaxProblems)
        {
            problems.Add(message);
        }
    }
}
