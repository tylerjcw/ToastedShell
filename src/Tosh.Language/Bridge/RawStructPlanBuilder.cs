using System.Runtime.InteropServices;

using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Language.Bridge;

/// <summary>
/// Turns a parsed <c>raw struct</c> declaration into the shared
/// <see cref="RawStructLayoutPlan"/> that both the runtime factory and (later)
/// the compiler's emitter consume.
///
/// Keeping the layout <em>decision</em> here — rather than inside either
/// emitter — is what stops the interpreted and compiled tiers from growing two
/// subtly different layout algorithms. That divergence would be near-invisible:
/// both tiers would run, and only one would read the right bytes.
/// </summary>
internal static class RawStructPlanBuilder
{
    public static RawStructLayoutPlan Build(
        RawStructDefinitionStatementSyntax syntax,
        Func<string, Type?> resolveNamedType,
        string sourceName,
        string sourceText)
    {
        var fields = new List<RawStructFieldPlan>(syntax.Fields.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in syntax.Fields)
        {
            // Two fields with one name emitted two CLR fields and shifted the
            // layout silently — always a typo, never intent.
            if (!seen.Add(field.Name))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.raw_struct_duplicate_field",
                    Title: $"Raw struct '{syntax.Name}' declares '{field.Name}' more than once.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: field.Span,
                    Label: "each field name must be unique",
                    Help: "Field names are matched case-insensitively, as member access is."));
            }

            fields.Add(BuildField(syntax.Name, field, resolveNamedType, sourceName, sourceText));
        }

        return new RawStructLayoutPlan(
            syntax.Name,
            syntax.IsUnion ? LayoutKind.Explicit : LayoutKind.Sequential,
            fields,
            syntax.Pack,
            syntax.DeclaredSize);
    }

    private static RawStructFieldPlan BuildField(
        string structName,
        RawStructFieldSyntax field,
        Func<string, Type?> resolveNamedType,
        string sourceName,
        string sourceText)
    {
        var typeName = field.TypeName.Trim();

        // `bool` is the single most common FFI footgun: default CLR marshalling
        // makes it a 4-byte Win32 BOOL, not C `_Bool`. Rejecting it costs three
        // lines and prevents a whole class of silently-wrong layouts.
        if (string.Equals(typeName, "bool", StringComparison.OrdinalIgnoreCase))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.raw_struct_bool_field",
                Title: $"Raw struct '{structName}' field '{field.Name}' cannot be 'bool'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: field.Span,
                Label: "use 'byte' for a C `_Bool`",
                Help: "Default marshalling maps `bool` to a 4-byte Win32 BOOL, which is not what a C `_Bool` occupies."));
        }

        // An inline char buffer: `cstring[65]` is `char name[65]`, sitting in the
        // struct rather than pointing at one.
        if (NativeTypeLexicon.IsCStringName(typeName))
        {
            if (field.ArrayLength is not { } charCount)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.raw_struct_cstring_requires_length",
                    Title: $"Raw struct '{structName}' field '{field.Name}' needs an inline buffer length.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: field.Span,
                    Label: "write something like 'cstring[65]'",
                    Help: "A bare `cstring` would be a pointer. `char name[65]` and `char name[256]` are different " +
                          "layouts, so the count is part of the ABI and cannot be inferred."));
            }

            return new RawStructFieldPlan(
                field.Name,
                typeof(string),
                UnmanagedType.ByValTStr,
                charCount);
        }

        var elementType = ResolveFieldType(structName, field, typeName, resolveNamedType, sourceName, sourceText);

        if (field.ArrayLength is not { } count)
        {
            return new RawStructFieldPlan(field.Name, elementType);
        }

        if (!TryMapArraySubType(elementType, out var subType))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.raw_struct_unsupported_array_element",
                Title: $"Raw struct '{structName}' field '{field.Name}' cannot be an array of '{typeName}'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: field.Span,
                Label: "inline arrays support scalar element types",
                Help: "Use a scalar element type, or `cstring[n]` for an inline char buffer."));
        }

        return new RawStructFieldPlan(
            field.Name,
            elementType.MakeArrayType(),
            UnmanagedType.ByValArray,
            count,
            subType);
    }

    private static Type ResolveFieldType(
        string structName,
        RawStructFieldSyntax field,
        string typeName,
        Func<string, Type?> resolveNamedType,
        string sourceName,
        string sourceText)
    {
        // Scalars first, then anything nameable in scope — which is how a
        // nested raw struct (`addr: SockAddr`) resolves, since those register
        // their emitted CLR type under their declared name.
        if (NativeTypeLexicon.TryResolveScalar(typeName, out var scalar) && scalar != typeof(void))
        {
            return scalar;
        }

        var resolved = resolveNamedType(typeName);

        if (resolved is not null && NativeTypeLexicon.IsSupportedInteropType(resolved, allowString: false))
        {
            return resolved;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.raw_struct_unsupported_field_type",
            Title: $"Raw struct '{structName}' field '{field.Name}' has an unsupported type '{typeName}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: field.Span,
            Label: $"'{typeName}' cannot sit in a native layout",
            Help: "Use a scalar type, a pointer type (`ptr`/`nint`), an inline buffer (`cstring[n]`), " +
                  "or another `raw struct`."));
    }

    private static bool TryMapArraySubType(Type elementType, out UnmanagedType subType)
    {
        subType = default;

        if (elementType == typeof(byte)) subType = UnmanagedType.U1;
        else if (elementType == typeof(sbyte)) subType = UnmanagedType.I1;
        else if (elementType == typeof(short)) subType = UnmanagedType.I2;
        else if (elementType == typeof(ushort)) subType = UnmanagedType.U2;
        else if (elementType == typeof(int)) subType = UnmanagedType.I4;
        else if (elementType == typeof(uint)) subType = UnmanagedType.U4;
        else if (elementType == typeof(long)) subType = UnmanagedType.I8;
        else if (elementType == typeof(ulong)) subType = UnmanagedType.U8;
        else if (elementType == typeof(float)) subType = UnmanagedType.R4;
        else if (elementType == typeof(double)) subType = UnmanagedType.R8;
        else if (elementType == typeof(char)) subType = UnmanagedType.U2;
        else if (elementType == typeof(IntPtr)) subType = UnmanagedType.SysInt;
        else if (elementType == typeof(UIntPtr)) subType = UnmanagedType.SysUInt;
        else if (NativeInteropUtilities.IsStructLayoutType(elementType))
        {
            // `struct foo bar[4]` — an inline array of structs is ordinary C.
            subType = UnmanagedType.Struct;
        }
        else return false;

        return true;
    }
}
