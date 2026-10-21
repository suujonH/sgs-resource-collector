using Dapper;
using Sgs.Collector.Discovery;

namespace Sgs.Collector.Data;

public sealed class ResourceRepository
{
    private readonly Db _db;

    public ResourceRepository(Db db)
    {
        _db = db;
    }

    public Task<ResourceRow> UpsertSeenAsync(string key, string fetchUrl, long runId, string now)
    {
        return _db.WriteAsync((connection, transaction) => connection.QuerySingleAsync<ResourceRow>(
            """
            INSERT INTO resources (resource_key, fetch_url, first_seen_at, last_seen_run) VALUES (@key, @fetchUrl, @now, @runId)
            ON CONFLICT(resource_key) DO UPDATE SET fetch_url = excluded.fetch_url, last_seen_run = excluded.last_seen_run
            RETURNING *;
            """,
            new { key, fetchUrl, now, runId },
            transaction: transaction));
    }

    public Task<ResourceRow> UpsertMemberAsync(string key, long parentId, string sourcePath, long runId, string now)
    {
        return _db.WriteAsync((connection, transaction) => connection.QuerySingleAsync<ResourceRow>(
            """
            INSERT INTO resources (resource_key, parent_id, source_path, first_seen_at, last_seen_run)
            VALUES (@key, @parentId, @sourcePath, @now, @runId)
            ON CONFLICT(resource_key) DO UPDATE SET parent_id = excluded.parent_id, source_path = excluded.source_path,
                last_seen_run = excluded.last_seen_run
            RETURNING *;
            """,
            new { key, parentId, sourcePath, now, runId },
            transaction: transaction));
    }

    public async Task<IReadOnlyDictionary<string, string>> GetMemberKeysAsync(long parentId)
    {
        await using var connection = await _db.OpenAsync();
        var rows = await connection.QueryAsync<(string SourcePath, string ResourceKey)>(
            "SELECT source_path, resource_key FROM resources WHERE parent_id = @parentId AND source_path IS NOT NULL",
            new { parentId });
        return rows.ToDictionary(row => row.SourcePath, row => row.ResourceKey, StringComparer.Ordinal);
    }

    public Task<long> AddVersionAsync(long resourceId, string hash, long size, string contentType, long runId, string now)
    {
        return _db.WriteAsync(async (connection, transaction) =>
        {
            var current = await connection.QuerySingleAsync<(long LatestVersion, string? LatestHash)>(
                "SELECT latest_version, latest_hash FROM resources WHERE id = @resourceId",
                new { resourceId },
                transaction: transaction);
            if (string.Equals(current.LatestHash, hash, StringComparison.Ordinal))
            {
                return current.LatestVersion;
            }

            var next = current.LatestVersion + 1;
            await connection.ExecuteAsync(
                """
                INSERT INTO versions (resource_id, version, hash, size, content_type, collected_at, run_id, synced)
                VALUES (@resourceId, @next, @hash, @size, @contentType, @now, @runId, 0)
                """,
                new { resourceId, next, hash, size, contentType, now, runId },
                transaction: transaction);
            await connection.ExecuteAsync(
                "UPDATE resources SET latest_version = @next, latest_hash = @hash WHERE id = @resourceId",
                new { next, hash, resourceId },
                transaction: transaction);
            return next;
        });
    }

    public async Task<IReadOnlyList<LinkRow>> GetLinksAsync(long resourceId)
    {
        await using var connection = await _db.OpenAsync();
        var rows = await connection.QueryAsync<LinkRow>(
            "SELECT url, source FROM links WHERE resource_id = @resourceId",
            new { resourceId });
        return rows.AsList();
    }

