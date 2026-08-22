---
id: TOAST-0046
title: "`-> void` is unspecified, and disagrees with `-> nothing` for the same declared type"
status: complete
area: toast
priority: 2
opened: 2026-08-21
closed: 2026-08-22
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

- [x] `-> void` and `-> nothing` behave identically, being the same type — asserted per
      spelling for every case rather than one standing in for the other, because "they are
      the same type" was true before this change too and they still disagreed
- [x] The chosen meaning is in `docs/spec/`, since none of it is specified today
- [x] The interpreted and compiled backends agree, in the differential corpus — two of the
      three cases. The third found a divergence that is not about `void`: `TOAST-0066`
- [x] A negative control — two of them, and the first one **passed**, which is the finding
      recorded below

## Correction — 2026-08-22

**The problem statement was stale in a way that mattered.** It says `-> nothing` prints
`hi`. Measured before starting, on this build *and* on the installed one that predates all
of it, neither spelling worked:

| | Result |
|---|---|
| `func f() -> void { echo "hi" }` | `return_type_conversion_failed` — converting the value to the CLR's `System.Void` |
| `func f() -> nothing { echo "hi" }` | `annotation_unknown_type` — the *runtime* resolver had never heard of `nothing` |

The binder maps both to `BoundType.Void`; the runtime knew neither. So they did not disagree
about a meaning — they failed for two different reasons, which is why they looked like two
behaviours.

## Resolution — 2026-08-22

**A fourth option, chosen by the user: `-> void` is allowed and *checked*, the way C# checks
it.** A void function may not say what it evaluates to.

- `echo`, `yield`, or `return` with an operand inside a `-> void` body is
  `tosh.compile.void_function_produces_output`. `echo` emits a pipeline value, so in a
  language where output *is* the return value it is exactly C#'s `return expr;`.
- `writeline` writes to the console and yields nothing, so a void function still prints. That
  distinction was measured rather than assumed — `echo "e" | count` is 1 and
  `writeline "w" | count` is 0 — and it is what makes the rule livable instead of crippling.
- If a value is produced by a route the check cannot see, it is refused when it runs, as
  `tosh.runtime.void_function_produced_value`. **Nothing is discarded silently**, which is
  what ruled out the "void means the output is dropped" option: a shell that swallows output
  is a sharp edge.

The check lives in `TypeChecker.Check`, which runs on all three surfaces — interpreter,
compiler and the language server — so the rule is stated once. Compiled it is an error and
stops the build; interpreted it is a warning and the runtime refusal follows, which is the
convention this codebase already uses for type diagnostics.

### The negative control that passed

The first control removed the shared conversion branch and **no test failed**. That meant the
branch was doing work nothing was watching — and it was: without it, `var x: void = null` is
refused. A return is not the only annotated position, and the rule has to hold in all of them
or `void` means one thing on a function and another on a variable.

Two tests were added and the control re-run; it now fails two, and removing the interpreted
return handling fails four. A control that passes is worth more than one that fails, and this
is the second time in three items that running one changed what shipped.

### What this found next door

The third corpus case — a void function contributing nothing to a pipeline — diverges: `0`
interpreted, `1` compiled. It is not about `void`. A compiled function whose result is null
contributes one pipeline value where the interpreter contributes none, and `-> void` is
merely where the null is guaranteed. Filed as `TOAST-0066`, along with the adjacent finding
that returning `null` from a `-> dynamic` function is refused interpreted — on the installed
build too, so it predates this work.

## Notes

Found while auditing the type system for `TOAST-0048`. `nothing` is an alias nothing in the
specification mentions — it exists only in `TypeNameResolver`'s primitive table.
