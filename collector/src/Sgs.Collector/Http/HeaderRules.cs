using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sgs.Collector.Data;

namespace Sgs.Collector.Http;

public sealed record HeaderEntry(string Name, string Value);

public sealed record PreviewHeader(string Name, string Value, string Source);

public sealed class CompiledHeaderRule
{
    internal CompiledHeaderRule(long id, string pattern, bool isExact, int literalLength, Regex? regex, IReadOnlyList<HeaderEntry> headers)
    {
        Id = id;
        Pattern = pattern;
        IsExact = isExact;
        LiteralLength = literalLength;
        Regex = regex;
        Headers = headers;
    }

    public long Id { get; }

    public string Pattern { get; }

    public bool IsExact { get; }

    // pattern 中非 * 非 ? 的字符个数，越大表示规则越具体
    public int LiteralLength { get; }

    // 精确规则为 null
    public Regex? Regex { get; }

    public IReadOnlyList<HeaderEntry> Headers { get; }
}

internal static class HeaderNames
{
    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Range", "Accept-Encoding", "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Expect"
    };

    private static readonly string[] ForbiddenPrefixes = ["Content-", "If-", "Proxy-"];

    // 这些头会破坏条件请求、自动解压或连接管理，规则不允许设置
    public static bool IsForbidden(string name)
    {
        if (ForbiddenNames.Contains(name))
        {
            return true;
        }

        foreach (var prefix in ForbiddenPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class HeaderRuleSet
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyList<CompiledHeaderRule> _wildcardRules;
    private readonly IReadOnlyList<CompiledHeaderRule> _exactRules;

    private HeaderRuleSet(IReadOnlyList<CompiledHeaderRule> wildcardRules, IReadOnlyList<CompiledHeaderRule> exactRules)
    {
        _wildcardRules = wildcardRules;
        _exactRules = exactRules;
    }

    public static HeaderRuleSet Empty { get; } = new([], []);

    public static HeaderRuleSet Build(IEnumerable<HeaderRuleRow> rows, ILogger? logger = null)
    {
        List<CompiledHeaderRule> wildcardRules = [];
        List<CompiledHeaderRule> exactRules = [];

        foreach (var row in rows)
        {
            if (row.Enabled != 1)
            {
                continue;
            }

            var headers = ParseHeaders(row, logger);
            if (headers is null)
            {
                continue;
            }

            var literalLength = row.Pattern.Count(c => c != '*' && c != '?');
            if (!row.Pattern.Contains('*') && !row.Pattern.Contains('?'))
            {
                exactRules.Add(new CompiledHeaderRule(row.Id, row.Pattern, true, literalLength, null, headers));
            }
            else
            {
                wildcardRules.Add(new CompiledHeaderRule(row.Id, row.Pattern, false, literalLength, CompileWildcard(row.Pattern), headers));
            }
        }

        return new HeaderRuleSet(
            wildcardRules.OrderBy(rule => rule.LiteralLength).ThenBy(rule => rule.Id).ToList(),
            exactRules.OrderBy(rule => rule.Id).ToList());
    }

    // 按应用顺序返回：先是命中的通配规则（越具体越靠后），再是精确规则；后应用的覆盖先应用的
    public IReadOnlyList<CompiledHeaderRule> Match(string key)
    {
        List<CompiledHeaderRule> matched = [];

        foreach (var rule in _wildcardRules)
        {
            if (rule.Regex is { } regex && regex.IsMatch(key))
            {
                matched.Add(rule);
            }
        }

        foreach (var rule in _exactRules)
        {
            if (string.Equals(rule.Pattern, key, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(rule);
            }
        }

        return matched;
    }

    private static Regex CompileWildcard(string pattern)
    {
        var expression = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking);
    }

    // 整个 headers_json 无法解析时返回 null（调用方跳过该规则）；坏掉的单个条目只丢弃它自己
    private static List<HeaderEntry>? ParseHeaders(HeaderRuleRow row, ILogger? logger)
    {
        List<HeaderEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<HeaderEntry>>(row.HeadersJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "请求头规则 {RuleId}（{Pattern}）的 headers_json 无法解析，已跳过该规则", row.Id, row.Pattern);
            return null;
        }

        if (entries is null)
        {
            logger?.LogWarning("请求头规则 {RuleId}（{Pattern}）的 headers_json 不是请求头数组，已跳过该规则", row.Id, row.Pattern);
            return null;
        }

        List<HeaderEntry> headers = [];
        foreach (var entry in entries)
        {
            // 反序列化不检查空值：缺少字段或值为 null 时得到 null 属性，数组里的 null 则是 null 元素
            if (entry is null || string.IsNullOrWhiteSpace(entry.Name) || entry.Value is null)
            {
                continue;
            }

            if (HeaderNames.IsForbidden(entry.Name))
            {
                continue;
            }

            // 值里的换行和 NUL 会造成请求头注入
            if (entry.Value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
            {
                continue;
            }

            headers.Add(entry);
        }

        return headers;
    }
}

public sealed class HeaderRuleProvider
{
    private readonly HeaderRuleRepository _repository;
    private readonly ILogger<HeaderRuleProvider> _logger;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private volatile HeaderRuleSet _current = HeaderRuleSet.Empty;

    public HeaderRuleProvider(HeaderRuleRepository repository, ILogger<HeaderRuleProvider> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public HeaderRuleSet Current => _current;

    // 串行重载：并发的两次重载如果交错，先读到旧数据的那次可能最后写入，把新规则覆盖掉
    public async Task<HeaderRuleSet> ReloadAsync()
    {
        await _reloadGate.WaitAsync();
        try
        {
            var rows = await _repository.ListAsync();
            var rules = HeaderRuleSet.Build(rows, _logger);
            _current = rules;
            return rules;
        }
        finally
        {
            _reloadGate.Release();
        }
    }
}

internal static class HeaderApplier
{
    public const string DefaultSource = "default";

    private const string RuleSourcePrefix = "rule:";

    private static readonly ConcurrentDictionary<string, bool> NameVerdicts = new(StringComparer.OrdinalIgnoreCase);

    public static void Apply(HttpRequestMessage request, Uri? referer, HeaderRuleSet rules, string key)
    {
        var uri = request.RequestUri ?? throw new ArgumentException("请求没有设置 RequestUri", nameof(request));

        foreach (var header in Resolve(uri, referer, rules, key))
        {
            Set(request, header.Name, header.Value);
        }
    }

    public static IReadOnlyList<PreviewHeader> Preview(Uri uri, Uri? referer, HeaderRuleSet rules, string key)
    {
        return Resolve(uri, referer, rules, key);
    }

    // 最终请求头列表：Apply 与 Preview 共用，保证预览和实际发出的一致
    private static List<PreviewHeader> Resolve(Uri uri, Uri? referer, HeaderRuleSet rules, string key)
    {
        List<PreviewHeader> headers = [];
        foreach (var entry in BrowserHeaders.Compute(uri, referer))
        {
            headers.Add(new PreviewHeader(entry.Name, entry.Value, DefaultSource));
        }

        foreach (var rule in rules.Match(key))
        {
            var source = RuleSourcePrefix + rule.Id.ToString(CultureInfo.InvariantCulture);
            foreach (var entry in rule.Headers)
            {
                if (HeaderNames.IsForbidden(entry.Name) || !IsAcceptedName(entry.Name))
                {
                    continue;
                }

                headers.RemoveAll(header => string.Equals(header.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(entry.Value))
                {
                    headers.Add(new PreviewHeader(entry.Name, entry.Value, source));
                }
            }
        }

        return headers;
    }

    // 用空请求试探头名：非法名称或 .NET 归为内容头的名称会被拒绝，Preview 与 Apply 都据此跳过
    private static bool IsAcceptedName(string name)
    {
        return NameVerdicts.GetOrAdd(name, static n =>
        {
            try
            {
                using var scratch = new HttpRequestMessage();
                return scratch.Headers.TryAddWithoutValidation(n, "x");
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                return false;
            }
        });
    }

    private static void Set(HttpRequestMessage request, string name, string value)
    {
        try
        {
            request.Headers.Remove(name);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            // 格式非法的头名（FormatException）和 .NET 归为内容头的名称（InvalidOperationException）：
            // Remove 会抛异常，TryAddWithoutValidation 也会拒绝，一条写坏的规则不能让每次抓取都失败
            return;
        }

        request.Headers.TryAddWithoutValidation(name, value);
    }
}
