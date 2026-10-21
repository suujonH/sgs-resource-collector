using System.Net;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;
using Sgs.Collector.Api;
using Sgs.Collector.Common;
using Sgs.Collector.Crawl;
using Sgs.Collector.Data;
using Sgs.Collector.Decode;
using Sgs.Collector.Discovery;
using Sgs.Collector.Hosting;
using Sgs.Collector.Http;
using Sgs.Collector.Options;
using Sgs.Collector.Storage;
using Sgs.Collector.Sync;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddOptions<CollectorOptions>().Bind(builder.Configuration.GetSection(CollectorOptions.SectionName)).ValidateOnStart();
builder.Services.AddOptions<WorkerOptions>().Bind(builder.Configuration.GetSection(WorkerOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<CollectorOptions>, CollectorOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<WorkerOptions>, WorkerOptionsValidator>();

builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<CollectorOptions>>().Value;
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var dataDir = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.DataDir));
    return new Db(Path.Combine(dataDir, "collector.db"));
});
builder.Services.AddSingleton<ResourceRepository>();
builder.Services.AddSingleton<BlobRepository>();
builder.Services.AddSingleton<RunRepository>();
builder.Services.AddSingleton<HeaderRuleRepository>();
builder.Services.AddSingleton<SyncRepository>();

builder.Services.AddSingleton(sp => new DiscoveryRules(sp.GetRequiredService<IOptions<CollectorOptions>>().Value.EntryUrl));
builder.Services.AddSingleton(sp => new StaticResourceParser(sp.GetRequiredService<IOptions<CollectorOptions>>().Value.EntryUrl));
builder.Services.AddSingleton<DiscoveryEngine>();
builder.Services.AddSingleton<PackageDecoder>();
builder.Services.AddSingleton<HeaderRuleProvider>();

builder.Services.AddSingleton<IResourceFetcher>(sp =>
{
    var options = sp.GetRequiredService<IOptions<CollectorOptions>>();
    // 重定向由 ResourceFetcher 自行处理（只跟随同主机）；UseCookies = false 让规则里手写的 Cookie 头生效
    var handler = new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxConnectionsPerServer = options.Value.MaxConcurrency,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };
    return new ResourceFetcher(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, options);
});

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;
    var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
    };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.SyncToken);
    return new WorkerClient(http);
});
builder.Services.AddSingleton<IBlobStore>(sp => sp.GetRequiredService<WorkerClient>());
builder.Services.AddSingleton<ICatalogClient>(sp => sp.GetRequiredService<WorkerClient>());

builder.Services.AddSingleton<BlobService>();
builder.Services.AddSingleton<ResourceProcessor>();
builder.Services.AddSingleton<CrawlRunner>();
builder.Services.AddSingleton<CatalogSync>();
builder.Services.AddSingleton<CatalogRestore>();
builder.Services.AddSingleton<RunCoordinator>();
builder.Services.AddHostedService<SchedulerService>();

var app = builder.Build();

// 配置非法时在这里抛出，异常消息里有中文原因
_ = app.Services.GetRequiredService<IOptions<CollectorOptions>>().Value;
_ = app.Services.GetRequiredService<IOptions<WorkerOptions>>().Value;

await app.Services.GetRequiredService<Db>().InitializeAsync();
await app.Services.GetRequiredService<RunRepository>().RecoverStaleAsync(Clock.NowIso());
await app.Services.GetRequiredService<HeaderRuleProvider>().ReloadAsync();

app.UseDefaultFiles();
app.UseStaticFiles();
ApiGuard.Use(app);
app.MapStatusApi();
app.MapRunsApi();
app.MapResourcesApi();
app.MapHeaderRulesApi();
app.MapSyncApi();

app.Run();
