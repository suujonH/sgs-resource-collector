namespace Sgs.Collector.Options;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public string BaseUrl { get; set; } = "";
    public string SyncToken { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 300;
    public int SyncBatchSize { get; set; } = 500;
}
