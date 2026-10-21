using System.IO.Hashing;

namespace Sgs.Collector.Common;

internal static class ContentHasher
{
    // 全项目唯一的哈希入口：xxHash128，固定 32 位小写十六进制
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(XxHash128.Hash(bytes));
}
