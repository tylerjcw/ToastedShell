---
id: TOAST-0097
title: "A type cannot be given a static member from outside, so `Option::from` has nowhere to live"
status: proposed
area: toast
priority: 3
opened: 2026-08-29
---

## Problem

`extend` adds *instance* methods and nothing else. A `static func` inside an `extend` block
parses and is silently never found:

```tosh
class C { prop X = 1 }
extend C { static func make() { return "hi" } }

C::make()        # tosh.runtime.expression_failed — no such static
```

`static func` in a **class body** works, so this is specific to `extend`. And a union has no body
that takes methods at all — `union M { A(v) func f() { } }` is a parse error — so for a union
there is no route to a static member from anywhere.

The failure mode is the one `TOAST-0016` already fixed once for instance extensions: a
declaration that is accepted, stored, and then matches nothing at the point of *use*, in a place
that looks unrelated to the declaration.

## Why it came up

`TOAST-0083` decided that `null` and `Option<T>` convert only by name, and the surface put to the
user for that decision read:

```tosh
var v: Option<string> = Option::from(Env::get("HOME"))
```

`Option::from` cannot exist. `Option` is a union, so it has no body for a static, and `extend`
cannot supply one. The conversion shipped as a free function instead:

```tosh
option-from $nullable        # what exists
Option::from($nullable)      # what was described
```

`or-null()` is unaffected — it is an instance method and lives in `extend Option` as intended.

## Candidate surface

```tosh
extend Option {
    static func from(value) {
        return ($value is null ? Option::None<dynamic>() : Option::Some($value))
    }
}
```

`_extensionMethods` is keyed by receiver type name and consulted only when there *is* a receiver.
A static extension needs the same table consulted from the static-member resolution path, where
the "receiver" is the type itself.

## Acceptance

- [ ] `static func` in an `extend` block is reachable as `Type::name(…)` and `Type.name(…)`
- [ ] It works for a union, whose own body cannot declare one at all
- [ ] A `static func` that is accepted must be findable — no silent registration, per `TOAST-0016`
- [ ] `option-from` becomes `Option::from`, with the free function kept or retired deliberately
- [ ] Extending a type that already declares a static of that name is a diagnostic, not a
      silent winner
- [ ] Interpreter and compiler agree
