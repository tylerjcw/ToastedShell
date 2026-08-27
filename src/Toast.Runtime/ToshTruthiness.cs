using System.Collections;
using System.Numerics;

namespace Tosh.Runtime;

/// <summary>
/// Defines ToastScript's canonical conversion from a shell value to a
/// truth value. Language, standard-library, and compiler surfaces must
/// delegate here rather than using CLR boolean conversion.
/// </summary>
public static class ToshTruthiness
{
    public static bool IsTruthy(object? value)
    {
        switch (value)
        {
            case null:
                return false;
            case bool boolean:
                return boolean;

            case byte number:
                return number != 0;
            case sbyte number:
                return number != 0;
            case short number:
                return number != 0;
            case ushort number:
                return number != 0;
            case int number:
                return number != 0;
            case uint number:
                return number != 0;
            case long number:
                return number != 0;
            case ulong number:
                return number != 0;
            case Int128 number:
                return number != 0;
            case UInt128 number:
                return number != 0;
            case IntPtr pointer:
                return pointer != IntPtr.Zero;
            case UIntPtr pointer:
                return pointer != UIntPtr.Zero;
            case BigInteger number:
                return number != BigInteger.Zero;

            case Half number:
                return !Half.IsNaN(number) && number != (Half)0;
            case float number:
                return !float.IsNaN(number) && number != 0f;
            case double number:
                return !double.IsNaN(number) && number != 0d;
            case decimal number:
                return number != 0m;
            case Complex number:
                return !double.IsNaN(number.Real) &&
                       !double.IsNaN(number.Imaginary) &&
                       number != Complex.Zero;

            case string text:
                return text.Length > 0;
            case ICollection collection:
                return collection.Count > 0;
            case IEnumerable enumerable:
                return HasAny(enumerable);
            default:
                return true;
        }
    }

    private static bool HasAny(IEnumerable enumerable)
    {
        var enumerator = enumerable.GetEnumerator();
        try
        {
            return enumerator.MoveNext();
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }
}
