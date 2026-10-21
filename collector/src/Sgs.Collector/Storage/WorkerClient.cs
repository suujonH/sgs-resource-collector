namespace Sgs.Collector.Storage;

public interface IBlobStore
{
    Task PutAsync(string hash, byte[] content, string contentType, CancellationToken ct);
}

public interface ICatalogClient
{
    Task<CatalogSyncResult> SyncAsync(IReadOnlyList<CatalogVersionDto> rows, CancellationToken ct);
    Task<CatalogStats> GetStatsAsync(CancellationToken ct);
    Task<CatalogExportPage> ExportAsync(string afterKey, long afterVersion, int limit, CancellationToken ct);
}

public sealed record CatalogVersionDto(string Key, long Version, string Hash, long Size, string ContentType, string CollectedAt);
public sealed record CatalogSyncResult(int Accepted, IReadOnlyList<SyncRejected> Rejected, IReadOnlyList<SyncConflict> Conflicts);
public sealed record SyncRejected(int Index, string Reason);
public sealed record SyncConflict(string Key, long Version, string ExistingHash);
public sealed record CatalogStats(long Resources, long Versions);
public sealed record CatalogExportPage(IReadOnlyList<CatalogVersionDto> Rows, CatalogCursor? Next);
public sealed record CatalogCursor(string Key, long Version);
