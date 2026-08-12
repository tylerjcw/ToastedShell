using Tosh.Compiler.IR;
using Tosh.Language.Parsing;

namespace Tosh.Language.Binding;

/// <summary>
/// Reads the members of a user-declared class or struct out of its declaration — <c>TS-P2-79</c>.
/// </summary>
/// <remarks>
/// <para>
/// The checker's member rules all worked from <c>targetType.ClrType</c>, and a ToastScript class
/// has none — <c>UserClassType.BackingClrType</c> is null until the declaration executes. So
/// every member check bailed at its first line and the three positions <c>TS-P2-22</c> left open
/// reported nothing at all: a method parameter, a constructor parameter, and a property
/// assignment.
/// </para>
/// <para>
/// What the checker needs was already there. <c>Lowerer.BuildUserTypeRegistry</c> harvests every
/// declaration into a <c>UserClassType</c>/<c>UserStructType</c> carrying the *syntax* node, so
/// the annotations are reachable without a runtime. This reads them, and nothing more: no
/// inheritance walk, because a base class is named as a string and resolving it needs the
/// registry the checker does not hold — a member declared on a base is therefore unknown here and
/// deliberately answered as "cannot tell" rather than "not found".
/// </para>
/// </remarks>
internal static class UserTypeMembers
{
    /// <summary>The declared members of <paramref name="type"/>, or null if it is not one.</summary>
    private static IReadOnlyList<ClassMemberSyntax>? MembersOf(BoundType type) => type switch
    {
        UserClassType { Definition: ClassDefinitionStatementSyntax cls } => cls.Members,
        UserStructType { Definition: StructDefinitionStatementSyntax str } => str.Members,
        _ => null,
    };

    /// <summary>True when <paramref name="type"/> is a user class or struct this can read.</summary>
    public static bool IsReadable(BoundType type) => MembersOf(type) is not null;

    /// <summary>
    /// True when the declaration says nothing definite about its members, so absence proves
    /// nothing.
    /// </summary>
    /// <remarks>
    /// A class that extends another, uses a trait, or is partial can carry members this
    /// declaration does not list. Reporting "not found" for those would be a false positive on
    /// perfectly good code, which is the one outcome a preview check must not produce.
    /// </remarks>
    public static bool MayHaveUnseenMembers(BoundType type) => type switch
    {
        UserClassType { Definition: ClassDefinitionStatementSyntax cls } =>
            cls.BaseClassName is not null ||
            cls.UsedTraits is { Count: > 0 } ||
            cls.ImplementedInterfaces is { Count: > 0 } ||
            cls.IsPartial,
        UserStructType { Definition: StructDefinitionStatementSyntax str } => str.IsPartial,
        _ => true,
    };

    /// <summary>
    /// The declaration-parameter fields of a struct — <c>TS-P2-79</c>.
    /// </summary>
    /// <remarks>
    /// <c>struct R(a: int, b: int)</c> puts its parameters in <c>Fields</c>, not <c>Members</c>,
    /// and they are ordinary readable members of the value. Reading only <c>Members</c> made
    /// <c>$r.a</c> report "Member 'a' was not found on type 'R'" against a struct that answers it
    /// perfectly well — a false positive shipped in the first cut of this check, missed because
    /// no swept script used that form with member access.
    /// </remarks>
    private static IReadOnlyList<RecordFieldDefinitionSyntax> FieldsOf(BoundType type) => type switch
    {
        UserStructType { Definition: StructDefinitionStatementSyntax str } => str.Fields,
        _ => Array.Empty<RecordFieldDefinitionSyntax>(),
    };

    public static bool TryGetProperty(BoundType type, string name, out ClassPropertyMemberSyntax property)
    {
        foreach (var member in MembersOf(type) ?? Array.Empty<ClassMemberSyntax>())
        {
            if (member is ClassPropertyMemberSyntax candidate &&
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate;
                return true;
            }
        }

        property = null!;
        return false;
    }

    /// <summary>Every method of <paramref name="name"/>, since ToastScript allows overloads.</summary>
    public static IReadOnlyList<ClassMethodMemberSyntax> GetMethods(BoundType type, string name)
    {
        var found = new List<ClassMethodMemberSyntax>();

        foreach (var member in MembersOf(type) ?? Array.Empty<ClassMemberSyntax>())
        {
            if (member is ClassMethodMemberSyntax candidate &&
                string.Equals(candidate.Method.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(candidate);
            }
        }

        return found;
    }

    /// <summary>
    /// The constructor parameter lists <paramref name="type"/> accepts — the primary constructor
    /// and every explicit one.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<FunctionParameterSyntax>> GetConstructors(BoundType type)
    {
        var found = new List<IReadOnlyList<FunctionParameterSyntax>>();

        if (type is UserClassType { Definition: ClassDefinitionStatementSyntax cls })
        {
            if (cls.PrimaryConstructorParameters.Count > 0)
            {
                found.Add(cls.PrimaryConstructorParameters);
            }

            foreach (var member in cls.Members)
            {
                if (member is ClassConstructorMemberSyntax ctor)
                {
                    found.Add(ctor.Parameters);
                }
            }
        }

        return found;
    }

    /// <summary>True when a name is declared at all, whatever kind of member it is.</summary>
    public static bool Declares(BoundType type, string name) =>
        TryGetProperty(type, name, out _) ||
        GetMethods(type, name).Count > 0 ||
        FieldsOf(type).Any(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase));
}
