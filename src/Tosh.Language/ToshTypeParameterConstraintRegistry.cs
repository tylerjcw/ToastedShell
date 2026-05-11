using System.Numerics;

namespace Tosh.Language;

/// <summary>
/// Built-in trait-style constraints on generic type parameters.
/// Each constraint is a named predicate over a CLR <see cref="Type"/>.
/// </summary>
/// <remarks>
/// Constraints are matched by name (case-insensitive) and short-circuit
/// the runtime's strict-binding checks at instantiation time.
/// Unknown constraint names are silently skipped so that downstream
/// extensions (interface or class names) can be added without breaking
/// existing scripts; in the future this will be tightened into a
/// diagnostic.
/// </remarks>
public static class ToshTypeParameterConstraintRegistry
{
    private static readonly Dictionary<string, Func<Type, bool>> _builtins =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Numeric"] = IsNumeric,
            ["INumber"] = IsNumeric,
            ["Number"] = IsNumeric,
            ["Add"] = static t => HasOperator(t, "op_Addition") || IsNumeric(t),
            ["Sub"] = static t => HasOperator(t, "op_Subtraction") || IsNumeric(t),
            ["Mul"] = static t => HasOperator(t, "op_Multiply") || IsNumeric(t),
            ["Div"] = static t => HasOperator(t, "op_Division") || IsNumeric(t),
            ["Comparable"] = static t => typeof(IComparable).IsAssignableFrom(t),
            ["Eq"] = static _ => true,

            // Phase 4.7 — C#-style special constraints.
            // `new()`     — type has a public parameterless ctor.
            // `class`     — reference type (or interface).
            // `struct`    — non-nullable value type.
            // `notnull`   — value type or reference type (always true at runtime,
            //                since CLR Type values are never the null literal).
            // `unmanaged` — value type containing only unmanaged fields.
            ["new"] = HasParameterlessCtor,
            ["new()"] = HasParameterlessCtor,
            ["class"] = static t => !t.IsValueType,
            ["struct"] = static t => t.IsValueType && Nullable.GetUnderlyingType(t) is null,
            ["notnull"] = static _ => true,
            ["unmanaged"] = IsUnmanaged,
        };

    /// <summary>
    /// Tries to resolve a built-in constraint name to a predicate.
    /// </summary>
    public static bool TryGet(string name, out Func<Type, bool> predicate)
    {
        if (_builtins.TryGetValue(name, out var p))
        {
            predicate = p;
            return true;
        }
        predicate = static _ => true;
        return false;
    }

    /// <summary>
    /// Enumerates the names of every built-in constraint.
    /// </summary>
    public static IEnumerable<string> KnownNames => _builtins.Keys;

    private static readonly HashSet<Type> _numericTypes = new()
    {
        typeof(byte), typeof(sbyte),
        typeof(short), typeof(ushort),
        typeof(int), typeof(uint),
        typeof(long), typeof(ulong),
        typeof(float), typeof(double),
        typeof(decimal),
        typeof(Half),
        typeof(BigInteger),
    };

    private static bool IsNumeric(Type t) => _numericTypes.Contains(t);

    private static bool HasOperator(Type t, string opMethodName)
    {
        // op_Addition(t, t) -> t etc., either declared on the type or inherited.
        var methods = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        foreach (var m in methods)
        {
            if (!string.Equals(m.Name, opMethodName, StringComparison.Ordinal)) continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == t && ps[1].ParameterType == t)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasParameterlessCtor(Type t)
    {
        if (t.IsValueType) return true; // value types always have a default ctor
        var ctor = t.GetConstructor(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        return ctor is not null;
    }

    private static bool IsUnmanaged(Type t)
    {
        if (!t.IsValueType) return false;
        if (t.IsPrimitive || t.IsEnum || t.IsPointer) return true;
        // Decimal is the canonical example of a managed value type.
        if (t == typeof(decimal)) return false;
        // Any field whose type is not unmanaged disqualifies the struct.
        var fields = t.GetFields(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        foreach (var f in fields)
        {
            if (!IsUnmanaged(f.FieldType)) return false;
        }
        return true;
    }
}

/// <summary>
/// A parsed constraint clause attached to a type parameter on a class.
/// </summary>
public sealed record ToshTypeParameterConstraint(string TypeParameter, IReadOnlyList<string> ConstraintNames);
