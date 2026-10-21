using Microsoft.Data.Sqlite;
using Sgs.Collector.Data;
using Sgs.Collector.Options;
using Sgs.Collector.Storage;
using Sgs.Collector.Sync;

namespace Sgs.Collector.Tests;

public sealed class CatalogSyncTests : IAsyncLifetime
{
    private const string Now = "2026-01-01T00:00:01.000Z";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sgs-collector-tests-" + Guid.NewGuid().ToString("N"));
    private Db _db = null!;
    private SyncRepository _sync = null!;

    public async Task InitializeAsync()
    {
        _db = new Db(Path.Combine(_directory, "App_Data", "collector.db"));
        await _db.InitializeAsync();
        _sync = new SyncRepository(_db);
        var runs = new RunRepository(_db);
        var runId = await runs.CreateAsync("manual", Now);
        var rows = Enumerable.Range(0, 4)
            .Select(i => new CatalogVersionDto("h/res" + i, 1, "hash" + i, 10, "text/plain", Now))
            .ToList();
        await _sync.ImportPageAsync(rows, runId, Now);
        await _sync.ResetAllAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private CatalogSync Create(FakeClient client, int batchSize) => new(
        _sync,
        client,
        Microsoft.Extensions.Options.Options.Create(new WorkerOptions { SyncBatchSize = batchSize }));

    [Fact]
    public async Task PushPending_RejectedAndConflict_StayUnsynced()
    {
        var client = new FakeClient();
        var report = await Create(client, 10).PushPendingAsync(null, CancellationToken.None);

        Assert.Equal(4, report.Pushed);
        Assert.Equal(2, report.Accepted);
        Assert.Equal(2, report.Problems.Count);
        var remaining = await _sync.GetUnsyncedAfterAsync(0, 0, 10);
        Assert.Equal(["h/res0", "h/res2"], remaining.Select(r => r.ResourceKey).ToArray());
    }

    [Fact]
    public async Task PushPending_SmallBatch_PassesProblemRowsWithoutLooping()
    {
        var client = new FakeClient();
        var progress = new List<(long, long)>();
        var report = await Create(client, 2).PushPendingAsync((d, t) => progress.Add((d, t)), CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.Equal(2, report.Problems.Count);
        Assert.Equal([(2L, 4L), (4L, 4L)], progress.ToArray());
        Assert.Equal(2, (await _sync.GetUnsyncedAfterAsync(0, 0, 10)).Count);
    }

    [Fact]
    public async Task FullResync_ResetsThenPushesAll()
    {
        var client = new FakeClient { RejectNothing = true };
        var sync = Create(client, 10);
        await sync.PushPendingAsync(null, CancellationToken.None);
        Assert.Equal(0, await _sync.CountUnsyncedAsync());

        var report = await sync.FullResyncAsync(null, CancellationToken.None);

        Assert.Equal(4, report.Pushed);
        Assert.Equal(0, await _sync.CountUnsyncedAsync());
    }

    // 批大小 10 时：第 0 行被拒绝，第 2 行冲突；批大小 2 时仅第一批：第 0 行被拒绝，第 1 行冲突
    private sealed class FakeClient : ICatalogClient
    {
        public int Calls { get; private set; }
        public bool RejectNothing { get; set; }

        public Task<CatalogSyncResult> SyncAsync(IReadOnlyList<CatalogVersionDto> rows, CancellationToken ct)
        {
            Calls++;
            if (RejectNothing || (rows.Count == 2 && Calls > 1))
            {
                return Task.FromResult(new CatalogSyncResult(rows.Count, [], []));
            }

            var conflictIndex = rows.Count == 2 ? 1 : 2;
            return Task.FromResult(new CatalogSyncResult(
                rows.Count - 2,
                [new SyncRejected(0, "坏数据")],
                [new SyncConflict(rows[conflictIndex].Key, rows[conflictIndex].Version, "other")]));
        }

        public Task<CatalogStats> GetStatsAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<CatalogExportPage> ExportAsync(string afterKey, long afterVersion, int limit, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
