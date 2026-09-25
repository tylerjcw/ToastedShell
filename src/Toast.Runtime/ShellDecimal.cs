using System.Globalization;

namespace Tosh.Runtime;

/// <summary>
/// What a floating value means when it is asked for as a <see cref="decimal"/>.
/// </summary>
/// <remarks>
/// <para>
/// The language defines this conversion rather than inheriting it, because the platform's
/// answer has changed underneath it. Through .NET 10, <c>(decimal)aDouble</c> rounded to 15
/// significant digits; .NET 11 writes the double's full binary value instead, so
/// <c>483.06</c> became <c>483.0600000000000023</c> and <c>0.1 as decimal == 0.1</c> went
/// from true to false. Neither is a bug in the platform — but a script's arithmetic must not
/// depend on which runtime the shell was built against, and the project's whole framework
/// version is meant to be one string to change.
/// </para>
/// <para>
/// The rule here is the double's <em>shortest round-trippable</em> spelling, read as a
/// decimal. <c>0.1</c> is the shortest spelling that reads back as that double, so
/// <c>0.1 as decimal</c> is <c>0.1m</c> — the number the author wrote, not the binary
/// approximation that stood in for it.
/// </para>
/// <para>
/// This keeps the property the 15-digit round broke. Round-tripping is injective: two doubles
/// that differ have different shortest spellings, so they get different decimals and never
/// compare equal. Rounding to 15 digits collapsed <c>0.3</c> and <c>0.30000000000000004</c>
/// onto one value — a fold-versus-runtime disagreement the constant folder once had — and made <c>2.718281828459045</c> look like it
/// carried digits its double had not kept (<c>ToshLexer.CarriesMorePrecisionThanDouble</c>).
/// </para>
/// </remarks>
public static class ShellDecimal
{
    /// <summary>
    /// The decimal a <see cref="double"/> denotes, or <see langword="false"/> where it
    /// denotes none.
    /// </summary>
    /// <remarks>
    /// NaN and the infinities have no decimal at all, and a value outside decimal's range —
    /// <c>1e300</c> — has none either. Both answer <see langword="false"/> rather than
    /// throwing, so a caller can fall through to whatever it does with a value it cannot
    /// convert.
    /// </remarks>
    public static bool TryFromDouble(double value, out decimal result)
    {
        result = default;

        return !double.IsNaN(value)
            && !double.IsInfinity(value)
            && decimal.TryParse(
                value.ToString("R", CultureInfo.InvariantCulture),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result);
    }

    /// <summary>
    /// The decimal a <see cref="float"/> denotes.
    /// </summary>
    /// <remarks>
    /// Round-tripped as a <see cref="float"/> rather than widened to <see cref="double"/>
    /// first. <c>0.1f</c> widens to <c>0.10000000149011612</c>, and those extra digits are an
    /// artefact of the widening — the value was only ever precise to a float.
    /// </remarks>
    public static bool TryFromSingle(float value, out decimal result)
    {
        result = default;

        return !float.IsNaN(value)
            && !float.IsInfinity(value)
            && decimal.TryParse(
                value.ToString("R", CultureInfo.InvariantCulture),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result);
    }

    /// <summary>
    /// The decimal any numeric value denotes, floating or not.
    /// </summary>
    /// <remarks>
    /// The integral types convert exactly and need no rule of ours, so they are left to
    /// <see cref="Convert"/>. Only the two floating types have a spelling to choose.
    /// </remarks>
    public static bool TryFrom(object? value, out decimal result)
    {
        switch (value)
        {
            case double floating:
                return TryFromDouble(floating, out result);

            case float single:
                return TryFromSingle(single, out result);

            case decimal already:
                result = already;
                return true;

            case byte or sbyte or short or ushort or int or uint or long or ulong:
                result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return true;

            default:
                result = default;
                return false;
        }
    }

    /// <summary>
    /// The decimal a <see cref="double"/> denotes, throwing where it denotes none.
    /// </summary>
    /// <remarks>
    /// For the callers that have already established the value is finite and in range, and
    /// for which returning some other number would be worse than failing.
    /// </remarks>
    public static decimal FromDouble(double value)
        => TryFromDouble(value, out var result)
            ? result
            : throw new OverflowException(
                $"The value {value.ToString("R", CultureInfo.InvariantCulture)} has no decimal representation.");
}
