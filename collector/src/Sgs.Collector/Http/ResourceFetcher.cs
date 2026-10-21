using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Sgs.Collector.Options;

namespace Sgs.Collector.Http;

public interface IResourceFetcher
{
    Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken ct);
}

public sealed record FetchRequest(Uri Uri, string Key, Uri? Referer, string? IfNoneMatch, string? IfModifiedSince, HeaderRuleSet Rules);

public enum FetchKind
{
    Ok,
    NotModified,
    HttpError,
    NetworkError
}

// Body、ContentType 只在 Ok 时有值；NetworkError 没有收到响应，StatusCode、ETag、LastModified、FinalUri 都是 null
public sealed record FetchResult(
    FetchKind Kind,
    int? StatusCode,
    byte[]? Body,
    string? ETag,
    string? LastModified,
    string? ContentType,
    Uri? FinalUri,
    string? Error);

public sealed class ResourceFetcher : IResourceFetcher
{
    private const int ReadChunkSize = 81920;
    private const int MaxRedirects = 5;

    private readonly HttpClient _http;
    private readonly CollectorOptions _options;
    private readonly Func<int, TimeSpan> _retryDelay;

    public ResourceFetcher(HttpClient http, IOptions<CollectorOptions> options, Func<int, TimeSpan>? retryDelay = null)
    {
        _http = http;
        _options = options.Value;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
    }

    public async Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken ct)
    {
        string? lastError = null;

        for (var attempt = 0; attempt <= _options.RetryCount; attempt++)
        {
            // 已取消就不再发请求：用户终止直接抛出，不会被当作超时或网络错误重试
            ct.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                await Task.Delay(_retryDelay(attempt), ct);
            }

            // 超时令牌同时管住发送请求和读取响应体
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            try
            {
                var result = await SendFollowingRedirectsAsync(request, timeout.Token);

                if (result.Kind == FetchKind.HttpError && IsRetryable(result.StatusCode) && attempt < _options.RetryCount)
                {
                    lastError = result.Error;
                    continue;
                }

                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"超时（{_options.TimeoutSeconds}s）";
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }
            catch (IOException ex)
            {
                lastError = ex.Message;
            }
        }

        return new FetchResult(FetchKind.NetworkError, null, null, null, null, null, null, lastError);
    }

    private HttpRequestMessage BuildMessage(FetchRequest request, Uri uri)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, uri);
        HeaderApplier.Apply(message, request.Referer, request.Rules, request.Key);
        if (!string.IsNullOrEmpty(request.IfNoneMatch))
        {
            message.Headers.TryAddWithoutValidation("If-None-Match", request.IfNoneMatch);
        }

        if (!string.IsNullOrEmpty(request.IfModifiedSince))
        {
            message.Headers.TryAddWithoutValidation("If-Modified-Since", request.IfModifiedSince);
        }

        return message;
    }

    // 生产环境的 HttpClient 必须设置 AllowAutoRedirect = false（DI 注册时配置），重定向由这里处理：
    // 只允许跟随同主机的跳转，每一跳都按原请求重新应用请求头，避免把 Cookie 等规则头发给其他主机
    private async Task<FetchResult> SendFollowingRedirectsAsync(FetchRequest request, CancellationToken token)
    {
        var current = request.Uri;
        for (var hop = 0; ; hop++)
        {
            using var message = BuildMessage(request, current);
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
            {
                return await ReadResponseAsync(request, response, current, token);
            }

            var target = new UriBuilder(location.IsAbsoluteUri ? location : new Uri(current, location)) { Fragment = string.Empty }.Uri;
            if (!string.Equals(target.Host, request.Uri.Host, StringComparison.OrdinalIgnoreCase)
                || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            {
                return new FetchResult(FetchKind.HttpError, status, null, null, null, null, current, "被重定向到其他主机：" + target.AbsoluteUri);
            }

            // 不允许从 https 降级到 http，否则规则头会以明文发出
            if (current.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp)
            {
                return new FetchResult(FetchKind.HttpError, status, null, null, null, null, current, "被重定向到不安全的地址：" + target.AbsoluteUri);
            }

            if (hop >= MaxRedirects)
            {
                return new FetchResult(FetchKind.HttpError, status, null, null, null, null, current, "重定向次数过多");
            }

            current = target;
        }
    }

    internal static TimeSpan DefaultRetryDelay(int attempt) => TimeSpan.FromSeconds(attempt * attempt);

    // 服务器是否回了和我们手上相同的校验值（即使它忽略了条件请求、回的是 200）
    internal static bool SameValidator(FetchRequest request, string? etag, string? lastModified)
    {
        if (!string.IsNullOrEmpty(request.IfNoneMatch) && !string.IsNullOrEmpty(etag))
        {
            return string.Equals(StripWeak(request.IfNoneMatch), StripWeak(etag), StringComparison.Ordinal);
        }

        if (string.IsNullOrEmpty(request.IfNoneMatch)
            && !string.IsNullOrEmpty(request.IfModifiedSince)
            && string.IsNullOrEmpty(etag)
            && !string.IsNullOrEmpty(lastModified))
        {
            return string.Equals(request.IfModifiedSince, lastModified, StringComparison.Ordinal);
        }

        return false;
    }

    private static string StripWeak(string etag) => etag.StartsWith("W/", StringComparison.Ordinal) ? etag[2..] : etag;

    private static bool IsRetryable(int? status) => status is 408 or 429 or >= 500;

    // 取原始文本：保留 W/ 前缀，也不会把 Last-Modified 改写成别的日期格式
    private static string? FirstRawValue(HttpHeaders headers, string name)
    {
        return headers.NonValidated.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }

    private async Task<FetchResult> ReadResponseAsync(FetchRequest request, HttpResponseMessage response, Uri requestedUri, CancellationToken token)
    {
        var status = (int)response.StatusCode;
        var etag = FirstRawValue(response.Headers, "ETag");
        var lastModified = FirstRawValue(response.Content.Headers, "Last-Modified");
        var finalUri = response.RequestMessage?.RequestUri ?? requestedUri;

        FetchResult Result(FetchKind kind, byte[]? body = null, string? contentType = null, string? error = null) =>
            new(kind, status, body, etag, lastModified, contentType, finalUri, error);

        FetchResult TooLarge() => Result(FetchKind.HttpError, error: $"响应体超过上限 {_options.MaxResponseMegabytes}MB");

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return Result(FetchKind.NotModified);
        }

        if (!response.IsSuccessStatusCode)
        {
            return Result(FetchKind.HttpError, error: $"HTTP {status}");
        }

        // 不读 Body：服务器忽略条件请求时也不下载没变的内容
        if (SameValidator(request, etag, lastModified))
        {
            return Result(FetchKind.NotModified);
        }

        var limit = _options.MaxResponseMegabytes * 1024L * 1024L;
        if (response.Content.Headers.ContentLength > limit)
        {
            return TooLarge();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        byte[]? body;
        try
        {
            body = await ReadLimitedAsync(stream, limit, token);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            // 自动解压遇到损坏的压缩数据：gzip/deflate 抛 InvalidDataException，brotli 抛 InvalidOperationException，
            // 都不属于 IOException。转成 IOException，和其它读取中断一样重试
            throw new IOException($"响应体解压失败：{ex.Message}", ex);
        }

        if (body is null)
        {
            return TooLarge();
        }

        return Result(FetchKind.Ok, body, response.Content.Headers.ContentType?.ToString());
    }

    // 没有 Content-Length（分块传输、自动解压之后）时只能边读边检查；超过上限返回 null
    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, long limit, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkSize];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
