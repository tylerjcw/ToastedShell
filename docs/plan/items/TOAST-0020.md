---
id: TOAST-0020
title: "A trait's declared member types are not enforced on the implementing class"
status: partial
area: toast
priority: 2
opened: 2026-08-17
---

## Problem

A trait can declare what a member returns, and a class can ignore it:

```tosh
trait Display { func render() -> string }
class T uses Display { func render() -> int => 42 }
(new T()).render()      # 42 — accepted, nothing reported
```

The same holds for a required property's type. Conformance checking today asks only
whether a member *exists*: `ToshEngine.Types.cs:407` calls
`traitDefinition.GetMissingMethods(definition)` and `GetMissingProperties(definition)`, and
both compare names.

So half of what a trait is for cannot be relied on. A caller holding a value known to be
`Display` still cannot assume `render()` gives back a string, which makes the trait a
naming convention rather than a contract — and it is exactly the assumption a renderer,
a formatter or a compiler wants to make.

## Acceptance

- [x] A class whose method returns a type incompatible with the trait's declaration is
      reported at class definition, naming the trait, the member, the expected type and
      the actual one
- [x] **The compatibility rule is written down before it is implemented** — decided
      2026-08-17, recorded below
- [x] A class that satisfies the declaration exactly is unaffected, pinned as a control
- [x] `TraitMemberSyntaxTests` flips from asserting `42` to asserting a diagnostic
- [x] A negative control: 2 of 9 fail with the check reverted
- [x] The same for a required property's declared type — **invariant**, decided separately
- [x] Interfaces checked for the same gap, with the same rule
- [ ] The compiler path agrees — **not done**, see below

## Decision — 2026-08-17

**Covariant returns, exact parameters, reported at class definition.**

A class may return the declared type or one derived from it, because narrowing a result
never surprises a caller holding the trait. A parameter must name the same type:
contravariance would be sound, but it is rarely wanted, frequently misread, and half a
variance rule is worse than a simple one. An **undeclared** type on either side agrees with
anything — a trait that says nothing constrains nothing, and a class that says nothing has
not contradicted the trait, it has only declined to repeat it. An alias and its CLR
spelling name one type and agree.

### What measuring changed about the question

The item read as "add checking to an unchecked language". It is not. A class's own
annotations already **coerce**: `func f() -> string => 42` yields `"42"`, a parameter
`f(x: string)` given `42` receives `"42"`, and an uncoercible value raises. The *trait's*
annotations were the only ones that did nothing at all, in either position. So the question
was never whether the language checks types — it was why one declaration site was inert.

### Where it is checked, and why not in the checker

In the engine's trait-conformance block, beside `GetMissingMethods`. The rule needs a
subtype relation; `TypeChecker` holds annotation *names* and not declarations — it records
that limitation itself — while the engine holds both the trait and the class. `IsCovariantWith`
asks `SatisfiesContract`, the walk that already answers "does this class fulfil that
contract" for interfaces and traits alike, and then walks the base chain.

## Properties and interfaces — 2026-08-17

**A trait property's type is invariant.** Decided separately from the method rule and for a
reason that only applies to properties: a property is *written* as well as read. Narrowing
it is unsound in a way narrowing a return is not — code holding the trait could assign the
declared type into what the class narrowed, and the class's own annotation would try to
coerce it and fail, at the assignment and nowhere near the declaration that permitted it.
C# and Java keep fields invariant for the same reason. A test pins the asymmetry directly:
the *same* narrowing is refused in property position and accepted in return position.

**Interfaces get the same rule as traits.** They are methods-only — `interface I { prop N }`
is refused by the parser — so covariant returns and exact parameters is the whole of it for
them. They had the identical gap and sat one block away; two neighbouring constructs
behaving differently would need a stated reason, and there was none.

The three call sites share `ThrowOnContractTypeMismatch`, which names the contract kind in
its help text, so a trait and an interface each say what they are.

## Remaining

One acceptance box is left: the **compiler** path, where traits are emitted by
`BoundUnitEmitter.TypeDeclarations` and a compiled class is a real CLR type rather than a
`ToshClassInstance` — the same divergence `TOAST-0022` records.

## The decision this needs first

"Compatible" is a variance question, and it has no recorded answer:

- Is `-> int` satisfied by an implementation returning `-> int` only, or by any subtype?
- Do aliases match — `int` against `Int32`, `str` against `string`?
- May an implementation return a **subclass** of the declared type? (Covariance — usually
  yes, and usually wanted.)
- May a parameter take a **supertype**? (Contravariance — sound, rarely implemented,
  frequently confusing.)
- What about `null`, and a declared type that is nullable versus one that is not?

Answering by writing code produces whatever the first implementation happened to accept,
and that becomes the language's rule by accident. This wants a paragraph in `docs/spec/`
first — which is the same discipline `TOAST-0014` is being run on.

## Notes

Split from `TOAST-0019`, which made a trait member's syntax match a class member's. That
change made the *declaration* expressible; this one makes it mean something.

Not blocking `TOAST-0014`. A rendering trait is useful the moment it can be declared and
dispatched, and a class that returns the wrong type from `render()` fails at the point the
renderer uses the result. Enforcement moves that failure to the declaration, which is
better but not required.

Related: `TS-P3-16`'s successor `TOAST-0018` carries the wider "specify the semantics"
arc. Variance is arguably part of it, and if this item waits, that is where it lands.
