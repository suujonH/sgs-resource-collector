using Dapper;
using Microsoft.Data.Sqlite;
using Sgs.Collector.Data;
using Sgs.Collector.Storage;
using Sgs.Collector.Sync;

namespace Sgs.Collector.Tests;

public sealed class CatalogRestoreTests : IAsyncLifetime
{
    private const string Now = "2026-01-01T00:00:01.000Z";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sgs-collector-tests-" + Guid.NewGuid().ToString("N"));
    private Db _db = null!;
    private SyncRepository _sync = null!;
    private RunRepository _runs = null!;

    public async Task InitializeAsync()
    {
        _db = new Db(Path.Combine(_directory, "App_Data", "collector.db"));
        await _db.InitializeAsync();
        _sync = new SyncRepository(_db);
        _runs = new RunRepository(_db);
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

    private static CatalogVersionDto Dto(string key, long version) =>
        new(key, version, $"hash-{key}-{version}", 5, "text/plain", Now);

    private static FakeClient TwoPages() => new(
        [Dto("a", 1), Dto("a", 2)],
        [Dto("b", 1)]);

    [Fact]
    public async Task Restore_TwoPages_ImportsSyncedVersionsAndLatest()
    {
        var restore = new CatalogRestore(_sync, _runs, TwoPages());
        var progress = new List<long>();

        var count = await restore.RestoreAsync((done, _) => progress.Add(done), CancellationToken.None);

        Assert.Equal(3, count);
        Assert.Equal([2L, 3L], progress.ToArray());
        await AssertStateAsync();
        var run = Assert.Single(await _runs.ListAsync(0, 10));
        Assert.Equal("restore", run.TriggerKind);
        Assert.Equal("completed", run.Status);
        Assert.Equal("{\"restored\":3}", run.StatsJson);
        Assert.Equal("skipped", run.SyncStatus);
    }

    [Fact]
    public async Task Restore_RunTwice_IsIdempotent()
    {
        await new CatalogRestore(_sync, _runs, TwoPages()).RestoreAsync(null, CancellationToken.None);
        await new CatalogRestore(_sync, _runs, TwoPages()).RestoreAsync(null, CancellationToken.None);

        await AssertStateAsync();
    }

    [Fact]
    public async Task Restore_HasCrawlVersions_Throws()
    {
        var runId = await _runs.CreateAsync("manual", Now);
        await _sync.ImportPageAsync([Dto("x", 1)], runId, Now);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new CatalogRestore(_sync, _runs, TwoPages()).RestoreAsync(null, CancellationToken.None));
    }

    private async Task AssertStateAsync()
    {
        await using var connection = await _db.OpenAsync();
        Assert.Equal(3, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM versions WHERE synced = 1"));
        Assert.Equal(3, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM versions"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>("SELECT latest_version FROM resources WHERE resource_key='a'"));
        Assert.Equal("hash-a-2", await connection.ExecuteScalarAsync<string>("SELECT latest_hash FROM resources WHERE resource_key='a'"));
        Assert.Equal(3, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM blobs"));
    }

    private sealed class FakeClient : ICatalogClient
    {
        private readonly CatalogVersionDto[] _page1;
        private readonly CatalogVersionDto[] _page2;

        public FakeClient(CatalogVersionDto[] page1, CatalogVersionDto[] page2)
        {
            _page1 = page1;
            _page2 = page2;
        }

        public Task<CatalogStats> GetStatsAsync(CancellationToken ct) => Task.FromResult(new CatalogStats(2, 3));

        public Task<CatalogExportPage> ExportAsync(string afterKey, long afterVersion, int limit, CancellationToken ct)
        {
            return Task.FromResult(afterKey == ""
                ? new CatalogExportPage(_page1, new CatalogCursor("a", 2))
                : new CatalogExportPage(_page2, null));
        }

        public Task<CatalogSyncResult> SyncAsync(IReadOnlyList<CatalogVersionDto> rows, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
