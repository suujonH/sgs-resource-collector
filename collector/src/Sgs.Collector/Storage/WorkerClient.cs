using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

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

public sealed class WorkerClient : IBlobStore, ICatalogClient
{
    private const int MaxAttempts = 3;
    private const int ErrorBodyLimit = 500;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Func<int, TimeSpan> _retryDelay;

    // retryDelay 的参数是第几次重试（1 起）
    public WorkerClient(HttpClient http, Func<int, TimeSpan>? retryDelay = null)
    {
        _http = http;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
    }

    public async Task PutAsync(string hash, byte[] content, string contentType, CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Put, "internal/blobs/" + hash);
            var body = new ByteArrayContent(content);
            body.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            request.Content = body;
            return request;
        }, ct);
    }

    public async Task<CatalogSyncResult> SyncAsync(IReadOnlyList<CatalogVersionDto> rows, CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "internal/sync")
            {
                Content = JsonContent.Create(new { versions = rows }, options: JsonOptions)
            }, ct);
        return await ReadJsonAsync<CatalogSyncResult>(response, ct);
    }

    public async Task<CatalogStats> GetStatsAsync(CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, "internal/stats"), ct);
        return await ReadJsonAsync<CatalogStats>(response, ct);
    }

    public async Task<CatalogExportPage> ExportAsync(string afterKey, long afterVersion, int limit, CancellationToken ct)
    {
        var path = "internal/export?afterKey=" + Uri.EscapeDataString(afterKey)
            + "&afterVersion=" + afterVersion.ToString(CultureInfo.InvariantCulture)
            + "&limit=" + limit.ToString(CultureInfo.InvariantCulture);
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
        return await ReadJsonAsync<CatalogExportPage>(response, ct);
    }

    private static TimeSpan DefaultRetryDelay(int retry) => retry == 1 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5);

    private static bool IsRetryable(int status) => status is 408 or 429 or >= 500;

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return result ?? throw new HttpRequestException("Worker 返回了空的 JSON 响应");
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var last = attempt == MaxAttempts;
            HttpResponseMessage? response = null;
            try
            {
                using var request = factory();
                response = await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException) when (!last)
            {
            }
            catch (TaskCanceledException) when (!last && !ct.IsCancellationRequested)
            {
            }

            if (response is not null)
            {
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var status = (int)response.StatusCode;
                if (last || !IsRetryable(status))
                {
                    var method = response.RequestMessage?.Method.Method ?? "?";
                    var path = response.RequestMessage?.RequestUri?.PathAndQuery ?? "?";
                    var statusCode = response.StatusCode;
                    var text = await response.Content.ReadAsStringAsync(ct);
                    response.Dispose();
                    if (text.Length > ErrorBodyLimit)
                    {
                        text = text[..ErrorBodyLimit];
                    }

                    throw new HttpRequestException($"Worker {method} {path} 返回 {status}: {text}", null, statusCode);
                }

                response.Dispose();
            }

            await Task.Delay(_retryDelay(attempt), ct);
        }
    }
}
