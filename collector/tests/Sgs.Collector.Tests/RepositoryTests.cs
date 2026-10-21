using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Sgs.Collector.Common;
using Sgs.Collector.Data;
using Sgs.Collector.Discovery;
using Sgs.Collector.Storage;

namespace Sgs.Collector.Tests;

// 每个用例使用独立的临时目录和真实 SQLite 文件，用例结束后删除
public sealed class RepositoryTests : IAsyncLifetime
{
    private const string Json = "application/json; charset=utf-8";
    private const string T1 = "2026-01-01T00:00:01.000Z";
    private const string T2 = "2026-01-01T00:00:02.000Z";
    private const string T3 = "2026-01-01T00:00:03.000Z";
    private const string T4 = "2026-01-01T00:00:04.000Z";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sgs-collector-tests-" + Guid.NewGuid().ToString("N"));
    private Db _db = null!;
    private ResourceRepository _resources = null!;
    private BlobRepository _blobs = null!;
    private RunRepository _runs = null!;
    private HeaderRuleRepository _headerRules = null!;
    private SyncRepository _sync = null!;

    public async Task InitializeAsync()
    {
        // 数据目录 App_Data 由 Db.InitializeAsync 负责创建
        _db = new Db(Path.Combine(_directory, "App_Data", "collector.db"));
        await _db.InitializeAsync();
        _resources = new ResourceRepository(_db);
        _blobs = new BlobRepository(_db);
        _runs = new RunRepository(_db);
        _headerRules = new HeaderRuleRepository(_db);
        _sync = new SyncRepository(_db);
    }

