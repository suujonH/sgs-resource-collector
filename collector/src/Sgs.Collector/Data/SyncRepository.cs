using Dapper;
using Sgs.Collector.Storage;

namespace Sgs.Collector.Data;

public sealed class SyncRepository
{
    private readonly Db _db;

    public SyncRepository(Db db)
    {
        _db = db;
    }

    public async Task<long> CountUnsyncedAsync()
    {
        await using var connection = await _db.OpenAsync();
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM versions WHERE synced = 0");
    }

    public async Task<long> CountVersionsAsync()
    {
        await using var connection = await _db.OpenAsync();
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM versions");
    }

    public async Task<long> CountCrawlVersionsAsync()
    {
        await using var connection = await _db.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM versions v JOIN runs r ON r.id = v.run_id WHERE r.trigger_kind <> 'restore'");
    }

    // 按游标翻页：被拒绝的行不会挡住后面的行
    public async Task<IReadOnlyList<UnsyncedVersionRow>> GetUnsyncedAfterAsync(long afterResourceId, long afterVersion, int limit)
    {
        await using var connection = await _db.OpenAsync();
        var rows = await connection.QueryAsync<UnsyncedVersionRow>(
            """
            SELECT v.resource_id, v.version, r.resource_key, v.hash, v.size, v.content_type, v.collected_at
            FROM versions v JOIN resources r ON r.id = v.resource_id
            WHERE v.synced = 0 AND (v.resource_id > @afterResourceId OR (v.resource_id = @afterResourceId AND v.version > @afterVersion))
            ORDER BY v.resource_id, v.version LIMIT @limit
            """,
            new { afterResourceId, afterVersion, limit });
        return rows.AsList();
    }

    // 只标记确切推送成功的主键
    public Task MarkSyncedAsync(IReadOnlyList<UnsyncedVersionRow> rows)
    {
        if (rows.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(
            "UPDATE versions SET synced = 1 WHERE resource_id=@ResourceId AND version=@Version",
            rows,
            transaction: transaction));
    }

    public Task ResetAllAsync()
    {
        return _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(
            "UPDATE versions SET synced = 0",
            transaction: transaction));
    }

    // 恢复用，幂等：重复执行或中途终止后重跑，结果都一样
    public Task ImportPageAsync(IReadOnlyList<CatalogVersionDto> rows, long runId, string now)
    {
        return _db.WriteAsync(async (connection, transaction) =>
        {
            foreach (var row in rows)
            {
                var id = await connection.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO resources (resource_key, first_seen_at) VALUES (@Key, @CollectedAt)
                      ON CONFLICT(resource_key) DO UPDATE SET resource_key = excluded.resource_key RETURNING id;
                    """,
                    new { row.Key, row.CollectedAt },
                    transaction: transaction);

                var args = new { id, runId, now, row.Version, row.Hash, row.Size, row.ContentType, row.CollectedAt };
                await connection.ExecuteAsync(
                    """
                    INSERT OR IGNORE INTO versions (resource_id, version, hash, size, content_type, collected_at, run_id, synced)
                      VALUES (@id, @Version, @Hash, @Size, @ContentType, @CollectedAt, @runId, 1);
                    """,
                    args,
                    transaction: transaction);
                await connection.ExecuteAsync(
                    """
                    UPDATE resources SET latest_version = @Version, latest_hash = @Hash, last_success_at = @CollectedAt
                      WHERE id = @id AND latest_version < @Version;
                    """,
                    args,
                    transaction: transaction);
                await connection.ExecuteAsync(
                    "INSERT OR IGNORE INTO blobs (hash, size, uploaded_at) VALUES (@Hash, @Size, @now);",
                    args,
                    transaction: transaction);
            }
        });
    }
}
