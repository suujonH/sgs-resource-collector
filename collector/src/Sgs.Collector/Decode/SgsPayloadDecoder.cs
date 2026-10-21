using Wasmtime;

namespace Sgs.Collector.Decode;

public sealed class SgsPayloadDecoder : IDisposable
{
    private const int ArrayBufferId = 0;
    private const int IdOffset = -8;
    private const int ArrayBufferView = 1 << 0;
    private const int Array = 1 << 1;
    private const int StaticArray = 1 << 2;
    private const int ValAlignOffset = 6;
    private const int ArrayBufferViewBufferOffset = 0;
    private const int ArrayBufferViewDataStartOffset = 4;
    private const int ArrayBufferViewDataLengthOffset = 8;
    private const int ArrayLengthOffset = 12;
    private const int ArrayBufferViewSize = 12;
    private const int ArraySize = 16;
    private const int SizeOffset = -4;
    private const int ValSigned = 1 << 11;
    private const int ValFloat = 1 << 12;

    private readonly Engine _engine;
    private readonly Module _module;
    private readonly Store _store;
    private readonly Linker _linker;
    private readonly Instance _instance;
    private readonly Memory _memory;
    private readonly Func<int, int, int> _new;
    private readonly Func<int, int> _retain;
    private readonly Action<int> _release;
    private readonly Func<int, int, int, int, int> _constructor;
    private readonly Func<int, int, int> _decrypt;
    private readonly int _rttiBase;
    private readonly int _uint8ArrayId;

    public SgsPayloadDecoder(byte[] wasmBytes)
    {
        _engine = new Engine();
        _module = Module.FromBytes(_engine, "aesresc", wasmBytes);
        _store = new Store(_engine);
        _linker = new Linker(_engine);

        var importedMemory = new Memory(_store, 256, 256, false);
        _linker.Define("env", "memory", importedMemory);
        _linker.Define("env", "abort", Function.FromCallback<int, int, int, int>(_store, (_, _, _, _) => { }));

        _instance = _linker.Instantiate(_store, _module);
        _memory = _instance.GetMemory("memory") ?? importedMemory;
        _new = _instance.GetFunction<int, int, int>("__new") ?? throw new InvalidOperationException("__new export not found.");
        _retain = _instance.GetFunction<int, int>("__retain") ?? throw new InvalidOperationException("__retain export not found.");
        _release = _instance.GetAction<int>("__release") ?? throw new InvalidOperationException("__release export not found.");
        _constructor = _instance.GetFunction<int, int, int, int, int>("CFBDecryptor#constructor") ?? throw new InvalidOperationException("CFBDecryptor#constructor export not found.");
        _decrypt = _instance.GetFunction<int, int, int>("CFBDecryptor#decrypt") ?? throw new InvalidOperationException("CFBDecryptor#decrypt export not found.");
        _rttiBase = Convert.ToInt32(_instance.GetGlobal("__rtti_base")?.GetValue() ?? throw new InvalidOperationException("__rtti_base export not found."));
        _uint8ArrayId = Convert.ToInt32(_instance.GetGlobal("Uint8Array_ID")?.GetValue() ?? throw new InvalidOperationException("Uint8Array_ID export not found."));
    }

    public byte[] OfbDec(byte[] data)
    {
        var len = data.Length;
        var count = 16 - len % 16;
        var padded = new byte[len + count];
        Buffer.BlockCopy(data, 0, padded, 0, len);

        var arrPtr = _retain(NewArray(_uint8ArrayId, padded));
        var decryptor = _constructor(0, 0, 0, 16);
        var resultPtr = _decrypt(decryptor, arrPtr);
        _release(arrPtr);
        var result = GetArrayBytes(resultPtr);
        _release(resultPtr);

        if (result.Length == len)
        {
            return result;
        }

        var trimmed = new byte[len];
        Buffer.BlockCopy(result, 0, trimmed, 0, Math.Min(len, result.Length));
        return trimmed;
    }

    private int NewArray(int id, byte[] values)
    {
        var info = GetArrayInfo(id);
        var align = GetValueAlign(info);
        if (align != 0 || (info & ValFloat) != 0)
        {
            throw new InvalidOperationException("Only Uint8Array values are supported.");
        }

        var length = values.Length;
        var buf = _new(length << align, (info & StaticArray) != 0 ? id : ArrayBufferId);
        int result;
        if ((info & StaticArray) != 0)
        {
            result = buf;
        }
        else
        {
            var arr = _new((info & Array) != 0 ? ArraySize : ArrayBufferViewSize, id);
            WriteInt32(arr + ArrayBufferViewBufferOffset, _retain(buf));
            WriteInt32(arr + ArrayBufferViewDataStartOffset, buf);
            WriteInt32(arr + ArrayBufferViewDataLengthOffset, length << align);
            if ((info & Array) != 0)
            {
                WriteInt32(arr + ArrayLengthOffset, length);
            }

            result = arr;
        }

        values.CopyTo(_memory.GetSpan(buf, values.Length));
        return result;
    }

    private byte[] GetArrayBytes(int arr)
    {
        var id = ReadInt32(arr + IdOffset);
        var info = GetArrayInfo(id);
        var align = GetValueAlign(info);
        if (align != 0 || (info & ValFloat) != 0 || (info & ValSigned) != 0)
        {
            throw new InvalidOperationException("Only Uint8Array result values are supported.");
        }

        var buf = (info & StaticArray) != 0 ? arr : ReadInt32(arr + ArrayBufferViewDataStartOffset);
        var length = (info & Array) != 0
            ? ReadInt32(arr + ArrayLengthOffset)
            : ReadInt32(buf + SizeOffset) >> align;

        return _memory.GetSpan(buf, length).ToArray();
    }

    private uint GetArrayInfo(int id)
    {
        var info = GetInfo(id);
        if ((info & (ArrayBufferView | Array | StaticArray)) == 0)
        {
            throw new InvalidOperationException("Wasm value is not an array.");
        }

        return info;
    }

    private uint GetInfo(int id)
    {
        var count = ReadUInt32(_rttiBase);
        if ((uint)id >= count)
        {
            throw new InvalidOperationException("Wasm RTTI id is out of range.");
        }

        return ReadUInt32(_rttiBase + 4 + id * 8);
    }

    private static int GetValueAlign(uint info)
    {
        var value = (int)((info >> ValAlignOffset) & 31);
        return 31 - int.LeadingZeroCount(value);
    }

    private int ReadInt32(int address) => _memory.ReadInt32(address);

    private uint ReadUInt32(int address) => unchecked((uint)_memory.ReadInt32(address));

    private void WriteInt32(int address, int value) => _memory.WriteInt32(address, value);

    public void Dispose()
    {
        _module.Dispose();
        _linker.Dispose();
        _store.Dispose();
        _engine.Dispose();
    }
}
