using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sgs.Collector.Common;
using Sgs.Collector.Data;
using Sgs.Collector.Discovery;
using Sgs.Collector.Http;

namespace Sgs.Collector.Api;

internal static partial class HeaderRulesApi
{
    private const int MaxPatternLength = 500;
    private const int MaxHeaders = 50;
    private const int MaxValueLength = 4000;
    private const int MaxNoteLength = 500;
    private const string SchemeSeparator = "://";
    private const string DefaultScheme = "https://";

    private static readonly JsonSerializerOptions HeadersJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IEndpointRouteBuilder MapHeaderRulesApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/header-rules", ListAsync);
        app.MapGet("/api/header-rules/preview", Preview);
        app.MapPost("/api/header-rules", CreateAsync);
        app.MapPut("/api/header-rules/{id:long}", UpdateAsync);
        app.MapDelete("/api/header-rules/{id:long}", DeleteAsync);
        return app;
    }

    [GeneratedRegex(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$")]
    private static partial Regex HeaderNameRegex();

    private static async Task<IResult> ListAsync(HeaderRuleRepository repository)
    {
        var rows = await repository.ListAsync();
        return Results.Json(rows.Select(ToDto).ToList());
    }

    private static async Task<IResult> CreateAsync(HeaderRuleRepository repository, HeaderRuleProvider provider, HeaderRuleRequest request)
    {
        var error = Validate(request, out var rule);
        if (error is not null)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, error);
        }

        var id = await repository.InsertAsync(rule.Pattern, rule.HeadersJson, rule.Enabled, rule.Note, Clock.NowIso());
        await provider.ReloadAsync();
        var row = await repository.GetAsync(id);
        return row is null
            ? ApiResults.Error(StatusCodes.Status404NotFound, "请求头规则不存在")
            : Results.Json(ToDto(row), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(HeaderRuleRepository repository, HeaderRuleProvider provider, long id, HeaderRuleRequest request)
    {
        var error = Validate(request, out var rule);
        if (error is not null)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, error);
        }

        if (!await repository.UpdateAsync(id, rule.Pattern, rule.HeadersJson, rule.Enabled, rule.Note, Clock.NowIso()))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "请求头规则不存在");
        }

        await provider.ReloadAsync();
        var row = await repository.GetAsync(id);
        return row is null
            ? ApiResults.Error(StatusCodes.Status404NotFound, "请求头规则不存在")
            : Results.Json(ToDto(row));
    }

    private static async Task<IResult> DeleteAsync(HeaderRuleRepository repository, HeaderRuleProvider provider, long id)
    {
        if (!await repository.DeleteAsync(id))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "请求头规则不存在");
        }

        await provider.ReloadAsync();
        return Results.NoContent();
    }

    private static IResult Preview(HeaderRuleProvider provider, DiscoveryRules rules, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "缺少 url 参数");
        }

        var text = url.Trim();
        if (!text.Contains(SchemeSeparator, StringComparison.Ordinal))
        {
            text = DefaultScheme + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "url 不是合法的绝对地址");
        }

        var uri = ResourceKeys.StripFragment(parsed);
        var key = ResourceKeys.FromUri(uri);
        var current = provider.Current;
        // 与真实请求一致：入口页没有 Referer，其余资源以入口页为 Referer
        var referer = string.Equals(key, ResourceKeys.FromUri(rules.EntryUri), StringComparison.Ordinal) ? null : rules.EntryUri;

        var matched = current.Match(key).Select(rule => new MatchedRuleDto(rule.Id, rule.Pattern)).ToList();
        var headers = HeaderApplier.Preview(uri, referer, current, key);
        return Results.Json(new HeaderPreviewDto(key, rules.InScope(uri), matched, headers));
    }

    private static HeaderRuleDto ToDto(HeaderRuleRow row)
    {
        var isExact = !row.Pattern.Contains('*') && !row.Pattern.Contains('?');
        return new HeaderRuleDto(row.Id, row.Pattern, isExact, ParseHeaders(row.HeadersJson), row.Enabled != 0, row.Note, row.UpdatedAt);
    }

    private static IReadOnlyList<HeaderEntry> ParseHeaders(string json)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<HeaderEntry?>>(json, HeadersJsonOptions);
            return entries is null
                ? []
                : entries.Where(e => e is { Name: not null, Value: not null }).Select(e => e!).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // 返回 null 表示通过；否则返回中文错误说明
    private static string? Validate(HeaderRuleRequest request, out ValidatedRule rule)
    {
        rule = new ValidatedRule("", "", true, null);

        var pattern = request.Pattern?.Trim() ?? "";
        if (pattern.Length == 0)
        {
            return "pattern 不能为空";
        }

        if (pattern.Length > MaxPatternLength)
        {
            return $"pattern 不能超过 {MaxPatternLength} 个字符";
        }

        var headers = request.Headers;
        if (headers is null || headers.Count < 1 || headers.Count > MaxHeaders)
        {
            return $"headers 必须有 1 到 {MaxHeaders} 项";
        }

        List<HeaderEntry> entries = [];
        foreach (var header in headers)
        {
            var name = header?.Name;
            if (string.IsNullOrEmpty(name) || !HeaderNameRegex().IsMatch(name))
            {
                return $"请求头名称无效：{name}";
            }

            if (HeaderNames.IsForbidden(name))
            {
                return $"不允许设置的请求头：{name}";
            }

            if (!CanAddToRequest(name))
            {
                return $"不支持的请求头：{name}";
            }

            var value = header!.Value;
            if (value is null)
            {
                return $"请求头 {name} 缺少 value";
            }

            if (value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
            {
                return $"请求头 {name} 的值不能包含换行或 NUL 字符";
            }

            if (value.Length > MaxValueLength)
            {
                return $"请求头 {name} 的值不能超过 {MaxValueLength} 个字符";
            }

            entries.Add(new HeaderEntry(name, value));
        }

        if (request.Note is { Length: > MaxNoteLength })
        {
            return $"note 不能超过 {MaxNoteLength} 个字符";
        }

        var note = string.IsNullOrEmpty(request.Note) ? null : request.Note;
        rule = new ValidatedRule(pattern, JsonSerializer.Serialize(entries, HeadersJsonOptions), request.Enabled ?? true, note);
        return null;
    }

    // R27：.NET 把 Allow、Expires、Last-Modified 等算作内容头，加不到请求上；HeaderApplier 会静默忽略它们
    private static bool CanAddToRequest(string name)
    {
        try
        {
            using var scratch = new HttpRequestMessage();
            return scratch.Headers.TryAddWithoutValidation(name, "x");
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private sealed record ValidatedRule(string Pattern, string HeadersJson, bool Enabled, string? Note);
}
