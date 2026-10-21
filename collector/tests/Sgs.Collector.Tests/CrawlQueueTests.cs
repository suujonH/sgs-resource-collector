using Sgs.Collector.Crawl;
using Sgs.Collector.Discovery;

namespace Sgs.Collector.Tests;

public sealed class CrawlQueueTests
{
    private const string EntryUrl = "https://web.sanguosha.com/220/h5_2/index_210000.php";
    private const string Root = "https://web.sanguosha.com/220/h5_2/";

    private static CrawlQueue NewQueue() => new(new DiscoveryRules(EntryUrl));

    [Fact]
    public async Task Reader_DoesNotCompleteBeforeReleaseBootstrap()
    {
        var queue = NewQueue();

        Assert.False(queue.Reader.Completion.IsCompleted);
        queue.TryEnqueue(new Uri(Root + "res/a.json"), "test");
        queue.MarkDone();
        Assert.False(queue.Reader.Completion.IsCompleted);

        queue.ReleaseBootstrap();
        Assert.True(queue.Reader.TryRead(out _));
        await queue.Reader.Completion;
    }

    [Fact]
    public async Task ReleaseBootstrap_CompletesTheChannelWhenNothingWasEnqueued()
    {
        var queue = NewQueue();

        queue.ReleaseBootstrap();

        await queue.Reader.Completion;
    }

    [Fact]
    public void TryEnqueue_SameKeyOnlyOnce_AndOutOfScopeCountedOnce()
    {
        var queue = NewQueue();

        Assert.True(queue.TryEnqueue(new Uri(Root + "res/a.json?v=1"), "test"));
        Assert.False(queue.TryEnqueue(new Uri(Root + "res/a.json?v=2#frag"), "test"));
        Assert.False(queue.TryEnqueue(new Uri("https://other.example.com/x.json"), "test"));
        Assert.False(queue.TryEnqueue(new Uri("https://other.example.com/x.json?v=3"), "test"));

        Assert.Equal(1, queue.QueueLength);
        Assert.Equal(1, queue.OutOfScopeCount);
    }

    [Fact]
    public void TryEnqueue_BootstrapKeysAreIgnored()
    {
        var queue = NewQueue();

        Assert.False(queue.TryEnqueue(new Uri(Root + "version.json?t=123"), "test"));
        Assert.False(queue.TryEnqueue(new Uri(Root + "libs/min/aesresc?v=1.0"), "test"));
        Assert.False(queue.TryEnqueue(new Uri(EntryUrl + "?x=1"), "test"));
        Assert.Equal(0, queue.QueueLength);
        Assert.Equal(0, queue.OutOfScopeCount);
    }

    [Fact]
    public void Register_ReturnsItemForBootstrapKey_AndRejectsOthers()
    {
        var queue = NewQueue();

        var item = queue.Register(new Uri(Root + "version.json?t=1#x"), "bootstrap", CrawlItemKind.Archive);

        Assert.Equal("web.sanguosha.com/220/h5_2/version.json", item.Key);
        Assert.Equal(string.Empty, item.FetchUri.Fragment);
        Assert.Equal(0, queue.QueueLength);
        Assert.Throws<InvalidOperationException>(() => queue.Register(new Uri(Root + "res/a.json"), "x", CrawlItemKind.Archive));
    }

    [Fact]
    public void TryEnqueue_AfterCompletion_Throws()
    {
        var queue = NewQueue();
        queue.ReleaseBootstrap();

        Assert.Throws<InvalidOperationException>(() => queue.TryEnqueue(new Uri(Root + "res/late.json"), "test"));
    }
}
