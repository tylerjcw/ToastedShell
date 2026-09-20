namespace Tosh.Language;

/// <summary>
/// What a built-in constraint means when the type argument is something the reader declared.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0055`. The built-in constraints are predicates over a CLR <see cref="Type"/>, and a
/// ToastScript class has none of its own — user classes share a backing type, so the bound
/// arrives null. Every registry check was therefore skipped, on the recorded ground that
/// precise enforcement happens at the next concrete instantiation. For a declared type that
/// instantiation never comes, so <c>where T: Numeric</c> accepted <c>new B&lt;Thing&gt;(…)</c>
/// — the constraint read as a constraint and enforced nothing at all.
/// </para>
/// <para>
/// A declaration answers most of these definitively. It is never one of the CLR numeric
/// primitives; a class is a reference and a struct or enum is a value; and whether it can be
/// added is a question about whether it says so, which is exactly what an operator overload
/// is. That last one is the point of the exercise: <c>where T: Add</c> is the constraint a
/// math or graphics type actually wants, and it could not be relied on for the very types
/// written to satisfy it.
/// </para>
/// <para>
/// A <c>null</c> answer means no opinion, and the caller stays conservative. Refusing what
/// cannot be checked would turn an unenforced constraint into a wrong error, which is the
/// failure the old rule existed to prevent — it was simply applied to everything rather than
/// to the cases that warrant it.
/// </para>
/// </remarks>
internal static class ToshDeclaredTypeConstraints
{
    /// <summary>The operator a constraint asks the declaration to overload.</summary>
    private static readonly Dictionary<string, string> _operatorConstraints =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Add"] = "+",
            ["Sub"] = "-",
            ["Mul"] = "*",
            ["Div"] = "/",
        };

    /// <summary>
    /// Whether a declared type satisfies a built-in constraint, or <c>null</c> for no opinion.
    /// </summary>
    internal static bool? Evaluate(string constraintName, object? definition)
    {
        if (definition is null || string.IsNullOrWhiteSpace(constraintName))
        {
            return null;
        }

        if (_operatorConstraints.TryGetValue(constraintName, out var symbol))
        {
            // A declaration that does not overload it cannot be added, subtracted, and so on.
            // Numeric fall-through does not apply here: nothing declared is a CLR primitive.
            return definition is ToshClassDefinition cls && DeclaresOperator(cls, symbol);
        }

        return constraintName.ToLowerInvariant() switch
        {
            // Nothing the reader declares is one of the CLR numeric primitives the registry
            // lists, so this is a definite no rather than something to be quiet about.
            "numeric" or "number" or "inumber" => false,

            // `Eq` is documented as always satisfied, and `notnull` is always true at runtime
            // because a CLR Type value is never the null literal. Declared types are no
            // different.
            "eq" or "notnull" => true,

            // Conservative on purpose: a declared value type may still hold references, and
            // this registry entry means a layout guarantee rather than a shape.
            "unmanaged" => false,

            "class" => definition is ToshClassDefinition or ToshRecordDefinition or ToshInterfaceDefinition,
            "struct" => definition is ToshStructDefinition or ToshEnumDefinition,

            // An enum member is `IComparable`; anything else has to say so by overloading a
            // comparison, the same way `Add` is answered.
            "comparable" => definition is ToshEnumDefinition
                || (definition is ToshClassDefinition comparable && DeclaresAnyOperator(comparable, "<", "<=", ">", ">=")),

            "new" or "new()" => definition switch
            {
                ToshClassDefinition constructible => constructible.IsConstructibleWithoutArguments,
                ToshEnumDefinition or ToshStructDefinition => true,
                _ => null,
            },

            _ => null,
        };
    }

    private static bool DeclaresAnyOperator(ToshClassDefinition definition, params string[] symbols)
    {
        foreach (var symbol in symbols)
        {
            if (DeclaresOperator(definition, symbol))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the class overloads an operator, itself or by inheritance.</summary>
    /// <remarks>
    /// Traits are deliberately not consulted: a trait body cannot declare an operator at all
    /// — `func +` inside one is refused by the parser with "Expected a variable name" — so
    /// there is nothing there to find. The author's own vector types are the case worth
    /// checking against, and they declare `func +(o) => $this.Combine($o, "+")` on the class
    /// while `uses Componentwise` supplies the ordinary method it delegates to.
    /// </remarks>
    private static bool DeclaresOperator(ToshClassDefinition definition, string symbol)
    {
        for (var current = definition; current is not null; current = current.BaseClass)
        {
            if (current.DeclaresInstanceMethodNamed(symbol))
            {
                return true;
            }
        }

        return false;
    }
}
