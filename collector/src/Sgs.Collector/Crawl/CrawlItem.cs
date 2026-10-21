namespace Sgs.Collector.Crawl;

public enum CrawlItemKind { Archive, DiscoverOnly }

public sealed record CrawlItem(string Key, Uri FetchUri, CrawlItemKind Kind, string Source);

public enum ItemOutcome { NotModified, Unchanged, NewVersion, Failed, Cancelled, DiscoverOnly }
