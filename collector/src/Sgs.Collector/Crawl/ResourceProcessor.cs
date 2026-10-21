using Microsoft.Data.Sqlite;
using Sgs.Collector.Common;
using Sgs.Collector.Data;
using Sgs.Collector.Decode;
using Sgs.Collector.Discovery;
using Sgs.Collector.Http;
using Sgs.Collector.Storage;

namespace Sgs.Collector.Crawl;

public sealed class ResourceProcessor
{
    // 与 Data/ResourceRepository.cs 中 SQL 写死的 'not_modified'、'failed' 一致
    public const string OutcomeNotModified = "not_modified";
    public const string OutcomeUnchanged = "unchanged";
    public const string OutcomeNewVersion = "new_version";
    public const string OutcomeFailed = "failed";

    private const int MaxErrorLength = 1000;

    private readonly IResourceFetcher _fetcher;
    private readonly ResourceRepository _resources;
    private readonly RunRepository _runs;
    private readonly BlobService _blobs;
    private readonly PackageDecoder _decoder;
    private readonly DiscoveryEngine _discovery;
    private readonly DiscoveryRules _rules;
    private readonly ILogger<ResourceProcessor> _logger;

    public ResourceProcessor(
        IResourceFetcher fetcher,
        ResourceRepository resources,
        RunRepository runs,
        BlobService blobs,
        PackageDecoder decoder,
        DiscoveryEngine discovery,
        DiscoveryRules rules,
        ILogger<ResourceProcessor> logger)
    {
        _fetcher = fetcher;
        _resources = resources;
        _runs = runs;
        _blobs = blobs;
        _decoder = decoder;
        _discovery = discovery;
        _rules = rules;
        _logger = logger;
    }

