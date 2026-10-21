using System.Text.Json;
using Microsoft.Extensions.Options;
using Sgs.Collector.Common;
using Sgs.Collector.Crawl;
using Sgs.Collector.Data;
using Sgs.Collector.Options;
using Sgs.Collector.Storage;
using Sgs.Collector.Sync;

namespace Sgs.Collector.Hosting;

public sealed record ManualTaskSnapshot(string Kind, long Done, long Total, string? Message, bool Finished, string StartedAt, string? FinishedAt);

public sealed record CoordinatorStatus(string State, RunLive? Current, RunLive? Last, DateTimeOffset? NextScheduledAt, ManualTaskSnapshot? ManualTask);

public sealed class ManualTaskState
{
    private readonly object _lock = new();
    private long _done;
    private long _total;
    private string? _message;
    private bool _finished;
    private string? _finishedAt;

    public ManualTaskState(string kind, string message, string startedAt)
    {
        Kind = kind;
        _message = message;
        StartedAt = startedAt;
    }

    public string Kind { get; }

    public string StartedAt { get; }

    public void SetProgress(long done, long total)
    {
        lock (_lock)
        {
            _done = done;
            _total = total;
        }
    }

    public void Finish(string message, string finishedAt)
    {
        lock (_lock)
        {
            _message = message;
            _finished = true;
            _finishedAt = finishedAt;
        }
    }

    public ManualTaskSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new ManualTaskSnapshot(Kind, _done, _total, _message, _finished, StartedAt, _finishedAt);
        }
    }
}

public sealed class RunCoordinator
{
    // 与 Data/*.cs 的 SQL 字面量一致
    public const string StateIdle = "idle";
    public const string StateRunning = "running";
    public const string StateCancelling = "cancelling";
    public const string StateSyncing = "syncing";
    public const string StateRestoring = "restoring";

    public const string TriggerManual = "manual";
    public const string TriggerSchedule = "schedule";

    public const string StatusCompleted = "completed";
    public const string StatusCancelled = "cancelled";
    public const string StatusFailed = "failed";

    public const string SyncOk = "ok";
    public const string SyncFailed = "failed";
    public const string SyncCancelled = "cancelled";
    public const string SyncSkipped = "skipped";

    public const string TaskSync = "sync";
    public const string TaskFullSync = "full-sync";
    public const string TaskRestore = "restore";

    private const string PhaseSyncing = "syncing";
    private const string PhaseDone = "done";
    private const int MaxReportedProblems = 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CrawlRunner _runner;
    private readonly CatalogSync _sync;
    private readonly CatalogRestore _restore;
    private readonly RunRepository _runs;
    private readonly SyncRepository _syncRepo;
    private readonly ICatalogClient _catalog;
    private readonly CollectorOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RunCoordinator> _logger;

    private readonly object _gate = new();
    private string _state = StateIdle;
    private RunProgress? _current;
    private RunProgress? _last;
    private CancellationTokenSource? _cts;
    private ManualTaskState? _manualTask;
    private DateTimeOffset? _nextScheduledAt;
    private Task? _active;

