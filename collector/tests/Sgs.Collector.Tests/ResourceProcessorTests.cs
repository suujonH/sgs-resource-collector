using System.IO.Compression;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Sgs.Collector.Common;
using Sgs.Collector.Crawl;
using Sgs.Collector.Data;
using Sgs.Collector.Decode;
using Sgs.Collector.Discovery;
using Sgs.Collector.Http;
using Sgs.Collector.Storage;

namespace Sgs.Collector.Tests;

// 假 fetcher + 记录上传的假 IBlobStore + 临时目录下的真实 SQLite + 真实解包与发现
public sealed class ResourceProcessorTests : IAsyncLifetime
{
    private const string EntryUrl = "https://web.sanguosha.com/220/h5_2/index_210000.php";
    private const string Root = "https://web.sanguosha.com/220/h5_2/";
    private const string Lm1 = "Thu, 01 Oct 2026 00:00:00 GMT";

    private static readonly byte[] PngA = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] PngB = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 4, 5, 6];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sgs-collector-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DiscoveryRules _rules = new(EntryUrl);
    private readonly FakeFetcher _fetcher = new();
    private readonly RecordingBlobStore _store = new();
    private readonly AesRescProvider _aes = new();
    private Db _db = null!;
    private ResourceRepository _resources = null!;
    private ResourceProcessor _processor = null!;
    private RunProgress _progress = null!;
    private RunContext _run = null!;
    private CrawlQueue _queue = null!;

    public async Task InitializeAsync()
    {
        _db = new Db(Path.Combine(_directory, "App_Data", "collector.db"));
        await _db.InitializeAsync();
        _resources = new ResourceRepository(_db);
        var runs = new RunRepository(_db);
        var runId = await runs.CreateAsync("manual", Clock.NowIso());
        _progress = new RunProgress(runId, "manual", Clock.NowIso());
        _run = new RunContext(runId, _progress, _aes, HeaderRuleSet.Empty);
        _queue = new CrawlQueue(_rules);
        _processor = new ResourceProcessor(
            _fetcher,
            _resources,
            runs,
            new BlobService(new BlobRepository(_db), _store),
            new PackageDecoder(),
            new DiscoveryEngine(_rules, new StaticResourceParser(_rules)),
            _rules,
            NullLogger<ResourceProcessor>.Instance);
    }

    public Task DisposeAsync()
    {
        _aes.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    // 1
    [Fact]
    public async Task FirstOk_CreatesV1_UploadsOnceBeforeVersionRow_AndStoresValidators()
    {
        var png = Item("res/a/icon.png?v=1");
        var defaultRes = Item("res/default.res.json");
        long? versionRowsAtUpload = null;
        _store.OnPut = async (hash, _) => versionRowsAtUpload = await ScalarAsync("SELECT COUNT(*) FROM versions WHERE hash=@hash", new { hash });
        _fetcher.Respond(r => r.Key == png.Key
            ? FakeFetcher.Ok(r, PngA, "image/png", "\"e1\"", Lm1)
            : FakeFetcher.Ok(r, Utf8("""{"resources":[{"url":"res/x/y.png"}]}"""), "application/json", "\"d1\""));

        Assert.Equal(ItemOutcome.NewVersion, await ProcessAsync(png));
        Assert.Equal(0, versionRowsAtUpload);

        var row = await RowAsync(png.Key);
        Assert.Equal(1, row.LatestVersion);
        Assert.Equal(ContentHasher.Hash(PngA), row.LatestHash);
        Assert.Equal("\"e1\"", row.Etag);
        Assert.Equal(Lm1, row.LastModified);
        Assert.Null(row.ProcessingRev);
        Assert.Equal("new_version", row.LastOutcome);
        Assert.Equal(200, row.LastHttpStatus);
        var put = Assert.Single(_store.Puts);
        Assert.Equal((ContentHasher.Hash(PngA), "image/png"), put);
        var version = Assert.Single(await _resources.GetVersionsAsync(row.Id));
        Assert.Equal(1, version.Version);
        Assert.Equal(PngA.LongLength, version.Size);

        var request = Assert.Single(_fetcher.Requests);
        Assert.Null(request.IfNoneMatch);
        Assert.Null(request.IfModifiedSince);
        Assert.Equal(_rules.EntryUri, request.Referer);

        Assert.Equal(ItemOutcome.NewVersion, await ProcessAsync(defaultRes));
        Assert.Equal(ProcessingRev.Current, (await RowAsync(defaultRes.Key)).ProcessingRev);
        Assert.Contains(Root.Replace("https://", "") + "res/x/y.png", Drain(_queue));
        Assert.Equal(2, _progress.Snapshot().NewVersions);
    }

    // 2
    [Fact]
    public async Task SecondRun_SendsValidators_And304IsNotModified()
    {
        var png = Item("res/a/icon.png");
        _fetcher.Respond(r => FakeFetcher.Ok(r, PngA, "image/png", "\"e1\"", Lm1));
        await ProcessAsync(png);

        _fetcher.Respond(r => r.IfNoneMatch == "\"e1\"" ? FakeFetcher.NotModified(r) : FakeFetcher.Ok(r, PngB, "image/png", "\"e2\""));
        Assert.Equal(ItemOutcome.NotModified, await ProcessAsync(png));

        var request = _fetcher.Requests[1];
        Assert.Equal("\"e1\"", request.IfNoneMatch);
        Assert.Equal(Lm1, request.IfModifiedSince);
        Assert.Single(_store.Puts);
        var row = await RowAsync(png.Key);
        Assert.Single(await _resources.GetVersionsAsync(row.Id));
        Assert.Equal("not_modified", row.LastOutcome);
        Assert.Equal(304, row.LastHttpStatus);
        Assert.Equal("\"e1\"", row.Etag);
        Assert.Equal(1, _progress.Snapshot().NotModified);
    }

    // 3
    [Fact]
    public async Task NoValidators_SameContent_IsUnchanged_WithoutUpload()
    {
        var png = Item("res/a/icon.png");
        _fetcher.Respond(r => FakeFetcher.Ok(r, PngA, "image/png"));
        await ProcessAsync(png);

        Assert.Equal(ItemOutcome.Unchanged, await ProcessAsync(png));

        Assert.Null(_fetcher.Requests[1].IfNoneMatch);
        Assert.Null(_fetcher.Requests[1].IfModifiedSince);
        Assert.Single(_store.Puts);
        var row = await RowAsync(png.Key);
        Assert.Single(await _resources.GetVersionsAsync(row.Id));
        Assert.Equal("unchanged", row.LastOutcome);
        Assert.Equal(1, _progress.Snapshot().Unchanged);
    }

    // 4
    [Fact]
    public async Task DifferentContent_CreatesV2()
    {
        var png = Item("res/a/icon.png");
        _fetcher.Respond(r => FakeFetcher.Ok(r, PngA, "image/png", "\"e1\""));
        await ProcessAsync(png);
        _fetcher.Respond(r => FakeFetcher.Ok(r, PngB, "image/png", "\"e2\""));

        Assert.Equal(ItemOutcome.NewVersion, await ProcessAsync(png));

        var row = await RowAsync(png.Key);
        Assert.Equal(2, row.LatestVersion);
        Assert.Equal(ContentHasher.Hash(PngB), row.LatestHash);
        Assert.Equal("\"e2\"", row.Etag);
        Assert.Equal([2L, 1L], (await _resources.GetVersionsAsync(row.Id)).Select(v => v.Version));
        Assert.Equal(2, _store.Puts.Count);
    }

    // 5
    [Fact]
    public async Task NeverSucceeded404_RecordsFailureWithoutHistory()
    {
        var png = Item("res/a/missing.png");
        _fetcher.Respond(FakeFetcher.NotFound);

        Assert.Equal(ItemOutcome.Failed, await ProcessAsync(png));

        var row = await RowAsync(png.Key);
        var failure = Assert.Single(await _resources.GetRecentFailuresAsync(row.Id, 10));
        Assert.Equal(0, failure.HadHistory);
        Assert.Equal(404, failure.HttpStatus);
        Assert.Equal("HTTP 404", failure.Error);
        Assert.Equal(0, row.LatestVersion);
        Assert.Null(row.Etag);
        Assert.Null(row.LastModified);
        Assert.Equal("failed", row.LastOutcome);
        var stats = _progress.Snapshot();
        Assert.Equal(1, stats.NeverSucceeded);
        Assert.Equal(0, stats.Failed);
    }

    // 6
    [Fact]
    public async Task HistoryThen404_CountsFailed_AndKeepsValidators()
    {
        var png = Item("res/a/icon.png");
        _fetcher.Respond(r => FakeFetcher.Ok(r, PngA, "image/png", "\"e1\"", Lm1));
        await ProcessAsync(png);
        _fetcher.Respond(FakeFetcher.NotFound);

        Assert.Equal(ItemOutcome.Failed, await ProcessAsync(png));

        var row = await RowAsync(png.Key);
        Assert.Equal("\"e1\"", row.Etag);
        Assert.Equal(Lm1, row.LastModified);
        Assert.Equal("failed", row.LastOutcome);
        Assert.Equal(1, Assert.Single(await _resources.GetRecentFailuresAsync(row.Id, 10)).HadHistory);
        var stats = _progress.Snapshot();
        Assert.Equal(1, stats.Failed);
        Assert.Equal(0, stats.NeverSucceeded);
    }

    // 7
    [Fact]
    public async Task UploadThrows_NoVersionRow_OneFailure_ValidatorsCleared()
    {
        var png = Item("res/a/icon.png");
        _fetcher.Respond(r => FakeFetcher.Ok(r, PngA, "image/png", "\"e1\"", Lm1));
        await ProcessAsync(png);
        _fetcher.Respond(r => FakeFetcher.Ok(r, PngB, "image/png", "\"e2\"", Lm1));
        _store.OnPut = (_, _) => throw new InvalidOperationException("R2 写入失败");

        Assert.Equal(ItemOutcome.Failed, await ProcessAsync(png));

        var row = await RowAsync(png.Key);
        Assert.Equal(1, row.LatestVersion);
        Assert.Single(await _resources.GetVersionsAsync(row.Id));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM versions WHERE hash=@hash", new { hash = ContentHasher.Hash(PngB) }));
        var failure = Assert.Single(await _resources.GetRecentFailuresAsync(row.Id, 10));
        Assert.Contains("R2 写入失败", failure.Error, StringComparison.Ordinal);
        Assert.Equal(200, failure.HttpStatus);
        Assert.Null(row.Etag);
        Assert.Null(row.LastModified);
        Assert.Equal("failed", row.LastOutcome);
    }

    // 8
    [Fact]
    public async Task PngReturningHtml_IsFailure_AndKeepsValidators()
    {
        var png = Item("res/a/icon.png");
        _fetcher.Respond(r => FakeFetcher.Ok(r, PngA, "image/png", "\"e1\""));
        await ProcessAsync(png);
        _fetcher.Respond(r => FakeFetcher.Ok(r, Utf8("<html>error</html>"), "text/html; charset=utf-8", "\"e2\""));

        Assert.Equal(ItemOutcome.Failed, await ProcessAsync(png));

        var row = await RowAsync(png.Key);
        Assert.Equal("\"e1\"", row.Etag);
        Assert.Equal("返回了 HTML 页面（疑似错误页）", row.LastError);
        Assert.Single(await _resources.GetVersionsAsync(row.Id));
        Assert.Single(_store.Puts);
    }

    // 9
    [Fact]
    public async Task ZipPackage_MembersVersionedIndependently_AndMemberLinksEnqueued()
    {
        var pkg = Item("sgsGame_test.sgs?v=1");
        var memberB = Utf8("""{"k":1}""");
        _fetcher.Respond(r => FakeFetcher.Ok(r, Zip(("a.js", Utf8("var p = \"res/a/b.png\";")), ("b.json", memberB)), "application/octet-stream", "\"p1\""));

        Assert.Equal(ItemOutcome.NewVersion, await ProcessAsync(pkg));

        Assert.Equal(1, (await RowAsync(pkg.Key + "/a.js")).LatestVersion);
        Assert.Equal(1, (await RowAsync(pkg.Key + "/b.json")).LatestVersion);
        Assert.Contains("web.sanguosha.com/220/h5_2/res/a/b.png", Drain(_queue));
        var pkgRow = await RowAsync(pkg.Key);
        Assert.Equal(ProcessingRev.Current, pkgRow.ProcessingRev);
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM links WHERE resource_id=@id", new { id = pkgRow.Id }));
        Assert.Equal(2, _progress.Snapshot().MemberNewVersions);

        _fetcher.Respond(r => FakeFetcher.Ok(r, Zip(("a.js", Utf8("var p = \"res/a/b.png\"; var q = 2;")), ("b.json", memberB)), "application/octet-stream", "\"p2\""));

        Assert.Equal(ItemOutcome.NewVersion, await ProcessAsync(pkg));

        Assert.Equal(2, (await RowAsync(pkg.Key)).LatestVersion);
        Assert.Equal(2, (await RowAsync(pkg.Key + "/a.js")).LatestVersion);
        var b = await RowAsync(pkg.Key + "/b.json");
        Assert.Equal(1, b.LatestVersion);
        Assert.Equal("unchanged", b.LastOutcome);
        var stats = _progress.Snapshot();
        Assert.Equal(3, stats.MemberNewVersions);
        Assert.Equal(1, stats.MemberUnchanged);
    }

    // 10
    [Fact]
    public async Task NotModified_ReplaysStoredLinks_OnlyForProcessedResources()
    {
        var defaultRes = Item("res/default.res.json");
        _fetcher.Respond(r => FakeFetcher.Ok(r, Utf8("""{"resources":[{"url":"res/x/y.png"}]}"""), "application/json", "\"d1\""));
        await ProcessAsync(defaultRes);

        var nextQueue = new CrawlQueue(_rules);
        _fetcher.Respond(FakeFetcher.NotModified);
        Assert.Equal(ItemOutcome.NotModified, await _processor.ProcessAsync(defaultRes, _run, nextQueue, CancellationToken.None));
        Assert.Equal(["web.sanguosha.com/220/h5_2/res/x/y.png"], Drain(nextQueue));

        var png = Item("res/a/icon.png");
        _fetcher.Respond(r => FakeFetcher.Ok(r, PngA, "image/png", "\"e1\""));
        await ProcessAsync(png);
        var pngRow = await RowAsync(png.Key);
        Assert.Null(pngRow.ProcessingRev);
        // 人为放一条链接：rev 为 NULL 的叶子资源返回 304 时不应查询它
        await ExecuteAsync("INSERT INTO links (resource_id, url, source) VALUES (@id, @url, 'test')", new { id = pngRow.Id, url = Root + "res/zzz.png" });

        var thirdQueue = new CrawlQueue(_rules);
        _fetcher.Respond(FakeFetcher.NotModified);
        Assert.Equal(ItemOutcome.NotModified, await _processor.ProcessAsync(png, _run, thirdQueue, CancellationToken.None));
        Assert.Empty(Drain(thirdQueue));
    }

    // 11
    [Fact]
    public async Task ProcessingRevMismatch_DoesNotSendValidators()
    {
        var defaultRes = Item("res/default.res.json");
        _fetcher.Respond(r => FakeFetcher.Ok(r, Utf8("""{"resources":[]}"""), "application/json", "\"d1\"", Lm1));
        await ProcessAsync(defaultRes);
        await ExecuteAsync("UPDATE resources SET processing_rev = @rev WHERE resource_key = @key", new { rev = ProcessingRev.Current + 1, key = defaultRes.Key });

        await ProcessAsync(defaultRes);

        Assert.Null(_fetcher.Requests[1].IfNoneMatch);
        Assert.Null(_fetcher.Requests[1].IfModifiedSince);
    }

    [Theory]
    [InlineData("web.sanguosha.com/220/h5_2/res/a.png", 1L, "\"e\"", null, null, true)]
    [InlineData("web.sanguosha.com/220/h5_2/res/a.png", 1L, null, Lm1, null, true)]
    [InlineData("web.sanguosha.com/220/h5_2/res/a.png", 0L, "\"e\"", null, null, false)]
    [InlineData("web.sanguosha.com/220/h5_2/res/a.png", 1L, null, null, null, false)]
    [InlineData("web.sanguosha.com/220/h5_2/libs/min/aesresc", 1L, "\"e\"", null, 1L, false)]
    [InlineData("web.sanguosha.com/220/h5_2/res/default.res.json", 1L, "\"e\"", null, null, false)]
    [InlineData("web.sanguosha.com/220/h5_2/res/default.res.json", 1L, "\"e\"", null, 1L, true)]
    [InlineData("web.sanguosha.com/220/h5_2/res/default.res.json", 1L, "\"e\"", null, 0L, false)]
    public void ShouldUseValidators_FollowsTheRules(string key, long latestVersion, string? etag, string? lastModified, long? rev, bool expected)
    {
        var row = new ResourceRow { LatestVersion = latestVersion, Etag = etag, LastModified = lastModified, ProcessingRev = rev };

        Assert.Equal(expected, ResourceProcessor.ShouldUseValidators(row, key));
    }

    // 12
    [Fact]
    public async Task MemberDecryptFailure_ArchivesOthers_EnqueuesLinks_FailsPackage_ClearsValidators()
    {
        var pkg = Item("res/config/Test_w.sgs");
        _fetcher.Respond(r => FakeFetcher.Ok(r, Zip(("a.json", Utf8("""{"p":"res/a/c.png"}"""))), "application/octet-stream", "\"p1\"", Lm1));
        Assert.Equal(ItemOutcome.NewVersion, await ProcessAsync(pkg));
        Assert.Equal("\"p1\"", (await RowAsync(pkg.Key)).Etag);

        var queue = new CrawlQueue(_rules);
        byte[] binary = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x00, 0x10];
        _fetcher.Respond(r => FakeFetcher.Ok(r, Zip(("a.json", Utf8("""{"p":"res/a/c.png","q":2}""")), ("b.sgs", binary)), "application/octet-stream", "\"p2\"", Lm1));

        Assert.Equal(ItemOutcome.Failed, await _processor.ProcessAsync(pkg, _run, queue, CancellationToken.None));

        Assert.Equal(2, (await RowAsync(pkg.Key + "/a.json")).LatestVersion);
        Assert.Contains("web.sanguosha.com/220/h5_2/res/a/c.png", Drain(queue));
        var row = await RowAsync(pkg.Key);
        Assert.Null(row.Etag);
        Assert.Null(row.LastModified);
        Assert.Equal("failed", row.LastOutcome);
        var failure = Assert.Single(await _resources.GetRecentFailuresAsync(row.Id, 10));
        Assert.Equal(1, failure.HadHistory);
        Assert.Contains("1 个成员处理失败", failure.Error, StringComparison.Ordinal);
        Assert.Contains("aesresc 未就绪", failure.Error, StringComparison.Ordinal);
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM resources WHERE parent_id=@id AND source_path='b.sgs'", new { id = row.Id }));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM resources WHERE parent_id=@id", new { id = row.Id }));
        var stats = _progress.Snapshot();
        Assert.Equal(1, stats.MemberFailed);
        Assert.Equal(1, stats.Failed);
    }

    // 13
    [Fact]
    public async Task MemberKey_IsFixedAtFirstAppearance()
    {
        var pkg = Item("res/test/pack.sgs");
        _fetcher.Respond(r => FakeFetcher.Ok(r, Zip(("a.sgs", Utf8("""{"k":1}"""))), "application/octet-stream"));
        await ProcessAsync(pkg);
        var first = await RowAsync(pkg.Key + "/a.json");
        Assert.Equal(1, first.LatestVersion);
        Assert.Equal("a.sgs", first.SourcePath);

        _fetcher.Respond(r => FakeFetcher.Ok(r, Zip(("a.sgs", Utf8("plain text now"))), "application/octet-stream"));
        Assert.Equal(ItemOutcome.NewVersion, await ProcessAsync(pkg));

        Assert.Equal(2, (await RowAsync(pkg.Key + "/a.json")).LatestVersion);
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM resources WHERE resource_key=@key", new { key = pkg.Key + "/a.txt" }));
    }

    [Fact]
    public async Task UserCancelAfterFetch_ChangesNothing()
    {
        var png = Item("res/a/icon.png");
        _fetcher.Respond(r => FakeFetcher.Ok(r, PngA, "image/png", "\"e1\"", Lm1));
        await ProcessAsync(png);

        using var cts = new CancellationTokenSource();
        _fetcher.Respond(r => FakeFetcher.Ok(r, PngB, "image/png", "\"e2\""));
        _store.OnPut = async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        Assert.Equal(ItemOutcome.Cancelled, await _processor.ProcessAsync(png, _run, _queue, cts.Token));

        var row = await RowAsync(png.Key);
        Assert.Equal("\"e1\"", row.Etag);
        Assert.Equal(Lm1, row.LastModified);
        Assert.Equal("new_version", row.LastOutcome);
        Assert.Single(await _resources.GetVersionsAsync(row.Id));
        Assert.Empty(await _resources.GetRecentFailuresAsync(row.Id, 10));
        var stats = _progress.Snapshot();
        Assert.Equal(1, stats.Cancelled);
        Assert.Equal(0, stats.Failed);
    }

    [Fact]
    public void CheckBody_DetectsRedirectEmptyAndHtml()
    {
        var png = Item("res/a/icon.png");
        var aes = Item("libs/min/aesresc?v=1");
        var php = Item("res/a/page.php");

        Assert.Equal("被重定向到其他主机：https://other.example.com/x",
            ResourceProcessor.CheckBody(png, new FetchResult(FetchKind.Ok, 200, PngA, null, null, "image/png", new Uri("https://other.example.com/x"), null)));
        Assert.Equal("响应体为空", ResourceProcessor.CheckBody(png, new FetchResult(FetchKind.Ok, 200, [], null, null, "image/png", png.FetchUri, null)));
        Assert.NotNull(ResourceProcessor.CheckBody(png, new FetchResult(FetchKind.Ok, 200, PngA, null, null, " TEXT/HTML ;charset=utf-8", png.FetchUri, null)));
        Assert.Null(ResourceProcessor.CheckBody(aes, new FetchResult(FetchKind.Ok, 200, PngA, null, null, "text/html", aes.FetchUri, null)));
        Assert.Null(ResourceProcessor.CheckBody(php, new FetchResult(FetchKind.Ok, 200, PngA, null, null, "text/html", php.FetchUri, null)));
        Assert.Null(ResourceProcessor.CheckBody(png, new FetchResult(FetchKind.Ok, 200, PngA, null, null, "image/png", png.FetchUri, null)));
    }

    [Theory]
    [InlineData(ItemOutcome.NotModified, "not_modified")]
    [InlineData(ItemOutcome.Unchanged, "unchanged")]
    [InlineData(ItemOutcome.NewVersion, "new_version")]
    [InlineData(ItemOutcome.Failed, "failed")]
    public void ToDb_MapsOutcomes(ItemOutcome outcome, string expected)
    {
        Assert.Equal(expected, ResourceProcessor.ToDb(outcome));
    }

    [Theory]
    [InlineData(ItemOutcome.Cancelled)]
    [InlineData(ItemOutcome.DiscoverOnly)]
    public void ToDb_RejectsOtherOutcomes(ItemOutcome outcome)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ResourceProcessor.ToDb(outcome));
    }

    // ---------- 辅助 ----------

    private Task<ItemOutcome> ProcessAsync(CrawlItem item) => _processor.ProcessAsync(item, _run, _queue, CancellationToken.None);

    private CrawlItem Item(string relative)
    {
        var uri = new Uri(_rules.H5Root, relative);
        return new CrawlItem(ResourceKeys.FromUri(uri), uri, CrawlItemKind.Archive, "test");
    }

    private async Task<ResourceRow> RowAsync(string key)
    {
        await using var connection = await _db.OpenAsync();
        return await connection.QuerySingleAsync<ResourceRow>("SELECT * FROM resources WHERE resource_key=@key", new { key });
    }

    private async Task<long> ScalarAsync(string sql, object param)
    {
        await using var connection = await _db.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(sql, param);
    }

    private Task ExecuteAsync(string sql, object param)
    {
        return _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(sql, param, transaction: transaction));
    }

    internal static List<string> Drain(CrawlQueue queue)
    {
        List<string> keys = [];
        while (queue.Reader.TryRead(out var item))
        {
            keys.Add(item.Key);
        }

        return keys;
    }

    internal static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return ms.ToArray();
    }
}

