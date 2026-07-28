# TōSh CLR ABI v1

**Status:** Normative. Frozen at v1.0.
**Stamp:** Every assembly emitted by `tosh` carries `[assembly: Tosh.Runtime.ToshAbi(1)]`.

This document defines the public contract that cross-language consumers
(C#, F#, VB, native interop, reflection-driven tooling) can rely on when
referencing a TōSh-compiled assembly. Anything not described here is an
implementation detail and may change without notice.

A worked example of the entire contract — a single `.tosh` file together
with the C# view of every emitted member — appears at the end.

> **Companion document:** [`COMPILED_TOSH.md`](COMPILED_TOSH.md) describes
> the broader compile pipeline; this document is the narrow ABI surface
> distilled out of section "Public CLR ABI v1" there.

---

## 1. Quick cheat-sheet for C# consumers

| TōSh declaration | C# type/member you reference |
|---|---|
| `func add(a, b) { … }` (untyped) | `static object Func_add(object a, object b)` on `<asm>.Program` |
| `func sum(a: long, b: long): long => $a + $b` | `static long sum(long a, long b)` on `<asm>.Program` |
| `class Point(x, y) { … }` | `public sealed class <asm>.Point` |
| `hollow class Shape { … }` | `public abstract class <asm>.Shape` |
| `sealed class Foo { … }` | `public sealed class <asm>.Foo` |
| `hermit class Math { … }` | `public abstract sealed class <asm>.Math` (CLR static class) |
| `prop X = …` | `public object X;` field |
| `shy prop X = …` | `private object X;` field |
| `guarded prop X = …` | `protected object X;` field |
| `local prop X = …` | `internal object X;` field |
| `fixed prop X = …` | `public readonly object X;` field |
| `func foo() { … }` (instance) | `public virtual object foo()` |
| `static func foo() { … }` | `public static object foo()` |
| `overrule func foo() { … }` | `public override object foo()` |
| `hollow func foo()` | `public abstract object foo()` |
| `module Foo { … }` | `public abstract sealed class <asm>.Foo` |
| `record Pair(a, b)` | `public sealed class <asm>.Pair` with positional ctor |
| `union Result = Ok | Err` | `public abstract class <asm>.Result` + sealed variant subclasses |

Reference the assembly in a `.csproj` with a normal `<PackageReference>` /
`<Reference>` element. The shipped reference assembly (`<asm>.ref.dll`) is
metadata-equivalent to the runtime assembly for everything in this spec.

---

## 2. Naming and identity

- **Assembly name:** matches the `-o` output stem (`tosh build -o foo.dll`
  produces assembly `foo`).
- **Root program type:** `<AssemblyName>.Program`. Public, sealed, abstract
  (CLR static class shape).
- **Identifier mangling rule** (stable across ABI v1):
  1. If the first character is a digit, prefix `_`.
  2. Replace every character outside `[A-Za-z0-9_]` with `_`.
  3. Otherwise the name is preserved verbatim.
- **Collision policy:** if two distinct tosh names mangle to the same CLR
  name in the same bucket (type bucket, top-level function bucket, member
  bucket of a single type), compilation fails with
  `tosh.compile.name_mangling_collision`.
- **Original-name recovery:** any member whose CLR name was changed by
  mangling is stamped with `[ToshOriginalName("…")]`. Members whose source
  spelling is already a valid CLR identifier are NOT stamped (keeps
  metadata small).

---

## 3. Top-level functions

Top-level functions live on `<asm>.Program` as `public static` methods.

A function is **typed** at the ABI boundary when:

- every parameter has a non-`object` type annotation, AND
- the return type annotation is non-`object`.

Otherwise the function is **dynamic**.

| Form | Emitted signature |
|---|---|
| Typed | `public static <T> <MangledName>(<T1> p1, …)` |
| Dynamic | `public static object Func_<MangledName>(object, …)` |

The `Func_` prefix is the stable disambiguator — C# can call the typed
form by its source name, and the dynamic form by `Func_<name>`.

`func main(...)` is always emitted dynamic to keep the program entrypoint
(see §10) consistent with execution semantics.

### Overloads

When two same-name functions resolve to distinct CLR signatures, both are
emitted under the same mangled name. When two same-name functions would
collide on the same CLR signature, the second receives an `__ov{N}`
suffix. Same-signature overloads are unreachable from the binder anyway —
the suffix is a defensive sigil only.

### Defaults

A typed parameter with a **literal** default expression is stamped with
`ParameterAttributes.HasDefault | Optional` and `SetConstant(value)`. C#
consumers can omit the trailing argument and reflection-based tooling
sees the constant. Non-literal defaults stamp `Optional` only — there is
no constant to record — and the function applies the default at runtime
inside its body.

Dynamic functions never stamp `HasDefault`. Defaults are applied
internally before parameter locals are read.

### Rest parameters (`...`)

A typed function whose final parameter is a rest parameter and whose
resolved CLR type is an array gets `[System.ParamArrayAttribute]` on the
last `ParameterBuilder`. C# consumers can call the function with a
variadic argument list (`f(1, 2, 3)`).

---

## 4. Classes

Class shells emit as a single CLR type per `class` declaration. Layout is
chosen by the class-level modifier ladder:

| Modifier | CLR type attributes |
|---|---|
| `hermit` | `public abstract sealed class` (static) |
| `hollow` | `public abstract class` |
| `sealed` | `public sealed class` |
| (default, extensible) | `public class` |

A class declared with `: Base` extends the named type. Unknown bases
fall back to `object`. `: Iface1, Iface2` adds interface implementations.
Used `traits` are added as additional implemented interfaces.

### Class fields (storage properties)

Every `prop X = …` produces a CLR field on the shell. All fields are
`object`-typed in v1 (see §3 of `COMPILED_TOSH.md` for the rationale).
Visibility resolves with this precedence:

| Modifier | `FieldAttributes` |
|---|---|
| `shy` | `Private` |
| `guarded` | `Family` (CLR `protected`) |
| `local` | `Assembly` (CLR `internal`) |
| (none) | `Public` |
| `fixed` | adds `InitOnly` |

When multiple visibility modifiers stack, `shy` wins, then `guarded`,
then `local`. This matches the evaluator's hide-from-outside-class
semantics. The modifiers `vital`, `lazy`, and `fading` carry no ABI
visibility — they're enforced by the evaluator only.

### Class methods

Methods emit as `public/private/family/assembly virtual` instance
methods (or `static` when declared `static`). The visibility ladder is
the same as for fields.

| Trait | `MethodAttributes` |
|---|---|
| `static` | `Static` (no `Virtual`) |
| `overrule` (override) | `Virtual` (`ReuseSlot`) |
| `hollow` (abstract) | `Virtual | NewSlot | Abstract` |
| (default instance) | `Virtual | NewSlot` |
| All methods | `+HideBySig` |

Method parameters and return type are always `object` in v1 (the typed
function path described in §3 applies only to top-level functions, not
to class members).

### Constructors

The primary constructor is positional: one `object` parameter per
declared primary-ctor field, in source order. Member-declared
`func init(…)` constructors map to additional ctors of the same shape.

---

## 5. Records and structs

`record Pair(a, b)` emits `public sealed class <asm>.Pair` with:

- a positional constructor (one `object` parameter per field, in source
  order),
- a `public object` field per declared field,
- the type stamped with `[ToshType("record", spanStart, spanLength)]`.

`struct` emits the same shape. v1 records do not synthesise CLR
`record` metadata, equality, deconstruct, or `with`-expression support.

---

## 6. Unions

`union Result = Ok(value) | Err(message)` emits:

- a `public abstract class <asm>.Result` base, stamped
  `[ToshType("union", …)]`,
- a `public string Variant { get; }` (read-only),
- a `protected` base ctor,
- one `public sealed class <asm>.Ok` / `<asm>.Err` per variant, stamped
  `[ToshType("union_variant", …)]`.

---

## 7. Modules

Every module declaration emits a CLR static-class shell:

- top-level: `public abstract sealed class <asm>.<Name>`,
- nested: `nested public abstract sealed class <Outer>.<Inner>`,
- members are `public static`.

Each shell is stamped with `[ToshModuleShell("Qualified.Name")]`.
The assembly receives one `[assembly: ToshModule("Qualified.Name", start, length)]`
per declared module (recursive for nested modules).

Partial modules merge into one CLR shell type by qualified name.

---

## 8. Generated attributes (required set)

All from the namespace `Tosh.Runtime`:

| Attribute | Target | Purpose |
|---|---|---|
| `ToshAbi(version)` | assembly | declares ABI version. v1 → `1`. |
| `ToshOriginalName(name)` | type/method/field/property | mangled-symbol round-trip. |
| `ToshType(kind, start, length)` | type | tags emitted shells (`class`, `struct`, `record`, `union`, `union_variant`, `enum`, `interface`, `trait`, `event`, `alias`). |
| `ToshModuleShell(qualifiedName)` | type | marks generated module shells. |
| `ToshModule(qualifiedName, start, length)` | assembly | one per declared module. |

Optional / mode-dependent:

- `[ReferenceAssembly]` on `--emit-refasm` output.

These attributes are part of the ABI: removing or renaming any of them is
a major-version break. Adding new attributes is non-breaking.

---

## 9. Exceptions

There are two ways a tosh `throw` reaches a .NET caller, depending on
what was thrown. A scope that has multiple failures while running
deferred cleanup uses the additive defer-unwind surface in §9.5.

### 9.1 Throwing an `Exception` subclass (recommended)

When the thrown value is itself an `Exception` (or any subclass), the
exception is raised verbatim — no wrapping — unless another failure
competes with it during deferred cleanup. C# consumers can catch a sole
failure by its concrete type:

```tosh
class HttpError(status, url) extends Error {
    prop Status = status
    prop Url    = url
}

func fetch(url) {
    # …
    throw (new HttpError(503, $url))
}
```

```csharp
try { mylib.Program.Fetch("https://…"); }
catch (HttpError ex)
{
    Console.WriteLine($"{ex.Status} {ex.Url}");
}
```

Tosh ships `Tosh.Runtime.ToshError : Exception` as the recommended base
class, surfaced inside tosh as the `Error` alias. `ToshError` carries:

- `string Message` — error message (inherited from `Exception`),
- `TextSpan Span` — source span the engine stamps when the exception
  escapes the `throw` site,
- `object? Cause` — optional inner payload (string, record, dictionary,
  another exception, …).

User-defined types are not required to extend `ToshError`; any
`Exception` subclass works. `ToshError` is unsealed specifically so
library authors can build typed error hierarchies.

### 9.2 Throwing non-exception values

When the thrown value is not an `Exception` (string, number, record,
array, anonymous map, …), it is wrapped in
`Tosh.Runtime.ThrowSignalException : Exception` with:

- `TextSpan Span` — source span of the `throw`,
- `object? Value` — the original payload,
- `Message` derived from `Value?.ToString()`.

C# consumers catching this form unwrap `.Value`:

```csharp
try { mylib.Program.DoIt(); }
catch (ThrowSignalException ex) when (ex.Value is string s) { … }
```

### 9.3 Marker

Every exception raised through a tosh `throw` (either form above) is
stamped with `ex.Data["tosh.thrown"] = true` so engine internals can
distinguish user throws from runtime CLR errors. The marker is part of
the public ABI; consumers may inspect it but should not rely on its
absence (future engine paths may set it on additional exception
shapes).

### 9.4 Control-flow signals

Internal control-flow signals (`ShellControlFlowException` and its
`Return` / `Break` / `Continue` subclasses) are NOT part of the public
ABI — they must never surface to a consumer's call frame. The compiled
runtime's catch helper rethrows them on sight, so a tosh
`catch (err) { … }` block cannot accidentally swallow them either.

### 9.5 Deferred-cleanup failures

Deferred cleanup is exhaustive: every reached `defer` is attempted once
in LIFO order even when another cleanup fails. A sole body or cleanup
failure crosses the CLR boundary unchanged. When failures compete, the
runtime preserves their original exception objects through the following
additive ABI surface:

| Type | Public contract |
|---|---|
| `Tosh.Runtime.ToshDeferAggregateException : AggregateException` | Ordered carrier for competing failures. `BodyFailure` is the failure that escaped the ordinary scope body, or `null` when the body completed without failure. `CleanupFailures` contains cleanup exceptions in actual LIFO execution order. `Failures` presents the body failure first, when present, followed by `CleanupFailures`. |
| `Tosh.Runtime.ToshDeferFailureState` | Shared unwind accumulator used by the interpreter and generated IL. `CaptureBodyFailure(Exception)`, `CaptureCleanupFailure(Exception)`, and `ThrowIfCleanupFailed()` implement the canonical ordering, sole-failure identity, nested defer-aggregate flattening, and cancellation rules. |
| `Tosh.Runtime.ToshDeferFailures` | Public inspection and diagnostic utility. `CleanupFailuresDataKey` identifies the reserved `Exception.Data` entry used for unchanged sole/cancellation exceptions. `IsDeferFailure(Exception)` recognizes the dedicated aggregate and valid non-empty deferred-failure metadata; `GetCleanupFailures(Exception)` returns the ordered cleanup failures; `ToDiagnosticException(Exception)` produces the ordered structured diagnostic representation used at an unhandled engine boundary. |

The runtime flattens only `ToshDeferAggregateException` instances created
by this protocol. It never flattens an arbitrary `AggregateException` or
`ToshDiagnosticException`, because either may represent a single logical
failure with its own internal structure. A non-exception TōSh throw remains
a `ThrowSignalException` entry, so its original `.Value` is retained.

Cancellation cleanup runs with a token shielded from the cancellation that
initiated unwind. If that cancellation competes with cleanup failures, the
original `OperationCanceledException` remains the outward exception so
.NET cancellation filters and task consumers continue to recognize it.
`ToshDeferFailures` exposes the ordered deferred-failure state attached to
that exception. Consumers should treat the reserved metadata as read-only
and use the helper methods instead of mutating `Exception.Data` directly.

A pending TōSh `return`, `break`, or `continue` is not a CLR failure and
does not appear in these collections. A cleanup failure supersedes that
pending jump. Control flow raised from within cleanup is cleanup-local and
suppressed for compatibility; internal `ShellControlFlowException`
instances still must not cross the public boundary.

---

## 10. Library vs executable

| Mode | Layout |
|---|---|
| Executable | emits `<asm>.Program.Main(string[] args)`. CLI may add an apphost wrapper. |
| Library | no `Main`. Top-level functions / modules / types are still emitted as public metadata. |
| Reference assembly | metadata-equivalent to the runtime assembly for everything described here; consumers compile against `<asm>.ref.dll`, execute against `<asm>.dll`. |

---

## 11. PDBs and SourceLink

- Portable PDBs are embedded in every emitted assembly.
- Method bodies carry sequence points so debuggers can step through
  source.
- `Microsoft.SourceLink.GitHub` is wired into the build, so the embedded
  PDB carries SourceLink JSON pointing at the commit's raw GitHub URLs.
- Symbol packages (`.snupkg`) ship alongside `.nupkg` for every packed
  TōSh runtime / SDK / template asset.

---

## 12. Compatibility policy

ABI compatibility for v1 is defined by:

- public type and member names (after mangling),
- member kind (field / method / type / constructor),
- signature shape (parameter types, return type, generic arity),
- the required attributes from §8.

**Non-breaking** changes within v1:

- adding new non-conflicting public members,
- adding `private`, `internal`, or `protected` helpers,
- improving method bodies, diagnostics, or host routing,
- adding new optional attributes,
- stamping `HasDefault` on a parameter that previously omitted it.

**Breaking** changes (require a major bump and a new ABI version):

- changing mangling rules,
- changing the field ↔ property representation choice without a
  compatibility shim,
- changing record / module / class shell identity rules,
- removing or renaming any required attribute,
- changing the visibility ladder for `shy` / `guarded` / `local`.

A consumer pinning against ABI v1 should check
`assembly.GetCustomAttribute<ToshAbiAttribute>()?.Version == 1` and bail
on mismatch.

---

## 13. Explicitly out of scope for v1

These are tracked for v2 and explicitly NOT promised by v1:

- primitive types (`long`, `string`, etc.) on class fields, class
  method parameters / returns, and record fields — v1 erases them all
  to `object`,
- XML doc comments lifted from `///` source,
- refinement-type encoding (`@positive`, `@nonempty`, …),
- `async` / `Task<T>` surface for tosh `async` constructs,
- operator / indexer / conversion-operator emission,
- configurable root namespace (currently always `<AssemblyName>`),
- deterministic NRT (`NullableAttribute` / `NullableContextAttribute`)
  completeness,
- generic arity at the user-facing ABI boundary.

---

## 14. Worked example

```tosh
# math.tosh — compiled with: tosh build math.tosh -o math.dll

module Geometry {
    var pi: float = 3.14159

    func area(radius: float = 1.0): float => $pi * $radius ** 2
}

record Point(x, y)

class Shape(name) {
    prop Name = name
    guarded prop Tag = "default"
    shy prop Internal = 0

    func describe() { echo $"Shape: $($this.Name)" }
}

sealed class Circle(name, radius) : Shape(name) {
    prop Radius = radius
    overrule func describe() { echo $"Circle '$($this.Name)' r=$($this.Radius)" }
}

func sum(...nums: long): long {
    var total: long = 0
    for $n in $nums { $total = $total + $n }
    return $total
}

func greet(who: string = "world"): string => $"Hello, $who!"
```

The C# view of `math.dll`:

```csharp
// Assembly: math.dll
// [assembly: Tosh.Runtime.ToshAbi(1)]
// [assembly: Tosh.Runtime.ToshModule("Geometry", ...)]

namespace math
{
    public abstract sealed class Program
    {
        public static long sum(params long[] nums);
        public static string greet(string who = "world");
    }

    public abstract sealed class Geometry
    {
        public static double pi;
        public static double area(double radius = 1.0);
    }

    public sealed class Point
    {
        public Point(object x, object y);
        public object x;
        public object y;
    }

    public class Shape
    {
        public Shape(object name);
        public object Name;
        protected object Tag;
        private object Internal;
        public virtual object describe();
    }

    public sealed class Circle : Shape
    {
        public Circle(object name, object radius);
        public object Radius;
        public override object describe();
    }
}
```

A C# consumer can now:

```csharp
using math;

var c = new Circle("c1", 3.0);
c.describe();                          // virtual call, prints "Circle 'c1' r=3"

long total = Program.sum(1L, 2L, 3L);  // params long[]
string g    = Program.greet();          // uses default "world"

var abi = typeof(Program).Assembly
    .GetCustomAttribute<Tosh.Runtime.ToshAbiAttribute>();
Debug.Assert(abi?.Version == 1);
```

---

## 15. Further reading

- [`COMPILED_TOSH.md`](COMPILED_TOSH.md) — full pipeline, refasm, profiles, and ABI rationale.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — emitter, binder, evaluator structure.
- [`BACKLOG.md`](BACKLOG.md) — wave plan, including the items that gated v1.
