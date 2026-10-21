using System.Collections.Concurrent;
using Sgs.Collector.Common;

namespace Sgs.Collector.Crawl;

public sealed record RunStats(long Discovered, long OutOfScope, long Completed, long NotModified, long Unchanged,
    long NewVersions, long MemberNewVersions, long MemberUnchanged, long MemberFailed, long Failed, long NeverSucceeded,
    long Cancelled, long DiscoverOnly, long LimitSkipped, long BytesDownloaded, IReadOnlyList<string> Warnings);

public sealed record InFlightItem(string Key, string StartedAt);

public sealed record RunEvent(string At, string Level, string Message);

public sealed record RunLive(long RunId, string Trigger, string StartedAt, string Phase, RunStats Stats, long QueueLength,
    IReadOnlyList<InFlightItem> InFlight, IReadOnlyList<RunEvent> Events, long SyncDone, long SyncTotal);

public sealed class RunProgress
{
    private const string LevelInfo = "info";
    private const string LevelWarn = "warn";
    private const string LevelError = "error";
    private const string InitialPhase = "bootstrap";
    private const string LimitMessage = "已达到 MaxItems 上限，剩余条目不处理";
    private const int MaxEvents = 300;
    private const int MaxWarnings = 200;

    private readonly object _eventLock = new();
    private readonly LinkedList<RunEvent> _events = new();
    private readonly List<string> _warnings = [];
    private readonly ConcurrentDictionary<string, string> _inFlight = new(StringComparer.Ordinal);
    private string _phase = InitialPhase;
    private CrawlQueue? _queue;

    private long _completed;
    private long _notModified;
    private long _unchanged;
    private long _newVersions;
    private long _failed;
    private long _neverSucceeded;
    private long _cancelled;
    private long _discoverOnly;
    private long _limitSkipped;
    private long _memberNewVersions;
    private long _memberUnchanged;
    private long _memberFailed;
    private long _bytesDownloaded;
    private long _syncDone;
    private long _syncTotal;

    public RunProgress(long runId, string trigger, string startedAt)
    {
        RunId = runId;
        Trigger = trigger;
        StartedAt = startedAt;
    }

    public long RunId { get; }

    public string Trigger { get; }

    public string StartedAt { get; }

    public void Complete(ItemOutcome outcome, bool hadHistory)
    {
        Interlocked.Increment(ref _completed);
        switch (outcome)
        {
            case ItemOutcome.NotModified:
                Interlocked.Increment(ref _notModified);
                break;
            case ItemOutcome.Unchanged:
                Interlocked.Increment(ref _unchanged);
                break;
            case ItemOutcome.NewVersion:
                Interlocked.Increment(ref _newVersions);
                break;
            case ItemOutcome.Failed:
                if (hadHistory)
                {
                    Interlocked.Increment(ref _failed);
                }
                else
                {
                    Interlocked.Increment(ref _neverSucceeded);
                }

                break;
            case ItemOutcome.Cancelled:
                Interlocked.Increment(ref _cancelled);
                break;
            case ItemOutcome.DiscoverOnly:
                Interlocked.Increment(ref _discoverOnly);
                break;
        }
    }

    public void AddBytes(long bytes) => Interlocked.Add(ref _bytesDownloaded, bytes);

    public void MemberNewVersion() => Interlocked.Increment(ref _memberNewVersions);

    public void MemberUnchanged() => Interlocked.Increment(ref _memberUnchanged);

    public void MemberFailed() => Interlocked.Increment(ref _memberFailed);

    public void LimitSkipped()
    {
        if (Interlocked.Increment(ref _limitSkipped) == 1)
        {
            Warn(LimitMessage);
        }
    }

    public void SetSync(long done, long total)
    {
        Interlocked.Exchange(ref _syncDone, done);
        Interlocked.Exchange(ref _syncTotal, total);
    }

    public void SetPhase(string phase)
    {
        lock (_eventLock)
        {
            _phase = phase;
        }

        Info("阶段：" + phase);
    }

    public void AttachQueue(CrawlQueue queue) => Volatile.Write(ref _queue, queue);

    public void BeginItem(string key) => _inFlight[key] = Clock.NowIso();

    public void EndItem(string key) => _inFlight.TryRemove(key, out _);

    public void Info(string message) => AddEvent(LevelInfo, message);

    public void Warn(string message) => AddEvent(LevelWarn, message);

    public void Error(string message) => AddEvent(LevelError, message);

    public RunStats Snapshot()
    {
        var queue = Volatile.Read(ref _queue);
        string[] warnings;
        lock (_eventLock)
        {
            warnings = [.. _warnings];
        }

        return new RunStats(
            queue?.KnownCount ?? 0,
            queue?.OutOfScopeCount ?? 0,
            Volatile.Read(ref _completed),
            Volatile.Read(ref _notModified),
            Volatile.Read(ref _unchanged),
            Volatile.Read(ref _newVersions),
            Volatile.Read(ref _memberNewVersions),
            Volatile.Read(ref _memberUnchanged),
            Volatile.Read(ref _memberFailed),
            Volatile.Read(ref _failed),
            Volatile.Read(ref _neverSucceeded),
            Volatile.Read(ref _cancelled),
            Volatile.Read(ref _discoverOnly),
            Volatile.Read(ref _limitSkipped),
            Volatile.Read(ref _bytesDownloaded),
            warnings);
    }

    public RunLive LiveSnapshot()
    {
        var stats = Snapshot();
        string phase;
        RunEvent[] events;
        lock (_eventLock)
        {
            phase = _phase;
            events = [.. _events];
        }

        var inFlight = _inFlight
            .Select(pair => new InFlightItem(pair.Key, pair.Value))
            .OrderBy(item => item.StartedAt, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();

        return new RunLive(RunId, Trigger, StartedAt, phase, stats, Volatile.Read(ref _queue)?.QueueLength ?? 0,
            inFlight, events, Volatile.Read(ref _syncDone), Volatile.Read(ref _syncTotal));
    }

    private void AddEvent(string level, string message)
    {
        var entry = new RunEvent(Clock.NowIso(), level, message);
        lock (_eventLock)
        {
            _events.AddFirst(entry);
            if (_events.Count > MaxEvents)
            {
                _events.RemoveLast();
            }

            if (string.Equals(level, LevelWarn, StringComparison.Ordinal) && _warnings.Count < MaxWarnings)
            {
                _warnings.Add(message);
            }
        }
    }
}
