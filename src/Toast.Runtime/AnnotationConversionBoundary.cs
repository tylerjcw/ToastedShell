namespace Tosh.Runtime;

/// <summary>
/// Portable annotation conversion for a type the compiler has already resolved to a CLR
/// type. Pure compiled artifacts can use this boundary without initializing the language
/// engine or the compiler host.
/// </summary>
public static class AnnotationConversionBoundary
{
    public static object? ConvertKnownType(
        object? value,
        Type targetType,
        string typeName,
        int spanStart,
        int spanLength,
        string owner,
        string sourceName,
        string sourceText)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        var allowsNull = typeName.EndsWith("?", StringComparison.Ordinal);
        if (value is null)
        {
            if (allowsNull)
            {
                return null;
            }

            throw CreateFailure(value, targetType, typeName, spanStart, spanLength, owner, sourceName, sourceText);
        }

        if (TypeConversion.TryConvert(value, targetType, out var converted))
        {
            return converted;
        }

        throw CreateFailure(value, targetType, typeName, spanStart, spanLength, owner, sourceName, sourceText);
    }

    private static ToshDiagnosticException CreateFailure(
        object? value,
        Type targetType,
        string typeName,
        int spanStart,
        int spanLength,
        string owner,
        string sourceName,
        string sourceText)
    {
        if (TypeConversion.WouldTruncate(value, targetType))
        {
            return ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.annotation_conversion_failed",
                Title: $"'{owner}' produced {value}, which cannot become '{typeName}' without discarding its fractional part.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: new TextSpan(spanStart, spanLength),
                Label: "round first with Math.Round, Math.Floor, Math.Ceiling or Math.Truncate"));
        }

        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.annotation_conversion_failed",
            Title: $"'{owner}' produced a value that could not be converted to '{typeName}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: new TextSpan(spanStart, spanLength),
            Label: $"the value does not match '{typeName}'"));
    }
}
