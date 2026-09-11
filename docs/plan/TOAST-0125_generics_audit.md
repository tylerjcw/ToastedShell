# TOAST-0125 — generics audit

Dated 2026-09-10. Measured by probing the installed semantics rather than by reading the
implementation, so every row below is a transcript, not an inference. Probes live in
`scratchpad/audit/`.

Tōast is a .NET language written in one, so C# is the reference. Where this diverges the
question is always whether the divergence is *chosen*.

## Headline

Generics work for the case they were built for — a class or method over one or two plain
CLR types — and fall away from it in three directions at once:

1. **A type argument that is not a plain CLR type binds `null`, and a null binding is read
   as "accept anything".** That single mechanism is behind most of the soundness findings
   here and behind `TOAST-0116` and `TOAST-0118` before them.
2. **Only one kind of constraint is enforced.** `where T: Numeric` is checked. An interface
   or base-class constraint is parsed, recorded, and never consulted.
3. **A closed generic is not checked as a closed generic anywhere except construction.**
   Annotations, contracts and type tests all see the open type or fail to parse.

What works, works well: inference, inheritance, recursion, multi-parameter generics, and
CLR generic interop are all sound.

---

## A · Soundness

A null binding disables the check it was supposed to feed, so these accept anything.

| # | case | observed | C# |
|---|---|---|---|
| A1 | `new Box<array>(…)`, `<list>`, `<dict>` | `Box<T>`, args `[null]` | a bound type |
| A2 | `new Box<Box<int>>(…)` — a user generic as an argument | `Box<T>`, args `[null]` | `Box<Box<int>>` |
| A3 | `new Box<Box>(…)` — an open user type as an argument | `Box<T>`, args `[null]` | not expressible |

`ResolveTypeArgument` returns `null` for anything `TryGetNamedType` claims, which is every
ToastScript-declared type and every collection alias. The value is kept nominally for
display and the type system then knows nothing about it.

**Fixed.** The null is deliberate and stays — resolving the name through the CLR resolver is
what `TS-P2-39` was, and it reached every loaded assembly. What was missing is that the
*name* was thrown away too. It is now carried on the instance, so `type-of` answers
`Holder<Circle>`, and a value is checked against it nominally through the same contract `is`
uses — which is why a subclass and an implemented interface both satisfy it. Only names the
script declared are enforced: an alias names a CLR shape rather than a declaration, and
refusing what cannot be checked that way would turn an unenforced annotation into a wrong
error.

**A4.** `func f(b: Box<int>)` accepts a `Box<string>`, and `var x: Box<int> = <a Box<string>>`
binds. A generic annotation checks the open type only.

---

## B · Constraints

| # | constraint | enforced |
|---|---|---|
| B1 | `where T: Numeric` | **yes**, including on a second parameter |
| B2 | `where T: SomeInterface` | **no** — `new IfaceCon<int>(1)` constructs |
| B3 | `where T: SomeBaseClass` | **no** — `new BaseCon<int>(1)` constructs |
| B4 | `new()`, `class`, `struct`, `notnull`, `unmanaged` | not available |

B2 and B3 are the ones that matter: the constraint is accepted at the declaration, so the
author believes it holds, and nothing ever checks it.

---

## C · Conversion — `TOAST-0124`

| # | case | observed |
|---|---|---|
| C1 | `new Box<double>(0)` | refused — `Int32` is not a `Double` |
| C2 | `new Box<long>(1)` | refused |
| C3 | `new Box<list<int>>([1, 2])` | refused — a ToastScript list is not a `List<Int32>` |

The binding check accepts only `IsInstanceOfType`. Narrowing *should* be refused — that is
what stops a `Point2D<int>` taking 3.5 — but widening is lossless and is performed
everywhere else in the language. C3 is a different question underneath: the alias resolves
to a CLR type whose relationship to the ToastScript value is never bridged.

---

## D · Name resolution

**D1. `any` resolves to `System.Runtime.InteropServices.JavaScript.JSType+Any`.**

The spec says `dynamic`, `any` and `object` are synonyms for explicit dynamic. `dynamic`
and `object` work. `any` fails in *every* annotation position — variable, parameter, return,
constructor parameter, type argument — because the name collides with a JS-interop type
nobody meant:

```
var x: any = 5        # 'x' produced a value that could not be converted to 'any'
func f(v: any)        # Argument 'v' could not be converted to 'any'
func g() -> any       # Function 'g' returned a value that could not be converted to 'any'
```

Cheap to fix and documented-but-broken, which makes it the worst kind of gap.

---

## E · Missing surface