    public Task DisposeAsync()
    {
        // 连接池会一直持有文件句柄，Windows 上必须先清空才能删除
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ---------- Db ----------

    [Fact]
    public async Task InitializeAsync_RunTwice_KeepsSingleDefaultHeaderRule()
    {
        await _db.InitializeAsync(); // 夹具里已经初始化过一次，这里是第二次

        var rule = Assert.Single(await _headerRules.ListAsync());
        Assert.Equal("*", rule.Pattern);
        Assert.Equal(1, rule.Enabled);
        Assert.Equal("默认浏览器请求头", rule.Note);

        using var json = JsonDocument.Parse(rule.HeadersJson);
        var headers = json.RootElement.EnumerateArray()
            .Select(item => (item.GetProperty("name").GetString(), item.GetProperty("value").GetString()))
            .ToArray();
        Assert.Equal(
            new (string?, string?)[]
            {
                ("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36"),
                ("Accept-Language", "zh-CN,zh;q=0.9"),
                ("sec-ch-ua", "\"Google Chrome\";v=\"149\", \"Chromium\";v=\"149\", \"Not)A;Brand\";v=\"24\""),
                ("sec-ch-ua-mobile", "?0"),
                ("sec-ch-ua-platform", "\"Windows\"")
            },
            headers);

        Assert.Equal(1, await ScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal("wal", await ScalarAsync<string>("PRAGMA journal_mode"));
        Assert.Equal(1, await ScalarAsync<long>("PRAGMA foreign_keys"));
    }

    [Fact]
    public async Task WriteAsync_WhenWorkThrows_RollsBackAndReleasesTheWriteGate()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(
                "INSERT INTO blobs (hash, size, uploaded_at) VALUES ('rolled-back', 1, 'x')",
                transaction: transaction);
            throw new InvalidOperationException("boom");
        }));

        Assert.False(await _blobs.ExistsAsync("rolled-back"));

        // 写入闸门已释放，后续写入不会卡住
        await _blobs.MarkUploadedAsync("after-failure", 1, T1);
        Assert.True(await _blobs.ExistsAsync("after-failure"));
    }

    [Fact]
    public async Task WriteAsync_SerializesConcurrentWriters()
    {
        var runId = await NewRunAsync();
        var tasks = new List<Task>();
        for (var i = 0; i < 40; i++)
        {
            var key = $"host/{i}";
            tasks.Add(_resources.UpsertSeenAsync(key, "https://" + key, runId, T1));
            tasks.Add(_blobs.MarkUploadedAsync("shared-hash", 1, T1));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(40, await ScalarAsync<long>("SELECT COUNT(*) FROM resources"));
        Assert.Equal(1, await ScalarAsync<long>("SELECT COUNT(*) FROM blobs"));
    }

    // ---------- ResourceRepository ----------

    [Fact]
    public async Task UpsertSeenAsync_FirstCallInserts_SecondCallReturnsSameIdAndUpdatesFetchUrl()
    {
        var run1 = await NewRunAsync();
        var run2 = await NewRunAsync();

        var first = await _resources.UpsertSeenAsync("host/a.json", "https://host/a.json?v=1", run1, T1);
        var second = await _resources.UpsertSeenAsync("host/a.json", "https://host/a.json?v=2", run2, T2);

        Assert.True(first.Id > 0);
        Assert.Equal("host/a.json", first.ResourceKey);
        Assert.Equal("https://host/a.json?v=1", first.FetchUrl);
        Assert.Equal(T1, first.FirstSeenAt);
        Assert.Equal(run1, first.LastSeenRun);
        Assert.Equal(0, first.LatestVersion);
        Assert.Null(first.LatestHash);
        Assert.Null(first.ParentId);
        Assert.Null(first.SourcePath);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("https://host/a.json?v=2", second.FetchUrl);
        Assert.Equal(run2, second.LastSeenRun);
        Assert.Equal(T1, second.FirstSeenAt); // 首次发现时间不会被覆盖
        Assert.Equal(1, await ScalarAsync<long>("SELECT COUNT(*) FROM resources"));
    }

    [Fact]
    public async Task UpsertMemberAsync_RecordsParentAndSourcePath_AndKeepsMemberKeyFixedPerSourcePath()
    {
        var run1 = await NewRunAsync();
        var run2 = await NewRunAsync();
        var pkg = await SeenAsync("host/pkg.sgs", run1);
        var other = await SeenAsync("host/other.sgs", run1);

        var member = await _resources.UpsertMemberAsync("host/pkg.sgs/cha_spell.json", pkg.Id, "cha_spell.sgs", run1, T1);
        var again = await _resources.UpsertMemberAsync("host/pkg.sgs/cha_spell.json", pkg.Id, "cha_spell.sgs", run2, T2);
        await _resources.UpsertMemberAsync("host/other.sgs/x.bin", other.Id, "x.bin", run1, T1);

        Assert.Equal(pkg.Id, member.ParentId);
        Assert.Equal("cha_spell.sgs", member.SourcePath);
        Assert.Null(member.FetchUrl);
        Assert.Equal(run1, member.LastSeenRun);
        Assert.Equal(T1, member.FirstSeenAt);
        Assert.Equal(member.Id, again.Id);
        Assert.Equal(run2, again.LastSeenRun);
        Assert.Equal(T1, again.FirstSeenAt);

        var keys = await _resources.GetMemberKeysAsync(pkg.Id);
        var entry = Assert.Single(keys);
        Assert.Equal("cha_spell.sgs", entry.Key);
        Assert.Equal("host/pkg.sgs/cha_spell.json", entry.Value);
        Assert.False(keys.ContainsKey("CHA_SPELL.SGS")); // Ordinal 比较

        // D5：同一个包内同一个原始路径只能对应一个 key，由 (parent_id, source_path) 唯一索引保证
        await Assert.ThrowsAsync<SqliteException>(() =>
            _resources.UpsertMemberAsync("host/pkg.sgs/renamed.json", pkg.Id, "cha_spell.sgs", run2, T2));
        var afterConflict = await _resources.GetMemberKeysAsync(pkg.Id);
        Assert.Equal("host/pkg.sgs/cha_spell.json", Assert.Single(afterConflict).Value);
    }

    [Fact]
    public async Task AddVersionAsync_AssignsSequentialVersions_AndIgnoresHashEqualToLatest()
    {
        var runId = await NewRunAsync();
        var resource = await SeenAsync("host/a.json", runId);
        var hashA = HashOf("A");
        var hashB = HashOf("B");

        var v1 = await _resources.AddVersionAsync(resource.Id, hashA, 1, Json, runId, T1);
        var v2 = await _resources.AddVersionAsync(resource.Id, hashB, 2, Json, runId, T2);
        var same = await _resources.AddVersionAsync(resource.Id, hashB, 2, Json, runId, T3);

        Assert.Equal(1, v1);
        Assert.Equal(2, v2);
        Assert.Equal(2, same); // 与最新版本哈希相同：不新建版本

        var versions = await _resources.GetVersionsAsync(resource.Id);
        Assert.Equal(new long[] { 2, 1 }, versions.Select(v => v.Version)); // 倒序
        var latest = versions[0];
        Assert.Equal(resource.Id, latest.ResourceId);
        Assert.Equal(hashB, latest.Hash);
        Assert.Equal(2, latest.Size);
        Assert.Equal(Json, latest.ContentType);
        Assert.Equal(T2, latest.CollectedAt);
        Assert.Equal(runId, latest.RunId);
        Assert.Equal(0, latest.Synced);

        var row = await _resources.GetAsync(resource.Id);
        Assert.NotNull(row);
        Assert.Equal(2, row.LatestVersion);
        Assert.Equal(hashB, row.LatestHash);

        // 只和最新版本比较：内容改回旧版本也算一个新版本
        var v3 = await _resources.AddVersionAsync(resource.Id, hashA, 1, Json, runId, T3);
        Assert.Equal(3, v3);
        Assert.Equal(3, await ScalarAsync<long>("SELECT COUNT(*) FROM versions"));
    }

    [Fact]
    public async Task CommitProcessedAsync_ReplacesLinksOnSecondCall_AndStoresValidatorsAndProcessingRev()
    {
        var runId = await NewRunAsync();
        var resource = await SeenAsync("host/pkg.sgs", runId);

        await _resources.CommitProcessedAsync(
            resource.Id, "new_version", 200, "\"etag-1\"", "Mon, 01 Jan 2026 00:00:00 GMT", 3,
            [Link("https://host/a.json", "script"), Link("https://host/b.json", "json"), Link("https://host/a.json", "duplicate")],
            T1);

        var first = await _resources.GetAsync(resource.Id);
        Assert.NotNull(first);
        Assert.Equal("\"etag-1\"", first.Etag);
        Assert.Equal("Mon, 01 Jan 2026 00:00:00 GMT", first.LastModified);
        Assert.Equal(3, first.ProcessingRev);
        Assert.Equal("new_version", first.LastOutcome);
        Assert.Equal(200, first.LastHttpStatus);
        Assert.Equal(T1, first.LastCheckedAt);
        Assert.Equal(T1, first.LastSuccessAt);
        Assert.Null(first.LastError);
        // 重复的 url 被 OR IGNORE 忽略，先写入的 source 保留
        Assert.Equal(
            new[] { "https://host/a.json|script", "https://host/b.json|json" },
            await LinkTextsAsync(resource.Id));

        await _resources.CommitProcessedAsync(
            resource.Id, "unchanged", 200, "\"etag-2\"", null, null,
            [Link("https://host/c.json", "regex")],
            T2);

        var second = await _resources.GetAsync(resource.Id);
        Assert.NotNull(second);
        Assert.Equal("\"etag-2\"", second.Etag);
        Assert.Null(second.LastModified);
        Assert.Null(second.ProcessingRev);
        Assert.Equal("unchanged", second.LastOutcome);
        Assert.Equal(T2, second.LastCheckedAt);
        Assert.Equal(T2, second.LastSuccessAt);
        Assert.Equal(new[] { "https://host/c.json|regex" }, await LinkTextsAsync(resource.Id)); // 旧链接被整体替换

        await _resources.CommitProcessedAsync(resource.Id, "unchanged", 200, null, null, null, [], T3);
        Assert.Empty(await _resources.GetLinksAsync(resource.Id));
    }

    [Fact]
    public async Task MarkNotModifiedAsync_KeepsValidatorsProcessingRevAndLinks_AndClearsError()
    {
        var runId = await NewRunAsync();
        var resource = await SeenAsync("host/pkg.sgs", runId);
        await _resources.CommitProcessedAsync(resource.Id, "new_version", 200, "\"e\"", "LM", 5, [Link("https://host/a.json", "script")], T1);
        await _resources.MarkFailedAsync(resource.Id, 500, "boom", false, T2);

        await _resources.MarkNotModifiedAsync(resource.Id, 304, T3);

        var row = await _resources.GetAsync(resource.Id);
        Assert.NotNull(row);
        Assert.Equal("not_modified", row.LastOutcome);
        Assert.Equal(304, row.LastHttpStatus);
        Assert.Equal(T3, row.LastCheckedAt);
        Assert.Equal(T3, row.LastSuccessAt);
        Assert.Null(row.LastError);
        Assert.Equal("\"e\"", row.Etag);
        Assert.Equal("LM", row.LastModified);
        Assert.Equal(5, row.ProcessingRev);
        Assert.Equal(new[] { "https://host/a.json|script" }, await LinkTextsAsync(resource.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkFailedAsync_ClearsValidatorsOnlyWhenRequested(bool clearValidators)
    {
        var runId = await NewRunAsync();
        var resource = await SeenAsync("host/pkg.sgs", runId);
        await _resources.CommitProcessedAsync(resource.Id, "new_version", 200, "\"e\"", "LM", 7, [Link("https://host/a.json", "script")], T1);

        await _resources.MarkFailedAsync(resource.Id, 503, "服务不可用", clearValidators, T2);

        var row = await _resources.GetAsync(resource.Id);
        Assert.NotNull(row);
        Assert.Equal("failed", row.LastOutcome);
        Assert.Equal(503, row.LastHttpStatus);
        Assert.Equal("服务不可用", row.LastError);
        Assert.Equal(T2, row.LastCheckedAt);
        Assert.Equal(T1, row.LastSuccessAt); // 失败不改变最后成功时间
        Assert.Equal(7, row.ProcessingRev);
        Assert.Single(await _resources.GetLinksAsync(resource.Id));
        if (clearValidators)
        {
            Assert.Null(row.Etag);
            Assert.Null(row.LastModified);
        }
        else
        {
            Assert.Equal("\"e\"", row.Etag);
            Assert.Equal("LM", row.LastModified);
        }
    }

    [Fact]
    public async Task MarkFailedAsync_AllowsNullHttpStatus()
    {
        var runId = await NewRunAsync();
        var resource = await SeenAsync("host/a.json", runId);

        await _resources.MarkFailedAsync(resource.Id, null, "网络错误", false, T1);

        var row = await _resources.GetAsync(resource.Id);
        Assert.NotNull(row);
        Assert.Equal("failed", row.LastOutcome);
        Assert.Null(row.LastHttpStatus);
        Assert.Equal("网络错误", row.LastError);
    }

    [Fact]
    public async Task MarkMemberCheckedAsync_SetsOutcomeAndClearsHttpStatusAndError()
    {
        var runId = await NewRunAsync();
        var pkg = await SeenAsync("host/pkg.sgs", runId);
        var member = await _resources.UpsertMemberAsync("host/pkg.sgs/a.json", pkg.Id, "a.sgs", runId, T1);
        await _resources.MarkFailedAsync(member.Id, 500, "boom", false, T1);

        await _resources.MarkMemberCheckedAsync(member.Id, "unchanged", T2);

        var row = await _resources.GetAsync(member.Id);
        Assert.NotNull(row);
        Assert.Equal("unchanged", row.LastOutcome);
        Assert.Null(row.LastHttpStatus);
        Assert.Null(row.LastError);
        Assert.Equal(T2, row.LastCheckedAt);
        Assert.Equal(T2, row.LastSuccessAt);
    }

    [Fact]
    public async Task SearchAsync_FiltersByStatusAndKeyword_AndPagesWithTotal()
    {
        var runId = await NewRunAsync();
        var ok1 = await SeenAsync("host/a/ok1.json", runId);
        var ok2 = await SeenAsync("host/a/OK2.json", runId);
        var failed = await SeenAsync("host/b/failed.json", runId);
        var never = await SeenAsync("host/b/never.json", runId);
        var multi = await SeenAsync("host/c/multi.json", runId);

        await _resources.AddVersionAsync(ok1.Id, HashOf("1"), 1, Json, runId, T1);
        await _resources.CommitProcessedAsync(ok1.Id, "new_version", 200, null, null, null, [], T1);
        await _resources.AddVersionAsync(ok2.Id, HashOf("2"), 1, Json, runId, T1); // last_outcome 仍为 NULL
        await _resources.AddVersionAsync(failed.Id, HashOf("3"), 1, Json, runId, T1);
        await _resources.MarkFailedAsync(failed.Id, 500, "boom", false, T2);
        await _resources.MarkFailedAsync(never.Id, 404, "missing", false, T2); // 从未成功
        await _resources.AddVersionAsync(multi.Id, HashOf("4"), 1, Json, runId, T1);
        await _resources.AddVersionAsync(multi.Id, HashOf("5"), 1, Json, runId, T2);

        var all = await _resources.SearchAsync(null, "all", 0, 50);
        Assert.Equal(5, all.Total);
        Assert.Equal(
            new[] { "host/a/OK2.json", "host/a/ok1.json", "host/b/failed.json", "host/b/never.json", "host/c/multi.json" },
            all.Items.Select(i => i.ResourceKey)); // ORDER BY resource_key（区分大小写的二进制顺序）

        Assert.Equal(new[] { "host/a/OK2.json", "host/a/ok1.json", "host/c/multi.json" }, await SearchKeysAsync(null, "ok"));
        Assert.Equal(new[] { "host/b/failed.json" }, await SearchKeysAsync(null, "failed"));
        Assert.Equal(new[] { "host/b/never.json" }, await SearchKeysAsync(null, "never"));
        Assert.Equal(new[] { "host/c/multi.json" }, await SearchKeysAsync(null, "multi"));
        Assert.Equal(5, (await _resources.SearchAsync(null, "unknown-status", 0, 50)).Total); // 其它值按 all 处理

        Assert.Equal(new[] { "host/a/OK2.json", "host/a/ok1.json" }, await SearchKeysAsync("OK", "all")); // 不区分大小写
        Assert.Empty(await SearchKeysAsync("ok", "failed"));
        Assert.Equal(0, (await _resources.SearchAsync("%", "all", 0, 50)).Total); // 子串匹配，% 不是通配符

        var page = await _resources.SearchAsync(null, "all", 1, 2);
        Assert.Equal(5, page.Total);
        Assert.Equal(new[] { "host/a/ok1.json", "host/b/failed.json" }, page.Items.Select(i => i.ResourceKey));

        var item = Assert.Single((await _resources.SearchAsync("failed", "all", 0, 10)).Items);
        Assert.Equal(failed.Id, item.Id);
        Assert.Null(item.ParentId);
        Assert.Equal(1, item.LatestVersion);
        Assert.Equal("failed", item.LastOutcome);
        Assert.Equal(500, item.LastHttpStatus);
        Assert.Equal(T2, item.LastCheckedAt);
        Assert.Null(item.LastSuccessAt);
        Assert.Equal("boom", item.LastError);
    }

    [Fact]
    public async Task DetailQueries_ReturnMembersAndRecentFailuresInDocumentedOrder()
    {
        var runId = await NewRunAsync();
        var pkg = await SeenAsync("host/pkg.sgs", runId);
        await _resources.UpsertMemberAsync("host/pkg.sgs/b.json", pkg.Id, "b.sgs", runId, T1);
        var memberA = await _resources.UpsertMemberAsync("host/pkg.sgs/a.json", pkg.Id, "a.sgs", runId, T1);
        await _resources.AddVersionAsync(memberA.Id, HashOf("1"), 1, Json, runId, T1);
        await _resources.MarkMemberCheckedAsync(memberA.Id, "new_version", T1);

        var members = await _resources.GetMembersAsync(pkg.Id);
        Assert.Equal(new[] { "host/pkg.sgs/a.json", "host/pkg.sgs/b.json" }, members.Select(m => m.ResourceKey));
        Assert.Equal(memberA.Id, members[0].Id);
        Assert.Equal(1, members[0].LatestVersion);
        Assert.Equal("new_version", members[0].LastOutcome);
        Assert.Equal(0, members[1].LatestVersion);
        Assert.Null(members[1].LastOutcome);

        await _runs.AddFailureAsync(runId, pkg.Id, 500, "e1", true, T1);
        await _runs.AddFailureAsync(runId, pkg.Id, 404, "e2", false, T2);
        await _runs.AddFailureAsync(runId, pkg.Id, null, "e3", true, T3);
        var failures = await _resources.GetRecentFailuresAsync(pkg.Id, 2);
        Assert.Equal(new[] { "e3", "e2" }, failures.Select(f => f.Error)); // 最近的在前，受 limit 限制
        Assert.Null(failures[0].HttpStatus);
        Assert.Equal(1, failures[0].HadHistory);
        Assert.Equal(0, failures[1].HadHistory);
        Assert.Equal(runId, failures[0].RunId);
        Assert.Equal(pkg.Id, failures[0].ResourceId);
        Assert.Equal(T3, failures[0].OccurredAt);

        Assert.Null(await _resources.GetAsync(999_999));
    }

    // ---------- BlobRepository ----------

    [Fact]
    public async Task MarkUploadedAsync_IsIdempotentAndKeepsTheFirstRecord()
    {
        var hash = HashOf("blob");
        Assert.False(await _blobs.ExistsAsync(hash));

        await _blobs.MarkUploadedAsync(hash, 10, T1);
        await _blobs.MarkUploadedAsync(hash, 99, T2); // 同一哈希重复登记：OR IGNORE

        Assert.True(await _blobs.ExistsAsync(hash));
        Assert.Equal(1, await ScalarAsync<long>("SELECT COUNT(*) FROM blobs"));
        Assert.Equal(10, await ScalarAsync<long>("SELECT size FROM blobs WHERE hash = @hash", new { hash }));
        Assert.Equal(T1, await ScalarAsync<string>("SELECT uploaded_at FROM blobs WHERE hash = @hash", new { hash }));
    }

    // ---------- RunRepository ----------

    [Fact]
    public async Task RunLifecycle_CreateFinishAndSetSyncResult_AreStored()
    {
        var id = await _runs.CreateAsync("schedule", T1);

        var created = await _runs.GetAsync(id);
        Assert.NotNull(created);
        Assert.Equal("schedule", created.TriggerKind);
        Assert.Equal("running", created.Status);
        Assert.Equal(T1, created.StartedAt);
        Assert.Null(created.FinishedAt);
        Assert.Null(created.StatsJson);
        Assert.Null(created.Error);
        Assert.Null(created.SyncStatus);
        Assert.Null(created.SyncError);

        await _runs.FinishAsync(id, "completed", "{\"total\":3}", null, T2);
        await _runs.SetSyncResultAsync(id, "failed", "同步失败");

        var finished = await _runs.GetAsync(id);
        Assert.NotNull(finished);
        Assert.Equal("completed", finished.Status);
        Assert.Equal("{\"total\":3}", finished.StatsJson);
        Assert.Null(finished.Error);
        Assert.Equal(T2, finished.FinishedAt);
        Assert.Equal("failed", finished.SyncStatus);
        Assert.Equal("同步失败", finished.SyncError);

        Assert.Null(await _runs.GetAsync(999_999));
    }

    [Fact]
    public async Task RecoverStaleAsync_FailsOnlyRunsThatAreStillRunning()
    {
        var running1 = await _runs.CreateAsync("manual", T1);
        var running2 = await _runs.CreateAsync("restore", T1);
        var completed = await _runs.CreateAsync("schedule", T1);
        await _runs.FinishAsync(completed, "completed", null, null, T2);

        var recovered = await _runs.RecoverStaleAsync(T3);

        Assert.Equal(2, recovered);
        foreach (var id in new[] { running1, running2 })
        {
            var run = await _runs.GetAsync(id);
            Assert.NotNull(run);
            Assert.Equal("failed", run.Status);
            Assert.Equal("收集进程在运行中退出", run.Error);
            Assert.Equal(T3, run.FinishedAt);
        }

        var untouched = await _runs.GetAsync(completed);
        Assert.NotNull(untouched);
        Assert.Equal("completed", untouched.Status);
        Assert.Null(untouched.Error);
        Assert.Equal(T2, untouched.FinishedAt);
        Assert.Equal(0, await _runs.RecoverStaleAsync(T4));
    }

    [Fact]
    public async Task GetLastCrawlEndAsync_IgnoresRestoreRuns()
    {
        Assert.Null(await _runs.GetLastCrawlEndAsync()); // 没有任何记录

        var restore1 = await _runs.CreateAsync("restore", T1);
        await _runs.FinishAsync(restore1, "completed", null, null, T1);
        Assert.Null(await _runs.GetLastCrawlEndAsync()); // 只有 restore 轮次

        var manual = await _runs.CreateAsync("manual", T1);
        await _runs.FinishAsync(manual, "completed", null, null, T2);
        Assert.Equal(T2, await _runs.GetLastCrawlEndAsync()); // 已结束：取 finished_at

        await _runs.CreateAsync("schedule", T3);
        Assert.Equal(T3, await _runs.GetLastCrawlEndAsync()); // 仍在运行：取 started_at

        var restore2 = await _runs.CreateAsync("restore", T4);
        await _runs.FinishAsync(restore2, "completed", null, null, T4);
        Assert.Equal(T3, await _runs.GetLastCrawlEndAsync()); // 更新的 restore 轮次被忽略
    }

    [Fact]
    public async Task ListAsync_ReturnsNewestFirstWithPaging()
    {
        var id1 = await _runs.CreateAsync("manual", T1);
        var id2 = await _runs.CreateAsync("schedule", T2);
        var id3 = await _runs.CreateAsync("restore", T3);

        Assert.Equal(new[] { id3, id2 }, (await _runs.ListAsync(0, 2)).Select(r => r.Id));
        Assert.Equal(new[] { id1 }, (await _runs.ListAsync(2, 2)).Select(r => r.Id));
    }

    [Fact]
    public async Task GetRunFailuresAsync_FiltersByHistoryAndPagesWithTotal()
    {
        var runId = await NewRunAsync();
        var otherRun = await NewRunAsync();
        var a = await SeenAsync("host/a", runId);
        var b = await SeenAsync("host/b", runId);
        var c = await SeenAsync("host/c", runId);
        await _runs.AddFailureAsync(runId, a.Id, 404, "未找到", false, T1);
        await _runs.AddFailureAsync(runId, b.Id, 500, "服务器错误", true, T2);
        await _runs.AddFailureAsync(runId, c.Id, null, "超时", true, T3);
        await _runs.AddFailureAsync(otherRun, a.Id, 404, "别的轮次", false, T3);

        var all = await _runs.GetRunFailuresAsync(runId, "all", 0, 10);
        Assert.Equal(3, all.Total);
        Assert.Equal(new[] { "host/a", "host/b", "host/c" }, all.Items.Select(i => i.ResourceKey));
        Assert.Equal(a.Id, all.Items[0].ResourceId);
        Assert.Equal(404, all.Items[0].HttpStatus);
        Assert.Equal("未找到", all.Items[0].Error);
        Assert.Equal(0, all.Items[0].HadHistory);
        Assert.Equal(T1, all.Items[0].OccurredAt);
        Assert.Null(all.Items[2].HttpStatus);
        Assert.Equal(1, all.Items[2].HadHistory);

        var history = await _runs.GetRunFailuresAsync(runId, "history", 0, 10);
        Assert.Equal(2, history.Total);
        Assert.Equal(new[] { "host/b", "host/c" }, history.Items.Select(i => i.ResourceKey));

        var never = await _runs.GetRunFailuresAsync(runId, "never", 0, 10);
        Assert.Equal(1, never.Total);
        Assert.Equal("host/a", Assert.Single(never.Items).ResourceKey);

        Assert.Equal(3, (await _runs.GetRunFailuresAsync(runId, "other-filter", 0, 10)).Total); // 其它值按 all 处理

        var page = await _runs.GetRunFailuresAsync(runId, "all", 1, 1);
        Assert.Equal(3, page.Total);
        Assert.Equal("host/b", Assert.Single(page.Items).ResourceKey);
    }

    [Fact]
    public async Task PruneFailuresAsync_DeletesOnlyFailuresOlderThanCutoff()
    {
        var runId = await NewRunAsync();
        var resource = await SeenAsync("host/a", runId);
        await _runs.AddFailureAsync(runId, resource.Id, 500, "old-1", true, T1);
        await _runs.AddFailureAsync(runId, resource.Id, 500, "old-2", true, T2);
        await _runs.AddFailureAsync(runId, resource.Id, 500, "kept", true, T3);

        var deleted = await _runs.PruneFailuresAsync(T3);

        Assert.Equal(2, deleted); // 严格早于截止时间
        var remaining = await _resources.GetRecentFailuresAsync(resource.Id, 10);
        Assert.Equal("kept", Assert.Single(remaining).Error);
    }

    [Fact]
    public async Task GetRunVersionsAsync_ListsOnlyThatRunOrderedByKeyWithPaging()
    {
        var run1 = await NewRunAsync();
        var run2 = await NewRunAsync();
        var a = await SeenAsync("host/a", run1);
        var b = await SeenAsync("host/b", run1);
        var c = await SeenAsync("host/c", run1);
        await _resources.AddVersionAsync(b.Id, HashOf("b"), 20, Json, run1, T2); // 故意先写 b，再写 a
        await _resources.AddVersionAsync(a.Id, HashOf("a"), 10, "image/webp", run1, T1);
        await _resources.AddVersionAsync(c.Id, HashOf("c"), 30, Json, run2, T3);

        var page = await _runs.GetRunVersionsAsync(run1, 0, 10);
        Assert.Equal(2, page.Total);
        Assert.Equal(new[] { "host/a", "host/b" }, page.Items.Select(i => i.ResourceKey));
        var first = page.Items[0];
        Assert.Equal(a.Id, first.ResourceId);
        Assert.Equal(1, first.Version);
        Assert.Equal(10, first.Size);
        Assert.Equal("image/webp", first.ContentType);
        Assert.Equal(T1, first.CollectedAt);

        var second = await _runs.GetRunVersionsAsync(run1, 1, 1);
        Assert.Equal(2, second.Total);
        Assert.Equal("host/b", Assert.Single(second.Items).ResourceKey);

        Assert.Equal("host/c", Assert.Single((await _runs.GetRunVersionsAsync(run2, 0, 10)).Items).ResourceKey);
    }

    // ---------- HeaderRuleRepository ----------

    [Fact]
    public async Task HeaderRules_InsertUpdateDelete_ReportAffectedRows()
    {
        var headersJson = "[{\"name\":\"Referer\",\"value\":\"https://web.sanguosha.com/\"}]";

        var id = await _headerRules.InsertAsync("*.sgs", headersJson, true, "备注", T1);

        var inserted = await _headerRules.GetAsync(id);
        Assert.NotNull(inserted);
        Assert.Equal("*.sgs", inserted.Pattern);
        Assert.Equal(headersJson, inserted.HeadersJson);
        Assert.Equal(1, inserted.Enabled);
        Assert.Equal("备注", inserted.Note);
        Assert.Equal(T1, inserted.UpdatedAt);

        Assert.True(await _headerRules.UpdateAsync(id, "web.sanguosha.com/*", "[]", false, null, T2));
        var updated = await _headerRules.GetAsync(id);
        Assert.NotNull(updated);
        Assert.Equal("web.sanguosha.com/*", updated.Pattern);
        Assert.Equal("[]", updated.HeadersJson);
        Assert.Equal(0, updated.Enabled);
        Assert.Null(updated.Note);
        Assert.Equal(T2, updated.UpdatedAt);
        Assert.False(await _headerRules.UpdateAsync(999_999, "x", "[]", true, null, T2));

        // ORDER BY id：初始化时插入的默认规则在前
        Assert.Equal(new[] { "*", "web.sanguosha.com/*" }, (await _headerRules.ListAsync()).Select(r => r.Pattern));

        Assert.True(await _headerRules.DeleteAsync(id));
        Assert.False(await _headerRules.DeleteAsync(id));
        Assert.Null(await _headerRules.GetAsync(id));
        Assert.Equal("*", Assert.Single(await _headerRules.ListAsync()).Pattern);
    }

    // ---------- SyncRepository ----------

    [Fact]
    public async Task GetUnsyncedAfterAsync_PagesByCursor_AndMarkSyncedAsyncMarksOnlyTheGivenRows()
    {
        var runId = await NewRunAsync();
        var a = await SeenAsync("host/a", runId);
        var b = await SeenAsync("host/b", runId);
        await _resources.AddVersionAsync(a.Id, HashOf("a1"), 1, Json, runId, T1);
        await _resources.AddVersionAsync(a.Id, HashOf("a2"), 2, Json, runId, T2);
        await _resources.AddVersionAsync(b.Id, HashOf("b1"), 3, "image/webp", runId, T3);
        Assert.Equal(3, await _sync.CountUnsyncedAsync());
        Assert.Equal(3, await _sync.CountVersionsAsync());

        var page1 = await _sync.GetUnsyncedAfterAsync(0, 0, 2);
        Assert.Equal(new[] { (a.Id, 1L), (a.Id, 2L) }, page1.Select(r => (r.ResourceId, r.Version)));
        Assert.Equal("host/a", page1[0].ResourceKey);
        Assert.Equal(HashOf("a1"), page1[0].Hash);
        Assert.Equal(1, page1[0].Size);
        Assert.Equal(Json, page1[0].ContentType);
        Assert.Equal(T1, page1[0].CollectedAt);

        var page2 = await _sync.GetUnsyncedAfterAsync(a.Id, 2, 2);
        Assert.Equal(new[] { (b.Id, 1L) }, page2.Select(r => (r.ResourceId, r.Version)));
        Assert.Empty(await _sync.GetUnsyncedAfterAsync(b.Id, 1, 2));

        // 游标之后的行不受前面未同步的行影响：被拒绝的行不会挡住后面的行
        var afterRejected = await _sync.GetUnsyncedAfterAsync(a.Id, 1, 10);
        Assert.Equal(new[] { (a.Id, 2L), (b.Id, 1L) }, afterRejected.Select(r => (r.ResourceId, r.Version)));

        await _sync.MarkSyncedAsync([]); // 空列表什么也不做
        Assert.Equal(3, await _sync.CountUnsyncedAsync());

        await _sync.MarkSyncedAsync([page1[1]]); // 只标记 (a, 2)
        Assert.Equal(2, await _sync.CountUnsyncedAsync());
        var remaining = await _sync.GetUnsyncedAfterAsync(0, 0, 10);
        Assert.Equal(new[] { (a.Id, 1L), (b.Id, 1L) }, remaining.Select(r => (r.ResourceId, r.Version)));

        await _sync.ResetAllAsync();
        Assert.Equal(3, await _sync.CountUnsyncedAsync());
    }

    [Fact]
    public async Task ImportPageAsync_IsIdempotent_AndRestoredVersionsAreSyncedAndNotCountedAsCrawl()
    {
        var restoreRun = await _runs.CreateAsync("restore", T1);
        CatalogVersionDto[] rows =
        [
            new("host/a.json", 1, HashOf("a1"), 10, Json, T1),
            new("host/a.json", 2, HashOf("a2"), 20, Json, T2),
            new("host/b.png", 1, HashOf("b1"), 30, "image/png", T3)
        ];

        await _sync.ImportPageAsync(rows, restoreRun, T4);
        await _sync.ImportPageAsync(rows, restoreRun, T4); // 重复执行，结果不变

        Assert.Equal(3, await _sync.CountVersionsAsync());
        Assert.Equal(0, await _sync.CountUnsyncedAsync());
        Assert.Equal(0, await _sync.CountCrawlVersionsAsync());
        Assert.Equal(2, await ScalarAsync<long>("SELECT COUNT(*) FROM resources"));
        Assert.Equal(3, await ScalarAsync<long>("SELECT COUNT(*) FROM blobs"));
        Assert.True(await _blobs.ExistsAsync(HashOf("a1")));
        Assert.True(await _blobs.ExistsAsync(HashOf("b1")));
        Assert.Equal(3, await ScalarAsync<long>("SELECT COUNT(*) FROM versions WHERE synced = 1 AND run_id = @restoreRun", new { restoreRun }));

        var a = await FindByKeyAsync("host/a.json");
        Assert.Equal(2, a.LatestVersion);
        Assert.Equal(HashOf("a2"), a.LatestHash);
        Assert.Equal(T1, a.FirstSeenAt);
        Assert.Equal(T2, a.LastSuccessAt);
        var version2 = (await _resources.GetVersionsAsync(a.Id))[0];
        Assert.Equal(2, version2.Version);
        Assert.Equal(20, version2.Size);
        Assert.Equal(Json, version2.ContentType);
        Assert.Equal(T2, version2.CollectedAt);
        Assert.Equal(1, version2.Synced);

        // 恢复之后继续收集：版本号接着 Worker 上的最新版本往下排
        var crawlRun = await NewRunAsync();
        var next = await _resources.AddVersionAsync(a.Id, HashOf("a3"), 5, Json, crawlRun, T4);
        Assert.Equal(3, next);
        Assert.Equal(1, await _sync.CountCrawlVersionsAsync());
        Assert.Equal(1, await _sync.CountUnsyncedAsync());
    }

    [Fact]
    public async Task ImportPageAsync_LowerVersionAfterHigherOne_DoesNotRollBackLatest()
    {
        var restoreRun = await _runs.CreateAsync("restore", T1);

        await _sync.ImportPageAsync([new CatalogVersionDto("host/a.json", 2, HashOf("a2"), 20, Json, T2)], restoreRun, T3);
        await _sync.ImportPageAsync([new CatalogVersionDto("host/a.json", 1, HashOf("a1"), 10, Json, T1)], restoreRun, T3);

        var a = await FindByKeyAsync("host/a.json");
        Assert.Equal(2, a.LatestVersion);
        Assert.Equal(HashOf("a2"), a.LatestHash);
        Assert.Equal(2, await _sync.CountVersionsAsync());
    }

    // ---------- 辅助 ----------

    private Task<long> NewRunAsync(string trigger = "manual") => _runs.CreateAsync(trigger, T1);

    private Task<ResourceRow> SeenAsync(string key, long runId, string now = T1) =>
        _resources.UpsertSeenAsync(key, "https://" + key + "?v=1", runId, now);

    private async Task<string[]> SearchKeysAsync(string? q, string status)
    {
        var result = await _resources.SearchAsync(q, status, 0, 50);
        return result.Items.Select(i => i.ResourceKey).ToArray();
    }

    private async Task<string[]> LinkTextsAsync(long resourceId)
    {
        var links = await _resources.GetLinksAsync(resourceId);
        return links.Select(l => $"{l.Url}|{l.Source}").OrderBy(text => text, StringComparer.Ordinal).ToArray();
    }

    private async Task<ResourceRow> FindByKeyAsync(string key)
    {
        await using var connection = await _db.OpenAsync();
        return await connection.QuerySingleAsync<ResourceRow>("SELECT * FROM resources WHERE resource_key = @key", new { key });
    }

    private async Task<T?> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var connection = await _db.OpenAsync();
        return await connection.ExecuteScalarAsync<T>(sql, param);
    }

    private static string HashOf(string text) => ContentHasher.Hash(Encoding.UTF8.GetBytes(text));

    private static DiscoveredLink Link(string url, string source) => new(new Uri(url), source);
}
