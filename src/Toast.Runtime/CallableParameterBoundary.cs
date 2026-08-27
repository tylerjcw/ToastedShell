namespace Tosh.Runtime;

/// <summary>
/// Portable callable-parameter nullability guard and diagnostic factory. Pure compiled
/// artifacts may reference <c>Tosh.Runtime</c>, but never the compiler host or language
/// engine, so the small part of annotation checking a CLR signature cannot express lives
/// here.
/// </summary>
public static class CallableParameterBoundary
{
    public static object? CheckNonNull(
        object? value,
        string typeName,
        int spanStart,
        int spanLength,
        string callableKind,
        string callableName,
        string parameterName,
        int argumentCount,
        string sourceName,
        string sourceText)
    {
        if (value is not null || AllowsNull(typeName)) return value;

        throw CreateConversionFailure(
            typeName,
            spanStart,
            spanLength,
            callableKind,
            callableName,
            parameterName,
            argumentCount,
            sourceName,
            sourceText);
    }

    public static ToshDiagnosticException CreateConversionFailure(
        string typeName,
        int spanStart,
        int spanLength,
        string callableKind,
        string callableName,
        string parameterName,
        int argumentCount,
        string sourceName,
        string sourceText)
    {
        var (code, title, label) = callableKind switch
        {
            "function-overload" => (
                "tosh.runtime.function_overload_not_found",
                $"No overload matched function '{callableName}' with {argumentCount} argument(s).",
                $"'{callableName}' does not have a matching overload"),
            "function" => (
                "tosh.runtime.parameter_type_conversion_failed",
                $"Argument '{parameterName}' could not be converted to '{typeName}'.",
                $"'{parameterName}' expects {typeName}"),
            "method" => (
                "tosh.runtime.expression_failed",
                $"No overload matched '{callableName}' with {argumentCount} argument(s).",
                (string?)null),
            "constructor" => (
                "tosh.runtime.constructor_parameter_type_conversion_failed",
                $"Constructor argument '{parameterName}' could not be converted to '{typeName}'.",
                $"'{parameterName}' expects {typeName}"),
            _ => (
                "tosh.runtime.parameter_type_conversion_failed",
                $"Argument '{parameterName}' could not be converted to '{typeName}'.",
                $"'{parameterName}' expects {typeName}"),
        };

        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: code,
            Title: title,
            SourceName: sourceName,
            SourceText: sourceText,
            Span: new TextSpan(spanStart, spanLength),
            Label: label));
    }

    private static bool AllowsNull(string typeName) =>
        typeName.EndsWith("?", StringComparison.Ordinal) ||
        string.Equals(typeName, "any", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(typeName, "dynamic", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(typeName, "void", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(typeName, "nothing", StringComparison.OrdinalIgnoreCase);
}
