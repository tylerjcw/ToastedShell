---
id: TOAST-0046
title: "`-> void` is unspecified, and disagrees with `-> nothing` for the same declared type"
status: open
area: toast
priority: 2
opened: 2026-08-21
---

## Problem

`void` and `nothing` are the **same** bound type — `TypeNameResolver` maps both to
`BoundType.Void` — and they behave differently:

```tosh
func f() -> void    { echo "hi" }   # ✖ "returned a value that could not be converted to 'void'"
func f() -> nothing { echo "hi" }   # prints hi
func f() -> void    { var x = 1 }   # fine
```

So `-> void` fails only when the body **ends in an expression**, which is exactly when
`CollapseTrailingExpressionIntoReturn` turns that expression into a `return`. The binder
sees one type; the runtime's annotation conversion tells the two names apart, and only one
of them has a CLR type to fail against.

## Why this is a decision and not just a fix

**`docs/spec/` does not specify `-> void` on a Tōast function at all.** The only mention is
native callbacks: *"A `void` callback is the exception: it must produce nothing."*

And in TōSh a function's output **is** its pipeline value. So "returns nothing" is a claim
about output, not merely about a return slot:

```tosh
func f() -> void { echo "hi" }
f
echo "after"
```

Should that print `hi`? Today `-> nothing` says yes and `-> void` errors. Both readings are
defensible and the language has never said which.

An attempt to fix this by returning `null` for void-annotated functions made `echo "hi"`
**vanish** — it changed `-> nothing`'s behaviour while repairing `-> void`. That is a
semantic decision, so it was reverted rather than shipped.

## The options

1. **`void` means "produces no output".** The body runs, anything it yields is discarded,
   and `f` contributes nothing to a pipeline. Honest to the name; silently swallows output,
   which is a surprising thing for a shell language to do.
2. **`void` means "no *return value*, output unaffected".** `echo "hi"` still prints; the
   annotation only says callers should not expect a value. Closest to today's `-> nothing`,
   and to how an unannotated function already behaves.
3. **`void` is not a legal return annotation for a Tōast function** — reject it at compile
   time and keep it for native callbacks, where it is specified. Smallest surface, and
   `dynamic` already covers "I am not saying".

Whichever is chosen, `void` and `nothing` must stop disagreeing: they are one type.

## Acceptance

- [ ] `-> void` and `-> nothing` behave identically, being the same type
- [ ] The chosen meaning is in `docs/spec/`, since none of it is specified today
- [ ] The interpreted and compiled backends agree, in the differential corpus
- [ ] A negative control

## Notes

Found while auditing the type system for `TOAST-0048`. `nothing` is an alias nothing in the
specification mentions — it exists only in `TypeNameResolver`'s primitive table.