// 按请求返回预设结果，并记录收到的全部请求（线程安全，CrawlRunnerTests 并发使用）
internal sealed class FakeFetcher : IResourceFetcher
{
    private readonly object _lock = new();
    private readonly List<FetchRequest> _requests = [];

    public Func<FetchRequest, CancellationToken, Task<FetchResult>> Handler { get; set; } = (r, _) => Task.FromResult(NotFound(r));

    public IReadOnlyList<FetchRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    public void Respond(Func<FetchRequest, FetchResult> handler) => Handler = (r, _) => Task.FromResult(handler(r));

    public Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken ct)
    {
        lock (_lock)
        {
            _requests.Add(request);
        }

        return Handler(request, ct);
    }

    public static FetchResult Ok(FetchRequest r, byte[] body, string? contentType, string? etag = null, string? lastModified = null) =>
        new(FetchKind.Ok, 200, body, etag, lastModified, contentType, r.Uri, null);

    public static FetchResult NotModified(FetchRequest r) => new(FetchKind.NotModified, 304, null, null, null, null, r.Uri, null);

    public static FetchResult NotFound(FetchRequest r) => new(FetchKind.HttpError, 404, null, null, null, null, r.Uri, "HTTP 404");
}

// 记录上传；OnPut 在记录之前执行，抛出异常时该次上传不计入
internal sealed class RecordingBlobStore : IBlobStore
{
    private readonly object _lock = new();
    private readonly List<(string Hash, string ContentType)> _puts = [];

    public Func<string, CancellationToken, Task>? OnPut { get; set; }

    public IReadOnlyList<(string Hash, string ContentType)> Puts
    {
        get
        {
            lock (_lock)
            {
                return [.. _puts];
            }
        }
    }

    public async Task PutAsync(string hash, byte[] content, string contentType, CancellationToken ct)
    {
        if (OnPut is { } onPut)
        {
            await onPut(hash, ct);
        }

        lock (_lock)
        {
            _puts.Add((hash, contentType));
        }
    }
}
