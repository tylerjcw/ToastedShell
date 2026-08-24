using Tosh.Runtime;

namespace Tosh.Language;

/// <summary>One parameter at a trait/interface member's type boundary.</summary>
internal readonly record struct ContractParameterType(string Name, string? TypeName);

/// <summary>The first declared-type disagreement between a contract and its implementation.</summary>
internal readonly record struct ContractMemberTypeMismatch(
    string What,
    string Expected,
    string Actual);

/// <summary>
/// Backend-neutral trait/interface member-type rules — <c>TOAST-0020</c>.
/// </summary>
internal static class ContractMemberTypeRules
{
    /// <summary>
    /// Returns the first mismatch using covariant returns and exact parameters.
    /// An undeclared type on either side constrains nothing.
    /// </summary>
    internal static ContractMemberTypeMismatch? FindMethodMismatch(
        IReadOnlyList<ContractParameterType> implementationParameters,
        string? implementationReturnTypeName,
        IReadOnlyList<ContractParameterType> contractParameters,
        string? contractReturnTypeName,
        Func<string, string, bool> isCovariantWith,
        Func<string, string, bool> namesSameType)
    {
        if (contractReturnTypeName is { Length: > 0 } expectedReturn
            && implementationReturnTypeName is { Length: > 0 } actualReturn
            && !isCovariantWith(actualReturn, expectedReturn))
        {
            return new ContractMemberTypeMismatch(
                "return type",
                expectedReturn,
                actualReturn);
        }

        var shared = Math.Min(contractParameters.Count, implementationParameters.Count);
        for (var index = 0; index < shared; index++)
        {
            var expectedParameter = contractParameters[index].TypeName;
            var actualParameter = implementationParameters[index].TypeName;
            if (expectedParameter is { Length: > 0 }
                && actualParameter is { Length: > 0 }
                && !namesSameType(actualParameter, expectedParameter))
            {
                return new ContractMemberTypeMismatch(
                    $"parameter '{implementationParameters[index].Name}'",
                    expectedParameter,
                    actualParameter);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns a writable property's invariant type mismatch, or null when either side is
    /// unannotated or both annotations name the same type.
    /// </summary>
    internal static ContractMemberTypeMismatch? FindPropertyMismatch(
        string propertyName,
        string? implementationTypeName,
        string? contractTypeName,
        Func<string, string, bool> namesSameType)
    {
        if (contractTypeName is not { Length: > 0 } expected
            || implementationTypeName is not { Length: > 0 } actual
            || namesSameType(actual, expected))
        {
            return null;
        }

        return new ContractMemberTypeMismatch(
            $"property '{propertyName}'",
            expected,
            actual);
    }

    /// <summary>Creates the identical source diagnostic for interpreter and compiler checks.</summary>
    internal static ToshDiagnostic CreateDiagnostic(
        string className,
        string contractName,
        string contractKind,
        string memberName,
        ContractMemberTypeMismatch mismatch,
        string? sourceName,
        string? sourceText,
        TextSpan span,
        ToshDiagnosticSeverity severity = ToshDiagnosticSeverity.Error)
        => new(
            Code: "tosh.runtime.contract_member_type_mismatch",
            Title: $"Class '{className}' implements '{contractName}.{memberName}' with an incompatible {mismatch.What}.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"the {contractKind} declares {mismatch.Expected}, the class declares {mismatch.Actual}",
            Help: mismatch.What == "return type"
                ? $"a class may return the type the {contractKind} declares, or one derived from it, but not an unrelated one."
                : $"this member must name the same type the {contractKind} declares. Only a return type may narrow.",
            Severity: severity);
}
