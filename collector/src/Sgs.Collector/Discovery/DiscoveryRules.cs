using System.Globalization;
using Sgs.Collector.Common;

namespace Sgs.Collector.Discovery;

public sealed class DiscoveryRules
{
    private const string AesRescPath = "libs/min/aesresc";
    private const string VersionJsonPath = "version.json";
    private const string DefaultRescVersion = "1.0";
    private const string RuntimeMarker = "/res/runtime/";
    private const string DesktopPlatform = "pc";
    private const string ConfigMarker = "/res/config/";
    private const string ProtoMarker = "/res/proto/";

    public DiscoveryRules(string entryUrl)
    {
        EntryUri = new Uri(entryUrl);
        H5Root = new Uri(EntryUri, ".");
        BootstrapKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            ResourceKeys.FromUri(EntryUri),
            ResourceKeys.FromUri(new Uri(H5Root, AesRescPath)),
            ResourceKeys.FromUri(new Uri(H5Root, VersionJsonPath))
        };
    }

    public Uri EntryUri { get; }

    public Uri H5Root { get; }

    // 只在启动阶段处理的三个资源；之后任何发现都不能再把它们放入队列
    public IReadOnlySet<string> BootstrapKeys { get; }

    // ---------- 地址构造（D3）：与游戏客户端发出的请求一致，CDN 按完整 URL 缓存 ----------

    public Uri BuildAesRescUri(string? rescVersion)
    {
        var version = string.IsNullOrEmpty(rescVersion) ? DefaultRescVersion : rescVersion;
        return new Uri(H5Root, AesRescPath + "?v=" + Uri.EscapeDataString(version));
    }

    public IReadOnlyList<Uri> BuildSeedUris(string? layaVersion, string? mainVersion)
    {
        return
        [
            Versioned("libs/min/laya_a.sgs", layaVersion),
            Versioned("libs/min/laya.sgs", layaVersion),
            Versioned("sgsGame_a.sgs", mainVersion),
            Versioned("sgsGame.sgs", mainVersion)
        ];
    }

    public Uri Versioned(string path, string? version)
    {
        var relative = string.IsNullOrEmpty(version) ? path : path + "?v=" + Uri.EscapeDataString(version);
        return new Uri(H5Root, relative);
    }

    public Uri BuildVersionJsonUri(DateTimeOffset now)
    {
        return new Uri(H5Root, VersionJsonPath + "?t=" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
    }

    // ---------- 范围 ----------

    public bool InScope(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        if (!string.Equals(uri.Host, EntryUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.Replace('\\', '/');
        if (string.Equals(path, EntryUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!path.StartsWith(H5Root.AbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsApiLikePath(uri))
        {
            return false;
        }

        var markerIndex = path.IndexOf(RuntimeMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return true;
        }

        var platformStart = markerIndex + RuntimeMarker.Length;
        var platformEnd = path.IndexOf('/', platformStart);
        var platform = platformEnd < 0 ? path[platformStart..] : path[platformStart..platformEnd];
        return platform.Equals(DesktopPlatform, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 静态判断：旧 SgsPathRules ----------

    public static bool IsApiLikePath(Uri uri)
    {
        var extension = Path.GetExtension(uri.AbsolutePath);
        return extension.Equals(".php", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDirectConfigJsonPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("220/h5_2/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["220/h5_2/".Length..];
        }

        return (normalized.StartsWith("res/config/", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
            && normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsConfigPackagePath(string relativePath) =>
        relativePath.EndsWith("Config_w.sgs", StringComparison.OrdinalIgnoreCase)
        || relativePath.EndsWith("Config.sgs", StringComparison.OrdinalIgnoreCase);

    // ---------- 静态判断：按资源 key ----------

    public static bool IsAesResc(string key) =>
        key.EndsWith("/" + AesRescPath, StringComparison.OrdinalIgnoreCase);

    public static bool IsVersionJson(string key) =>
        key.EndsWith("/" + VersionJsonPath, StringComparison.OrdinalIgnoreCase);

    public static bool IsDefaultResJson(string key) =>
        key.EndsWith("/default.res.json", StringComparison.OrdinalIgnoreCase);

    public static bool IsEncryptedContext(string key) =>
        key.Contains(ConfigMarker, StringComparison.OrdinalIgnoreCase)
        || key.Contains(ProtoMarker, StringComparison.OrdinalIgnoreCase);

    public static bool ShouldScanText(string key) =>
        key.EndsWith("/before.min.js", StringComparison.OrdinalIgnoreCase)
        || key.Contains("sgsGame", StringComparison.OrdinalIgnoreCase)
        || key.Contains(ConfigMarker, StringComparison.OrdinalIgnoreCase)
        || key.Contains(ProtoMarker, StringComparison.OrdinalIgnoreCase);

    // 内容会被解析的资源，用于 processing_rev 判断
    public static bool IsProcessedKey(string key) =>
        IsAesResc(key) || IsVersionJson(key) || IsDefaultResJson(key) || ShouldScanText(key);
}
