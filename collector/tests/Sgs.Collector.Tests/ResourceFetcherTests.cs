using System.Net;
using System.Text.Json;
using Sgs.Collector.Data;
using Sgs.Collector.Http;
using Sgs.Collector.Options;

namespace Sgs.Collector.Tests;

// 用桩 HttpMessageHandler 驱动真实的 HttpClient，重试延迟一律为零；响应体上限固定为 1MB
public sealed class ResourceFetcherTests
{
    private const long OneMegabyte = 1024L * 1024L;
    private const string TargetKey = "web.sanguosha.com/220/h5_2/res/config/a.json";
    private const string ETagV1 = "\"v1\"";
    private const string ETagV2 = "\"v2\"";
    private const string LastModified1 = "Mon, 01 Jan 2024 00:00:00 GMT";
    private const string LastModified2 = "Tue, 02 Jan 2024 00:00:00 GMT";
    private const string TooLargeMessage = "响应体超过上限 1MB";

    private static readonly Uri Target = new("https://web.sanguosha.com/220/h5_2/res/config/a.json?v=1");
    private static readonly Uri EntryPage = new("https://web.sanguosha.com/220/h5_2/index_210000.php");
    private static readonly Uri Redirected = new("https://cdn.sanguosha.com/final/a.json");

    // ---------- 304 与条件请求 ----------

