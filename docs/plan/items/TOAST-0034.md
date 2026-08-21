---
id: TOAST-0034
title: "A declared type is not used: the compile-time inferrer pins down literals and `new` and nothing else"
status: open
area: toast
priority: 2
opened: 2026-08-21
---

## Problem

Phase B's first bullet is "complete type-system support needed by compiler data
structures". Measured rather than assumed, the gap is sharper than that wording: **a type
the author has already written down is not used.**

```tosh
func f() -> int => 7
var v = f()          # tosh.compile.implicit_dynamic: could not pin down a concrete type
```

The return type is declared. The inferrer does not consult it.

## Measured 2026-08-21

Each case compiled on its own with `tosh --compile`, no flags:

| Expression | Inferred? |
|---|---|
| `var a = 1` | yes |
| `var xs = [1, 2, 3]` | yes |
| `var k = new K()` — a declared class | yes |
| `var v = f()` where `func f() -> int` | **no** |
| `var v = $k.M()` where `func M() -> int` | **no** |
| `var v = $k.V` where `prop V: int` | **no** |
| `var h = new System.Collections.Hashtable()` | **no** |
| `var v = Math.Round(1.5, 0)` | **no** |
| `var r = {\| a = 1 \|}` | **no** |

So inference reaches literals and a user-class construction, and stops at every call and
every member read. Annotating the *variable* works (`var v: int = (new K()).M()` compiles),
which is what makes this a propagation gap rather than a representation one: the compiler
can hold the type, it just will not derive it.

## Why this is the load-bearing one

`TOAST-0038`'s readiness probe fails with four `implicit_dynamic` errors, and every one is
a value that came from a call. Compiler-shaped code is mostly calls — a lexer returns
tokens, a parser returns a tree — so this decides whether Phase B's exit is reachable by
fixing the compiler or only by annotating every local in every program.

## Acceptance

- [ ] A call to a function with a declared return type infers that type
- [ ] A method call on a value of known type infers the method's declared return type
- [ ] A property read infers the property's declared type
- [ ] `new SomeClrType()` infers that type
- [ ] A record literal infers a record type
- [ ] A CLR static call infers its return type, or is stated as deliberately out of scope
- [ ] The table above becomes a test, including the rows that already pass as controls
- [ ] A negative control

## Notes

Found while turning Phase B's five bullets into items — the bullets were transcribed from
`docs/SELF_HOSTING_RFC.md` and each was measured before being written down. This one came
back much more specific than its bullet.
