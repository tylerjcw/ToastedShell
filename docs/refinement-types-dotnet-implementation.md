# Implementing Refinement Types in a .NET Language

Refinement types can be compiled as base CLR types plus compiler-generated validation and normalization methods. The predicate and coercer should be bound once and emitted directly to IL like ordinary language expressions—never reparsed or interpreted at runtime.

## 1. Compiler Representation

Represent a refinement type in the compiler's type system using its underlying type and already-bound predicate and coercion expressions:

```csharp
internal sealed record RefinementTypeSymbol(
    string Name,
    TypeSymbol BaseType,
    ParameterSymbol ValueParameter,   // Synthetic symbol representing "_"
    BoundExpression Predicate,
    BoundExpression? Coercer,
    SourceSpan DeclarationSpan
) : TypeSymbol(Name);
```

During binding, introduce `_` as a real synthetic parameter whose type is the refinement's base type:

```csharp
private RefinementTypeSymbol BindRefinement(TypeDeclarationSyntax syntax)
{
    var baseType = BindType(syntax.BaseType);
    var valueParameter = new ParameterSymbol("_", baseType);

    using var scope = EnterScope(valueParameter);

    var predicate = BindExpression(
        syntax.Predicate,
        expectedType: BuiltInTypes.Bool);

    RequireImplicitConversion(predicate.Type, BuiltInTypes.Bool);

    BoundExpression? coercer = null;
    if (syntax.Coercer is not null)
    {
        coercer = BindExpression(
            syntax.Coercer,
            expectedType: baseType);

        RequireImplicitConversion(coercer.Type, baseType);
    }

    EnsureClosedExpression(predicate, except: valueParameter);
    EnsurePureExpression(predicate);

    if (coercer is not null)
    {
        EnsureClosedExpression(coercer, except: valueParameter);
        EnsurePureExpression(coercer);
    }

    return new RefinementTypeSymbol(
        syntax.Name,
        baseType,
        valueParameter,
        predicate,
        coercer,
        syntax.Span);
}
```

The important detail is that `_` becomes a bound parameter symbol. Do not retain it as text and substitute or reparse it later.

Type declarations usually need two binding passes:

1. Register the refinement type's name so later declarations can refer to it.
2. Resolve its base type and bind its predicate and coercer.

This also lets the compiler detect cycles such as a refinement whose base type eventually refers back to itself.

## 2. Generate Normal CLR Methods

Given a declaration such as:

```text
type IntPercent = int
    where (_ >= 0 and _ <= 100)
    coerce Math.Clamp(_, 0, 100)
```

emit the equivalent of:

```csharp
[CompilerGenerated]
internal static class __Refinement_IntPercent
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsValid(int value)
        => value >= 0 && value <= 100;

    internal static int Convert(int value, SourceLocation location)
    {
        if (IsValid(value))
            return value;

        value = Math.Clamp(value, 0, 100);

        // Never trust the coercer merely because it returned.
        if (IsValid(value))
            return value;

        throw RefinementViolationException.Create(
            typeName: "IntPercent",
            value,
            location);
    }
}
```

While emitting `IsValid` and `Convert`, map the synthetic `_` parameter to argument zero. Both methods should be emitted from bound compiler IR, using the same expression emitter used for ordinary functions.

The runtime helper should primarily provide structured diagnostics:

```csharp
public sealed class RefinementViolationException : Exception
{
    public string RefinementName { get; }
    public object? RejectedValue { get; }
    public SourceLocation Location { get; }

    private RefinementViolationException(
        string refinementName,
        object? value,
        SourceLocation location)
        : base($"Value '{value}' does not satisfy refinement '{refinementName}'.")
    {
        RefinementName = refinementName;
        RejectedValue = value;
        Location = location;
    }

    public static RefinementViolationException Create<T>(
        string typeName,
        T value,
        SourceLocation location)
        => new(typeName, value, location);
}
```

For dynamically typed inputs, first perform the language's ordinary conversion to the base type, then call the typed refinement method:

```csharp
internal static int ConvertObject(object? input, SourceLocation location)
{
    var baseValue = RuntimeConversion.ToInt32(input, location);
    return __Refinement_IntPercent.Convert(baseValue, location);
}
```

## 3. Canonical Conversion Semantics

Use one canonical algorithm for every refinement conversion:

```text
convert to base type
→ test predicate
→ if invalid and a coercer exists, run the coercer
→ ensure the result is still the base type
→ test the predicate again
→ return the valid value or throw
```

In generic C#, that algorithm looks like this:

```csharp
public static T Apply<T>(
    T value,
    Func<T, bool> predicate,
    Func<T, T>? coercer,
    string typeName,
    SourceLocation location)
{
    if (predicate(value))
        return value;

    if (coercer is not null)
    {
        value = coercer(value);

        if (predicate(value))
            return value;
    }

    throw RefinementViolationException.Create(typeName, value, location);
}
```

This helper illustrates the semantics, but generated code should normally call its generated `IsValid` and `Convert` methods directly. That avoids delegate allocation and permits normal JIT inlining.

If the language is dynamically typed, the coercer might produce an arbitrary object. In that case, convert the coercer's result back to the base type before testing the predicate again. A coercer must never be allowed to smuggle a value of the wrong CLR type into a refined slot.

