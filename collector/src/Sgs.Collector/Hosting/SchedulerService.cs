using Microsoft.Extensions.Options;
using Sgs.Collector.Common;
using Sgs.Collector.Data;
using Sgs.Collector.Options;

namespace Sgs.Collector.Hosting;

public sealed class SchedulerService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly RunRepository _runs;
    private readonly RunCoordinator _coordinator;
    private readonly CollectorOptions _options;
    private readonly ILogger<SchedulerService> _logger;

    public SchedulerService(
        RunRepository runs,
        RunCoordinator coordinator,
        IOptions<CollectorOptions> options,
        ILogger<SchedulerService> logger)
    {
        _runs = runs;
        _coordinator = coordinator;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.IntervalMinutes <= 0)
        {
            _coordinator.NextScheduledAt = null;
            return;
        }

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            do
            {
                try
                {
                    var last = await _runs.GetLastCrawlEndAsync();
                    if (last is null)
                    {
                        // D11：首轮必须手动启动
                        _coordinator.NextScheduledAt = null;
                        continue;
                    }

                    var next = Clock.ParseIso(last).AddMinutes(_options.IntervalMinutes);
                    _coordinator.NextScheduledAt = next;
                    if (DateTimeOffset.UtcNow >= next)
                    {
                        _coordinator.TryStartRun(RunCoordinator.TriggerSchedule);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "定时调度检查失败");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 程序退出
        }
    }
}
