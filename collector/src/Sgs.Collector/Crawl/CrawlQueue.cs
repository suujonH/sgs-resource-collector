using System.Threading.Channels;
using Sgs.Collector.Common;
using Sgs.Collector.Discovery;

namespace Sgs.Collector.Crawl;

public sealed class CrawlQueue
{
    private readonly DiscoveryRules _rules;
    private readonly Channel<CrawlItem> _channel = Channel.CreateUnbounded<CrawlItem>();
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly HashSet<string> _outOfScope = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private int _pending = 1;

    public CrawlQueue(DiscoveryRules rules)
    {
        _rules = rules;
        foreach (var key in rules.BootstrapKeys)
        {
            _known.Add(key);
        }
    }

    public ChannelReader<CrawlItem> Reader => _channel.Reader;

    public long KnownCount
    {
        get
        {
            lock (_lock)
            {
                return _known.Count;
            }
        }
    }

    public long OutOfScopeCount
    {
        get
        {
            lock (_lock)
            {
                return _outOfScope.Count;
            }
        }
    }

    public long QueueLength => _channel.Reader.Count;

    // 启动条目直接处理、不进通道；_known 已在构造时占位
    public CrawlItem Register(Uri uri, string source, CrawlItemKind kind)
    {
        var stripped = ResourceKeys.StripFragment(uri);
        var key = ResourceKeys.FromUri(stripped);
        if (!_rules.BootstrapKeys.Contains(key))
        {
            throw new InvalidOperationException("只有启动条目可以直接处理：" + key);
        }

        return new CrawlItem(key, stripped, kind, source);
    }

    public bool TryEnqueue(Uri uri, string source)
    {
        var stripped = ResourceKeys.StripFragment(uri);
        var key = ResourceKeys.FromUri(stripped);
        lock (_lock)
        {
            if (_known.Contains(key))
            {
                return false;
            }

            if (!_rules.InScope(stripped))
            {
                _outOfScope.Add(key);
                return false;
            }

            var item = new CrawlItem(key, stripped, CrawlItemKind.Archive, source);
            _known.Add(key);
            Interlocked.Increment(ref _pending);
            if (!_channel.Writer.TryWrite(item))
            {
                throw new InvalidOperationException("队列已关闭，仍有条目入队");
            }

            return true;
        }
    }

    public void MarkDone()
    {
        if (Interlocked.Decrement(ref _pending) == 0)
        {
            _channel.Writer.TryComplete();
        }
    }

    public void ReleaseBootstrap() => MarkDone();
}
