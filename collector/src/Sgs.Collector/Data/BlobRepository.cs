using Dapper;

namespace Sgs.Collector.Data;

public sealed class BlobRepository
{
    private readonly Db _db;

    public BlobRepository(Db db)
    {
        _db = db;
    }

    public async Task<bool> ExistsAsync(string hash)
    {
        await using var connection = await _db.OpenAsync();
        var found = await connection.ExecuteScalarAsync<long?>(
            "SELECT 1 FROM blobs WHERE hash=@hash",
            new { hash });
        return found.HasValue;
    }

    // 两个 worker 可能同时上传同一哈希，所以用 OR IGNORE
    public Task MarkUploadedAsync(string hash, long size, string now)
    {
        return _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(
            "INSERT OR IGNORE INTO blobs (hash, size, uploaded_at) VALUES (@hash, @size, @now)",
            new { hash, size, now },
            transaction: transaction));
    }
}
