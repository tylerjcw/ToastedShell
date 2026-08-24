---
id: TOAST-0066
title: "A compiled function's null result contributes a pipeline value where the interpreter's contributes none"
status: complete
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

## Resolution — 2026-08-23

**The convention needed a way to say "no value", so there is one.** A compiled function
returns a single `object?` and had no way to distinguish producing nothing from producing
null, so the null stood in for the absent value and was counted. `ToshNoValue.Instance` is
that distinction, carried at run time because it cannot be decided at compile time:
`func f(x) { if ($x) { return 1 } }` contributes one value or none depending on the branch,
and the interpreter reports 1 and 0.

It is emitted at three points, all meaning "this produced nothing": falling off the end of an
untyped function, a bare `return`, and a returned expression that itself yielded no value.
That third one is how `-> void` reaches it — `void` is not a concrete CLR type, so a void
function takes the untyped path, and its trailing `writeline "hi"` is collapsed into a return
whose expression produces nothing.

**Only a pipeline stage distinguishes the two**, which is what made this safe to do. In an
assignment, a subexpression argument, or a comparison, the interpreter reads a function that
produced nothing as null — so the sentinel is normalised away at the call site and at the
command-value boundary, and never reaches a value a reader can hold. `InvokeValueOrNothing`
exists only for the return path for that reason.

The adjacent gap the item predicted was real: `-> dynamic` refused null with
`return_type_conversion_failed`. `dynamic` is the opt-out from annotation checking and
cannot itself be a check only some values pass. `string` still refuses null and `string?`
accepts it, which is the nullability rule working rather than a hole in it.

Recorded while closing: both backends refuse a null return through a non-nullable annotation,
in different words. Filed as [`TOAST-0074`](TOAST-0074.md).

## Acceptance

- [x] A compiled function producing nothing contributes nothing to a pipeline
- [x] A function returning `null` *deliberately* behaves the same on both backends — it
      contributes one value, and dropping nulls to fix the count would have silenced it
- [x] `-> dynamic` accepts `null`
- [x] The case moves from `KnownDivergences()` into `Corpus()` — with eleven companions
      covering both sides of the distinction, the branch-dependent case, and the value
      positions where the sentinel must not be visible
- [x] `docs/spec/` states whether a null result is a pipeline value — a new *A function that
      produces nothing contributes nothing* paragraph, plus one on null and return
      annotations; every claim in both was run against the implementation
- [x] A negative control — normalising in the stage instead of skipping fails four cases

## Notes

Found by `TOAST-0046`'s differential corpus, which is exactly what that corpus is for: the
two `-> void` cases using `writeline` agree on both backends, and this third one did not.
Related: `TOAST-0065`, `TOAST-0022` and `TOAST-0030` record the other divergences.
