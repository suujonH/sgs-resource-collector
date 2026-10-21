using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Sgs.Collector.Data;
using Sgs.Collector.Http;

namespace Sgs.Collector.Tests;

public sealed class HeaderRulesTests
{
    private const string Key = "web.sanguosha.com/220/h5_2/res/config/a.json";
    private const string DocumentAccept =
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7";

    private static readonly Uri Target = new("https://web.sanguosha.com/220/h5_2/res/config/a.json?v=1");
    private static readonly Uri EntryPage = new("https://web.sanguosha.com/220/h5_2/index_210000.php");

    // ---------- HeaderNames ----------

    [Theory]
    [InlineData("Host")]
    [InlineData("host")]
    [InlineData("Range")]
    [InlineData("Accept-Encoding")]
    [InlineData("ACCEPT-ENCODING")]
    [InlineData("Connection")]
    [InlineData("Keep-Alive")]
    [InlineData("Transfer-Encoding")]
    [InlineData("TE")]
    [InlineData("te")]
    [InlineData("Trailer")]
    [InlineData("Upgrade")]
    [InlineData("Expect")]
    [InlineData("Content-Type")]
    [InlineData("content-length")]
    [InlineData("Content-Encoding")]
    [InlineData("If-None-Match")]
    [InlineData("if-modified-since")]
    [InlineData("If-Range")]
    [InlineData("Proxy-Authorization")]
    [InlineData("proxy-connection")]
    public void IsForbidden_RejectsConnectionConditionalAndBodyHeaders(string name)
    {
        Assert.True(HeaderNames.IsForbidden(name));
    }

    [Theory]
    [InlineData("User-Agent")]
    [InlineData("Accept")]
    [InlineData("Accept-Language")]
    [InlineData("Referer")]
    [InlineData("Cookie")]
    [InlineData("Upgrade-Insecure-Requests")] // 只是以 Upgrade 开头，不是 Upgrade 本身
    [InlineData("Sec-Fetch-Dest")]
    [InlineData("sec-ch-ua")]
    [InlineData("X-Requested-With")]
    [InlineData("Hostile")]
    [InlineData("Test")]
    [InlineData("If")] // 前缀要求带连字符
    [InlineData("Contents")]
    [InlineData("Proxy")]
    public void IsForbidden_AllowsOrdinaryHeaders(string name)
    {
        Assert.False(HeaderNames.IsForbidden(name));
    }

    // ---------- HeaderRuleSet.Match ----------

    [Fact]
    public void Match_ExactRulesAreAppliedAfterWildcardRules()
    {
        var set = Build(
            Rule(1, Key, HeadersJson(("X-Level", "exact"))),
            Rule(2, "*", HeadersJson(("X-Level", "all"))),
            Rule(3, "web.sanguosha.com/*", HeadersJson(("X-Level", "host"))));

        // 精确规则的 id 最小，仍然排在所有通配规则之后
        Assert.Equal(new long[] { 2, 3, 1 }, set.Match(Key).Select(r => r.Id));
        Assert.Equal(new[] { "X-Level: exact [rule:1]" }, Describe(HeaderApplier.Preview(Target, null, set, Key), "X-Level"));
    }

    [Fact]
    public void Match_MoreSpecificWildcardIsAppliedLaterAndOverridesTheBroaderOne()
    {
        var set = Build(
            Rule(1, "web.sanguosha.com/220/*", HeadersJson(("X-Level", "narrow"))),
            Rule(2, "*", HeadersJson(("X-Level", "broad"))));

        // 按 LiteralLength 升序而不是 id 升序：更宽的 * 先应用，更具体的后应用
        Assert.Equal(new long[] { 2, 1 }, set.Match(Key).Select(r => r.Id));
        Assert.Equal(new[] { "X-Level: narrow [rule:1]" }, Describe(HeaderApplier.Preview(Target, null, set, Key), "X-Level"));
    }

    [Fact]
    public void Match_WildcardRulesWithTheSameLiteralLengthAreOrderedById()
    {
        var set = Build(
            Rule(5, "a*", HeadersJson(("X-Level", "five"))),
            Rule(4, "*a", HeadersJson(("X-Level", "four"))));

        Assert.Equal(new long[] { 4, 5 }, set.Match("aa").Select(r => r.Id));
    }

