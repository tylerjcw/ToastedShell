using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Language.Binding.BoundNodes;

/// <summary>
/// Static type information attached to a <see cref="BoundExpression"/>.
/// ToastScript is dynamic by default; the binder fills concrete types
/// in only when it can do so cheaply and unambiguously (literals,
/// well-typed arithmetic, range start/step inference, etc.). Everything
/// else stays <see cref="Dynamic"/> and falls back to the runtime's
/// reflection-based dispatch.
/// </summary>
/// <remarks>
/// This is intentionally **not** a full type system. v1 distinguishes:
/// <list type="bullet">
///   <item><see cref="Dynamic"/> — the default. No static information.</item>
///   <item>A concrete CLR <see cref="Type"/>. Used by codegen to skip
///         boxing and pick specialized stdlib overloads.</item>
///   <item><see cref="Void"/> — statements / pipeline stages that
///         logically produce nothing.</item>
/// </list>
/// Refinements, unions, and generics are out of scope for v1; they are
/// represented as <see cref="Dynamic"/> until a later iteration adds
/// dedicated cases.
/// </remarks>
public readonly record struct BoundType
{
    public static BoundType Dynamic { get; } = new(BoundTypeKind.Dynamic, clrType: null);

    public static BoundType Void { get; } = new(BoundTypeKind.Void, clrType: null);

    public BoundTypeKind Kind { get; }

    public Type? ClrType { get; }

    private BoundType(BoundTypeKind kind, Type? clrType)
    {
        Kind = kind;
        ClrType = clrType;
    }

    public static BoundType FromClr(Type type) =>
        new(BoundTypeKind.Concrete, type ?? throw new ArgumentNullException(nameof(type)));

    public bool IsDynamic => Kind == BoundTypeKind.Dynamic;

    public bool IsVoid => Kind == BoundTypeKind.Void;

    public bool IsConcrete => Kind == BoundTypeKind.Concrete;

    public override string ToString() => Kind switch
    {
        BoundTypeKind.Dynamic => "dynamic",
        BoundTypeKind.Void => "void",
        BoundTypeKind.Concrete => ClrType!.Name,
        _ => "?",
    };
}

public enum BoundTypeKind
{
    Dynamic,
    Concrete,
    Void,
}
