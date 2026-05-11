using Tosh.Runtime;

namespace Tosh.Compiler.IR;

/// <summary>
/// Static type information attached to a <see cref="BoundExpression"/>
/// or to a binder symbol. ToastScript is dynamic by default; the
/// binder fills concrete types in only when the source is annotated
/// or the value is cheaply inferable. Anything ambiguous remains
/// <see cref="Dynamic"/> and falls back to runtime reflection
/// dispatch.
/// </summary>
/// <remarks>
/// <para>
/// This class is the foundation of ToastScript's gradual type
/// system. Subclasses model the distinct shapes the type checker
/// needs to reason about: primitives, collections, refinements,
/// user-declared classes/records/structs/enums/unions/interfaces/traits,
/// callables, generics, and the special "no static information"
/// (<see cref="Dynamic"/>) and "produces nothing"
/// (<see cref="Void"/>) cases.
/// </para>
/// <para>
/// The original <c>BoundType</c> was a 3-state record struct
/// (<c>Dynamic | Concrete(Type) | Void</c>). To keep that very
/// large existing call surface working, the legacy factories and
/// query members (<see cref="Dynamic"/>, <see cref="Void"/>,
/// <see cref="FromClr(Type)"/>, <see cref="IsDynamic"/>,
/// <see cref="IsConcrete"/>, <see cref="IsVoid"/>,
/// <see cref="ClrType"/>, <see cref="Kind"/>) all behave exactly
/// as before. Newly-introduced shapes
/// (<see cref="ListType"/>, <see cref="DictType"/>,
/// <see cref="RefinementType"/>, <see cref="UserClassType"/>, …)
/// extend the hierarchy and report <c>Kind = Concrete</c> when they
/// have a meaningful CLR backing, falling back to <c>Dynamic</c>
/// otherwise.
/// </para>
/// </remarks>
public abstract record BoundType
{
    /// <summary>The "no static information" sentinel.</summary>
    public static BoundType Dynamic { get; } = DynamicType.Instance;

    /// <summary>Statements / pipeline stages that produce nothing.</summary>
    public static BoundType Void { get; } = VoidType.Instance;

    /// <summary>
    /// Wraps an arbitrary CLR <see cref="Type"/> as a
    /// <see cref="ConcreteType"/>. Used for primitives and any BCL
    /// shape the binder cannot resolve to a more specific subtype
    /// (e.g. user classes resolve via <see cref="UserClassType"/>).
    /// </summary>
    public static BoundType FromClr(Type type) =>
        new ConcreteType(type ?? throw new ArgumentNullException(nameof(type)));

    /// <summary>
    /// What kind of type this is. The original 3-state taxonomy is
    /// preserved verbatim so consumers that branch on <see cref="Kind"/>
    /// keep compiling. Newly-introduced subclasses report
    /// <see cref="BoundTypeKind.Concrete"/> when they correspond to a
    /// well-defined runtime shape (CLR-backed lists, dicts,
    /// refinements over a concrete base, etc.) and
    /// <see cref="BoundTypeKind.Dynamic"/> otherwise.
    /// </summary>
    public abstract BoundTypeKind Kind { get; }

    /// <summary>
    /// The CLR <see cref="Type"/> this maps to, when one exists.
    /// Refinements expose their base CLR type; user-declared types
    /// (classes / records / structs / unions / enums / interfaces /
    /// traits) expose their backing CLR shape if the runtime has
    /// already produced one (otherwise <c>null</c>); generic
    /// instances expose their constructed <see cref="Type"/> when
    /// every type argument is concrete.
    /// </summary>
    public virtual Type? ClrType => null;

    public bool IsDynamic => Kind == BoundTypeKind.Dynamic;

    public bool IsVoid => Kind == BoundTypeKind.Void;

    public bool IsConcrete => Kind == BoundTypeKind.Concrete;

    /// <summary>
    /// The display name. Subclasses override to show a richer
    /// rendering (e.g. <c>list&lt;int&gt;</c>, <c>Email</c>).
    /// </summary>
    public abstract string DisplayName { get; }

    public override string ToString() => DisplayName;
}

public enum BoundTypeKind
{
    Dynamic,
    Concrete,
    Void,
}

/// <summary>The dynamic sentinel. "No static information."</summary>
public sealed record DynamicType : BoundType
{
    internal static readonly DynamicType Instance = new();
    private DynamicType() { }
    public override BoundTypeKind Kind => BoundTypeKind.Dynamic;
    public override string DisplayName => "dynamic";
}