    [Fact]
    public void Match_ExactRulesAreOrderedById()
    {
        var set = Build(
            Rule(9, Key, HeadersJson(("X-Level", "nine"))),
            Rule(3, Key.ToUpperInvariant(), HeadersJson(("X-Level", "three"))));

        Assert.Equal(new long[] { 3, 9 }, set.Match(Key).Select(r => r.Id));
    }

    [Fact]
    public void Match_QuestionMarkMatchesExactlyOneCharacter()
    {
        var set = Build(Rule(1, "res/a?.json", HeadersJson(("X-Level", "one"))));

        Assert.Single(set.Match("res/a1.json"));
        Assert.Single(set.Match("res/a中.json"));
        Assert.Empty(set.Match("res/a.json")); // 零个字符
        Assert.Empty(set.Match("res/a12.json")); // 两个字符
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var set = Build(
            Rule(1, "Web.SanGuoSha.com/*.JSON", HeadersJson(("X-Level", "wildcard"))),
            Rule(2, "Web.Sanguosha.com/220/H5_2/Res/Config/A.json", HeadersJson(("X-Level", "exact"))));

        Assert.Equal(new long[] { 1, 2 }, set.Match(Key).Select(r => r.Id));
    }

    [Theory]
    [InlineData("*.json", "web.sanguosha.com/a.json", true)]
    [InlineData("*.json", "web.sanguosha.com/a.json.bak", false)] // 两端锚定
    [InlineData("web.*", "x/web.sanguosha.com", false)]
    [InlineData("a.b*", "a.bc", true)]
    [InlineData("a.b*", "axbc", false)] // 点号是普通字符，不是正则通配
    [InlineData("a+b*", "a+bc", true)]
    [InlineData("a+b*", "aab", false)]
    [InlineData("x(1)*", "x(1)y", true)]
    [InlineData("x[a]*", "x[a]y", true)]
    [InlineData("x[a]*", "xay", false)]
    [InlineData("a|b*", "a|bc", true)]
    [InlineData("a|b*", "a", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("*", "anything/at/all", true)]
    [InlineData("a?c*", "abcdef", true)]
    [InlineData("a?c*", "ac", false)]
    public void Match_WildcardPatternsAreAnchoredAndOtherCharactersAreLiteral(string pattern, string key, bool expected)
    {
        var set = Build(Rule(1, pattern, HeadersJson(("X-Level", "1"))));

        Assert.Equal(expected, set.Match(key).Count == 1);
    }

    [Theory]
    [InlineData("web.sanguosha.com/a.json", "web.sanguosha.com/a.json", true)]
    [InlineData("web.sanguosha.com/a.json", "web.sanguosha.com/a.json2", false)]
    [InlineData("web.sanguosha.com/a.json", "x/web.sanguosha.com/a.json", false)]
    [InlineData("web.sanguosha.com/a.json", "web.sanguosha.com/aXjson", false)] // 点号不是通配
    [InlineData("web.sanguosha.com/a+b.json", "web.sanguosha.com/a+b.json", true)]
    public void Match_ExactPatternRequiresTheWholeKey(string pattern, string key, bool expected)
    {
        var set = Build(Rule(1, pattern, HeadersJson(("X-Level", "1"))));

        Assert.Equal(expected, set.Match(key).Count == 1);
    }

    [Fact]
    public void Match_ReturnsEmptyWhenNothingMatchesAndForTheEmptySet()
    {
        var set = Build(Rule(1, "other.example.com/*", HeadersJson(("X-Level", "1"))));

        Assert.Empty(set.Match(Key));
        Assert.Empty(HeaderRuleSet.Empty.Match(Key));
    }

    [Fact]
    public void Build_ClassifiesPatternsAndComputesLiteralLength()
    {
        var set = Build(
            Rule(1, "web.sanguosha.com/a.json", HeadersJson(("X-Level", "1"))),
            Rule(2, "web.sanguosha.com/*.json", HeadersJson(("X-Level", "2"))),
            Rule(3, "web.sanguosha.com/a?.json", HeadersJson(("X-Level", "3"))));

        var exact = Assert.Single(set.Match("web.sanguosha.com/a.json"), r => r.IsExact);
        Assert.Equal(1, exact.Id);
        Assert.Equal("web.sanguosha.com/a.json", exact.Pattern);
        Assert.Equal(24, exact.LiteralLength); // 精确规则：整个 pattern 都是字面字符
        Assert.Null(exact.Regex);

        var star = Assert.Single(set.Match("web.sanguosha.com/a.json"), r => !r.IsExact);
        Assert.Equal(2, star.Id);
        Assert.Equal(23, star.LiteralLength); // 不计 *
        Assert.NotNull(star.Regex);

        var question = Assert.Single(set.Match("web.sanguosha.com/a1.json"), r => r.Id == 3);
        Assert.False(question.IsExact);
        Assert.Equal(24, question.LiteralLength); // 不计 ?
        Assert.NotNull(question.Regex);
    }

    [Fact]
    public void Build_OnlyEnabledRulesTakeEffect()
    {
        var set = Build(
            Rule(1, "*", HeadersJson(("X-Level", "disabled")), enabled: false),
            Rule(2, Key, HeadersJson(("X-Level", "disabled-exact")), enabled: false),
            Rule(3, "*", HeadersJson(("X-Other", "on"))));

        Assert.Equal(new long[] { 3 }, set.Match(Key).Select(r => r.Id));
        var preview = HeaderApplier.Preview(Target, null, set, Key);
        Assert.Empty(Describe(preview, "X-Level"));
        Assert.Equal(new[] { "X-Other: on [rule:3]" }, Describe(preview, "X-Other"));
    }

    [Fact]
    public void Build_KeepsEmptyHeaderListRulesAndDoesNotRequireSortedInput()
    {
        var set = Build(
            Rule(7, "*", "[]"),
            Rule(2, "*", HeadersJson(("X-Level", "2"))));

        // 同样的 LiteralLength 按 id 升序，与传入顺序无关
        Assert.Equal(new long[] { 2, 7 }, set.Match(Key).Select(r => r.Id));
        Assert.Empty(set.Match(Key)[1].Headers);
    }

    // ---------- HeaderRuleSet.Build：headers_json 的容错 ----------

    [Theory]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("\"text\"")]
    [InlineData("[1]")]
    [InlineData("null")]
    [InlineData("[{\"name\":5,\"value\":\"x\"}]")]
    public void Build_SkipsTheWholeRuleAndLogsOneWarningWhenHeadersJsonCannotBeParsed(string brokenJson)
    {
        var logger = new RecordingLogger();

        var set = HeaderRuleSet.Build(
            [
                Rule(1, "*", HeadersJson(("X-Before", "1"))),
                Rule(2, "broken/*", brokenJson),
                Rule(3, "*", HeadersJson(("X-After", "1")))
            ],
            logger);

        Assert.Equal(new long[] { 1, 3 }, set.Match("broken/a").Select(r => r.Id));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("broken/*", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithoutLoggerSkipsBrokenRulesSilently()
    {
        var set = HeaderRuleSet.Build([Rule(1, "*", "not json"), Rule(2, "*", HeadersJson(("X-Level", "2")))]);

        Assert.Equal(new long[] { 2 }, set.Match(Key).Select(r => r.Id));
    }

    [Fact]
    public void Build_DoesNotParseDisabledRules()
    {
        var logger = new RecordingLogger();

        var set = HeaderRuleSet.Build([Rule(1, "*", "not json", enabled: false)], logger);

        Assert.Empty(set.Match(Key));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Build_DropsBlankNamesAndNullValuesIndividuallyWithoutLogging()
    {
        var logger = new RecordingLogger();
        const string json = """
            [
              {"name":"X-Keep","value":"1"},
              {"name":"   ","value":"blank-name"},
              {"name":"","value":"empty-name"},
              {"name":null,"value":"null-name"},
              {"value":"missing-name"},
              {"name":"X-NullValue","value":null},
              {"name":"X-MissingValue"},
              null,
              {"name":"X-Delete","value":""},
              {"name":"X-Last","value":"2"}
            ]
            """;

        var set = HeaderRuleSet.Build([Rule(1, "*", json)], logger);

        var rule = Assert.Single(set.Match(Key));
        // 空字符串的值是“删除该头”的有效写法，必须保留
        Assert.Equal(new[] { "X-Keep=1", "X-Delete=", "X-Last=2" }, rule.Headers.Select(h => $"{h.Name}={h.Value}"));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Build_ReadsPropertyNamesCaseInsensitivelyAndIgnoresUnknownProperties()
    {
        var set = Build(Rule(1, "*", """[{"Name":"X-Upper","VALUE":"1","note":"ignored"}]"""));

        var header = Assert.Single(Assert.Single(set.Match(Key)).Headers);
        Assert.Equal("X-Upper", header.Name);
        Assert.Equal("1", header.Value);
    }

    [Fact]
    public void Build_DropsForbiddenHeadersButKeepsTheRest()
    {
        var set = Build(Rule(1, "*", HeadersJson(
            ("Host", "evil.example.com"),
            ("Range", "bytes=0-1"),
            ("accept-encoding", "identity"),
            ("Connection", "close"),
            ("Keep-Alive", "timeout=5"),
            ("Transfer-Encoding", "chunked"),
            ("TE", "trailers"),
            ("Trailer", "X"),
            ("Upgrade", "websocket"),
            ("Expect", "100-continue"),
            ("Content-Type", "text/plain"),
            ("Content-Length", "1"),
            ("If-None-Match", "\"x\""),
            ("If-Modified-Since", "x"),
            ("Proxy-Authorization", "x"),
            ("X-Keep", "1"),
            ("Upgrade-Insecure-Requests", "1"))));

        var rule = Assert.Single(set.Match(Key));
        Assert.Equal(new[] { "X-Keep", "Upgrade-Insecure-Requests" }, rule.Headers.Select(h => h.Name));
    }

    // ---------- HeaderApplier.Preview ----------

    [Fact]
    public void Preview_ListsDefaultHeadersThenRuleHeadersWithSources()
    {
        var set = Build(
            Rule(7, "*", HeadersJson(("User-Agent", "UA/1"), ("Accept-Language", "zh-CN"))),
            Rule(12, Key, HeadersJson(("Accept-Language", "en-US"), ("X-Extra", "1"))));

        var preview = HeaderApplier.Preview(EntryPage, null, set, Key);

        // 精确规则覆盖通配规则的同名头；被覆盖的头从原位置移到末尾
        Assert.Equal(
            new[]
            {
                "Accept: " + DocumentAccept + " [default]",
                "Upgrade-Insecure-Requests: 1 [default]",
                "Sec-Fetch-Dest: document [default]",
                "Sec-Fetch-Mode: navigate [default]",
                "Sec-Fetch-Site: none [default]",
                "Sec-Fetch-User: ?1 [default]",
                "User-Agent: UA/1 [rule:7]",
                "Accept-Language: en-US [rule:12]",
                "X-Extra: 1 [rule:12]"
            },
            Describe(preview));
    }

    [Fact]
    public void Preview_WithRefererListsSubresourceHeaders()
    {
        var preview = HeaderApplier.Preview(Target, EntryPage, HeaderRuleSet.Empty, Key);

        Assert.Equal(
            new[]
            {
                "Accept: */* [default]",
                "Referer: https://web.sanguosha.com/220/h5_2/index_210000.php [default]",
                "Sec-Fetch-Dest: empty [default]",
                "Sec-Fetch-Mode: cors [default]",
                "Sec-Fetch-Site: same-origin [default]"
            },
            Describe(preview));
    }

    [Fact]
    public void Preview_RuleHeaderOverridesDefaultHeaderWithTheSameNameIgnoringCase()
    {
        var set = Build(Rule(3, "*", HeadersJson(("referer", "https://example.com/"), ("ACCEPT", "text/plain"))));

        var preview = HeaderApplier.Preview(Target, EntryPage, set, Key);

        Assert.Equal(
            new[]
            {
                "Sec-Fetch-Dest: empty [default]",
                "Sec-Fetch-Mode: cors [default]",
                "Sec-Fetch-Site: same-origin [default]",
                "referer: https://example.com/ [rule:3]",
                "ACCEPT: text/plain [rule:3]"
            },
            Describe(preview));
    }

    [Fact]
    public void Preview_EmptyValueDeletesDefaultAndEarlierRuleHeadersIgnoringCase()
    {
        var set = Build(
            Rule(1, "*", HeadersJson(("User-Agent", "UA/1"), ("X-Extra", "1"))),
            Rule(2, Key, HeadersJson(("user-agent", ""), ("sec-fetch-user", ""), ("ACCEPT", ""))));

        var preview = HeaderApplier.Preview(EntryPage, null, set, Key);

        Assert.Equal(
            new[]
            {
                "Upgrade-Insecure-Requests: 1 [default]",
                "Sec-Fetch-Dest: document [default]",
                "Sec-Fetch-Mode: navigate [default]",
                "Sec-Fetch-Site: none [default]",
                "X-Extra: 1 [rule:1]"
            },
            Describe(preview));
    }

    [Fact]
    public void Preview_EmptyValueForAMissingHeaderIsNotAnError_AndALaterRuleCanAddTheHeaderBack()
    {
        var set = Build(
            Rule(1, "*", HeadersJson(("X-Never-Set", ""), ("X-Extra", ""))),
            Rule(2, "web.sanguosha.com/*", HeadersJson(("X-Extra", "back"))));

        var preview = HeaderApplier.Preview(Target, null, set, Key);

        Assert.Empty(Describe(preview, "X-Never-Set"));
        Assert.Equal(new[] { "X-Extra: back [rule:2]" }, Describe(preview, "X-Extra"));
    }

    [Fact]
    public void Preview_ForbiddenHeadersNeverAppearEvenWhenARuleTriesToSetOrDeleteThem()
    {
        var set = Build(Rule(1, "*", HeadersJson(
            ("Host", "evil.example.com"),
            ("Accept-Encoding", "identity"),
            ("If-None-Match", "\"x\""),
            ("Content-Type", "text/plain"),
            ("Range", ""),
            ("X-Keep", "1"))));

        var preview = HeaderApplier.Preview(Target, EntryPage, set, Key);

        Assert.Equal(new[] { "X-Keep: 1 [rule:1]" }, preview.Where(h => h.Source != "default").Select(h => $"{h.Name}: {h.Value} [{h.Source}]"));
        Assert.Equal(5, preview.Count(h => h.Source == "default"));
    }

    [Fact]
    public void Preview_IgnoresDisabledRulesAndRulesThatDoNotMatchTheKey()
    {
        var set = Build(
            Rule(1, "*", HeadersJson(("X-Disabled", "1")), enabled: false),
            Rule(2, "other.example.com/*", HeadersJson(("X-Other-Host", "1"))));

        var preview = HeaderApplier.Preview(Target, EntryPage, set, Key);

        Assert.All(preview, h => Assert.Equal("default", h.Source));
    }

    [Fact]
    public void Preview_WithTheEmptySetReturnsOnlyBrowserHeaders()
    {
        var preview = HeaderApplier.Preview(EntryPage, null, HeaderRuleSet.Empty, Key);

        Assert.Equal(6, preview.Count);
        Assert.All(preview, h => Assert.Equal("default", h.Source));
    }

    // ---------- HeaderApplier.Apply ----------

    [Fact]
    public void Apply_WritesExactlyTheHeadersThatPreviewReports()
    {
        var set = Build(
            Rule(1, "*", HeadersJson(("User-Agent", "UA/1"), ("Accept-Language", "zh-CN"), ("X-Deleted-Later", "1"))),
            Rule(2, "web.sanguosha.com/*", HeadersJson(("Accept", "text/plain"), ("X-Deleted-Later", ""))),
            Rule(3, Key, HeadersJson(("accept-language", "en-US"), ("Sec-Fetch-Mode", ""), ("Cookie", "a=b"))));
        var preview = HeaderApplier.Preview(Target, EntryPage, set, Key);
        using var request = new HttpRequestMessage(HttpMethod.Get, Target);

        HeaderApplier.Apply(request, EntryPage, set, Key);

        // 已知头在请求里以规范大小写保存（accept-language -> Accept-Language），所以按不区分大小写的字典逐项比较
        var actual = Snapshot(request);
        Assert.Equal(preview.Count, actual.Count);
        foreach (var header in preview)
        {
            Assert.Equal(header.Value, actual[header.Name]);
        }

        Assert.Equal("UA/1", actual["User-Agent"]);
        Assert.Equal("text/plain", actual["Accept"]);
        Assert.Equal("en-US", actual["Accept-Language"]);
        Assert.Equal("a=b", actual["Cookie"]);
        Assert.False(actual.ContainsKey("Sec-Fetch-Mode"));
        Assert.False(actual.ContainsKey("X-Deleted-Later"));
    }

    [Fact]
    public void Apply_ComputesBrowserHeadersFromTheRequestUri()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://web.sanguosha.com/220/h5_2/res/a.css?v=1");

        HeaderApplier.Apply(request, EntryPage, HeaderRuleSet.Empty, "web.sanguosha.com/220/h5_2/res/a.css");

        var headers = Snapshot(request);
        Assert.Equal("text/css,*/*;q=0.1", headers["Accept"]);
        Assert.Equal("style", headers["Sec-Fetch-Dest"]);
        Assert.Equal("no-cors", headers["Sec-Fetch-Mode"]);
        Assert.Equal("same-origin", headers["Sec-Fetch-Site"]);
        Assert.Equal(EntryPage.AbsoluteUri, headers["Referer"]);
    }

    [Fact]
    public void Apply_ReplacesAHeaderThatIsAlreadyOnTheRequest()
    {
        var set = Build(Rule(1, "*", HeadersJson(("X-Extra", "new"))));
        using var request = new HttpRequestMessage(HttpMethod.Get, Target);
        request.Headers.TryAddWithoutValidation("X-Extra", "old");

        HeaderApplier.Apply(request, null, set, Key);

        Assert.Equal("new", Snapshot(request)["X-Extra"]);
    }

    [Fact]
    public void Apply_IgnoresHeaderNamesTheRequestRejectsInsteadOfThrowing()
    {
        // 格式非法的名称、以及 .NET 归为内容头的名称：Remove 会抛异常，TryAddWithoutValidation 会返回 false
        var set = Build(Rule(1, "*", HeadersJson(
            ("Bad Name", "x"),
            ("Last-Modified", "x"),
            ("Allow", "GET"),
            ("X-Keep", "1"))));
        using var request = new HttpRequestMessage(HttpMethod.Get, Target);

        HeaderApplier.Apply(request, null, set, Key);

        var headers = Snapshot(request);
        Assert.Equal("1", headers["X-Keep"]);
        Assert.False(headers.ContainsKey("Bad Name"));
        Assert.False(headers.ContainsKey("Last-Modified"));
        Assert.False(headers.ContainsKey("Allow"));
        Assert.Equal("document", headers["Sec-Fetch-Dest"]); // 其它头照常写入
    }

    [Fact]
    public void Apply_RequestWithoutUriIsRejected()
    {
        using var request = new HttpRequestMessage();

        Assert.Throws<ArgumentException>(() => HeaderApplier.Apply(request, null, HeaderRuleSet.Empty, Key));
    }

    // ---------- BrowserHeaders ----------

    [Fact]
    public void Compute_WithoutRefererReturnsNavigationHeadersInOrder()
    {
        // 没有 referer 说明是入口页：不论扩展名都按文档导航处理
        var headers = BrowserHeaders.Compute(EntryPage, null);

        Assert.Equal(
            new[]
            {
                "Accept: " + DocumentAccept,
                "Upgrade-Insecure-Requests: 1",
                "Sec-Fetch-Dest: document",
                "Sec-Fetch-Mode: navigate",
                "Sec-Fetch-Site: none",
                "Sec-Fetch-User: ?1"
            },
            headers.Select(h => $"{h.Name}: {h.Value}"));
    }

    [Theory]
    [InlineData("https://web.sanguosha.com/a/b.js?v=1", "*/*", "script", "no-cors")]
    [InlineData("https://web.sanguosha.com/a/index.php", "*/*", "script", "no-cors")]
    [InlineData("https://web.sanguosha.com/a/b.css", "text/css,*/*;q=0.1", "style", "no-cors")]
    [InlineData("https://web.sanguosha.com/a/b.png", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8", "image", "no-cors")]
    [InlineData("https://web.sanguosha.com/a/b.jpg", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8", "image", "no-cors")]
    [InlineData("https://web.sanguosha.com/a/b.jpeg", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8", "image", "no-cors")]
    [InlineData("https://web.sanguosha.com/a/b.webp", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8", "image", "no-cors")]
    [InlineData("https://web.sanguosha.com/a/b.gif", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8", "image", "no-cors")]
    [InlineData("https://web.sanguosha.com/a/b.svg", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8", "image", "no-cors")]
    [InlineData("https://web.sanguosha.com/a/b.avif", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8", "image", "no-cors")]
    [InlineData("https://web.sanguosha.com/a/b.ico", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8", "image", "no-cors")]
    [InlineData("https://web.sanguosha.com/a/B.PNG", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8", "image", "no-cors")]
    [InlineData("https://web.sanguosha.com/a/b.woff", "*/*", "font", "cors")]
    [InlineData("https://web.sanguosha.com/a/b.woff2", "*/*", "font", "cors")]
    [InlineData("https://web.sanguosha.com/a/b.ttf", "*/*", "font", "cors")]
    [InlineData("https://web.sanguosha.com/a/b.otf", "*/*", "font", "cors")]
    [InlineData("https://web.sanguosha.com/a/b.json", "*/*", "empty", "cors")]
    [InlineData("https://web.sanguosha.com/a/b.sgs", "*/*", "empty", "cors")]
    [InlineData("https://web.sanguosha.com/a/noext", "*/*", "empty", "cors")]
    public void Compute_WithRefererClassifiesTheRequestByExtension(string url, string accept, string dest, string mode)
    {
        var headers = BrowserHeaders.Compute(new Uri(url), EntryPage);

        Assert.Equal(
            new[]
            {
                "Accept: " + accept,
                "Referer: " + EntryPage.AbsoluteUri,
                "Sec-Fetch-Dest: " + dest,
                "Sec-Fetch-Mode: " + mode,
                "Sec-Fetch-Site: same-origin"
            },
            headers.Select(h => $"{h.Name}: {h.Value}"));
    }

    [Theory]
    [InlineData("https://web.sanguosha.com/a.js", "https://web.sanguosha.com/220/index.php?x=1", "same-origin")]
    [InlineData("https://web.sanguosha.com:443/a.js", "https://web.sanguosha.com/index.php", "same-origin")] // 显式默认端口
    [InlineData("https://WEB.SanGuoSha.com/a.js", "https://web.sanguosha.com/index.php", "same-origin")]
    [InlineData("https://cdn.sanguosha.com/a.js", "https://web.sanguosha.com/index.php", "cross-site")]
    [InlineData("https://sanguosha.com/a.js", "https://web.sanguosha.com/index.php", "cross-site")]
    [InlineData("http://web.sanguosha.com/a.js", "https://web.sanguosha.com/index.php", "cross-site")]
    [InlineData("https://web.sanguosha.com:8443/a.js", "https://web.sanguosha.com/index.php", "cross-site")]
    public void Compute_SecFetchSiteIsSameOriginOnlyWhenSchemeHostAndPortAllMatch(string url, string referer, string expected)
    {
        var headers = BrowserHeaders.Compute(new Uri(url), new Uri(referer));

        Assert.Equal(expected, headers.Single(h => h.Name == "Sec-Fetch-Site").Value);
    }

    // ---------- 辅助 ----------

    private static HeaderRuleRow Rule(long id, string pattern, string headersJson, bool enabled = true) => new()
    {
        Id = id,
        Pattern = pattern,
        HeadersJson = headersJson,
        Enabled = enabled ? 1 : 0,
        UpdatedAt = "2026-01-01T00:00:00.000Z"
    };

    private static HeaderRuleSet Build(params HeaderRuleRow[] rows) => HeaderRuleSet.Build(rows);

    private static string HeadersJson(params (string Name, string Value)[] headers) =>
        JsonSerializer.Serialize(headers.Select(h => new { name = h.Name, value = h.Value }));

    private static string[] Describe(IEnumerable<PreviewHeader> headers, string? onlyName = null) => headers
        .Where(h => onlyName is null || string.Equals(h.Name, onlyName, StringComparison.OrdinalIgnoreCase))
        .Select(h => $"{h.Name}: {h.Value} [{h.Source}]")
        .ToArray();

    private static Dictionary<string, string> Snapshot(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers.NonValidated)
        {
            headers[header.Key] = header.Value.ToString();
        }

        return headers;
    }
}

public sealed class HeaderRuleProviderTests : IAsyncLifetime
{
    private const string T1 = "2026-01-01T00:00:01.000Z";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sgs-collector-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingLogger _logger = new();
    private HeaderRuleRepository _repository = null!;
    private HeaderRuleProvider _provider = null!;

    public async Task InitializeAsync()
    {
        var db = new Db(Path.Combine(_directory, "App_Data", "collector.db"));
        await db.InitializeAsync();
        _repository = new HeaderRuleRepository(db);
        _provider = new HeaderRuleProvider(_repository, _logger);
    }

    public Task DisposeAsync()
    {
        // 连接池会一直持有文件句柄，Windows 上必须先清空才能删除
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void Current_BeforeTheFirstReloadIsTheEmptySet()
    {
        Assert.Same(HeaderRuleSet.Empty, _provider.Current);
    }

    [Fact]
    public async Task ReloadAsync_ReadsTheDefaultRuleAndReplacesCurrent()
    {
        var loaded = await _provider.ReloadAsync();

        Assert.Same(loaded, _provider.Current);
        var rule = Assert.Single(loaded.Match("web.sanguosha.com/anything.json"));
        Assert.Equal("*", rule.Pattern);
        Assert.Equal(
            new[] { "User-Agent", "Accept-Language", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform" },
            rule.Headers.Select(h => h.Name));
    }

    [Fact]
    public async Task ReloadAsync_PicksUpRuleChangesOnlyWhenCalled()
    {
        var first = await _provider.ReloadAsync();
        var id = await _repository.InsertAsync(
            "web.sanguosha.com/*", """[{"name":"X-Extra","value":"1"}]""", true, null, T1);

        Assert.Same(first, _provider.Current); // 没有重载之前仍是旧快照
        Assert.Single(first.Match("web.sanguosha.com/a"));

        var second = await _provider.ReloadAsync();

        Assert.NotSame(first, second);
        Assert.Same(second, _provider.Current);
        Assert.Equal(2, second.Match("web.sanguosha.com/a").Count);
        Assert.Single(first.Match("web.sanguosha.com/a")); // 旧快照不受影响，正在运行的一轮继续使用它

        Assert.True(await _repository.UpdateAsync(id, "web.sanguosha.com/*", """[{"name":"X-Extra","value":"1"}]""", false, null, T1));
        Assert.True(await _repository.DeleteAsync(1)); // 初始化时插入的默认规则
        var third = await _provider.ReloadAsync();

        Assert.Empty(third.Match("web.sanguosha.com/a")); // 剩下的规则已被禁用
    }

    [Fact]
    public async Task ReloadAsync_PassesTheLoggerToBuild()
    {
        await _repository.InsertAsync("broken/*", "not json", true, null, T1);

        var loaded = await _provider.ReloadAsync();

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("broken/*", entry.Message, StringComparison.Ordinal);
        Assert.Single(loaded.Match("broken/a")); // 只剩默认规则
    }
}

// 记录日志条目的最小实现，既当 ILogger 传给 Build，也当 ILogger<HeaderRuleProvider> 传给 Provider
internal sealed class RecordingLogger : ILogger<HeaderRuleProvider>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
