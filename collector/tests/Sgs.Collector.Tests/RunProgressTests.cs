using Sgs.Collector.Crawl;

namespace Sgs.Collector.Tests;

public sealed class RunProgressTests
{
    private static RunProgress New() => new(7, "manual", "2024-01-01T00:00:00.000Z");

    [Fact]
    public void Complete_AllocatesCountersByOutcome()
    {
        var progress = New();

        progress.Complete(ItemOutcome.NotModified, true);
        progress.Complete(ItemOutcome.Unchanged, true);
        progress.Complete(ItemOutcome.NewVersion, false);
        progress.Complete(ItemOutcome.Failed, true);
        progress.Complete(ItemOutcome.Failed, false);
        progress.Complete(ItemOutcome.Failed, false);
        progress.Complete(ItemOutcome.Cancelled, false);
        progress.Complete(ItemOutcome.DiscoverOnly, false);

        var stats = progress.Snapshot();
        Assert.Equal(8, stats.Completed);
        Assert.Equal(1, stats.NotModified);
        Assert.Equal(1, stats.Unchanged);
        Assert.Equal(1, stats.NewVersions);
        Assert.Equal(1, stats.Failed);
        Assert.Equal(2, stats.NeverSucceeded);
        Assert.Equal(1, stats.Cancelled);
        Assert.Equal(1, stats.DiscoverOnly);
        Assert.Equal(0, stats.Discovered);
        Assert.Equal(0, stats.OutOfScope);
    }

    [Fact]
    public void LimitSkipped_WarnsOnlyOnce()
    {
        var progress = New();

        progress.LimitSkipped();
        progress.LimitSkipped();
        progress.LimitSkipped();

        var stats = progress.Snapshot();
        Assert.Equal(3, stats.LimitSkipped);
        Assert.Single(stats.Warnings);
        Assert.Single(progress.LiveSnapshot().Events, e => e.Level == "warn");
    }

    [Fact]
    public void Events_KeepAtMost300_NewestFirst()
    {
        var progress = New();

        for (var i = 0; i < 350; i++)
        {
            progress.Info("m" + i);
        }

        var events = progress.LiveSnapshot().Events;
        Assert.Equal(300, events.Count);
        Assert.Equal("m349", events[0].Message);
        Assert.Equal("m50", events[^1].Message);
    }

    [Fact]
    public void LiveSnapshot_ReportsPhaseInFlightAndMetadata()
    {
        var progress = New();
        Assert.Equal("bootstrap", progress.LiveSnapshot().Phase);

        progress.SetPhase("crawling");
        progress.SetSync(2, 5);
        progress.BeginItem("a");
        progress.BeginItem("b");
        progress.EndItem("a");

        var live = progress.LiveSnapshot();
        Assert.Equal(7, live.RunId);
        Assert.Equal("manual", live.Trigger);
        Assert.Equal("crawling", live.Phase);
        Assert.Equal(2, live.SyncDone);
        Assert.Equal(5, live.SyncTotal);
        Assert.Equal("b", Assert.Single(live.InFlight).Key);
        Assert.Equal("info", live.Events[0].Level);
    }

    [Fact]
    public async Task ConcurrentUpdates_AreNotLost()
    {
        var progress = New();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                progress.Complete(ItemOutcome.NewVersion, true);
                progress.AddBytes(2);
                progress.Info("x");
            }
        })));

        var stats = progress.Snapshot();
        Assert.Equal(16000, stats.Completed);
        Assert.Equal(16000, stats.NewVersions);
        Assert.Equal(32000, stats.BytesDownloaded);
    }
}
