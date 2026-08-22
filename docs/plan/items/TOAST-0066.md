---
id: TOAST-0066
title: "A compiled function's null result contributes a pipeline value where the interpreter's contributes none"
status: proposed
area: toast
priority: 2
opened: 2026-08-22
---

## Problem

A function that produces nothing still occupies a slot in the pipeline when compiled:

```tosh
func f() -> void { writeline "hi" }
echo (f | count)
```

| Backend | Result |
|---|---|
| interpreted | `0` |
| compiled | `1` |

`writeline` writes to the console and yields no value, so `f` produces nothing and the
interpreter contributes nothing. The compiled function returns `null` and that `null` is
piped as a value.

## It is not about `void`

`TOAST-0046` reached it through `-> void`, which is only the case where the null is
*guaranteed*. The difference is in how a compiled user function's result reaches the
pipeline: a null result becomes one value rather than none.

That makes it a question about the calling convention rather than about a type. The obvious
fix — dropping nulls in `RunUserFuncStage` — would also silence a function that returns
`null` deliberately, which the interpreter does distinguish, so the convention needs deciding
rather than patching.

## Adjacent, and probably the same conversation

Returning `null` from an annotated function fails interpreted, for **every** annotation
including `dynamic`:

```tosh
func g() -> dynamic { return null }   # ✖ "returned a value that could not be converted to 'dynamic'"
```

Measured on the installed build as well, so it predates the work that found it. `dynamic`
accepting no value at all is hard to defend: the annotation exists to say "anything". The
nullability rule for return annotations is unstated, and this is the same gap seen from the
other side.

## Acceptance

- [ ] A compiled function producing nothing contributes nothing to a pipeline
- [ ] A function returning `null` *deliberately* behaves the same on both backends, whatever
      that is decided to be
- [ ] `-> dynamic` accepts `null`, or the specification says why it does not
- [ ] The case moves from `KnownDivergences()` into `Corpus()`
- [ ] `docs/spec/` states whether a null result is a pipeline value
- [ ] A negative control

## Notes

Found by `TOAST-0046`'s differential corpus, which is exactly what that corpus is for: the
two `-> void` cases using `writeline` agree on both backends, and this third one did not.
Related: `TOAST-0065`, `TOAST-0022` and `TOAST-0030` record the other divergences.
