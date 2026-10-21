using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Sgs.Collector.Discovery;

public sealed class StaticResourceParser
{
    private const string Extensions = "js|sgs|json|jsonl|proto|xml|atlas|sk|skel|png|jpg|jpeg|webp|gif|svg|dds|ktx|mp3|ogg|mp4|webm|swf|part|txt|ttf|otf|woff|woff2";

    private static readonly Regex StaticPathRegex = new(
        "(?<quote>['\"])(?<path>(?:https?://[^'\"\\s]+?\\.(?:" + Extensions + ")(?:\\?[^'\"]*)?|(?:\\.\\./|\\./)?(?:res|libs|version|sgsCensus)/[^'\"]+?\\.(?:" + Extensions + ")(?:\\?[^'\"]*)?|sgsGame[^'\"\\s]*?\\.sgs(?:\\?[^'\"]*)?))(\\k<quote>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Uri _entryUrl;
    private readonly Uri _h5Root;
    private readonly UTF8Encoding _strictUtf8 = new(false, true);

    public StaticResourceParser(string entryUrl)
    {
        _entryUrl = new Uri(entryUrl);
        _h5Root = new Uri(_entryUrl, ".");
    }

    public StaticResourceParser(DiscoveryRules rules)
    {
        _entryUrl = rules.EntryUri;
        _h5Root = rules.H5Root;
    }

    public EntrySnapshot ParseEntry(string html, Uri baseUri)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        foreach (var comment in document.DocumentNode.DescendantsAndSelf().Where(static node => node.NodeType == HtmlNodeType.Comment).ToArray())
        {
            comment.Remove();
        }

        var inlineScripts = document.DocumentNode.Descendants("script")
            .Where(static script => string.IsNullOrWhiteSpace(script.GetAttributeValue("src", "")))
            .Select(static script => script.InnerText);
        return new EntrySnapshot(ParseScriptUris(document, baseUri).ToArray(), string.Join("\n", inlineScripts));
    }

    public IEnumerable<VersionEntry> ParseVersionJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                continue;
            }

            var version = property.Value.ValueKind switch
            {
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.String => property.Value.GetString() ?? "",
                _ => ""
            };
            yield return new VersionEntry(property.Name, version);
        }
    }

    public IEnumerable<string> ParseDefaultResJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var resource in resources.EnumerateArray())
        {
            if (resource.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            {
                var value = url.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }
    }

    public IEnumerable<string> ScanStaticResourcePaths(string text)
    {
        foreach (Match match in StaticPathRegex.Matches(text))
        {
            var value = match.Groups["path"].Value;
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
                || ContainsTemplatePlaceholder(value))
            {
                continue;
            }

            yield return value;
        }
    }

    public bool TryCreateResourceUri(string value, Uri referer, out Uri uri)
    {
        uri = _entryUrl;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (ContainsTemplatePlaceholder(value))
        {
            return false;
        }

        if (value.Contains("://", StringComparison.Ordinal) && !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.EndsWith("?v=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = _entryUrl.Scheme + ":" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (value.StartsWith("res/", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("libs/", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("version/", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("sgsGame", StringComparison.OrdinalIgnoreCase))
            {
                absolute = new Uri(_h5Root, value);
            }
            else if (!Uri.TryCreate(referer, value, out absolute))
            {
                return false;
            }
        }

        if (absolute.Scheme != Uri.UriSchemeHttps && absolute.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        uri = absolute;
        return true;
    }

    public string? ToH5RelativePath(Uri uri)
    {
        if (!string.Equals(uri.Host, _h5Root.Host, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rootPath = _h5Root.AbsolutePath;
        var path = uri.AbsolutePath.TrimStart('/');
        if (uri.AbsolutePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            path = uri.AbsolutePath[rootPath.Length..].TrimStart('/');
        }

        return Uri.UnescapeDataString(path);
    }

    public bool TryDecodeText(byte[] bytes, out string text)
    {
        text = "";
        if (bytes.Length == 0)
        {
            text = "";
            return true;
        }

        var sampleLength = Math.Min(bytes.Length, 4096);
        var controls = 0;
        for (var i = 0; i < sampleLength; i++)
        {
            var b = bytes[i];
            if (b == 0)
            {
                return false;
            }

            if (b < 0x09 || (b > 0x0D && b < 0x20))
            {
                controls++;
            }
        }

        if (controls > 8)
        {
            return false;
        }

        try
        {
            text = _strictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    // 取最后一个匹配：JS 中后赋值者生效
    public static string? FindWindowString(string scriptText, string name)
    {
        var pattern = "window\\." + Regex.Escape(name) + "\\s*=\\s*(['\"])(?<v>[^'\"\\r\\n]*)\\1";
        string? result = null;
        foreach (Match match in Regex.Matches(scriptText, pattern))
        {
            var value = match.Groups["v"].Value.Trim();
            result = value.Length == 0 ? null : value;
        }

        return result;
    }

    private static IEnumerable<Uri> ParseScriptUris(HtmlDocument document, Uri baseUri)
    {
        foreach (var script in document.DocumentNode.Descendants("script"))
        {
            var src = script.GetAttributeValue("src", "");
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            if (Uri.TryCreate(baseUri, src, out var uri))
            {
                yield return uri;
            }
        }
    }

    public static bool ContainsTemplatePlaceholder(string value)
    {
        return value.Contains('{', StringComparison.Ordinal)
            || value.Contains('}', StringComparison.Ordinal)
            || value.Contains("%7B", StringComparison.OrdinalIgnoreCase)
            || value.Contains("%7D", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record EntrySnapshot(IReadOnlyList<Uri> EffectiveScripts, string InlineScriptText);

public sealed record VersionEntry(string Path, string Version);
