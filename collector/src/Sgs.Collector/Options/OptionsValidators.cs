using Microsoft.Extensions.Options;

namespace Sgs.Collector.Options;

public sealed class CollectorOptionsValidator : IValidateOptions<CollectorOptions>
{
    public ValidateOptionsResult Validate(string? name, CollectorOptions options)
    {
        var errors = new List<string>();

        if (!OptionsChecks.IsHttpUrl(options.EntryUrl))
        {
            errors.Add("Collector.EntryUrl 必须是绝对的 http/https 地址");
        }

        OptionsChecks.CheckRange(errors, "Collector.MaxConcurrency", options.MaxConcurrency, 1, 64);
        OptionsChecks.CheckRange(errors, "Collector.TimeoutSeconds", options.TimeoutSeconds, 5, 600);
        OptionsChecks.CheckRange(errors, "Collector.RetryCount", options.RetryCount, 0, 5);
        OptionsChecks.CheckRange(errors, "Collector.MaxResponseMegabytes", options.MaxResponseMegabytes, 1, 95);
        OptionsChecks.CheckMin(errors, "Collector.IntervalMinutes", options.IntervalMinutes, 0);
        OptionsChecks.CheckMin(errors, "Collector.FailureRetentionDays", options.FailureRetentionDays, 1);
        OptionsChecks.CheckMin(errors, "Collector.MaxItems", options.MaxItems, 0);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class WorkerOptionsValidator : IValidateOptions<WorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkerOptions options)
    {
        var errors = new List<string>();

        if (!OptionsChecks.IsHttpUrl(options.BaseUrl))
        {
            errors.Add("Worker.BaseUrl 必须是绝对的 http/https 地址");
        }

        if (string.IsNullOrWhiteSpace(options.SyncToken))
        {
            errors.Add("Worker.SyncToken 不能为空（请在 appsettings.Local.json 或环境变量 Worker__SyncToken 中配置）");
        }

        OptionsChecks.CheckRange(errors, "Worker.TimeoutSeconds", options.TimeoutSeconds, 10, 3600);
        OptionsChecks.CheckRange(errors, "Worker.SyncBatchSize", options.SyncBatchSize, 1, 1000);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal static class OptionsChecks
{
    public static bool IsHttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    public static void CheckRange(List<string> errors, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{name} 必须在 {min} 到 {max} 之间（当前值 {value}）");
        }
    }

    public static void CheckMin(List<string> errors, string name, int value, int min)
    {
        if (value < min)
        {
            errors.Add($"{name} 不能小于 {min}（当前值 {value}）");
        }
    }
}
