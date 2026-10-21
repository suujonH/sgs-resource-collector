using System.Net;
using System.Text;
using Sgs.Collector.Storage;

namespace Sgs.Collector.Tests;

public sealed class WorkerClientTests
{
    private static WorkerClient NewClient(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://worker.example.com/") }, _ => TimeSpan.Zero);

    [Fact]
    public async Task GetStatsAsync_RetriesAfter503ThenSucceeds()
    {
        var handler = new StubHandler(call => call == 0
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json("{\"resources\":3,\"versions\":9}"));

        var stats = await NewClient(handler).GetStatsAsync(CancellationToken.None);

        Assert.Equal(new CatalogStats(3, 9), stats);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GetStatsAsync_Persistent500_ThrowsWithStatusCodeAfterThreeAttempts()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => NewClient(handler).GetStatsAsync(CancellationToken.None));

        Assert.Contains("500", ex.Message);
        Assert.Contains("boom", ex.Message);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task GetStatsAsync_401IsNotRetried()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => NewClient(handler).GetStatsAsync(CancellationToken.None));

        Assert.Contains("401", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SyncAsync_DeserializesResultAndPostsCamelCaseBody()
    {
        string? body = null;
        var handler = new StubHandler(
            _ => Json("{\"accepted\":2,\"rejected\":[{\"index\":1,\"reason\":\"invalid_hash\"}],\"conflicts\":[{\"key\":\"k\",\"version\":3,\"existingHash\":\"h\"}]}"),
            request => body = request.Content!.ReadAsStringAsync().Result);
        var rows = new[] { new CatalogVersionDto("k", 1, "abc", 5, "text/plain", "2024-01-01T00:00:00.000Z") };

        var result = await NewClient(handler).SyncAsync(rows, CancellationToken.None);

        Assert.Equal(2, result.Accepted);
        Assert.Equal(new SyncRejected(1, "invalid_hash"), Assert.Single(result.Rejected));
        Assert.Equal(new SyncConflict("k", 3, "h"), Assert.Single(result.Conflicts));
        Assert.Contains("\"versions\":[{\"key\":\"k\",\"version\":1", body);
        Assert.Equal("internal/sync", handler.LastPath);
    }

    [Fact]
    public async Task PutAsync_SendsBytesWithContentLengthAndType()
    {
        long? length = null;
        string? type = null;
        var handler = new StubHandler(_ => Json("{\"ok\":true,\"size\":3}"), request =>
        {
            length = request.Content!.Headers.ContentLength;
            type = request.Content.Headers.ContentType!.MediaType;
        });

        await NewClient(handler).PutAsync("0123456789abcdef0123456789abcdef", [1, 2, 3], "image/webp", CancellationToken.None);

        Assert.Equal(3, length);
        Assert.Equal("image/webp", type);
        Assert.Equal("PUT", handler.LastMethod);
        Assert.Equal("internal/blobs/0123456789abcdef0123456789abcdef", handler.LastPath);
    }

    [Fact]
    public async Task ExportAsync_EscapesKeyAndParsesCursor()
    {
        var handler = new StubHandler(_ => Json("{\"rows\":[],\"next\":{\"key\":\"a\",\"version\":2}}"));

        var page = await NewClient(handler).ExportAsync("路径/a b", 4, 100, CancellationToken.None);

        Assert.Equal(new CatalogCursor("a", 2), page.Next);
        Assert.Equal("internal/export?afterKey=" + Uri.EscapeDataString("路径/a b") + "&afterVersion=4&limit=100", handler.LastPath);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _respond;
        private readonly Action<HttpRequestMessage>? _inspect;

        public StubHandler(Func<int, HttpResponseMessage> respond, Action<HttpRequestMessage>? inspect = null)
        {
            _respond = respond;
            _inspect = inspect;
        }

        public int Calls { get; private set; }

        public string? LastPath { get; private set; }

        public string? LastMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri!.PathAndQuery.TrimStart('/');
            LastMethod = request.Method.Method;
            _inspect?.Invoke(request);
            return Task.FromResult(_respond(Calls++));
        }
    }
}
