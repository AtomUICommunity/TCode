using System.Runtime.InteropServices;
using System.Text;

namespace TKCode.Terminal.Pty.Unix;

/// <summary>
/// 把字符串数组编码成一块原生内存：UTF-8 字符串区 + 以 NULL 结尾的 <c>char*[]</c>。
/// 不使用运行时封送（DisableRuntimeMarshalling），调用结束后 Dispose。
/// </summary>
internal sealed unsafe class NativeStringBlock : IDisposable
{
    private readonly List<nint> _allocations = [];

    public byte* Add(string? value)
    {
        if (value is null)
        {
            return null;
        }
        if (value.Contains('\0'))
        {
            throw new ArgumentException("Strings passed to the PTY layer cannot contain NUL.");
        }
        var count = Encoding.UTF8.GetByteCount(value);
        var p = (byte*)NativeMemory.Alloc((nuint)count + 1);
        _allocations.Add((nint)p);
        Encoding.UTF8.GetBytes(value, new Span<byte>(p, count));
        p[count] = 0;
        return p;
    }

    public byte** AddArray(IEnumerable<string> values)
    {
        var list = values as IReadOnlyList<string> ?? values.ToList();
        var array = (byte**)NativeMemory.Alloc((nuint)(list.Count + 1), (nuint)sizeof(byte*));
        _allocations.Add((nint)array);
        for (var i = 0; i < list.Count; i++)
        {
            array[i] = Add(list[i]);
        }
        array[list.Count] = null;
        return array;
    }

    public void Dispose()
    {
        foreach (var p in _allocations)
        {
            NativeMemory.Free((void*)p);
        }
        _allocations.Clear();
    }
}
