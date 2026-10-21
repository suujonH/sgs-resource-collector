using Microsoft.Extensions.Options;
using Sgs.Collector.Decode;
using Sgs.Collector.Discovery;
using Sgs.Collector.Http;
using Sgs.Collector.Options;

namespace Sgs.Collector.Crawl;

public sealed class CrawlRunner
{
    private const string PhaseBootstrap = "bootstrap";
    private const string PhaseCrawling = "crawling";
    private const string BootstrapSource = "bootstrap";
    private const string SeedSource = "seed";

    private readonly CollectorOptions _options;
    private readonly DiscoveryRules _rules;
    private readonly ResourceProcessor _processor;
    private readonly HeaderRuleProvider _headerRules;
    private readonly ILogger<CrawlRunner> _logger;

    public CrawlRunner(
        IOptions<CollectorOptions> options,
        DiscoveryRules rules,
        ResourceProcessor processor,
        HeaderRuleProvider headerRules,
        ILogger<CrawlRunner> logger)
    {
        _options = options.Value;
        _rules = rules;
        _processor = processor;
        _headerRules = headerRules;
        _logger = logger;
    }

    public async Task RunAsync(RunProgress progress, CancellationToken ct)
    {
        var headers = await _headerRules.ReloadAsync();
        using var aes = new AesRescProvider();
        var queue = new CrawlQueue(_rules);
        progress.AttachQueue(queue);
        var run = new RunContext(progress.RunId, progress, aes, headers);

        progress.SetPhase(PhaseBootstrap);

        var entry = await _processor.ProcessEntryAsync(
            queue.Register(_rules.EntryUri, BootstrapSource, CrawlItemKind.DiscoverOnly), run, queue, ct);
        ct.ThrowIfCancellationRequested();

        await _processor.ProcessAsync(
            queue.Register(_rules.BuildAesRescUri(entry?.RescVersion), BootstrapSource, CrawlItemKind.Archive), run, queue, ct);
        ct.ThrowIfCancellationRequested();
        if (!aes.HasModule)
        {
            progress.Warn("aesresc 不可用：res/config、res/proto 下的加密包本轮无法解码");
        }

        foreach (var seed in _rules.BuildSeedUris(entry?.LayaVersion, entry?.MainVersion))
        {
            queue.TryEnqueue(seed, SeedSource);
        }

        if (string.IsNullOrEmpty(entry?.LayaVersion) || string.IsNullOrEmpty(entry?.MainVersion))
        {
            progress.Warn("入口页未找到 mainVersion/layaVersion，种子包改用无版本号地址（可能读到 CDN 旧缓存）");
        }

        ct.ThrowIfCancellationRequested();

        var outcome = await _processor.ProcessAsync(
            queue.Register(_rules.BuildVersionJsonUri(DateTimeOffset.UtcNow), BootstrapSource, CrawlItemKind.Archive), run, queue, ct);
        ct.ThrowIfCancellationRequested();
        if (outcome == ItemOutcome.Failed)
        {
            progress.Warn("version.json 处理失败：本轮资源发现不完整");
        }

        queue.ReleaseBootstrap();
        ct.ThrowIfCancellationRequested();

        progress.SetPhase(PhaseCrawling);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var shared = new WorkerShared(runCts);
        var workerCount = Math.Max(1, _options.MaxConcurrency);
        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(() => WorkerLoopAsync(queue, run, shared, runCts.Token));
        }

        await Task.WhenAll(workers);

        if (shared.Fatal is { } fatal)
        {
            throw new InvalidOperationException("收集因内部错误中止：" + fatal.Message, fatal);
        }

        ct.ThrowIfCancellationRequested();
    }

    // 本身不向外抛出任何异常
    private async Task WorkerLoopAsync(CrawlQueue queue, RunContext run, WorkerShared shared, CancellationToken token)
    {
        try
        {
            // ReadAllAsync 只在“等待新条目”时检查 token，所以每个条目开始前要自己检查
            await foreach (var item in queue.Reader.ReadAllAsync(token))
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (_options.MaxItems > 0 && Interlocked.Increment(ref shared.Started) > _options.MaxItems)
                    {
                        run.Progress.LimitSkipped();
                        continue;
                    }

                    await _processor.ProcessAsync(item, run, queue, token);
                }
                finally
                {
                    queue.MarkDone();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 终止：静默退出
        }
        catch (Exception ex)
        {
            // 致命错误（如数据库故障）：记录第一个，并让其它 worker 停下
            Interlocked.CompareExchange(ref shared.Fatal, ex, null);
            run.Progress.Error("内部错误：" + ex.Message);
            _logger.LogError(ex, "收集 worker 内部错误");
            shared.Cts.Cancel();
        }
    }

    private sealed class WorkerShared
    {
        public int Started;
        public Exception? Fatal;

        public WorkerShared(CancellationTokenSource cts)
        {
            Cts = cts;
        }

        public CancellationTokenSource Cts { get; }
    }
}
