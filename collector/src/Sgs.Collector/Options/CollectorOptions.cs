namespace Sgs.Collector.Options;

public sealed class CollectorOptions
{
    public const string SectionName = "Collector";

    public string DataDir { get; set; } = "App_Data";
    public string EntryUrl { get; set; } = "https://web.sanguosha.com/220/h5_2/index_210000.php";
    public int MaxConcurrency { get; set; } = 16;
    public int TimeoutSeconds { get; set; } = 60;
    public int RetryCount { get; set; } = 2;
    public int MaxResponseMegabytes { get; set; } = 95;
    public int IntervalMinutes { get; set; } = 360;
    public int FailureRetentionDays { get; set; } = 30;
    public int MaxItems { get; set; } = 0;
}
