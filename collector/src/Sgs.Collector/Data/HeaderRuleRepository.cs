using Dapper;

namespace Sgs.Collector.Data;

public sealed class HeaderRuleRepository
{
    private readonly Db _db;

    public HeaderRuleRepository(Db db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<HeaderRuleRow>> ListAsync()
    {
        await using var connection = await _db.OpenAsync();
        var rows = await connection.QueryAsync<HeaderRuleRow>("SELECT * FROM header_rules ORDER BY id");
        return rows.AsList();
    }

    public async Task<HeaderRuleRow?> GetAsync(long id)
    {
        await using var connection = await _db.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<HeaderRuleRow>(
            "SELECT * FROM header_rules WHERE id = @id",
            new { id });
    }

    public Task<long> InsertAsync(string pattern, string headersJson, bool enabled, string? note, string now)
    {
        return _db.WriteAsync((connection, transaction) => connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO header_rules (pattern, headers_json, enabled, note, updated_at)
            VALUES (@pattern, @headersJson, @enabled, @note, @now) RETURNING id;
            """,
            new { pattern, headersJson, enabled = enabled ? 1L : 0L, note, now },
            transaction: transaction));
    }

    public async Task<bool> UpdateAsync(long id, string pattern, string headersJson, bool enabled, string? note, string now)
    {
        var affected = await _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(
            """
            UPDATE header_rules SET pattern=@pattern, headers_json=@headersJson, enabled=@enabled, note=@note, updated_at=@now
            WHERE id=@id
            """,
            new { pattern, headersJson, enabled = enabled ? 1L : 0L, note, now, id },
            transaction: transaction));
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        var affected = await _db.WriteAsync((connection, transaction) => connection.ExecuteAsync(
            "DELETE FROM header_rules WHERE id = @id",
            new { id },
            transaction: transaction));
        return affected > 0;
    }
}
