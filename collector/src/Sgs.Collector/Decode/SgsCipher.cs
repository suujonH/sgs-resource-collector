using System.Security.Cryptography;

namespace Sgs.Collector.Decode;

// 现行客户端（sgsGame 中的 Ofb_Dec）用内置密钥的 AES-128-CFB（分段 16 字节）解密 res/config、res/proto 下的资源。
// 密钥与 IV 取自客户端代码；站点更换密钥后，解密结果通不过 PackageDecoder 的校验，成员会记为失败
internal static class SgsCipher
{
    internal static readonly byte[] Key = [21, 51, 69, 107, 85, 94, 55, 120, 97, 78, 63, 45, 114, 101, 81, 62];
    internal static readonly byte[] Iv = [144, 101, 62, 55, 36, 70, 86, 69, 35, 117, 67, 33, 109, 85, 97, 226];

    public static byte[] Decrypt(byte[] data)
    {
        // 与客户端一致：补零到 16 字节的整数倍再解密，最后截回原长度
        var padded = new byte[(data.Length / 16 + 1) * 16];
        data.CopyTo(padded, 0);
        using var aes = Aes.Create();
        aes.Key = Key;
        return aes.DecryptCfb(padded, Iv, PaddingMode.None, 128)[..data.Length];
    }
}
