using Sgs.Collector.Common;
using Sgs.Collector.Crawl;
using Sgs.Collector.Decode;

namespace Sgs.Collector.Discovery;

public sealed record DiscoveredLink(Uri Uri, string Source);

public sealed record EntryDiscovery(IReadOnlyList<DiscoveredLink> Links, string? MainVersion, string? LayaVersion, string? RescVersion);

public sealed class DiscoveryEngine
{
    private const string StaticTextScanSource = "static-text-scan";

    private readonly DiscoveryRules _rules;
    private readonly StaticResourceParser _parser;

    public DiscoveryEngine(DiscoveryRules rules, StaticResourceParser parser)
    {
        _rules = rules;
        _parser = parser;
    }

    public EntryDiscovery FromEntry(byte[] content, Uri entryUri)
    {
        if (!_parser.TryDecodeText(content, out var text))
        {
            return new EntryDiscovery([], null, null, null);
        }

        var snapshot = _parser.ParseEntry(text, entryUri);
        var links = snapshot.EffectiveScripts.Select(static uri => new DiscoveredLink(uri, "entry-script")).ToArray();
        return new EntryDiscovery(
            links,
            StaticResourceParser.FindWindowString(snapshot.InlineScriptText, "mainVersion"),
            StaticResourceParser.FindWindowString(snapshot.InlineScriptText, "layaVersion"),
            StaticResourceParser.FindWindowString(snapshot.InlineScriptText, "rescVersion"));
    }

    public IReadOnlyList<DiscoveredLink> FromResource(CrawlItem item, byte[] content, IReadOnlyList<DecodedMember> members)
    {
        var links = new List<DiscoveredLink>();
        if (members.Count == 0)
        {
            if (DiscoveryRules.IsVersionJson(item.Key) && _parser.TryDecodeText(content, out var versionText))
            {
                foreach (var entry in _parser.ParseVersionJson(versionText))
                {
                    if (StaticResourceParser.ContainsTemplatePlaceholder(entry.Path) || DiscoveryRules.IsDirectConfigJsonPath(entry.Path))
                    {
                        continue;
                    }

                    links.Add(new DiscoveredLink(_rules.Versioned(entry.Path, entry.Version), "version-json"));
                }
            }

            if (DiscoveryRules.IsDefaultResJson(item.Key) && _parser.TryDecodeText(content, out var defaultResText))
            {
                foreach (var value in _parser.ParseDefaultResJson(defaultResText))
                {
                    if (DiscoveryRules.IsDirectConfigJsonPath(value))
                    {
                        continue;
                    }

                    AddRelative(links, value, "default-res", item.FetchUri);
                }
            }

            if (DiscoveryRules.ShouldScanText(item.Key) && _parser.TryDecodeText(content, out var scanText))
            {
                ScanInto(links, scanText, item.FetchUri);
            }
        }
        else
        {
            foreach (var member in members)
            {
                if (member.Bytes is null)
                {
                    continue;
                }

                var memberKey = ResourceKeys.ForMember(item.Key, member.SuggestedPath);
                if (DiscoveryRules.ShouldScanText(memberKey) && _parser.TryDecodeText(member.Bytes, out var memberText))
                {
                    ScanInto(links, memberText, item.FetchUri);
                }
            }
        }

        return links;
    }

    private void ScanInto(List<DiscoveredLink> links, string text, Uri baseUri)
    {
        foreach (var path in _parser.ScanStaticResourcePaths(text))
        {
            AddRelative(links, path, StaticTextScanSource, baseUri);
        }
    }

    // 非本站/接口路径的过滤交给 CrawlQueue 的范围检查
    private void AddRelative(List<DiscoveredLink> links, string value, string source, Uri baseUri)
    {
        if (!_parser.TryCreateResourceUri(value, baseUri, out var uri))
        {
            return;
        }

        var relative = _parser.ToH5RelativePath(uri);
        if (!string.IsNullOrEmpty(relative) && DiscoveryRules.IsDirectConfigJsonPath(relative))
        {
            return;
        }

        if (source == StaticTextScanSource && !string.IsNullOrEmpty(relative) && DiscoveryRules.IsConfigPackagePath(relative))
        {
            return;
        }

        links.Add(new DiscoveredLink(uri, source));
    }
}