| # | case | note |
|---|---|---|
| E1 | `trait G<T>` | not recognised as a declaration at all — falls through to a command |
| E2 | `struct G<T>` | same |
| ~~E3~~ | ~~`interface Co<out T>` / `<in T>`~~ | **wrong — variance is implemented.** See below. |

**E3 was a mistake in this audit.** It was recorded as "parses and means nothing" on the
strength of a probe that declared a variant interface and then looked at a runtime
`fulfills`, which is not where variance lives. Variance is a static assignability rule and
it is implemented in the type checker, with four tests covering covariance, contravariance,
invariance, and the rejection direction of each. Confirmed from the outside as well:
`IBox<out T>` accepts an `IBox<int>` in an `IBox<long>` slot, and the same code without
`out` is refused with `tosh.type.mismatch`.

---

## F · Bugs

| # | case | observed | expected |
|---|---|---|---|
| F1 | `Two<int, string>(1, "x")` | `Function 'Two' expects 2 argument(s) but received 1` | calls it |
| F2 | `class Impl() fulfills Co<int> { func Get() -> int }` | `implements 'Co<int>.Get' with an incompatible return type — the interface declares T, the class declares int` | no diagnostic |
| F3 | `$x is Box<int>` | does not parse | a closed type test |
| F4 | `shared prop` on `Counter<T>` | one slot for every closure | C# gives each closed type its own |
| ~~F5~~ | ~~`prop V: T` with no initialiser~~ | `null` | see below |

F1 fires only for the parenthesised call form with more than one argument; the
space-separated `Two<int, string> 1 "x"` works. F2 makes every closed generic interface
implementation report a false mismatch — the contract check never substitutes the argument
for the parameter.

**F5 is not a generics finding.** `prop I: int` with no initialiser is null too, as is
`bool`, `string` and `double`. The divergence from C#'s `default(T)` is uniform across every
annotated property, not something generics does differently — so "fixing" it for a type
parameter alone would make generics inconsistent with the rest of the language rather than
closer to C#. Recorded as a language-wide question, not a gap in this area.

---

## G · What is already right

Recorded so it is not "fixed" later:

- Generic **class, record, union, interface, free function, instance/static/shared method**
  all declare and close correctly.
- **Inference** from arguments; explicit type arguments beat inference; partial inference is
  refused as C# refuses it; conflicting inference is refused with a message naming both.
- **Inheritance**: `class Child<T> extends Base<T>($x)` and `class Child extends Base<int>($x)`.
- **Recursive** generic types.
- **Multi-parameter** generics, including per-parameter constraint checking.
- **CLR generics**: `List<int>`, `Dictionary<string,int>`, `Array.Empty<int>()`, and passing
  a ToastScript generic into a CLR collection.

---

## Outcome

Every defect above is fixed. What is left are two features and one decision, which are
listed here rather than half-built — a generic construct that parses and does not bind is
the failure this audit named in E3, and building one deliberately would be worse than
leaving the gap.

| | done |
|---|---|
| **D1** | `any` is a synonym for dynamic again, in every position |
| **C** | a type parameter widens like C#, converting rather than merely permitting |
| **A1–A3** | a ToastScript type argument keeps its name and is checked nominally |
| **A4** | an annotation checks the closure, in every position |
| **B2/B3** | interface and base-class constraints are enforced |
| **F2** | a closed contract is compared closed |
| **F1** | a type-argument list no longer hides the call parenthesis |
| **F3** | `$x is Box<int>` parses and compares the closure |
| ~~F5~~ | withdrawn — uniform across the language, not a generics gap |
| ~~E3~~ | withdrawn — variance is implemented and tested |

### Left, as features

**E1 · generic traits.** `trait Holder<T>` is not recognised. Worth having: ToastLib's
`Componentwise` would be `Componentwise<T>`, and traits are the distinctive construct here.
The work is not the parser — it is that a trait injects default method *bodies* into the
using class, so its type parameters must be substituted through the injected members at
application time, against a using class that has type parameters of its own.

**E2 · generic structs.** `struct Pair<K, V>(k: K, v: V)` is not recognised. A struct is a
value-type analogue of a class and C# has generic ones, so this is a real gap rather than a
category error. `ToshStructDefinition` has no type-argument concept at all, so the work is
to port binding storage, constraint validation, nominal names, strict binding and the bound
descriptor — the same ground this audit covered for classes, which is most of what took the
effort.

### Left, as a decision

**F4 · per-closure statics.** `shared prop` on `Counter<T>` is one slot for every closure;
C# gives each closed type its own. Changing it is a real semantic change with a small
practical payoff, and someone may be relying on the shared slot. Worth deciding on purpose
rather than drifting into either answer.