    public static string ToDb(ItemOutcome outcome) => outcome switch
    {
        ItemOutcome.NotModified => OutcomeNotModified,
        ItemOutcome.Unchanged => OutcomeUnchanged,
        ItemOutcome.NewVersion => OutcomeNewVersion,
        ItemOutcome.Failed => OutcomeFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "该结果没有对应的数据库取值")
    };

    public async Task<EntryDiscovery?> ProcessEntryAsync(CrawlItem item, RunContext run, CrawlQueue queue, CancellationToken ct)
    {
        run.Progress.BeginItem(item.Key);
        try
        {
            var fr = await _fetcher.FetchAsync(new FetchRequest(item.FetchUri, item.Key, null, null, null, run.Headers), ct);
            if (fr.Kind != FetchKind.Ok)
            {
                run.Progress.Warn($"入口页面获取失败：{fr.StatusCode} {fr.Error}");
                run.Progress.Complete(ItemOutcome.Failed, true);
                return null;
            }

            var body = fr.Body!;
            run.Progress.AddBytes(body.LongLength);
            var entry = _discovery.FromEntry(body, item.FetchUri);
            foreach (var link in entry.Links)
            {
                queue.TryEnqueue(link.Uri, link.Source);
            }

            run.Progress.Complete(ItemOutcome.DiscoverOnly, true);
            return entry;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Progress.Complete(ItemOutcome.Cancelled, true);
            return null;
        }
        finally
        {
            run.Progress.EndItem(item.Key);
        }
    }

    // 除“用户终止”（OperationCanceledException）和“数据库故障”外，绝不抛出异常
    public async Task<ItemOutcome> ProcessAsync(CrawlItem item, RunContext run, CrawlQueue queue, CancellationToken ct)
    {
        run.Progress.BeginItem(item.Key);
        try
        {
            var res = await _resources.UpsertSeenAsync(item.Key, item.FetchUri.AbsoluteUri, run.RunId, Clock.NowIso());
            var state = new AttemptState { HadHistory = res.LatestVersion > 0 };
            try
            {
                return await ProcessCoreAsync(item, res, state, run, queue, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户终止：不写失败记录，不动验证头
                run.Progress.Complete(ItemOutcome.Cancelled, state.HadHistory);
                return ItemOutcome.Cancelled;
            }
            catch (Exception ex) when (!state.FailureRecording && ex is not SqliteException)
            {
                // 获取成功后的处理错误（数据库故障除外，向外抛出由 CrawlRunner 按致命错误处理）。RecordFailureAsync 自身的数据库异常不进这里，直接向外抛出
                await RecordFailureAsync(item.Key, res.Id, state, run, state.HttpStatus, Describe(ex), clearValidators: true);
                return ItemOutcome.Failed;
            }
        }
        finally
        {
            run.Progress.EndItem(item.Key);
        }
    }

    internal static bool ShouldUseValidators(ResourceRow res, string key)
    {
        // D13：aesresc 每轮无条件下载
        if (DiscoveryRules.IsAesResc(key))
        {
            return false;
        }

        if (res.LatestVersion == 0)
        {
            return false;
        }

        if (res.Etag is null && res.LastModified is null)
        {
            return false;
        }

        // 处理过的资源在 ProcessingRev 提升后重新处理；以前是叶子、现在需要解析的资源也重新处理
        return res.ProcessingRev is null ? !DiscoveryRules.IsProcessedKey(key) : res.ProcessingRev == ProcessingRev.Current;
    }

    internal static string? CheckBody(CrawlItem item, FetchResult fr)
    {
        if (fr.FinalUri is { } finalUri && !string.Equals(finalUri.Host, item.FetchUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return $"被重定向到其他主机：{finalUri}";
        }

        if (fr.Body is null || fr.Body.Length == 0)
        {
            return "响应体为空";
        }

        if (fr.ContentType is { } contentType)
        {
            var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
            var mediaType = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
            var extension = Path.GetExtension(ResourceKeys.LastSegment(item.Key));
            if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
                && extension.Length > 0
                && !extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".php", StringComparison.OrdinalIgnoreCase))
            {
                return "返回了 HTML 页面（疑似错误页）";
            }
        }

        return null;
    }

    internal static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        return text.Length > MaxErrorLength ? text[..MaxErrorLength] : text;
    }

    private async Task<ItemOutcome> ProcessCoreAsync(CrawlItem item, ResourceRow res, AttemptState state, RunContext run, CrawlQueue queue, CancellationToken ct)
    {
        var useValidators = ShouldUseValidators(res, item.Key);
        var fr = await _fetcher.FetchAsync(
            new FetchRequest(item.FetchUri, item.Key, _rules.EntryUri,
                useValidators ? res.Etag : null, useValidators ? res.LastModified : null, run.Headers),
            ct);
        state.HttpStatus = fr.StatusCode;

        if (fr.Kind is FetchKind.HttpError or FetchKind.NetworkError)
        {
            // 获取失败：保留验证头
            await RecordFailureAsync(item.Key, res.Id, state, run, fr.StatusCode, fr.Error ?? $"HTTP {fr.StatusCode}", clearValidators: false);
            return ItemOutcome.Failed;
        }

        if (fr.Kind == FetchKind.NotModified)
        {
            // 叶子资源（rev 为 NULL）没有链接，免查
            if (res.ProcessingRev is not null)
            {
                foreach (var link in await _resources.GetLinksAsync(res.Id))
                {
                    queue.TryEnqueue(new Uri(link.Url), link.Source);
                }
            }

            await _resources.MarkNotModifiedAsync(res.Id, fr.StatusCode ?? 304, Clock.NowIso());
            run.Progress.Complete(ItemOutcome.NotModified, true);
            return ItemOutcome.NotModified;
        }

        var body = fr.Body ?? [];
        run.Progress.AddBytes(body.LongLength);
        if (CheckBody(item, fr) is { } bodyError)
        {
            await RecordFailureAsync(item.Key, res.Id, state, run, fr.StatusCode, bodyError, clearValidators: false);
            return ItemOutcome.Failed;
        }

        ItemOutcome outcome;
        var hash = ContentHasher.Hash(body);
        if (string.Equals(hash, res.LatestHash, StringComparison.Ordinal))
        {
            outcome = ItemOutcome.Unchanged;
        }
        else
        {
            var contentType = ContentTypes.Resolve(item.Key, fr.ContentType);
            // 先上传，再写版本行：数据库里的每个版本，R2 里一定有内容
            await _blobs.EnsureUploadedAsync(hash, body, contentType, ct);
            var version = await _resources.AddVersionAsync(res.Id, hash, body.LongLength, contentType, run.RunId, Clock.NowIso());
            state.HadHistory = true;
            run.Progress.Info($"新版本 {item.Key} v{version}");
            outcome = ItemOutcome.NewVersion;
        }

        if (DiscoveryRules.IsAesResc(item.Key))
        {
            run.AesResc.SetModule(body);
        }

        var members = _decoder.Decode(item.Key, body, run.AesResc.Decrypt);
        var memberErrors = await ArchiveMembersAsync(res, item.Key, members, run, ct);

        // 即使有成员失败也先入队：链接必须在该条目 MarkDone 之前入队
        var links = _discovery.FromResource(item, body, members);
        foreach (var link in links)
        {
            queue.TryEnqueue(link.Uri, link.Source);
        }

        if (memberErrors.Count > 0)
        {
            throw new MemberFailureException($"{memberErrors.Count} 个成员处理失败：{memberErrors[0]}");
        }

        long? rev = DiscoveryRules.IsProcessedKey(item.Key) || members.Count > 0 || links.Count > 0 ? ProcessingRev.Current : null;
        // 验证头、processing_rev、links 只在整个条目成功后，在同一个事务里写入
        ct.ThrowIfCancellationRequested();
        await _resources.CommitProcessedAsync(res.Id, ToDb(outcome), fr.StatusCode ?? 200, fr.ETag, fr.LastModified, rev, links, Clock.NowIso());
        run.Progress.Complete(outcome, true);
        return outcome;
    }

    private async Task<List<string>> ArchiveMembersAsync(ResourceRow parent, string parentKey, IReadOnlyList<DecodedMember> members, RunContext run, CancellationToken ct)
    {
        List<string> errors = [];
        if (members.Count == 0)
        {
            return errors;
        }

        List<DecodedMember> valid = [];
        foreach (var member in members)
        {
            if (member.Error is not null)
            {
                // 失败成员不建行，避免把错误的路径固定下来
                errors.Add($"{member.SourcePath}：{member.Error}");
                run.Progress.MemberFailed();
                continue;
            }

            valid.Add(member);
        }

        var prefix = parentKey + "/";
        var existing = (await _resources.GetMemberKeysAsync(parent.Id))
            .Where(pair => pair.Value.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value[prefix.Length..], StringComparer.Ordinal);

        foreach (var assignment in MemberKeyAssigner.Assign(existing, valid))
        {
            // 仓储调用不可取消，且未变化的成员不上传：每个成员开始前检查一次
            ct.ThrowIfCancellationRequested();
            if (assignment.Warning is not null)
            {
                run.Progress.Warn($"{parentKey}：{assignment.Warning}");
            }

            if (assignment.MemberPath is null)
            {
                continue;
            }

            var key = prefix + assignment.MemberPath;
            var bytes = assignment.Member.Bytes!;
            try
            {
                var row = await _resources.UpsertMemberAsync(key, parent.Id, assignment.Member.SourcePath, run.RunId, Clock.NowIso());
                var hash = ContentHasher.Hash(bytes);
                if (string.Equals(hash, row.LatestHash, StringComparison.Ordinal))
                {
                    await _resources.MarkMemberCheckedAsync(row.Id, OutcomeUnchanged, Clock.NowIso());
                    run.Progress.MemberUnchanged();
                    continue;
                }

                var contentType = ContentTypes.Resolve(key, null);
                await _blobs.EnsureUploadedAsync(hash, bytes, contentType, ct);
                var version = await _resources.AddVersionAsync(row.Id, hash, bytes.LongLength, contentType, run.RunId, Clock.NowIso());
                await _resources.MarkMemberCheckedAsync(row.Id, OutcomeNewVersion, Clock.NowIso());
                run.Progress.MemberNewVersion();
                run.Progress.Info($"新版本 {key} v{version}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not SqliteException)
            {
                // 继续处理其它成员（数据库故障除外，向外抛出）
                errors.Add($"{assignment.Member.SourcePath}：{Describe(ex)}");
                run.Progress.MemberFailed();
            }
        }

        return errors;
    }

    private async Task RecordFailureAsync(string key, long resourceId, AttemptState state, RunContext run, int? status, string error, bool clearValidators)
    {
        // 这里的数据库异常不捕获，向外抛出，由 CrawlRunner 按致命错误处理
        state.FailureRecording = true;
        var now = Clock.NowIso();
        await _runs.AddFailureAsync(run.RunId, resourceId, status, error, state.HadHistory, now);
        await _resources.MarkFailedAsync(resourceId, status, error, clearValidators, now);
        run.Progress.Complete(ItemOutcome.Failed, state.HadHistory);
        run.Progress.Error($"失败 {key}：{error}");
        _logger.LogDebug("资源处理失败 {Key}：{Error}", key, error);
    }

    private sealed class AttemptState
    {
        public bool HadHistory { get; set; }

        public int? HttpStatus { get; set; }

        // 已开始写失败记录：之后的异常（数据库故障）不能再被当作处理错误重复记录
        public bool FailureRecording { get; set; }
    }
}

internal sealed class MemberFailureException : Exception
{
    public MemberFailureException(string message)
        : base(message)
    {
    }
}
