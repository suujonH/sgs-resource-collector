using System.IO.Compression;
using System.Text;
using Sgs.Collector.Common;
using Sgs.Collector.Discovery;

namespace Sgs.Collector.Decode;

// SourcePath：包内原始路径；SuggestedPath：按内容推断出的路径；Error 非空时 Bytes 为 null
public sealed record DecodedMember(string SourcePath, string SuggestedPath, byte[]? Bytes, string? Error);

public sealed class PackageDecoder
{
    private readonly UTF8Encoding _strictUtf8 = new(false, true);

    // 返回空列表表示 content 不是包。ZIP 结构错误、成员路径非法直接抛出，由调用方把整个包记为失败；
    // 单个成员的解密、解压错误只记在该成员的 Error 上
    public IReadOnlyList<DecodedMember> Decode(string key, byte[] content, Func<byte[], byte[]> decrypt)
    {
        var encrypted = DiscoveryRules.IsEncryptedContext(key);

        if (IsZip(content))
        {
            List<DecodedMember> result = [];
            DecodeZip(content, "", encrypted, decrypt, result);
            return result;
        }

        if (encrypted && key.EndsWith(".sgs", StringComparison.OrdinalIgnoreCase))
        {
            return [DecodeLeaf(ResourceKeys.LastSegment(key), content, encrypted: true, decrypt, topLevel: true)];
        }

        return [];
    }

    private void DecodeZip(byte[] bytes, string prefix, bool encrypted, Func<byte[], byte[]> decrypt, List<DecodedMember> result)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var path = CombineMember(prefix, entry.FullName);
            var child = ms.ToArray();
            if (IsZip(child))
            {
                DecodeZip(child, path, encrypted, decrypt, result);
            }
            else
            {
                result.Add(DecodeLeaf(path, child, encrypted, decrypt, topLevel: false));
            }
        }
    }

    private DecodedMember DecodeLeaf(string path, byte[] bytes, bool encrypted, Func<byte[], byte[]> decrypt, bool topLevel)
    {
        try
        {
            var decrypted = false;
            if (encrypted && !LooksLikeText(bytes) && !IsGzip(bytes))
            {
                bytes = decrypt(bytes);
                decrypted = true;
            }

            if (IsGzip(bytes))
            {
                bytes = Gunzip(bytes);
            }
            else if (decrypted && !LooksLikeText(bytes))
            {
                return new DecodedMember(path, path, null, "解密结果既不是 gzip 也不是 UTF-8 文本（aesresc 可能与资源版本不匹配）");
            }

            var extension = GuessExtension(bytes);
            var suggested = path;
            if (topLevel || (path.EndsWith(".sgs", StringComparison.OrdinalIgnoreCase) && (extension is ".json" or ".proto" or ".js" or ".txt")))
            {
                suggested = ChangePortableExtension(path, extension);
            }

            return new DecodedMember(path, suggested, bytes, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DecodedMember(path, path, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private string GuessExtension(byte[] bytes)
    {
        if (TryDecodeText(bytes, out var text))
        {
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return ".json";
            }

            if (trimmed.StartsWith("syntax = \"proto3\";", StringComparison.Ordinal))
            {
                return ".proto";
            }

            if (trimmed.StartsWith("window.", StringComparison.Ordinal)
                || trimmed.StartsWith("function", StringComparison.Ordinal)
                || trimmed.StartsWith("class", StringComparison.Ordinal)
                || trimmed.StartsWith("var ", StringComparison.Ordinal)
                || trimmed.StartsWith("let ", StringComparison.Ordinal)
                || trimmed.StartsWith("const ", StringComparison.Ordinal)
                || trimmed.Contains("=>", StringComparison.Ordinal))
            {
                return ".js";
            }

            return ".txt";
        }

        return ".bin";
    }

    private bool LooksLikeText(byte[] bytes) => TryDecodeText(bytes, out _);

    // 前 2048 字节不含 NUL，且整体是严格 UTF-8。与 StaticResourceParser.TryDecodeText 是两套不同的判断
    private bool TryDecodeText(byte[] bytes, out string text)
    {
        text = "";
        if (bytes.Length == 0)
        {
            return true;
        }

        var sampleLength = Math.Min(bytes.Length, 2048);
        for (var i = 0; i < sampleLength; i++)
        {
            var b = bytes[i];
            if (b == 0)
            {
                return false;
            }
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

    private static bool IsZip(byte[] bytes) => bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    private static bool IsGzip(byte[] bytes) => bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

    private static byte[] Gunzip(byte[] bytes)
    {
        using var source = new MemoryStream(bytes);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static string CombineMember(string prefix, string child)
    {
        if (Path.IsPathRooted(child) || child.StartsWith("/", StringComparison.Ordinal) || child.StartsWith("\\", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid rooted package member path: {child}");
        }

        child = child.Replace('\\', '/');
        ValidatePortablePath(child);
        return string.IsNullOrEmpty(prefix) ? child : prefix.TrimEnd('/') + "/" + child;
    }

    private static void ValidatePortablePath(string portablePath)
    {
        if (string.IsNullOrWhiteSpace(portablePath)
            || Path.IsPathRooted(portablePath)
            || portablePath.StartsWith("/", StringComparison.Ordinal)
            || portablePath.StartsWith("\\", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid package member path: {portablePath}");
        }

        // CombineMember 已把 \ 换成 /；空段既不是 . 也不是 ..，所以不需要像旧代码那样丢弃空段
        foreach (var segment in portablePath.Split('/'))
        {
            if (segment is "." or "..")
            {
                throw new InvalidDataException($"Invalid package member path segment: {portablePath}");
            }
        }
    }

    private static string ChangePortableExtension(string path, string extension)
    {
        var slash = path.LastIndexOf('/');
        var dir = slash >= 0 ? path[..(slash + 1)] : "";
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[..dot];
        }

        return dir + name + extension;
    }
}