    [Fact]
    public async Task FetchAsync_304_ReturnsNotModifiedWithValidatorsAndWithoutBody()
    {
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.NotModified, etag: ETagV1, lastModified: LastModified1, finalUri: Redirected));

        var result = await Fetcher(handler).FetchAsync(Request(ifNoneMatch: ETagV1), CancellationToken.None);

        Assert.Equal(FetchKind.NotModified, result.Kind);
        Assert.Equal(304, result.StatusCode);
        Assert.Null(result.Body);
        Assert.Equal(ETagV1, result.ETag);
        Assert.Equal(LastModified1, result.LastModified);
        Assert.Equal(Redirected, result.FinalUri);
        Assert.Null(result.ContentType);
        Assert.Null(result.Error);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("\"v1\"", "\"v1\"")]
    [InlineData("W/\"v1\"", "\"v1\"")]
    [InlineData("\"v1\"", "W/\"v1\"")]
    [InlineData("W/\"v1\"", "W/\"v1\"")]
    public async Task FetchAsync_200WithTheSameETag_ReturnsNotModifiedWithoutReadingTheBody(string ifNoneMatch, string etag)
    {
        // 服务器忽略条件请求、直接回 200：ETag 没变就不下载（UnreadableContent 被读取时会抛异常）
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK, etag: etag, lastModified: LastModified1, finalUri: Redirected, content: new UnreadableContent()));

        var result = await Fetcher(handler).FetchAsync(Request(ifNoneMatch: ifNoneMatch), CancellationToken.None);

        Assert.Equal(FetchKind.NotModified, result.Kind);
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.Body);
        Assert.Null(result.ContentType);
        Assert.Equal(etag, result.ETag);
        Assert.Equal(LastModified1, result.LastModified);
        Assert.Equal(Redirected, result.FinalUri);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FetchAsync_200WithADifferentETag_DownloadsTheBody()
    {
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK, body: "new"u8.ToArray(), etag: ETagV2));

        var result = await Fetcher(handler).FetchAsync(Request(ifNoneMatch: ETagV1), CancellationToken.None);

        Assert.Equal(FetchKind.Ok, result.Kind);
        Assert.Equal("new"u8.ToArray(), result.Body);
        Assert.Equal(ETagV2, result.ETag);
    }

    [Fact]
    public async Task FetchAsync_200WithoutETagAndTheSameLastModified_ReturnsNotModifiedWithoutReadingTheBody()
    {
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK, lastModified: LastModified1, content: new UnreadableContent()));

        var result = await Fetcher(handler).FetchAsync(Request(ifModifiedSince: LastModified1), CancellationToken.None);

        Assert.Equal(FetchKind.NotModified, result.Kind);
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.Body);
        Assert.Null(result.ETag);
        Assert.Equal(LastModified1, result.LastModified);
    }

    [Fact]
    public async Task FetchAsync_200WithoutETagAndADifferentLastModified_DownloadsTheBody()
    {
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK, body: "new"u8.ToArray(), lastModified: LastModified2));

        var result = await Fetcher(handler).FetchAsync(Request(ifModifiedSince: LastModified1), CancellationToken.None);

        Assert.Equal(FetchKind.Ok, result.Kind);
        Assert.Equal("new"u8.ToArray(), result.Body);
        Assert.Equal(LastModified2, result.LastModified);
    }

    [Fact]
    public async Task FetchAsync_200WithoutAnyStoredValidator_AlwaysDownloadsTheBody()
    {
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK, body: "all"u8.ToArray(), etag: ETagV1, lastModified: LastModified1));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.Ok, result.Kind);
        Assert.Equal("all"u8.ToArray(), result.Body);
    }

    [Theory]
    [InlineData("\"a\"", null, "\"a\"", null, true)]
    [InlineData("\"a\"", null, "\"b\"", null, false)]
    [InlineData("W/\"a\"", null, "\"a\"", null, true)]
    [InlineData("\"a\"", null, "W/\"a\"", null, true)]
    [InlineData("W/\"a\"", null, "W/\"a\"", null, true)]
    [InlineData("\"a\"", null, "\"A\"", null, false)] // Ordinal 比较
    [InlineData("\"a\"", "LM", "\"a\"", "LM2", true)] // 两边都有 ETag 时只看 ETag
    [InlineData("\"a\"", "LM", "\"b\"", "LM", false)]
    [InlineData("\"a\"", null, null, null, false)] // 服务器没回 ETag
    [InlineData("\"a\"", "LM", null, "LM", false)] // 存有 ETag 时不退回比较 Last-Modified
    [InlineData(null, "LM", null, "LM", true)]
    [InlineData("", "LM", "", "LM", true)] // 空字符串与 null 等价
    [InlineData(null, "LM", null, "LM2", false)]
    [InlineData(null, "LM", "\"b\"", "LM", false)] // 服务器给了 ETag，但我们没存过
    [InlineData(null, "LM", null, null, false)]
    [InlineData(null, null, "\"a\"", "LM", false)]
    [InlineData(null, null, null, null, false)]
    public void SameValidator_FollowsTheDocumentedRules(string? ifNoneMatch, string? ifModifiedSince, string? etag, string? lastModified, bool expected)
    {
        var request = Request(ifNoneMatch: ifNoneMatch, ifModifiedSince: ifModifiedSince);

        Assert.Equal(expected, ResourceFetcher.SameValidator(request, etag, lastModified));
    }

    // ---------- 重试 ----------

    [Fact]
    public async Task FetchAsync_503Then503Then200_ReturnsOkOnTheThirdAttempt()
    {
        var delays = new List<int>();
        var handler = StubHandler.Respond(call => call < 2
            ? Response(HttpStatusCode.ServiceUnavailable)
            : Response(HttpStatusCode.OK, body: "ok"u8.ToArray()));

        var result = await Fetcher(handler, delays: delays).FetchAsync(Request(referer: EntryPage), CancellationToken.None);

        Assert.Equal(FetchKind.Ok, result.Kind);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("ok"u8.ToArray(), result.Body);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(new[] { 1, 2 }, delays); // 重试前才延迟，参数是第几次重试
        // 每次重试都是一个全新的请求，请求头完全一致
        Assert.Equal(
            handler.Requests[0].Headers.OrderBy(h => h.Key, StringComparer.Ordinal),
            handler.Requests[2].Headers.OrderBy(h => h.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task FetchAsync_503OnEveryAttempt_ReturnsHttpErrorWhenTheRetriesAreExhausted()
    {
        var delays = new List<int>();
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.ServiceUnavailable, etag: ETagV1, lastModified: LastModified1, finalUri: Redirected));

        var result = await Fetcher(handler, retryCount: 2, delays: delays).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.HttpError, result.Kind);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("HTTP 503", result.Error);
        Assert.Null(result.Body);
        Assert.Null(result.ContentType);
        Assert.Equal(ETagV1, result.ETag);
        Assert.Equal(LastModified1, result.LastModified);
        Assert.Equal(Redirected, result.FinalUri);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(new[] { 1, 2 }, delays);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    public async Task FetchAsync_RetryableStatus_IsRetried(int status)
    {
        var handler = StubHandler.Respond(call => call == 0
            ? Response((HttpStatusCode)status)
            : Response(HttpStatusCode.OK, body: "ok"u8.ToArray()));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.Ok, result.Kind);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    public async Task FetchAsync_NonRetryableStatus_ReturnsHttpErrorAfterASingleCall(int status)
    {
        var handler = StubHandler.Respond(_ => Response((HttpStatusCode)status));
        var delays = new List<int>();

        var result = await Fetcher(handler, delays: delays).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.HttpError, result.Kind);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal($"HTTP {status}", result.Error);
        Assert.Single(handler.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task FetchAsync_404_KeepsTheResponseMetadataAndLeavesBodyAndContentTypeEmpty()
    {
        var handler = StubHandler.Respond(_ => Response(
            HttpStatusCode.NotFound, body: "not found"u8.ToArray(), etag: ETagV1, lastModified: LastModified1, contentType: "text/html", finalUri: Redirected));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.HttpError, result.Kind);
        Assert.Equal(404, result.StatusCode);
        Assert.Null(result.Body);
        Assert.Null(result.ContentType);
        Assert.Equal(ETagV1, result.ETag);
        Assert.Equal(LastModified1, result.LastModified);
        Assert.Equal(Redirected, result.FinalUri);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FetchAsync_RetryCountZero_NeverRetries()
    {
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.ServiceUnavailable));
        var delays = new List<int>();

        var result = await Fetcher(handler, retryCount: 0, delays: delays).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.HttpError, result.Kind);
        Assert.Equal(503, result.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Empty(delays);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 4)]
    [InlineData(3, 9)]
    [InlineData(5, 25)]
    public void DefaultRetryDelay_GrowsWithTheSquareOfTheAttemptNumber(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ResourceFetcher.DefaultRetryDelay(attempt));
    }

    // ---------- 网络错误与超时 ----------

    [Fact]
    public async Task FetchAsync_HttpRequestExceptionOnEveryAttempt_ReturnsNetworkErrorWithTheLastMessage()
    {
        var handler = StubHandler.Respond(call => throw new HttpRequestException($"连接失败 {call}"));

        var result = await Fetcher(handler, retryCount: 2).FetchAsync(Request(), CancellationToken.None);

        AssertNetworkError(result, "连接失败 2");
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task FetchAsync_NetworkErrorThenSuccess_ReturnsOk()
    {
        var handler = StubHandler.Respond(call => call == 0
            ? throw new HttpRequestException("连接被重置")
            : Response(HttpStatusCode.OK, body: "ok"u8.ToArray()));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.Ok, result.Kind);
        Assert.Null(result.Error);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FetchAsync_IOExceptionWhileReadingTheBody_IsRetriedAndThenReturnsNetworkError()
    {
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK, content: new StreamContent(new FaultingStream())));

        var result = await Fetcher(handler, retryCount: 1).FetchAsync(Request(), CancellationToken.None);

        AssertNetworkError(result, "连接中断");
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("gzip")] // gzip / deflate 解码失败抛 InvalidDataException
    [InlineData("brotli")] // brotli 解码失败抛 InvalidOperationException
    public async Task FetchAsync_CorruptCompressedBody_IsTreatedAsANetworkErrorInsteadOfEscaping(string encoding)
    {
        Exception Corrupt() => encoding == "gzip"
            ? new InvalidDataException("解码失败")
            : new InvalidOperationException("解码失败");
        var recoverable = StubHandler.Respond(call => Response(
            HttpStatusCode.OK,
            content: call == 0 ? new StreamContent(new FaultingStream(Corrupt())) : new ByteArrayContent("ok"u8.ToArray())));

        var recovered = await Fetcher(recoverable, retryCount: 1).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.Ok, recovered.Kind); // 当作可重试的传输错误
        Assert.Equal(2, recoverable.Requests.Count);

        var alwaysCorrupt = StubHandler.Respond(_ => Response(HttpStatusCode.OK, content: new StreamContent(new FaultingStream(Corrupt()))));

        var failed = await Fetcher(alwaysCorrupt, retryCount: 1).FetchAsync(Request(), CancellationToken.None);

        AssertNetworkError(failed, "响应体解压失败：解码失败");
        Assert.Equal(2, alwaysCorrupt.Requests.Count);
    }

    [Fact]
    public async Task FetchAsync_InvalidOperationOutsideTheBodyRead_IsNotSwallowed()
    {
        // 只有读响应体时的解码失败才转成网络错误；其它 InvalidOperationException 是程序错误，必须暴露出来
        var handler = StubHandler.Respond(_ => throw new InvalidOperationException("程序错误"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Fetcher(handler).FetchAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task FetchAsync_TheLastAttemptDecidesTheKindOfTheResult()
    {
        var statusLast = StubHandler.Respond(call => call < 2
            ? throw new HttpRequestException("连接失败")
            : Response(HttpStatusCode.ServiceUnavailable));
        var networkLast = StubHandler.Respond(call => call < 2
            ? Response(HttpStatusCode.ServiceUnavailable)
            : throw new HttpRequestException("连接失败"));

        var first = await Fetcher(statusLast).FetchAsync(Request(), CancellationToken.None);
        var second = await Fetcher(networkLast).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.HttpError, first.Kind);
        Assert.Equal(503, first.StatusCode);
        AssertNetworkError(second, "连接失败");
    }

    [Fact]
    public async Task FetchAsync_TimeoutOnEveryAttempt_ReturnsNetworkErrorAfterTheRetries()
    {
        // 超时设为 0 秒：超时令牌几乎立即取消，测试不必真的等待
        var handler = StubHandler.RespondAsync(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Response(HttpStatusCode.OK);
        });

        var result = await Fetcher(handler, retryCount: 1, timeoutSeconds: 0).FetchAsync(Request(), CancellationToken.None);

        AssertNetworkError(result, "超时（0s）");
        Assert.Equal(2, handler.Requests.Count);
    }

    // ---------- 取消 ----------

    [Fact]
    public async Task FetchAsync_AlreadyCancelledToken_ThrowsBeforeSendingAnything()
    {
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetcher(handler).FetchAsync(Request(), cts.Token));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(0)] // 取消发生在最后一次尝试：没有下一轮循环可以补抛，必须在 catch 处区分用户终止和超时
    [InlineData(2)]
    public async Task FetchAsync_CancelledWhileSending_ThrowsInsteadOfRetryingOrReportingATimeout(int retryCount)
    {
        using var cts = new CancellationTokenSource();
        var handler = StubHandler.RespondAsync(async (_, token) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return Response(HttpStatusCode.OK);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetcher(handler, retryCount: retryCount).FetchAsync(Request(), cts.Token));

        Assert.Single(handler.Requests); // 用户终止不重试
    }

    [Fact]
    public async Task FetchAsync_CancelledWhileWaitingToRetry_Throws()
    {
        using var cts = new CancellationTokenSource();
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.ServiceUnavailable));
        var options = Microsoft.Extensions.Options.Options.Create(new CollectorOptions { RetryCount = 2, MaxResponseMegabytes = 1 });
        var fetcher = new ResourceFetcher(new HttpClient(handler), options, _ =>
        {
            cts.Cancel(); // 第一次失败之后进入重试等待时用户终止
            return TimeSpan.FromSeconds(30);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync(Request(), cts.Token));

        Assert.Single(handler.Requests);
    }

    // ---------- 响应体上限（MaxResponseMegabytes = 1） ----------

    [Fact]
    public async Task FetchAsync_ContentLengthOverTheLimit_IsRejectedBeforeReadingTheBodyAndNotRetried()
    {
        var content = new UnreadableContent();
        content.Headers.ContentLength = OneMegabyte + 1;
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK, etag: ETagV1, lastModified: LastModified1, finalUri: Redirected, content: content));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.HttpError, result.Kind);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(TooLargeMessage, result.Error);
        Assert.Null(result.Body);
        Assert.Null(result.ContentType);
        Assert.Equal(ETagV1, result.ETag);
        Assert.Equal(LastModified1, result.LastModified);
        Assert.Equal(Redirected, result.FinalUri);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FetchAsync_BodyOverTheLimitWithoutContentLength_StopsReadingOnceTheLimitIsExceeded()
    {
        var stream = new GeneratedStream(2 * OneMegabyte);
        var content = new StreamContent(stream);
        Assert.Null(content.Headers.ContentLength); // 不可 Seek 的流：没有 Content-Length，只能边读边检查
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK, etag: ETagV1, content: content));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.HttpError, result.Kind);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(TooLargeMessage, result.Error);
        Assert.Null(result.Body);
        Assert.Null(result.ContentType);
        Assert.Equal(ETagV1, result.ETag);
        Assert.Single(handler.Requests); // 不重试
        Assert.True(stream.TotalRead > OneMegabyte);
        Assert.True(stream.TotalRead < 2 * OneMegabyte, "超过上限后应立即停止读取");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchAsync_BodyOneByteOverTheLimit_ReturnsHttpError(bool withoutContentLength)
    {
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK, content: BodyOfLength(OneMegabyte + 1, withoutContentLength)));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.HttpError, result.Kind);
        Assert.Equal(TooLargeMessage, result.Error);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchAsync_BodyExactlyAtTheLimit_ReturnsOk(bool withoutContentLength)
    {
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK, content: BodyOfLength(OneMegabyte, withoutContentLength)));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.Ok, result.Kind);
        Assert.NotNull(result.Body);
        Assert.Equal(OneMegabyte, result.Body.Length);
    }

    // ---------- 成功结果与重定向 ----------

    [Fact]
    public async Task FetchAsync_200_ReturnsOkWithBodyAndResponseMetadata()
    {
        var handler = StubHandler.Respond(_ => Response(
            HttpStatusCode.OK,
            body: "hello"u8.ToArray(),
            etag: "W/\"v1\"",
            lastModified: LastModified1,
            contentType: "application/json; charset=utf-8",
            finalUri: Target));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.Ok, result.Kind);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("hello"u8.ToArray(), result.Body);
        Assert.Equal("W/\"v1\"", result.ETag); // 原始值，弱校验标记原样保留
        Assert.Equal(LastModified1, result.LastModified);
        Assert.Equal("application/json; charset=utf-8", result.ContentType);
        Assert.Equal(Target, result.FinalUri);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task FetchAsync_ValidatorsAreReturnedAsTheServerSentThem()
    {
        // 不是 RFC 1123 格式的日期也要原样返回，之后原样作为 If-Modified-Since 回传
        const string rfc850 = "Sunday, 06-Nov-94 08:49:37 GMT";
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK, etag: "unquoted", lastModified: rfc850));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal("unquoted", result.ETag);
        Assert.Equal(rfc850, result.LastModified);
    }

    [Fact]
    public async Task FetchAsync_Redirected_ReportsTheFinalUri()
    {
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK, body: "ok"u8.ToArray(), finalUri: Redirected));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.Ok, result.Kind);
        Assert.Equal(Redirected, result.FinalUri);
        Assert.NotEqual(Target, result.FinalUri);
    }

    // ---------- 发出的请求 ----------

    [Fact]
    public async Task FetchAsync_SendsBrowserHeadersRuleHeadersAndConditionalHeaders()
    {
        var rules = HeaderRuleSet.Build(
        [
            Rule(1, "*", HeadersJson(
                ("User-Agent", "UA/1"),
                ("If-None-Match", "\"evil\""), // 规则不能改写条件请求头
                ("Accept-Encoding", "identity"),
                ("Range", "bytes=0-1")))
        ]);
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK));

        await Fetcher(handler).FetchAsync(
            Request(referer: EntryPage, ifNoneMatch: ETagV1, ifModifiedSince: LastModified1, rules: rules),
            CancellationToken.None);

        var seen = Assert.Single(handler.Requests);
        Assert.Equal("GET", seen.Method);
        Assert.Equal(Target, seen.Uri);
        Assert.Equal("UA/1", seen.Headers["User-Agent"]);
        Assert.Equal("*/*", seen.Headers["Accept"]);
        Assert.Equal(EntryPage.AbsoluteUri, seen.Headers["Referer"]);
        Assert.Equal("same-origin", seen.Headers["Sec-Fetch-Site"]);
        Assert.Equal(ETagV1, seen.Headers["If-None-Match"]);
        Assert.Equal(LastModified1, seen.Headers["If-Modified-Since"]);
        Assert.False(seen.Headers.ContainsKey("Accept-Encoding"));
        Assert.False(seen.Headers.ContainsKey("Range"));
    }

    [Fact]
    public async Task FetchAsync_WithoutStoredValidators_SendsNoConditionalHeaders()
    {
        var rules = HeaderRuleSet.Build([Rule(1, "*", HeadersJson(("If-None-Match", "\"evil\""), ("If-Modified-Since", "evil")))]);
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK));

        await Fetcher(handler).FetchAsync(Request(ifNoneMatch: null, ifModifiedSince: "", rules: rules), CancellationToken.None);

        var seen = Assert.Single(handler.Requests);
        Assert.False(seen.Headers.ContainsKey("If-None-Match"));
        Assert.False(seen.Headers.ContainsKey("If-Modified-Since"));
    }

    [Fact]
    public async Task FetchAsync_WithoutReferer_SendsNavigationHeaders()
    {
        var handler = StubHandler.Respond(_ => Response(HttpStatusCode.OK));

        await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        var seen = Assert.Single(handler.Requests);
        Assert.Equal("document", seen.Headers["Sec-Fetch-Dest"]);
        Assert.Equal("none", seen.Headers["Sec-Fetch-Site"]);
        Assert.Equal("1", seen.Headers["Upgrade-Insecure-Requests"]);
        Assert.False(seen.Headers.ContainsKey("Referer"));
    }

    // ---------- 重定向 ----------

    private static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = Response(status);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    [Fact]
    public async Task FetchAsync_SameHostRedirect_IsFollowedWithTheSameHeaders()
    {
        var rules = HeaderRuleSet.Build([Rule(1, "*", HeadersJson(("X-Rule", "yes")))]);
        var handler = StubHandler.Respond(call => call == 0
            ? Redirect("/220/h5_2/res/config/b.json#frag")
            : Response(HttpStatusCode.OK, body: "ok"u8.ToArray()));

        var result = await Fetcher(handler).FetchAsync(Request(ifNoneMatch: ETagV1, rules: rules), CancellationToken.None);

        Assert.Equal(FetchKind.Ok, result.Kind);
        Assert.Equal(new Uri("https://web.sanguosha.com/220/h5_2/res/config/b.json"), result.FinalUri);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("yes", handler.Requests[1].Headers["X-Rule"]);
        Assert.Equal(ETagV1, handler.Requests[1].Headers["If-None-Match"]);
    }

    [Fact]
    public async Task FetchAsync_CrossHostRedirect_ReturnsHttpErrorWithoutContactingTheOtherHost()
    {
        var handler = StubHandler.Respond(_ => Redirect("https://evil.example.com/a.json"));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.HttpError, result.Kind);
        Assert.Equal(302, result.StatusCode);
        Assert.Equal("被重定向到其他主机：https://evil.example.com/a.json", result.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FetchAsync_HttpsToHttpRedirect_IsRejected()
    {
        var handler = StubHandler.Respond(_ => Redirect("http://web.sanguosha.com/220/h5_2/a.json"));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.HttpError, result.Kind);
        Assert.Equal("被重定向到不安全的地址：http://web.sanguosha.com/220/h5_2/a.json", result.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FetchAsync_MoreThanFiveRedirects_ReturnsHttpError()
    {
        var handler = StubHandler.Respond(call => Redirect($"/hop{call}"));

        var result = await Fetcher(handler).FetchAsync(Request(), CancellationToken.None);

        Assert.Equal(FetchKind.HttpError, result.Kind);
        Assert.Equal("重定向次数过多", result.Error);
        Assert.Equal(6, handler.Requests.Count);
    }

    // ---------- 辅助 ----------

    private static FetchRequest Request(
        string? ifNoneMatch = null,
        string? ifModifiedSince = null,
        Uri? referer = null,
        HeaderRuleSet? rules = null)
    {
        return new FetchRequest(Target, TargetKey, referer, ifNoneMatch, ifModifiedSince, rules ?? HeaderRuleSet.Empty);
    }

    private static ResourceFetcher Fetcher(StubHandler handler, int retryCount = 2, int timeoutSeconds = 60, List<int>? delays = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CollectorOptions
        {
            RetryCount = retryCount,
            TimeoutSeconds = timeoutSeconds,
            MaxResponseMegabytes = 1
        });
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new ResourceFetcher(client, options, attempt =>
        {
            delays?.Add(attempt);
            return TimeSpan.Zero;
        });
    }

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        byte[]? body = null,
        string? etag = null,
        string? lastModified = null,
        string? contentType = null,
        Uri? finalUri = null,
        HttpContent? content = null)
    {
        var response = new HttpResponseMessage(status) { Content = content ?? new ByteArrayContent(body ?? []) };
        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
        }

        if (lastModified is not null)
        {
            response.Content.Headers.TryAddWithoutValidation("Last-Modified", lastModified);
        }

        if (contentType is not null)
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        if (finalUri is not null)
        {
            // 真实的 HttpClient 重定向之后 RequestMessage 指向最后一次请求，桩需要自己设置
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri);
        }

        return response;
    }

    // withoutContentLength 为 true 时用不可 Seek 的流，此时没有 Content-Length，上限只能在读取过程中检查
    private static HttpContent BodyOfLength(long length, bool withoutContentLength)
    {
        return withoutContentLength ? new StreamContent(new GeneratedStream(length)) : new ByteArrayContent(new byte[length]);
    }

    private static void AssertNetworkError(FetchResult result, string error)
    {
        Assert.Equal(FetchKind.NetworkError, result.Kind);
        Assert.Equal(error, result.Error);
        Assert.Null(result.StatusCode);
        Assert.Null(result.Body);
        Assert.Null(result.ETag);
        Assert.Null(result.LastModified);
        Assert.Null(result.ContentType);
        Assert.Null(result.FinalUri);
    }

    private static HeaderRuleRow Rule(long id, string pattern, string headersJson) => new()
    {
        Id = id,
        Pattern = pattern,
        HeadersJson = headersJson,
        Enabled = 1,
        UpdatedAt = "2026-01-01T00:00:00.000Z"
    };

    private static string HeadersJson(params (string Name, string Value)[] headers) =>
        JsonSerializer.Serialize(headers.Select(h => new { name = h.Name, value = h.Value }));

    private sealed record SeenRequest(string Method, Uri Uri, Dictionary<string, string> Headers);

    // 每次调用时就拍下请求头的快照：fetcher 用完请求会释放它
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _respond;

        private StubHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public List<SeenRequest> Requests { get; } = [];

        public static StubHandler Respond(Func<int, HttpResponseMessage> respond) =>
            new((call, _) => Task.FromResult(respond(call)));

        public static StubHandler RespondAsync(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) =>
            new(respond);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Requests.Count;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers.NonValidated)
            {
                headers[header.Key] = header.Value.ToString();
            }

            Requests.Add(new SeenRequest(request.Method.Method, request.RequestUri!, headers));
            return _respond(call, cancellationToken);
        }
    }

    // 响应体一读取就抛异常，用来证明“不读 Body”
    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("不应读取响应体");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private abstract class ForwardOnlyStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => ReadCore(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadCore(buffer.Span));

        protected abstract int ReadCore(Span<byte> buffer);
    }

    // 按需生成指定长度的零字节，并记录已经被读走多少
    private sealed class GeneratedStream : ForwardOnlyStream
    {
        private readonly long _length;

        public GeneratedStream(long length)
        {
            _length = length;
        }

        public long TotalRead { get; private set; }

        protected override int ReadCore(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, _length - TotalRead);
            buffer[..count].Clear();
            TotalRead += count;
            return count;
        }
    }

    private sealed class FaultingStream : ForwardOnlyStream
    {
        private readonly Exception _error;

        public FaultingStream(Exception? error = null)
        {
            _error = error ?? new IOException("连接中断");
        }

        protected override int ReadCore(Span<byte> buffer) => throw _error;
    }
}
