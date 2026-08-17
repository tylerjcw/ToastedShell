---
id: TOAST-0020
title: "A trait's declared member types are not enforced on the implementing class"
status: open
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

- [ ] A class whose method returns a type incompatible with the trait's declaration is
      reported at class definition, naming the trait, the member, the expected type and
      the actual one
- [ ] The same for a required property's declared type
- [ ] **The compatibility rule is written down before it is implemented** — see below
- [ ] A class that satisfies the declaration exactly is unaffected, pinned as a control
- [ ] Interfaces checked for the same gap; `ToshInterfaceDefinition` sits beside
      `ToshTraitDefinition` in the same validation block
- [ ] The compiler path agrees — traits are emitted by `BoundUnitEmitter.TypeDeclarations`
- [ ] `TraitMemberSyntaxTests.A_declared_return_type_is_not_yet_enforced` flips from
      asserting `42` to asserting a diagnostic
- [ ] A negative control: reverting fails the new tests

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
