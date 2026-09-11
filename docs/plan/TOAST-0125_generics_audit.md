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
| E3 | `interface Co<out T>` / `<in T>` | **parses and means nothing** — no variance is implemented |

E3 is the dangerous one: accepting a keyword that has no effect is worse than rejecting it.

---

## F · Bugs

| # | case | observed | expected |
|---|---|---|---|
| F1 | `Two<int, string>(1, "x")` | `Function 'Two' expects 2 argument(s) but received 1` | calls it |
| F2 | `class Impl() fulfills Co<int> { func Get() -> int }` | `implements 'Co<int>.Get' with an incompatible return type — the interface declares T, the class declares int` | no diagnostic |
| F3 | `$x is Box<int>` | does not parse | a closed type test |
| F4 | `shared prop` on `Counter<T>` | one slot for every closure | C# gives each closed type its own |
| F5 | `prop V: T` with no initialiser, `T` = `int` | `null` | `default(T)` — `0` |

F1 fires only for the parenthesised call form with more than one argument; the
space-separated `Two<int, string> 1 "x"` works. F2 makes every closed generic interface
implementation report a false mismatch — the contract check never substitutes the argument
for the parameter.

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

## Suggested order

1. **D1** — `any`. One name, documented, broken everywhere, trivially fixed.
2. **C / `TOAST-0124`** — widening. Unblocks `Vector2D<double>` and generic factories.
3. **A1–A3** — make a type argument that names a ToastScript type or alias bind to
   something the checker can use. This is the root of the soundness column.
4. **B2/B3** — enforce interface and base-class constraints.
5. **F2** — substitute type arguments in the contract check.
6. **A4** — check the closure in annotations. Depends on 3.
7. **F1**, **F3**, **F5**, **E1/E2** — bugs and surface, in whatever order suits.
8. **E3** — either implement variance or refuse the keywords.
9. **F4** — decide deliberately; per-closure statics is the C# rule and a real change.
