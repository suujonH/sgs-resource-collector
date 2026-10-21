using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Sgs.Collector.Common;
using Sgs.Collector.Crawl;
using Sgs.Collector.Data;
using Sgs.Collector.Decode;
using Sgs.Collector.Discovery;
using Sgs.Collector.Http;
using Sgs.Collector.Options;
using Sgs.Collector.Storage;

namespace Sgs.Collector.Tests;

// 假 fetcher 模拟一个微型站点，其余组件全部真实
public sealed class CrawlRunnerTests : IAsyncLifetime
{
    private const string EntryUrl = "https://web.sanguosha.com/220/h5_2/index_210000.php";
    private const string Root = "https://web.sanguosha.com/220/h5_2/";

    private const string EntryHtml = """
        <html><head>
        <script>window.mainVersion = "2026101901"; window.layaVersion = "2026101601"; window.rescVersion = "2019112501";</script>
        <script src="libs/after.js?v=1"></script>
        </head><body></body></html>
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sgs-collector-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DiscoveryRules _rules = new(EntryUrl);
    private readonly FakeFetcher _fetcher = new();
    private Db _db = null!;
    private RunRepository _runs = null!;
    private HeaderRuleProvider _headerRules = null!;
    private ResourceProcessor _processor = null!;

    public async Task InitializeAsync()
    {
        _db = new Db(Path.Combine(_directory, "App_Data", "collector.db"));
        await _db.InitializeAsync();
        _runs = new RunRepository(_db);
        _headerRules = new HeaderRuleProvider(new HeaderRuleRepository(_db), NullLogger<HeaderRuleProvider>.Instance);
        _processor = new ResourceProcessor(
            _fetcher,
            new ResourceRepository(_db),
            _runs,
            new BlobService(new BlobRepository(_db), new RecordingBlobStore()),
            new PackageDecoder(),
            new DiscoveryEngine(_rules, new StaticResourceParser(_rules)),
            _rules,
            NullLogger<ResourceProcessor>.Instance);
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

    [Fact]
    public async Task RunAsync_BootstrapsInOrder_CrawlsEverything_AndCounts()
    {
        _fetcher.Respond(r => Site(r, """{"res/a.png":"11","res/b.png":"22"}"""));
        var progress = await NewProgressAsync();

        await NewRunner(maxConcurrency: 4).RunAsync(progress, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        var requests = _fetcher.Requests;
        Assert.Equal(EntryUrl, requests[0].Uri.AbsoluteUri);
        Assert.Equal(Root + "libs/min/aesresc?v=2019112501", requests[1].Uri.AbsoluteUri);
        Assert.Equal(Root + "version.json", requests[2].Uri.GetLeftPart(UriPartial.Path));
        Assert.StartsWith("?t=", requests[2].Uri.Query, StringComparison.Ordinal);

        var urls = requests.Skip(3).Select(r => r.Uri.AbsoluteUri).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                Root + "libs/after.js?v=1",
                Root + "libs/min/laya_a.sgs?v=2026101601",
                Root + "libs/min/laya.sgs?v=2026101601",
                Root + "sgsGame_a.sgs?v=2026101901",
                Root + "sgsGame.sgs?v=2026101901",
                Root + "res/a.png?v=11",
                Root + "res/b.png?v=22"
            },
            urls);
        Assert.Equal(10, requests.Count);

        var stats = progress.Snapshot();
        Assert.Equal(10, stats.Completed);
        Assert.Equal(1, stats.DiscoverOnly);
        Assert.Equal(8, stats.NewVersions);
        Assert.Equal(1, stats.NeverSucceeded);
        Assert.Equal(0, stats.Failed);
        Assert.Equal(0, stats.Cancelled);
        Assert.Empty(stats.Warnings);
    }

    [Fact]
    public async Task RunAsync_CancelledDuringCrawl_ThrowsQuickly_WithoutProcessingTheRest()
    {
        const int pngCount = 40;
        var versionJson = "{" + string.Join(",", Enumerable.Range(0, pngCount).Select(i => $"\"res/p{i}.png\":\"1\"")) + "}";
        using var cts = new CancellationTokenSource();
        var triggered = 0;
        var pngRequests = 0;
        _fetcher.Handler = async (r, ct) =>
        {
            if (!r.Key.EndsWith(".png", StringComparison.Ordinal))
            {
                return Site(r, versionJson);
            }

            Interlocked.Increment(ref pngRequests);
            if (Interlocked.Exchange(ref triggered, 1) == 0)
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }

            // 其余 png 一直等到被取消
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return FakeFetcher.Ok(r, [1, 2, 3], "image/png");
        };
        var progress = await NewProgressAsync();
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewRunner(maxConcurrency: 4).RunAsync(progress, cts.Token).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"耗时 {stopwatch.Elapsed}");
        Assert.True(Volatile.Read(ref pngRequests) < pngCount);
        var stats = progress.Snapshot();
        Assert.True(stats.Cancelled >= 1);
        Assert.Equal(0, stats.Failed + stats.NeverSucceeded);
    }

    private CrawlRunner NewRunner(int maxConcurrency)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CollectorOptions { EntryUrl = EntryUrl, MaxConcurrency = maxConcurrency });
        return new CrawlRunner(options, _rules, _processor, _headerRules, NullLogger<CrawlRunner>.Instance);
    }

    private async Task<RunProgress> NewProgressAsync()
    {
        var now = Clock.NowIso();
        var runId = await _runs.CreateAsync("manual", now);
        return new RunProgress(runId, "manual", now);
    }

    // 微型站点：入口页、aesresc、version.json，res/b.png 返回 404，其余资源返回任意内容
    private static FetchResult Site(FetchRequest r, string versionJson)
    {
        var path = r.Uri.AbsolutePath;
        if (path.EndsWith("/index_210000.php", StringComparison.Ordinal))
        {
            return FakeFetcher.Ok(r, ResourceProcessorTests.Utf8(EntryHtml), "text/html; charset=utf-8");
        }

        if (path.EndsWith("/libs/min/aesresc", StringComparison.Ordinal))
        {
            return FakeFetcher.Ok(r, [0x10, 0x20, 0x30, 0x40], "application/octet-stream");
        }

        if (path.EndsWith("/version.json", StringComparison.Ordinal))
        {
            return FakeFetcher.Ok(r, ResourceProcessorTests.Utf8(versionJson), "application/json");
        }

        if (path.EndsWith("/res/b.png", StringComparison.Ordinal))
        {
            return FakeFetcher.NotFound(r);
        }

        return FakeFetcher.Ok(r, ResourceProcessorTests.Utf8("content of " + path), "application/octet-stream");
    }
}
