using Microsoft.AspNetCore.StaticFiles;

namespace Sgs.Collector.Common;

internal static class ContentTypes
{
    private const string OctetStream = "application/octet-stream";

    private static readonly FileExtensionContentTypeProvider Provider = new();

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = "application/json; charset=utf-8",
        [".jsonl"] = "application/x-ndjson; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".atlas"] = "text/plain; charset=utf-8",
        [".proto"] = "text/plain; charset=utf-8",
        [".log"] = "text/plain; charset=utf-8",
        [".xml"] = "application/xml; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".webp"] = "image/webp",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".swf"] = "application/x-shockwave-flash",
        [".sk"] = OctetStream,
        [".skel"] = OctetStream,
        [".sgs"] = OctetStream,
        [".part"] = OctetStream,
        [".bin"] = OctetStream,
        [".dds"] = OctetStream,
        [".ktx"] = OctetStream,
    };

    public static string Resolve(string key, string? responseContentType)
    {
        var fileName = ResourceKeys.LastSegment(key);

        if (ByExtension.TryGetValue(Path.GetExtension(fileName), out var known))
        {
            return known;
        }

        if (Provider.TryGetContentType(fileName, out var provided))
        {
            return provided;
        }

        if (!string.IsNullOrWhiteSpace(responseContentType))
        {
            return responseContentType.Trim();
        }

        return OctetStream;
    }
}
