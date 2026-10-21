namespace Sgs.Collector.Http;

internal static class BrowserHeaders
{
    // User-Agent、Accept-Language、sec-ch-ua 三类已移到默认规则（header_rules 表），这里不再输出
    public static IReadOnlyList<HeaderEntry> Compute(Uri requestUri, Uri? referer)
    {
        var kind = RequestKind.From(requestUri, referer);
        List<HeaderEntry> headers = [new("Accept", kind.Accept)];

        if (referer is null)
        {
            headers.Add(new("Upgrade-Insecure-Requests", "1"));
            headers.Add(new("Sec-Fetch-Dest", kind.FetchDest));
            headers.Add(new("Sec-Fetch-Mode", kind.FetchMode));
            headers.Add(new("Sec-Fetch-Site", "none"));
            headers.Add(new("Sec-Fetch-User", "?1"));
        }
        else
        {
            headers.Add(new("Referer", referer.AbsoluteUri));
            headers.Add(new("Sec-Fetch-Dest", kind.FetchDest));
            headers.Add(new("Sec-Fetch-Mode", kind.FetchMode));
            headers.Add(new("Sec-Fetch-Site", SameOrigin(requestUri, referer) ? "same-origin" : "cross-site"));
        }

        return headers;
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}

internal sealed record RequestKind(string Accept, string FetchDest, string FetchMode)
{
    public static RequestKind From(Uri uri, Uri? referer)
    {
        if (referer is null)
        {
            return new RequestKind("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7", "document", "navigate");
        }

        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (extension is ".js" or ".php")
        {
            return new RequestKind("*/*", "script", "no-cors");
        }

        if (extension is ".css")
        {
            return new RequestKind("text/css,*/*;q=0.1", "style", "no-cors");
        }

        if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".svg" or ".avif" or ".ico")
        {
            return new RequestKind("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8", "image", "no-cors");
        }

        if (extension is ".woff" or ".woff2" or ".ttf" or ".otf")
        {
            return new RequestKind("*/*", "font", "cors");
        }

        return new RequestKind("*/*", "empty", "cors");
    }
}
