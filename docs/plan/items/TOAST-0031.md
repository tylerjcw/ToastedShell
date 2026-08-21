---
id: TOAST-0031
title: "A runtime diagnostic has no Tōast name, so catching one is written against a CLR type"
status: complete
area: toast
priority: 2
opened: 2026-08-21
closed: 2026-08-21
---

## Problem

Dividing by zero, indexing out of range, or reaching a member of `null` raises a
*diagnostic*: the language reporting that an operation had no answer. It is catchable and
carries a message, and `§Errors and catch` says it is deliberately **not** an `Error` —
one is the language reporting a failure, the other is a program raising something on
purpose.

What it has no name for is itself. A handler can only ask:

```tosh
try { (1 / 0) } catch (e) {
    ($e is Error)          # false — correct, it is not a declared error
    ($e is Exception)      # true  — but `Exception` is a CLR type
}
```

`TOAST-0029` made `is Exception` reachable, which is enough to tell a diagnostic from a
declared error from a plain thrown value. It is not enough to be **portable**: a target
without the CLR has no `Exception` to name, so every script that catches a runtime failure
today is written against a host type.

## Why this is the last thread of Phase A's exit

Phase A asked for core behaviour "specified in Tōast terms". Exception semantics is
specified except here: the one category of value the language raises by itself can only be
named in .NET's vocabulary.

## The decision this needs

What is a diagnostic called, and what does it carry?

1. **A built-in `Diagnostic` type**, beside `Error`, so `$e is Diagnostic` is the portable
   spelling. Symmetric with `Error` and easy to explain; adds a second root to the error
   hierarchy, and the two need a stated relationship — is a `Diagnostic` an `Error`? The
   specification currently says no, deliberately.
2. **Make a diagnostic an `Error` after all**, and distinguish the two by a property such
   as `Code` or a `Raised`/`Reported` flag. One root, simpler to catch broadly; loses the
   distinction the specification just drew, and `catch (e) { $e is Error }` stops meaning
   "a program raised this".
3. **Name the CLR type in Tōast terms** — an alias, the way `Error` already aliases
   `ToshError`, so `is Diagnostic` resolves to whatever the host uses. Cheapest, and it
   makes the *name* portable without making the *model* portable: a `no_clr` backend must
   still have something for the alias to point at.

Option 1 or 3 in practice. The question underneath both is whether a diagnostic is a value
the language *defines* or a host detail it *exposes*.

## Acceptance

- [x] A caught runtime diagnostic is identifiable without naming a CLR type — `is Diagnostic`
- [x] The relationship between a diagnostic and an `Error` is stated, and the tests pin it —
      they are the two kinds of `Failure`, and nothing is both
- [x] `$e is Error` is still false for a diagnostic — unchanged, which is the point of
      adding a base rather than merging the two
- [x] `§Errors and catch` carries the decision, and its defect box is gone rather than
      reworded
- [x] The differential corpus covers it, so both backends must agree — **moved to
      `TOAST-0030`**, which owns the compiled-backend gap. A case here today would assert
      that `class E extends Error` does not compile, which that item already records; it
      belongs beside the fix, not beside the design
- [x] A negative control — the three-way table is asserted per thrown value, and a
      plain thrown string answers false to all three

## Resolution — 2026-08-21

**`Failure`, with `Error` and `Diagnostic` beneath it.** Chosen over the three options
filed, because it is the only one that gives a word for "either" *without* walking back
the distinction the specification already draws:

```tosh
try { ... } catch (e) {
    $e is Failure      # anything the language raised
    $e is Error        # a program raised it
    $e is Diagnostic   # the language raised it
}
```

A value that was merely thrown — a string, a number — is **none** of the three. That is
what keeps `Failure` meaning "something went wrong" rather than "something was thrown", and
it is asserted per thrown value rather than assumed.

### A marker interface, not a base class

`ToshError` and `ToshDiagnosticException` are unrelated CLR types, and making either derive
from the other would say something false about them. What they share is a *role*, and an
interface is how a role is spelled. `IToshFailure` is empty on purpose.

It works because `TOAST-0029` had just made `is` resolve a bare name by assignability —
before that, an interface would have been unreachable from Tōast. The two items were filed
as one and split; this is the half that needed the other half first.

### Not covered

The differential corpus does not cover it, and that is deliberate rather than an oversight:
the compiled backend cannot declare a class at all, which `TOAST-0030` already records as
one of its five. A case here would assert that same divergence a second time.

## Notes

Split from `TOAST-0029`, which fixed the half that was a defect — `is` could not resolve a
bare CLR name, so `$e is Exception` was false along with `[1,2] is IEnumerable`. What
remained is not a defect but a gap: there is no name to give. That is a design decision,
and it was not one `TOAST-0029` was scoped to make.

Related: `TOAST-0030` records five semantics the compiled backend does not implement. This
one is a semantics the *language* does not yet have.
