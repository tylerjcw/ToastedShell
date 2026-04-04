using System.Runtime.InteropServices;

namespace Tosh.Tests;

[StructLayout(LayoutKind.Sequential)]
public struct NativePoint
{
    public NativePoint(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeTimeVal
{
    public long tv_sec;
    public long tv_usec;
}