    public RunCoordinator(
        CrawlRunner runner,
        CatalogSync sync,
        CatalogRestore restore,
        RunRepository runs,
        SyncRepository syncRepo,
        ICatalogClient catalog,
        IOptions<CollectorOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<RunCoordinator> logger)
    {
        _runner = runner;
        _sync = sync;
        _restore = restore;
        _runs = runs;
        _syncRepo = syncRepo;
        _catalog = catalog;
        _options = options.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    public DateTimeOffset? NextScheduledAt
    {
        get
        {
            lock (_gate)
            {
                return _nextScheduledAt;
            }
        }
        set
        {
            lock (_gate)
            {
                _nextScheduledAt = value;
            }
        }
    }

    public bool TryStartRun(string trigger)
    {
        lock (_gate)
        {
            if (!string.Equals(_state, StateIdle, StringComparison.Ordinal))
            {
                return false;
            }

            _state = StateRunning;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            var token = _cts.Token;
            _active = Task.Run(() => ExecuteRunAsync(trigger, token));
            return true;
        }
    }

    public bool TryCancel()
    {
        lock (_gate)
        {
            var cancellable = string.Equals(_state, StateRunning, StringComparison.Ordinal)
                || string.Equals(_state, StateSyncing, StringComparison.Ordinal)
                || string.Equals(_state, StateRestoring, StringComparison.Ordinal);
            if (!cancellable || _cts is null)
            {
                return false;
            }

            _state = StateCancelling;
            _cts.Cancel();
            return true;
        }
    }

    public bool TryStartSync(bool full)
    {
        lock (_gate)
        {
            if (!string.Equals(_state, StateIdle, StringComparison.Ordinal))
            {
                return false;
            }

            _state = StateSyncing;
            var task = new ManualTaskState(full ? TaskFullSync : TaskSync, full ? "正在全量重新同步" : "正在同步", Clock.NowIso());
            _manualTask = task;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            var token = _cts.Token;
            _active = Task.Run(() => ExecuteSyncAsync(full, task, token));
            return true;
        }
    }

    public bool TryStartRestore()
    {
        lock (_gate)
        {
            if (!string.Equals(_state, StateIdle, StringComparison.Ordinal))
            {
                return false;
            }

            _state = StateRestoring;
            var task = new ManualTaskState(TaskRestore, "正在从 Worker 恢复", Clock.NowIso());
            _manualTask = task;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            var token = _cts.Token;
            _active = Task.Run(() => ExecuteRestoreAsync(task, token));
            return true;
        }
    }

    // 等待当前后台任务（含不可取消的收尾写库）结束；ct 取消时提前返回，任务自身的异常不外抛
    public async Task WaitForIdleAsync(CancellationToken ct)
    {
        Task? active;
        lock (_gate)
        {
            active = _active;
        }

        if (active is null)
        {
            return;
        }

        try
        {
            await active.WaitAsync(ct);
        }
        catch
        {
            // 超时取消或任务自身失败（已在任务内记录日志）都不影响关闭流程
        }
    }

    public CoordinatorStatus GetStatus()
    {
        lock (_gate)
        {
            return new CoordinatorStatus(_state, _current?.LiveSnapshot(), _last?.LiveSnapshot(), _nextScheduledAt, _manualTask?.Snapshot());
        }
    }

    private async Task ExecuteRunAsync(string trigger, CancellationToken ct)
    {
        long runId = 0;
        RunProgress? progress = null;
        var status = StatusFailed;
        string? error = null;

        try
        {
            runId = await _runs.CreateAsync(trigger, Clock.NowIso());
            progress = new RunProgress(runId, trigger, Clock.NowIso());
            lock (_gate)
            {
                _current = progress;
            }

            await EnsureCatalogConsistentAsync(ct);
            await _runner.RunAsync(progress, ct);
            status = ct.IsCancellationRequested ? StatusCancelled : StatusCompleted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = StatusCancelled;
        }
        catch (Exception ex)
        {
            status = StatusFailed;
            error = ex.Message;
            _logger.LogError(ex, "收集轮次 {RunId} 失败", runId);
        }

        try
        {
            // 收尾：数据库写入一律不可取消
            if (runId > 0)
            {
                var statsJson = progress is null ? null : JsonSerializer.Serialize(progress.Snapshot(), JsonOptions);
                await _runs.FinishAsync(runId, status, statsJson, error, Clock.NowIso());
                await _runs.PruneFailuresAsync(Clock.FormatIso(DateTimeOffset.UtcNow.AddDays(-_options.FailureRetentionDays)));

                if (string.Equals(status, StatusCompleted, StringComparison.Ordinal))
                {
                    lock (_gate)
                    {
                        if (string.Equals(_state, StateRunning, StringComparison.Ordinal))
                        {
                            _state = StateSyncing;
                        }
                    }

                    progress!.SetPhase(PhaseSyncing);
                    try
                    {
                        var report = await _sync.PushPendingAsync(progress.SetSync, ct);
                        if (report.Problems.Count == 0)
                        {
                            await _runs.SetSyncResultAsync(runId, SyncOk, null);
                            progress.Info($"同步完成：{report.Accepted} 条");
                        }
                        else
                        {
                            var message = $"{report.Problems.Count} 条未能同步：" + string.Join("；", report.Problems.Take(MaxReportedProblems));
                            await _runs.SetSyncResultAsync(runId, SyncFailed, message);
                            progress.Warn(message);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        await _runs.SetSyncResultAsync(runId, SyncCancelled, null);
                    }
                    catch (Exception ex)
                    {
                        await _runs.SetSyncResultAsync(runId, SyncFailed, ex.Message);
                        progress.Error("同步失败：" + ex.Message);
                        _logger.LogError(ex, "收集轮次 {RunId} 的同步失败", runId);
                    }
                }
                else
                {
                    await _runs.SetSyncResultAsync(runId, SyncSkipped, null);
                }

                progress?.SetPhase(PhaseDone);
            }
        }
        catch (Exception ex)
        {
            // 收尾失败只记日志，不再抛出
            _logger.LogError(ex, "收集轮次 {RunId} 收尾失败", runId);
        }
        finally
        {
            lock (_gate)
            {
                _last = progress;
                _current = null;
                _state = StateIdle;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    private async Task ExecuteSyncAsync(bool full, ManualTaskState task, CancellationToken ct)
    {
        string message;
        try
        {
            var report = full
                ? await _sync.FullResyncAsync(task.SetProgress, ct)
                : await _sync.PushPendingAsync(task.SetProgress, ct);
            message = report.Problems.Count == 0
                ? $"同步完成：{report.Accepted} 条"
                : $"同步完成，但 {report.Problems.Count} 条未能同步：{report.Problems[0]}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            message = "同步已终止";
        }
        catch (Exception ex)
        {
            message = "同步失败：" + ex.Message;
            _logger.LogError(ex, "手动同步失败");
        }

        FinishManualTask(task, message);
    }

    private async Task ExecuteRestoreAsync(ManualTaskState task, CancellationToken ct)
    {
        string message;
        try
        {
            var count = await _restore.RestoreAsync(task.SetProgress, ct);
            message = $"恢复完成：{count} 个版本";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            message = "恢复已终止（可再次执行以继续）";
        }
        catch (Exception ex)
        {
            message = "恢复失败：" + ex.Message;
            _logger.LogError(ex, "从 Worker 恢复失败");
        }

        FinishManualTask(task, message);
    }

    private void FinishManualTask(ManualTaskState task, string message)
    {
        lock (_gate)
        {
            task.Finish(message, Clock.NowIso());
            _state = StateIdle;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // D14：本地版本数少于 Worker 时拒绝收集，否则新版本号会与已发布的冲突
    private async Task EnsureCatalogConsistentAsync(CancellationToken ct)
    {
        var local = await _syncRepo.CountVersionsAsync();

        CatalogStats remote;
        try
        {
            remote = await _catalog.GetStatsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法连接 Worker：" + ex.Message, ex);
        }

        if (local < remote.Versions)
        {
            throw new InvalidOperationException(
                $"本地版本数（{local}）少于 Worker（{remote.Versions}），本地数据库可能丢失或来自旧备份。为避免版本号冲突，本轮已拒绝执行。请在界面执行“从 Worker 恢复”（本地已有收集数据时，需先停止程序并删除 App_Data/collector.db*）。");
        }
    }
}
