using Dapper;

namespace Sgs.Collector.Data;

public sealed class RunRepository
{
    private readonly Db _db;

    public RunRepository(Db db)
    {
        _db = db;
    }

    public Task<long> CreateAsync(string trigger, string now)
    {
        return _db.WriteAsync((connection, transaction) => connection.ExecuteScalarAsync<long>(
            "INSERT INTO runs (trigger_kind, status, started_at) VALUES (@trigger, 'running', @now) RETURNING id;",
            new { trigger, now },
            transaction: transaction));
    }

    public Task FinishAsync(long id, string status, string? statsJson, string? error, string now)
    {
        return _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(
            "UPDATE runs SET status=@status, stats_json=@statsJson, error=@error, finished_at=@now WHERE id=@id",
            new { status, statsJson, error, now, id },
            transaction: transaction));
    }

    public Task SetSyncResultAsync(long id, string syncStatus, string? syncError)
    {
        return _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(
            "UPDATE runs SET sync_status=@syncStatus, sync_error=@syncError WHERE id=@id",
            new { syncStatus, syncError, id },
            transaction: transaction));
    }

    public Task<int> RecoverStaleAsync(string now)
    {
        return _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(
            "UPDATE runs SET status='failed', error='收集进程在运行中退出', finished_at=@now WHERE status='running'",
            new { now },
            transaction: transaction));
    }

    public async Task<string?> GetLastCrawlEndAsync()
    {
        await using var connection = await _db.OpenAsync();
        return await connection.ExecuteScalarAsync<string?>(
            "SELECT COALESCE(finished_at, started_at) FROM runs WHERE trigger_kind IN ('manual','schedule') ORDER BY id DESC LIMIT 1");
    }

    public async Task<RunRow?> GetAsync(long id)
    {
        await using var connection = await _db.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<RunRow>(
            "SELECT * FROM runs WHERE id = @id",
            new { id });
    }

    public async Task<PagedResult<RunRow>> ListAsync(int offset, int limit)
    {
        await using var connection = await _db.OpenAsync();
        var total = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM runs");
        var rows = await connection.QueryAsync<RunRow>(
            "SELECT * FROM runs ORDER BY id DESC LIMIT @limit OFFSET @offset",
            new { limit, offset });
        return new PagedResult<RunRow>(total, rows.AsList());
    }

    public Task AddFailureAsync(long runId, long resourceId, long? httpStatus, string error, bool hadHistory, string now)
    {
        return _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(
            """
            INSERT INTO failures (run_id, resource_id, http_status, error, had_history, occurred_at)
            VALUES (@runId, @resourceId, @httpStatus, @error, @hadHistory, @now)
            """,
            new { runId, resourceId, httpStatus, error, hadHistory = hadHistory ? 1L : 0L, now },
            transaction: transaction));
    }

    public Task<int> PruneFailuresAsync(string olderThanIso)
    {
        return _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(
            "DELETE FROM failures WHERE occurred_at < @olderThanIso",
            new { olderThanIso },
            transaction: transaction));
    }

    public async Task<PagedResult<RunVersionItemRow>> GetRunVersionsAsync(long runId, int offset, int limit)
    {
        await using var connection = await _db.OpenAsync();
        var total = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM versions WHERE run_id = @runId",
            new { runId });
        var items = await connection.QueryAsync<RunVersionItemRow>(
            """
            SELECT v.resource_id, r.resource_key, v.version, v.size, v.content_type, v.collected_at
            FROM versions v JOIN resources r ON r.id=v.resource_id WHERE v.run_id=@runId ORDER BY r.resource_key LIMIT @limit OFFSET @offset
            """,
            new { runId, limit, offset });
        return new PagedResult<RunVersionItemRow>(total, items.AsList());
    }

    public async Task<PagedResult<RunFailureItemRow>> GetRunFailuresAsync(long runId, string filter, int offset, int limit)
    {
        var condition = filter switch
        {
            "history" => " AND f.had_history = 1",
            "never" => " AND f.had_history = 0",
            _ => ""
        };

        await using var connection = await _db.OpenAsync();
        var total = await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM failures f WHERE f.run_id = @runId{condition}",
            new { runId });
        var items = await connection.QueryAsync<RunFailureItemRow>(
            $"""
            SELECT f.resource_id, r.resource_key, f.http_status, f.error, f.had_history, f.occurred_at
            FROM failures f JOIN resources r ON r.id=f.resource_id WHERE f.run_id=@runId{condition}
            ORDER BY r.resource_key, f.id LIMIT @limit OFFSET @offset
            """,
            new { runId, limit, offset });
        return new PagedResult<RunFailureItemRow>(total, items.AsList());
    }
}
