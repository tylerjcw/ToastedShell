using System.Runtime.InteropServices;
using System.Text;

namespace Tosh.Runtime;

public static class NativeInteropUtilities
{
    public static bool IsSupportedInteropType(Type type, bool allowString = true)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type.IsEnum)
        {
            return IsSupportedInteropType(Enum.GetUnderlyingType(type), allowString: false);
        }

        return type == typeof(void) ||
               type == typeof(bool) ||
               type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong) ||
               type == typeof(char) ||
               type == typeof(float) ||
               type == typeof(double) ||
               (allowString && type == typeof(string)) ||
               type == typeof(IntPtr) ||
               type == typeof(UIntPtr) ||
               IsStructLayoutType(type);
    }

    public static bool IsStructLayoutType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (!type.IsValueType || type.IsPrimitive || type.IsEnum)
        {
            return false;
        }

        return type.StructLayoutAttribute is { Value: not LayoutKind.Auto };
    }

    public static int SizeOf(Type type)
    {
        return Marshal.SizeOf(type);
    }

    public static IntPtr ResolvePointer(object? value)
    {
        return value switch
        {
            NativeBuffer buffer => buffer.Pointer,
            IntPtr pointer => pointer,
            UIntPtr unsignedPointer => new IntPtr(unchecked((long)unsignedPointer.ToUInt64())),
            _ => throw new InvalidOperationException("Expected a native buffer or pointer-sized value."),
        };
    }

    public static object? CreateDefaultValue(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string))
        {
            return null;
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    public static object? ReadValue(Type type, IntPtr pointer)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(IntPtr))
        {
            return Marshal.ReadIntPtr(pointer);
        }

        if (type == typeof(UIntPtr))
        {
            var value = Marshal.ReadIntPtr(pointer);
            return new UIntPtr(unchecked((ulong)value.ToInt64()));
        }

        return Marshal.PtrToStructure(pointer, type);
    }

    public static void WriteValue(IntPtr pointer, object? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Cannot write a null native value.");
        }

        var type = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();

        if (type == typeof(IntPtr))
        {
            Marshal.WriteIntPtr(pointer, (IntPtr)value);
            return;
        }

        if (type == typeof(UIntPtr))
        {
            var unsignedPointer = (UIntPtr)value;
            Marshal.WriteIntPtr(pointer, new IntPtr(unchecked((long)unsignedPointer.ToUInt64())));
            return;
        }

        Marshal.StructureToPtr(value, pointer, fDeleteOld: false);
    }

    public static byte[] EncodeCString(string? value)
    {
        if (value is null)
        {
            return [0];
        }

        var encoding = OperatingSystem.IsWindows() ? Encoding.Default : Encoding.UTF8;
        var bytes = encoding.GetBytes(value);
        var terminated = new byte[bytes.Length + 1];
        bytes.CopyTo(terminated, 0);
        terminated[^1] = 0;
        return terminated;
    }

    public static string DecodeCString(ReadOnlySpan<byte> bytes)
    {
        var encoding = OperatingSystem.IsWindows() ? Encoding.Default : Encoding.UTF8;
        return encoding.GetString(bytes);
    }
}
