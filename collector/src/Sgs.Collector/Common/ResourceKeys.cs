using System.Globalization;

namespace Sgs.Collector.Common;

internal static class ResourceKeys
{
    public static Uri StripFragment(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Fragment))
        {
            return uri;
        }

        return new Uri(uri.GetLeftPart(UriPartial.Query));
    }

    public static string FromUri(Uri uri)
    {
        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        return authority + Uri.UnescapeDataString(uri.AbsolutePath);
    }

    public static string ForMember(string packageKey, string memberPath) => packageKey + "/" + memberPath;

    public static string LastSegment(string key) => key[(key.LastIndexOf('/') + 1)..];

    public static string ToUrlPath(string key) => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
}