/// <summary>The void sentinel. Statements / pipeline stages that produce nothing.</summary>
public sealed record VoidType : BoundType
{
    internal static readonly VoidType Instance = new();
    private VoidType() { }
    public override BoundTypeKind Kind => BoundTypeKind.Void;
    public override string DisplayName => "void";
}

/// <summary>
/// A concrete CLR type. Produced by
/// <see cref="BoundType.FromClr(Type)"/>; the workhorse "I know
/// this is exactly that .NET type" representation.
/// </summary>
public sealed record ConcreteType(Type Type) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => Type;
    public override string DisplayName => Type.Name;
}

/// <summary>
/// A homogeneous list with a statically-known element type.
/// Models tosh's <c>list&lt;T&gt;</c> shorthand. The CLR shape is
/// <c>List&lt;T&gt;</c> when the element is concrete; otherwise we
/// fall back to the non-generic <see cref="System.Collections.IList"/>
/// for assignment-compat purposes.
/// </summary>
public sealed record ListType(BoundType Element) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => Element.ClrType is { } e
        ? typeof(List<>).MakeGenericType(e)
        : typeof(System.Collections.IList);
    public override string DisplayName => $"list<{Element.DisplayName}>";
}

/// <summary>
/// A lazy stream of values with a statically-known element type.
/// Models tosh's pipeline-stage view, which materializes at the
/// <c>var x = …</c> site as either a single <c>T</c> (when exactly
/// one value flows through) or an <c>object[]</c> of <c>T</c>
/// (when multiple). Assignability is therefore deliberately loose:
/// a <c>stream&lt;T&gt;</c> source can flow into a slot typed as
/// <c>T</c>, <c>list&lt;T&gt;</c>, <c>T[]</c>, or another
/// <c>stream&lt;T&gt;</c>. Distinct from <see cref="ListType"/> so
/// the type checker can express the polymorphic-materialization
/// rule without conflating it with eagerly-materialized lists.
/// </summary>
public sealed record StreamType(BoundType Element) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => Element.ClrType is { } e
        ? typeof(IEnumerable<>).MakeGenericType(e)
        : typeof(System.Collections.IEnumerable);
    public override string DisplayName => $"stream<{Element.DisplayName}>";
}

/// <summary>A dictionary with statically-known key and value types.</summary>
public sealed record DictType(BoundType Key, BoundType Value) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType =>
        (Key.ClrType, Value.ClrType) is ({ } k, { } v)
            ? typeof(Dictionary<,>).MakeGenericType(k, v)
            : typeof(System.Collections.IDictionary);
    public override string DisplayName => $"dict<{Key.DisplayName}, {Value.DisplayName}>";
}

/// <summary>A homogeneous set with a statically-known element type.</summary>
public sealed record SetType(BoundType Element) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => Element.ClrType is { } e
        ? typeof(HashSet<>).MakeGenericType(e)
        : typeof(System.Collections.IEnumerable);
    public override string DisplayName => $"set<{Element.DisplayName}>";
}

/// <summary>A CLR array of statically-known element type.</summary>
public sealed record ArrayType(BoundType Element) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => Element.ClrType?.MakeArrayType();
    public override string DisplayName => $"{Element.DisplayName}[]";
}

/// <summary>A heterogeneous tuple with positional element types.</summary>
public sealed record TupleType(IReadOnlyList<BoundType> Elements) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;

    /// <summary>
    /// Tuples don't have a single uniform CLR backing in tosh's
    /// runtime, so this stays null until the type checker grows a
    /// dedicated tuple type. Consumers should switch on the subtype.
    /// </summary>
    public override Type? ClrType => null;

    public override string DisplayName =>
        $"({string.Join(", ", Elements.Select(e => e.DisplayName))})";

    public bool Equals(TupleType? other) =>
        other is not null && Elements.SequenceEqual(other.Elements);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var e in Elements) hash.Add(e);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A nullable wrapper around an inner type. Roughly corresponds to
/// the <c>T?</c> tosh syntax. For value types the CLR backing is
/// <c>Nullable&lt;T&gt;</c>; for reference types the inner CLR type
/// is reused (reference types are intrinsically nullable).
/// </summary>
public sealed record NullableType(BoundType Inner) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => Inner.ClrType is { } t
        ? (t.IsValueType ? typeof(Nullable<>).MakeGenericType(t) : t)
        : null;
    public override string DisplayName => $"{Inner.DisplayName}?";
}

