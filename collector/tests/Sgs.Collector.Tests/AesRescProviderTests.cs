using Sgs.Collector.Decode;

namespace Sgs.Collector.Tests;

// 只测不需要 WASM 的两点：没有模块时的状态和错误，以及 SetModule 之后的状态。
// 解密器要到第一次成功的 Decrypt 才会创建，这里不会加载 WASM
public sealed class AesRescProviderTests
{
    [Fact]
    public void HasModule_IsFalseBeforeSetModuleIsCalled()
    {
        using var provider = new AesRescProvider();

        Assert.False(provider.HasModule);
    }

    [Fact]
    public void Decrypt_WithoutAModule_ThrowsInvalidOperationException()
    {
        using var provider = new AesRescProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.Decrypt([1, 2, 3]));

        Assert.Equal("aesresc 未就绪，无法解密", ex.Message);
    }

    [Fact]
    public void SetModule_MakesHasModuleTrue()
    {
        using var provider = new AesRescProvider();

        provider.SetModule([1, 2, 3]);

        Assert.True(provider.HasModule);
    }
}
