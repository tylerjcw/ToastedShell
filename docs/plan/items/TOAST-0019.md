---
id: TOAST-0019
title: "A trait member is not written the way a class member is"
status: complete
area: toast
priority: 2
opened: 2026-08-17
closed: 2026-08-17
---

> **Refiled 2026-08-17.** As first written this item claimed a trait member *cannot declare
> a return type*. That was wrong: it can, spelled `func f(): T`. The defect is narrower and
> different — traits accept a **different syntax** from every other declaration for three
> things. The original claim came from testing only the `->` spelling and concluding the
> feature was absent.

## Problem

Traits had their own hand-rolled member parser, so three spellings that work everywhere
else did not work inside a `trait`:

| Written | In a class | In a trait, before |
|---|---|---|
| `func f() -> T` | ok | **error** — only `func f(): T` |
| `func f() -> T => expr` | ok | **error** — only `{ ... }` |
| `prop X: T` | ok | **error** — only a bare `prop X` |

Two declarations two lines apart accepted different syntax for the same thing.

The property case had a specific cause. The lexer glues `X:` into a single bareword, so
`prop X: int` reached the trait parser as the *name* `X:`, which `ExpectVariableName`
rejected — the diagnostic said "Expected a variable name" and pointed at a name the reader
had written correctly. Class properties already handled this through
`ParseTypedIdentifierToken`; the trait parser did not call it.

## Acceptance

- [x] `func name() -> T` parses in a trait, as a required member
- [x] `func name() -> T => expr` parses in a trait, as a member with a default
- [x] `prop name: T` parses in a trait, as a required member
- [x] Every previous spelling still works — `: T`, a `{ ... }` body, a bare `prop X`
- [x] A missing typed property is still reported, so the fix is not "parse the type and
      forget it"
- [x] A negative control: 6 of 9 new tests fail with the parser reverted; the 3 that pass
      are the backward-compatibility cases

## Resolution

The trait parser now calls the same helpers every other declaration uses:
`TryParseReturnTypeAnnotation` for `->`, `IsFatArrow`/`ParseFunctionArrowBody` for a short
body, and `ParseTypedIdentifierToken` + `ParseTypeNameSuffix` for a property name and type.
One rule in one place, rather than a second grammar that drifts.

**The `:` return-type form is still accepted.** It was the only spelling traits ever took,
so removing it would have been a second defect rather than a fix.

The decision that prompted this — `TOAST-0014`'s rendering extension point — is now
expressible as written:

```tosh
trait Display { func render() -> string }
```

## Deliberately not done

**A declared return type is not enforced.** A class may satisfy `func render() -> string`
with an implementation returning `int`, and nothing reports it. Split out as `TOAST-0020`,
because "compatible" needs a variance rule — exact name, alias, subclass, interface — and
deciding that inside a parser-parity change is how a semantics decision ends up in a
mechanical diff.

`TraitMemberSyntaxTests.A_declared_return_type_is_not_yet_enforced` pins the current
behaviour, so `TOAST-0020` landing will fail that test rather than passing unnoticed.

## Notes

Traits do **not** apply to CLR-backed values: `42 is Show` is `false` even with an
`extend Int32` supplying the member. Expected, and out of scope here — but it is why
`TOAST-0014` specifies built-in rendering rules for scalars and containers and uses the
trait only as the user extension point.

Noticed while probing and not chased: `class A { abstract func f() -> string }` reports
"write '{ ... }' after 'func'", so an abstract member appears to require a body. Not
verified beyond one probe, and not part of this item.