    public Task MarkNotModifiedAsync(long id, long httpStatus, string now)
    {
        return _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(
            """
            UPDATE resources SET last_checked_at=@now, last_success_at=@now, last_outcome='not_modified',
                last_http_status=@httpStatus, last_error=NULL WHERE id=@id
            """,
            new { now, httpStatus, id },
            transaction: transaction));
    }

    public Task CommitProcessedAsync(
        long id,
        string outcome,
        long httpStatus,
        string? etag,
        string? lastModified,
        long? processingRev,
        IReadOnlyList<DiscoveredLink> links,
        string now)
    {
        return _db.WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(
                "DELETE FROM links WHERE resource_id = @id",
                new { id },
                transaction: transaction);
            foreach (var link in links)
            {
                await connection.ExecuteAsync(
                    "INSERT OR IGNORE INTO links (resource_id, url, source) VALUES (@id, @Url, @Source)",
                    new { id, Url = link.Uri.AbsoluteUri, link.Source },
                    transaction: transaction);
            }

            await connection.ExecuteAsync(
                """
                UPDATE resources SET etag=@etag, last_modified=@lastModified, processing_rev=@processingRev,
                    last_checked_at=@now, last_success_at=@now, last_outcome=@outcome, last_http_status=@httpStatus, last_error=NULL
                WHERE id=@id
                """,
                new { etag, lastModified, processingRev, now, outcome, httpStatus, id },
                transaction: transaction);
        });
    }

    public Task MarkMemberCheckedAsync(long id, string outcome, string now)
    {
        return _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(
            """
            UPDATE resources SET last_checked_at=@now, last_success_at=@now, last_outcome=@outcome,
                last_http_status=NULL, last_error=NULL WHERE id=@id
            """,
            new { now, outcome, id },
            transaction: transaction));
    }

    public Task MarkFailedAsync(long id, long? httpStatus, string error, bool clearValidators, string now)
    {
        var sql = "UPDATE resources SET last_checked_at=@now, last_outcome='failed', last_http_status=@httpStatus, last_error=@error"
            + (clearValidators ? ", etag=NULL, last_modified=NULL" : "")
            + " WHERE id=@id";
        return _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(
            sql,
            new { now, httpStatus, error, id },
            transaction: transaction));
    }

    public async Task<PagedResult<ResourceListItemRow>> SearchAsync(string? q, string status, int offset, int limit)
    {
        var conditions = new List<string>();
        if (!string.IsNullOrEmpty(q))
        {
            conditions.Add("instr(lower(resource_key), lower(@q)) > 0");
        }

        var statusCondition = status switch
        {
            "ok" => "latest_version > 0 AND (last_outcome IS NULL OR last_outcome <> 'failed')",
            "failed" => "latest_version > 0 AND last_outcome = 'failed'",
            "never" => "latest_version = 0",
            "multi" => "latest_version > 1",
            _ => null
        };
        if (statusCondition is not null)
        {
            conditions.Add(statusCondition);
        }

        var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);

        await using var connection = await _db.OpenAsync();
        var total = await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM resources {where}",
            new { q });
        var items = await connection.QueryAsync<ResourceListItemRow>(
            $"""
            SELECT id, resource_key, parent_id, latest_version, last_outcome, last_http_status, last_checked_at, last_success_at, last_error
            FROM resources {where} ORDER BY resource_key LIMIT @limit OFFSET @offset
            """,
            new { q, limit, offset });
        return new PagedResult<ResourceListItemRow>(total, items.AsList());
    }

    public async Task<ResourceRow?> GetAsync(long id)
    {
        await using var connection = await _db.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<ResourceRow>(
            "SELECT * FROM resources WHERE id = @id",
            new { id });
    }

    public async Task<IReadOnlyList<VersionRow>> GetVersionsAsync(long id)
    {
        await using var connection = await _db.OpenAsync();
        var rows = await connection.QueryAsync<VersionRow>(
            "SELECT * FROM versions WHERE resource_id = @id ORDER BY version DESC",
            new { id });
        return rows.AsList();
    }

    public async Task<IReadOnlyList<MemberRow>> GetMembersAsync(long id)
    {
        await using var connection = await _db.OpenAsync();
        var rows = await connection.QueryAsync<MemberRow>(
            "SELECT id, resource_key, latest_version, last_outcome FROM resources WHERE parent_id=@id ORDER BY resource_key",
            new { id });
        return rows.AsList();
    }

    public async Task<IReadOnlyList<FailureRow>> GetRecentFailuresAsync(long id, int limit)
    {
        await using var connection = await _db.OpenAsync();
        var rows = await connection.QueryAsync<FailureRow>(
            "SELECT * FROM failures WHERE resource_id = @id ORDER BY id DESC LIMIT @limit",
            new { id, limit });
        return rows.AsList();
    }
}
