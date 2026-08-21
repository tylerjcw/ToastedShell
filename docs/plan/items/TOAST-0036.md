---
id: TOAST-0036
title: "There is no concrete function type, so no higher-order value can be typed"
status: open
area: toast
priority: 2
opened: 2026-08-21
---

## Problem

Phase B's third bullet asks that "higher-order calls, interfaces, unions, narrowing,
generics, and method references" be made reliable. Measured, **four of those six already
compile**, and the two that do not share one cause: there is no way to write the type of a
function.

`func` is a keyword, not a concrete type. Annotating with it does not make a value
concrete:

```tosh
var f: func = func(x: int) -> int => $x + 1   # tosh.compile.implicit_dynamic
```

The annotation is present and still means "some function", so the compiler learns nothing
from it — no parameter types, no return type, nothing to emit a typed call against.

## Measured 2026-08-21

Each compiled on its own with `tosh --compile`, no flags:

| Shape | Result |
|---|---|
| interface with an implementing class | compiles |
| union declaration and use | compiles |
| generic class `Box<T>` | compiles |
| method reference `$k.M(1)` | compiles |
| `match` narrowing over a hierarchy | compiles |
| anonymous function in a variable | **`implicit_dynamic`** |
| function as a parameter (`g: func`) | **`missing_type_annotation`** |
| function reference `&dbl` | **`implicit_dynamic`** |
| closure capture returning a `func` | **`implicit_dynamic`** |

So the bullet is mostly already satisfied, and what remains is not a reliability problem
but a **missing piece of the type system**: a signature type, something like
`func(int) -> int`, that says what a function value accepts and returns.

## The decision this needs

What is the spelling, and how much does it have to express?

1. **`func(int) -> int`** — mirrors the declaration syntax, reads the same way as the thing
   it describes. Wants a rule for optional and rest parameters.
2. **A named delegate declaration** — `delegate Transform(x: int) -> int`, then
   `var f: Transform`. Cheap to resolve and emit; adds a declaration form.
3. **Structural only** — infer from the assigned lambda and require the annotation only at
   boundaries. Least new syntax; does nothing for a `func` *parameter*, which is where the
   `missing_type_annotation` above comes from.

Option 1 or 2, and the question underneath is whether a function type is a *value* the
language names or a *shape* it merely checks.

## Acceptance

- [ ] A function value can be given a concrete type, and that type is used
- [ ] A function-typed parameter compiles without `--compile-allow-dynamic`
- [ ] A function reference (`&name`) and a closure both compile typed
- [ ] The five shapes that already compile stay compiling, as controls
- [ ] `docs/spec/` states the function type
- [ ] A negative control

## Notes

The bullet's wording implied six unreliable features. Measuring first turned it into one
missing type and five working ones — which is a much smaller item, and a different kind of
work than "make X reliable" suggests.
