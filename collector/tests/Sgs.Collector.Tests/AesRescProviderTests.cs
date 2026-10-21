using Sgs.Collector.Decode;

namespace Sgs.Collector.Tests;

// 只测不需要 WASM 的部分：AES-CFB 解密、没有模块时的行为，以及 SetModule 之后的状态。
// WASM 解密器要到 AES-CFB 结果不是 gzip 时才会创建，这里不会加载 WASM
public sealed class AesRescProviderTests
{
    [Fact]
    public void HasModule_IsFalseBeforeSetModuleIsCalled()
    {
        using var provider = new AesRescProvider();

        Assert.False(provider.HasModule);
    }

    [Fact]
    public void Decrypt_AesCfbEncryptedGzip_ReturnsTheGzipEvenWithAnInvalidModule()
    {
        using var provider = new AesRescProvider();
        provider.SetModule([1, 2, 3]); // 不是合法的 WASM：AES-CFB 成功时不会用到它
        byte[] gzip = [0x1F, 0x8B, 0x08, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E];

        Assert.Equal(gzip, provider.Decrypt(CfbEncrypt(gzip)));
    }

    [Fact]
    public void Decrypt_NotAesCfbWithoutAModule_ReturnsTheAesCfbOutput()
    {
        using var provider = new AesRescProvider();
        byte[] data = [0x1F, 0x8B, 0x08, 0x00, 0x01, 0x02, 0x03];

        Assert.Equal(SgsCipher.Decrypt(data), provider.Decrypt(data));
    }

    private static byte[] CfbEncrypt(byte[] data)
    {
        var padded = new byte[(data.Length / 16 + 1) * 16];
        data.CopyTo(padded, 0);
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = SgsCipher.Key;
        return aes.EncryptCfb(padded, SgsCipher.Iv, System.Security.Cryptography.PaddingMode.None, 128)[..data.Length];
    }

    [Fact]
    public void SetModule_MakesHasModuleTrue()
    {
        using var provider = new AesRescProvider();

        provider.SetModule([1, 2, 3]);

        Assert.True(provider.HasModule);
    }
}
