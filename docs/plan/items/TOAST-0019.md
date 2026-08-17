---
id: TOAST-0019
title: "A trait member cannot declare a return type"
status: open
area: toast
priority: 2
opened: 2026-08-17
---

## Problem

A trait member accepts typed parameters but rejects a return type annotation, whether it
is a required member or one with a default body:

```tosh
trait Show { func fmt(spec: string) }          # ok — typed parameter
trait Show { func show() -> string }           # error: expected 'func' or 'prop'
trait Show { func show() -> string => "d" }    # error: expected 'func' or 'prop'
```

So a trait can require *that* a member exists and what it takes, but not what it gives
back. The implementing class may return anything:

```tosh
trait Show { func show() }
class C uses Show { func show() -> int => 42 }   # accepted
```

A trait is the language's mechanism for "types that can do X". Half of what a caller needs
to know about X is its result type, and today that half cannot be written down.

## Acceptance

- [ ] `func name() -> T` parses in a trait, as a required member
- [ ] `func name() -> T => expr` parses in a trait, as a member with a default
- [ ] A class whose implementation returns an incompatible type is reported
- [ ] `prop name: T` checked for the same gap — a required property's type may have the
      same hole
- [ ] The compiler path agrees with the interpreter, since traits are emitted by
      `BoundUnitEmitter.TypeDeclarations`
- [ ] A negative control: reverting fails the new tests

## Notes

Found deciding how `TOAST-0014` should spell its extension point. The chosen answer is a
`Display`/`Render` trait — the most Tōast-shaped option, and the one a native target can
dispatch without reflection — and the trait it wants is:

```tosh
trait Display { func render() -> string }
```

which is exactly the form that does not parse. The trait can be declared without the
return type and the renderer can check the result at runtime, so this is not a hard
blocker; it does mean the spec would define a rendering contract whose return type the
language cannot state.

Worth fixing before `TOAST-0014` lands its trait, so the spec and the language agree from
the first version rather than the spec describing an aspiration.

Related: traits do **not** apply to CLR-backed values — `42 is Show` is `false` even with
an `extend Int32` supplying the member. That is expected and not part of this item, but it
is why `TOAST-0014` specifies built-in rendering rules for scalars and containers and uses
the trait only as the user extension point.
