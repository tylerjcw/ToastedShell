using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

using Tosh.Runtime;

namespace Tosh.Language.Bridge;

/// <summary>
/// The single dynamic assembly backing runtime native interop. Delegate types
/// and raw-struct types are co-located deliberately: delegate signatures
/// reference emitted struct types, and splitting them across two dynamic
/// assemblies invites resolution surprises for no benefit.
/// </summary>
internal static class NativeInteropModule
{
    private static readonly AssemblyBuilder Assembly = AssemblyBuilder.DefineDynamicAssembly(
        new AssemblyName("Tosh.NativeInterop"),
        AssemblyBuilderAccess.Run);

    public static readonly ModuleBuilder Module = Assembly.DefineDynamicModule("Tosh.NativeInterop");
}

/// <summary>
/// Emits a real sequential-layout, blittable CLR type from a
/// <see cref="RawStructLayoutPlan"/>.
///
/// This is the keystone of fluent native interop: TōSh <c>struct</c> declarations
/// produce a dictionary-backed object model that never becomes a
/// <see cref="Type"/>, and the compiler's struct shells are <c>AutoLayout</c>
/// with every field typed <c>object</c>. Neither can cross the native boundary,
/// so <c>raw struct</c> emits its own type here.
///
/// Modelled on <c>NativeDelegateTypeFactory</c>, which established the
/// DefineType + CustomAttributeBuilder + structural-cache pattern in this
/// codebase.
/// </summary>
internal static class NativeStructTypeFactory
{
    private static readonly ConcurrentDictionary<string, Type> Cache = new(StringComparer.Ordinal);
    private static int _nextTypeId;

    /// <summary>
    /// Caching is keyed by <see cref="RawStructLayoutPlan.StructuralKey"/>, never
    /// by name. Two declarations with identical layout are the same type, and a
    /// module that is required twice — or a file re-sourced in the REPL — must
    /// not mint a second, incompatible type with the same name. That failure
    /// surfaces as a <c>Marshal.StructureToPtr</c> error naming neither cause.
    /// </summary>
    public static Type GetOrCreate(RawStructLayoutPlan plan) =>
        Cache.GetOrAdd(plan.StructuralKey, _ => Create(plan));

    private static Type Create(RawStructLayoutPlan plan)
    {
        var attributes =
            TypeAttributes.Public |
            TypeAttributes.Sealed |
            TypeAttributes.AnsiClass |
            (plan.Kind == LayoutKind.Explicit
                ? TypeAttributes.ExplicitLayout
                : TypeAttributes.SequentialLayout);

        var typeName = $"Tosh.Native.{Sanitize(plan.Name)}_{Interlocked.Increment(ref _nextTypeId)}";

        var typeBuilder = plan.Pack is { } pack
            ? NativeInteropModule.Module.DefineType(typeName, attributes, typeof(ValueType), (PackingSize)pack)
            : NativeInteropModule.Module.DefineType(typeName, attributes, typeof(ValueType));

        foreach (var field in plan.Fields)
        {
            var fieldBuilder = typeBuilder.DefineField(field.Name, field.ClrType, FieldAttributes.Public);

            if (field.MarshalAs is { } marshalAs)
            {
                fieldBuilder.SetCustomAttribute(BuildMarshalAs(marshalAs, field.SizeConst, field.ArraySubType));
            }

            // A union is every field at offset zero.
            if (plan.Kind == LayoutKind.Explicit)
            {
                fieldBuilder.SetOffset(0);
            }
        }

        var created = typeBuilder.CreateType()
                      ?? throw new InvalidOperationException($"Failed to create raw struct type '{plan.Name}'.");

        VerifyLayout(plan, created);
        return created;
    }

    private static CustomAttributeBuilder BuildMarshalAs(UnmanagedType marshalAs, int? sizeConst, UnmanagedType? arraySubType)
    {
        var constructor = typeof(MarshalAsAttribute).GetConstructor([typeof(UnmanagedType)])
                          ?? throw new InvalidOperationException("Unable to locate MarshalAsAttribute constructor.");

        var fields = new List<FieldInfo>();
        var values = new List<object?>();

        if (sizeConst is { } size)
        {
            fields.Add(typeof(MarshalAsAttribute).GetField(nameof(MarshalAsAttribute.SizeConst))!);
            values.Add(size);
        }

        if (arraySubType is { } subType)
        {
            fields.Add(typeof(MarshalAsAttribute).GetField(nameof(MarshalAsAttribute.ArraySubType))!);
            values.Add(subType);
        }

        return new CustomAttributeBuilder(constructor, [marshalAs], [.. fields], [.. values]);
    }

    /// <summary>
    /// Two checks, both cheap, both converting a silent memory-corruption bug
    /// into a diagnostic at declaration time.
    /// </summary>
    private static void VerifyLayout(RawStructLayoutPlan plan, Type created)
    {
        int actualSize;

        try
        {
            actualSize = Marshal.SizeOf(created);
        }
        catch (Exception exception)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.raw_struct_not_blittable",
                Title: $"Raw struct '{plan.Name}' does not have a valid native layout.",
                Label: "one of its fields cannot be marshalled",
                Help: exception.Message));
        }

        // `size n` is an optional assertion, not a requirement — declarations
        // never restate padding, so the runtime computes the real size.
        if (plan.DeclaredSize is { } declared && declared != actualSize)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.raw_struct_size_mismatch",
                Title: $"Raw struct '{plan.Name}' is {actualSize} bytes, but 'size {declared}' was declared.",
                Label: $"the declared size is off by {Math.Abs(actualSize - declared)} byte(s)",
                Help: "Sequential layout aligns fields naturally, so declarations should not restate the C header's " +
                      "`pad` / `__pad0` members. Remove them, or correct the declared size."));
        }
    }

    private static string Sanitize(string name)
    {
        var chars = name.Where(static c => char.IsLetterOrDigit(c) || c == '_').ToArray();
        return chars.Length == 0 ? "RawStruct" : new string(chars);
    }
}