/// <summary>
/// A refinement type — a base type plus a runtime predicate /
/// coercion clause set. The static part is the base; the dynamic
/// validation lives in the runtime's
/// <c>RefinementAnnotation</c> machinery, held loosely here as
/// <c>object</c> to avoid pulling the runtime layer into the
/// binding namespace.
/// </summary>
public sealed record RefinementType(
    BoundType Base,
    string Name,
    object Annotation) : BoundType
{
    public override BoundTypeKind Kind => Base.Kind;
    public override Type? ClrType => Base.ClrType;
    public override string DisplayName => Name;
}

/// <summary>
/// A user-declared class. <see cref="Definition"/> is held as
/// <c>object</c> so that the binding layer doesn't take a hard
/// dependency on the runtime's <c>ToshClassDefinition</c>; consumers
/// that need the rich shape cast on demand.
/// </summary>
public sealed record UserClassType(
    string Name,
    object Definition,
    Type? BackingClrType) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => BackingClrType;
    public override string DisplayName => Name;
}

/// <summary>A user-declared record (immutable named-fields shape).</summary>
public sealed record UserRecordType(
    string Name,
    object Definition,
    Type? BackingClrType) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => BackingClrType;
    public override string DisplayName => Name;
}

/// <summary>A user-declared struct (mutable value-type-ish shape).</summary>
public sealed record UserStructType(
    string Name,
    object Definition,
    Type? BackingClrType) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => BackingClrType;
    public override string DisplayName => Name;
}

/// <summary>A user-declared enum.</summary>
public sealed record UserEnumType(
    string Name,
    object Definition,
    Type? BackingClrType) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => BackingClrType;
    public override string DisplayName => Name;
}

/// <summary>A user-declared discriminated union.</summary>
public sealed record UserUnionType(
    string Name,
    object Definition,
    Type? BackingClrType) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => BackingClrType;
    public override string DisplayName => Name;
}

/// <summary>A user-declared interface.</summary>
public sealed record UserInterfaceType(
    string Name,
    object Definition,
    Type? BackingClrType) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => BackingClrType;
    public override string DisplayName => Name;
}

/// <summary>A user-declared trait (mixin-style protocol).</summary>
public sealed record UserTraitType(
    string Name,
    object Definition,
    Type? BackingClrType) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => BackingClrType;
    public override string DisplayName => Name;
}

/// <summary>
/// A function/callable shape. <see cref="Parameters"/> can be empty
/// for thunks; <see cref="Return"/> is <see cref="Void"/> for
/// statement-only callables.
/// </summary>
public sealed record FunctionType(
    IReadOnlyList<BoundType> Parameters,
    BoundType Return) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType => null;
    public override string DisplayName =>
        $"({string.Join(", ", Parameters.Select(p => p.DisplayName))}) -> {Return.DisplayName}";

    public bool Equals(FunctionType? other) =>
        other is not null
        && Return.Equals(other.Return)
        && Parameters.SequenceEqual(other.Parameters);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Return);
        foreach (var p in Parameters) hash.Add(p);
        return hash.ToHashCode();
    }
}

/// <summary>
/// An instantiation of a generic template
/// (<see cref="UserClassType"/>, <see cref="UserRecordType"/>, …)
/// at concrete or symbolic type arguments. The template plus the
/// argument list is the canonical form; <see cref="ClrType"/>
/// returns a constructed CLR type only when every argument is
/// concrete and the template is CLR-backed.
/// </summary>
public sealed record GenericInstanceType(
    BoundType Template,
    IReadOnlyList<BoundType> TypeArguments) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Concrete;
    public override Type? ClrType
    {
        get
        {
            if (Template.ClrType is not { IsGenericTypeDefinition: true } open) return null;
            var args = new Type[TypeArguments.Count];
            for (var i = 0; i < args.Length; i++)
            {
                if (TypeArguments[i].ClrType is not { } a) return null;
                args[i] = a;
            }
            return open.MakeGenericType(args);
        }
    }

    public override string DisplayName =>
        $"{Template.DisplayName}<{string.Join(", ", TypeArguments.Select(a => a.DisplayName))}>";

    public bool Equals(GenericInstanceType? other) =>
        other is not null
        && Template.Equals(other.Template)
        && TypeArguments.SequenceEqual(other.TypeArguments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Template);
        foreach (var a in TypeArguments) hash.Add(a);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A free type parameter (e.g. the <c>T</c> inside the body of a
/// generic class declaration). Only meaningful before generic
/// substitution; after substitution it is replaced with the bound
/// argument.
/// </summary>
public sealed record TypeParameterType(string Name) : BoundType
{
    public override BoundTypeKind Kind => BoundTypeKind.Dynamic;
    public override Type? ClrType => null;
    public override string DisplayName => Name;
}
