namespace Sgs.Collector.Decode;

// 每轮新建一个：SgsPayloadDecoder 不是线程安全的，WASM 内存固定 16 MB，所以轮末要释放
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

    public byte[] Decrypt(byte[] data)
    {
        lock (_lock)
        {
            if (_module is null)
            {
                throw new InvalidOperationException("aesresc 未就绪，无法解密");
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
