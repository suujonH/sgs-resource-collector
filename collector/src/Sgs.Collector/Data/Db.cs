using System.Data;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Sgs.Collector.Common;

namespace Sgs.Collector.Data;

public sealed class Db
{
    private static readonly JsonSerializerOptions HeadersJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    static Db()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Db(string databasePath)
    {
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = true,
            DefaultTimeout = 30,
            ForeignKeys = true
        }.ToString();
    }

    // 只用于读；写入一律走 WriteAsync
    public async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("PRAGMA synchronous = NORMAL;");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // 本进程是唯一写入者：进程内串行写入，可以完全避免 SQLite 忙等待（忙等待会阻塞线程池线程）。
    // 不传取消令牌：写入不可中途取消。
    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> work)
    {
        await _writeGate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync();
            await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            var result = await work(connection, transaction);
            await transaction.CommitAsync();
            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task WriteAsync(Func<SqliteConnection, SqliteTransaction, Task> work)
    {
        return WriteAsync<object?>(async (connection, transaction) =>
        {
            await work(connection, transaction);
            return null;
        });
    }

    public async Task InitializeAsync()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync("PRAGMA journal_mode = WAL;");
        await connection.ExecuteAsync(Schema.Sql);

        var ruleCount = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM header_rules");
        if (ruleCount == 0)
        {
            await connection.ExecuteAsync(
                "INSERT INTO header_rules (pattern, headers_json, enabled, note, updated_at) VALUES (@pattern, @headersJson, 1, @note, @now)",
                new { pattern = "*", headersJson = BuildDefaultHeadersJson(), note = "默认浏览器请求头", now = Clock.NowIso() });
        }

        await connection.ExecuteAsync("PRAGMA user_version = 1;");
    }

    private static string BuildDefaultHeadersJson()
    {
        HeaderEntry[] headers =
        [
            new("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36"),
            new("Accept-Language", "zh-CN,zh;q=0.9"),
            new("sec-ch-ua", "\"Google Chrome\";v=\"149\", \"Chromium\";v=\"149\", \"Not)A;Brand\";v=\"24\""),
            new("sec-ch-ua-mobile", "?0"),
            new("sec-ch-ua-platform", "\"Windows\"")
        ];
        return JsonSerializer.Serialize(headers, HeadersJsonOptions);
    }

    private sealed record HeaderEntry(string Name, string Value);
}
