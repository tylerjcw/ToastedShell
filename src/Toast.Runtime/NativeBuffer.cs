using System.Runtime.InteropServices;
using System.Text;

namespace Tosh.Runtime;

public sealed class NativeBuffer : IDisposable
{
    private IntPtr _pointer;

    public NativeBuffer(int byteLength)
    {
        if (byteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength), "Native buffer size cannot be negative.");
        }

        _pointer = Marshal.AllocHGlobal(byteLength);
        ByteLength = byteLength;
        Clear();
    }

    ~NativeBuffer()
    {
        FreeCore();
    }

    public int ByteLength { get; }

    public bool OwnsMemory => true;

    public bool IsFreed => _pointer == IntPtr.Zero;

    public IntPtr Pointer
    {
        get
        {
            ThrowIfFreed();
            return _pointer;
        }
    }

    public nint Address => Pointer;

    public void Dispose()
    {
        FreeCore();
        GC.SuppressFinalize(this);
    }

    public void Clear()
    {
        ThrowIfFreed();

        for (var index = 0; index < ByteLength; index++)
        {
            Marshal.WriteByte(_pointer, index, 0);
        }
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes, int offset = 0)
    {
        ThrowIfFreed();
        ValidateRange(offset, bytes.Length);
        Marshal.Copy(bytes.ToArray(), 0, IntPtr.Add(_pointer, offset), bytes.Length);
    }

    public byte[] ReadBytes(int count, int offset = 0)
    {
        ThrowIfFreed();
        ValidateRange(offset, count);
        var bytes = new byte[count];
        Marshal.Copy(IntPtr.Add(_pointer, offset), bytes, 0, count);
        return bytes;
    }

    public void WriteCString(string? value, int offset = 0)
    {
        var bytes = NativeInteropUtilities.EncodeCString(value);
        WriteBytes(bytes, offset);
    }

    public string? ReadCString(int offset = 0)
    {
        ThrowIfFreed();

        if (offset < 0 || offset > ByteLength)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var remaining = ByteLength - offset;

        if (remaining == 0)
        {
            return string.Empty;
        }

        var bytes = ReadBytes(remaining, offset);
        var terminatorIndex = Array.IndexOf(bytes, (byte)0);

        if (terminatorIndex >= 0)
        {
            return NativeInteropUtilities.DecodeCString(bytes.AsSpan(0, terminatorIndex));
        }

        return NativeInteropUtilities.DecodeCString(bytes);
    }

    public override string ToString()
    {
        return IsFreed
            ? $"NativeBuffer(freed, {ByteLength} bytes)"
            : $"NativeBuffer(0x{Pointer.ToInt64():x}, {ByteLength} bytes)";
    }

    private void ValidateRange(int offset, int count)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        // Written as a subtraction rather than `offset + count > ByteLength`, which can
        // *overflow* and pass: `native-alloc` is script-reachable, so a script chooses
        // `ByteLength`, and a large offset and count sum to a negative number that satisfies
        // the check. The subtraction cannot overflow, because `ByteLength` is a length and
        // so non-negative while `offset` is already known to be — their difference always
        // fits. Note this is the only bound on `offset`: an offset past the end is caught
        // here rather than above, which is why it reports as a range error.
        if (count > ByteLength - offset)
        {
            throw new InvalidOperationException($"Buffer range {offset}..{(long)offset + count} exceeds native buffer size {ByteLength}.");
        }
    }

    private void ThrowIfFreed()
    {
        if (IsFreed)
        {
            throw new InvalidOperationException("This native buffer has already been freed.");
        }
    }

    private void FreeCore()
    {
        if (_pointer == IntPtr.Zero)
        {
            return;
        }

        Marshal.FreeHGlobal(_pointer);
        _pointer = IntPtr.Zero;
    }
}
