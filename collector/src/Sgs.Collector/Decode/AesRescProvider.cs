namespace Sgs.Collector.Decode;

// 每轮新建一个：SgsPayloadDecoder（旧版客户端的解密方式）不是线程安全的，WASM 内存固定 16 MB，所以轮末要释放
public sealed class AesRescProvider : IDisposable
{
    private readonly object _lock = new();
    private byte[]? _module;
    private SgsPayloadDecoder? _decoder;

    public bool HasModule
    {
        get
        {
            lock (_lock)
            {
                return _module is not null;
            }
        }
    }

    public void SetModule(byte[] bytes)
    {
        lock (_lock)
        {
            if (_module is not null && _module.AsSpan().SequenceEqual(bytes))
            {
                return;
            }

            _decoder?.Dispose();
            _decoder = null;
            _module = bytes;
        }
    }

    // 先按现行客户端的 AES-CFB 解密；结果不是 gzip 时再试旧版客户端的 aesresc WASM。
    // 两者都不成立时返回 AES-CFB 的结果，由 PackageDecoder 判定为解密失败
    public byte[] Decrypt(byte[] data)
    {
        var decrypted = SgsCipher.Decrypt(data);
        // 连同压缩方法字节一起判断，降低密钥不对时碰巧得到 gzip 头的概率
        if (decrypted.Length >= 3 && decrypted[0] == 0x1F && decrypted[1] == 0x8B && decrypted[2] == 0x08)
        {
            return decrypted;
        }

        lock (_lock)
        {
            if (_module is null)
            {
                return decrypted;
            }

            try
            {
                _decoder ??= new SgsPayloadDecoder(_module);
                return _decoder.OfbDec(data);
            }
            catch
            {
                // WASM trap 之后实例状态可能已损坏，丢弃它，下次调用时重建
                _decoder?.Dispose();
                _decoder = null;
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _decoder?.Dispose();
            _decoder = null;
        }
    }
}