## 4. Inject Checks at Narrowing Boundaries

A declaration such as:

```text
var opacity: UnitFloat = expression
```

lowers approximately to:

```csharp
float temporary = ConvertToSingle(expression);
float opacity = __Refinement_UnitFloat.Convert(
    temporary,
    new SourceLocation(file, line, column));
```

Refinement conversion is required at every boundary where an ordinary base value flows into a refined slot:

- Variable initialization and reassignment
- Function and method arguments
- Function and method returns
- Constructor arguments
- Property setters and field initialization
- Explicit casts to the refinement
- Deserialization and .NET interop boundaries
- Collection elements such as `list<IntPercent>`
- Dynamic invocation and reflection entry points

Conversion from a refinement to its base type is free:

```text
IntPercent <: int
```

Conversion from `int` to `IntPercent` requires validation or coercion.

If a value is already statically known to have the exact refinement type, internal calls can omit redundant checks. Public, reflection-based, dynamic, or interop entry points should still validate because callers can bypass the language's static type system.

## 5. Static Type Rules

Treat the refinement as a subtype of its base type:

```text
Refined<Base, Predicate> <: Base
```

The reverse relationship is not implicit unless the compiler inserts the generated conversion.

Arithmetic and other operations normally lose the refinement:

```text
IntPercent + IntPercent -> int
```

The result could be 200, so it cannot remain an `IntPercent`. Assigning the result back to an `IntPercent` invokes its refinement conversion again.

For two refinement types `A` and `B`, allow `A -> B` without a runtime check only when the compiler can prove that `A`'s predicate implies `B`'s predicate. A first implementation can simply insert a runtime check for all such conversions. A later implementation could use constant folding, interval analysis, or an SMT solver to prove safe conversions.

Compile-time constants can be checked early:

```text
var x: IntPercent = 50    // Proven valid
var y: IntPercent = 200   // Normalize at compile time if coercion is pure,
                          // or emit the normal runtime conversion
```

Avoid executing arbitrary user code during compilation. Constant-fold only expressions that the compiler knows are pure and deterministic.

## 6. Erased Versus Reified CLR Representation

### Erased representation

For internal execution, a refinement can use the same CLR representation as its base type:

```text
IntPercent -> System.Int32
UnitFloat  -> System.Single
```

This produces efficient IL and makes ordinary arithmetic straightforward. It does, however, create two CLR-level issues:

- `IntPercent` and `TimeoutMs` both become `int`, so overloads using them have identical CLR signatures.
- External .NET callers cannot see that an `int` parameter carries a refinement.

The compiler can solve internal signature collisions with name mangling. Public .NET APIs need either metadata understood by tooling or reified wrapper types.

### Reified representation

For a CLR-visible nominal type, generate a wrapper:

```csharp
public readonly record struct IntPercent
{
    public int Value { get; }

    private IntPercent(int value) => Value = value;

    public static IntPercent Create(int value)
        => new(__Refinement_IntPercent.Convert(
            value,
            SourceLocation.External));

    public static implicit operator int(IntPercent value)
        => value.Value;

    public static explicit operator IntPercent(int value)
        => Create(value);
}
```

A hybrid representation often works best:

- Erase refinements inside generated implementation methods.
- Use wrappers in the public CLR-facing API.
- Validate and unwrap when values cross between the two representations.

There is an important struct caveat: `default(PosInt)` always exists and contains zero, violating a positive-integer invariant. If the CLR type itself must guarantee the invariant, use a reference type or add initialization state and reject default struct instances. A reference type has its own corresponding issue: CLR `null` remains possible at untrusted boundaries.

## 7. Purity and Captures

Predicates and coercers should ideally be:

- Pure
- Deterministic
- Terminating
- Closed except for `_` and compile-time constants
- Synchronous

Allowing I/O, mutation, async operations, or captured mutable variables inside a type invariant makes repeated validation observably inconsistent and limits compiler optimization.

For exported refinement types, rejecting lexical captures is particularly useful. Otherwise a compiled refinement might depend on a local variable or closure instance that no longer has a meaningful lifetime when another module uses the type.

## 8. Edge Cases in the Examples

The `TimeoutMs` example uses:

```text
where (_ > 0 and _ <= 300000)
coerce Math.Clamp(_, 0, 300000)
```

That coercer does not repair zero, even though zero violates the predicate. Its lower bound should probably be `1`:

```text
coerce Math.Clamp(_, 1, 300000)
```

Similarly, `Math.Abs(int.MinValue)` throws because the positive magnitude of `int.MinValue` cannot fit in an `int`. `PosInt` and `NonNegInt` therefore need an explicit overflow policy, such as saturating to `int.MaxValue` or rejecting the value.

Floating-point refinements also need a policy for `NaN` and infinity. Comparisons against `NaN` are false, and clamping may still produce `NaN`; the mandatory post-coercion predicate check ensures such a value is rejected unless the refinement explicitly permits it.

## Core Compilation Principle

Refinement predicates and coercers should become bound compiler IR and ordinary emitted methods. The compiled artifact should not require the parser, evaluator, original source text, or an interpreter to enforce a refinement.
